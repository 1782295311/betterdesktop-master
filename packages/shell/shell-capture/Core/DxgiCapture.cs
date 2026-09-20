using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BetterDesktop.Capture.Contracts;
using BetterDesktop.Shell.Capture.Native;

namespace BetterDesktop.Shell.Capture.Core;

/// <summary>
/// DXGI Desktop Duplication 采集（后端 #2：WGC 不可用时的无边框可靠路径）。
/// 手工 COM 互操作（见 Native/D3DInterop.cs）；逐输出重复、复制到 staging 后 CPU 读回，
/// 多输出合成到虚拟屏矩形。单帧 4K 的 GPU→CPU 拷贝是唯一大开销，路径上无多余拷贝。
/// </summary>
public sealed class DxgiCapture : IBackendCapture
{
    private readonly IntPtr _factory;
    private readonly IntPtr _device;
    private readonly IntPtr _context;

    private DxgiCapture(IntPtr factory, IntPtr device, IntPtr context)
    {
        _factory = factory;
        _device = device;
        _context = context;
    }

    public CaptureBackend Kind => CaptureBackend.Dxgi;

    public string Name => "DXGI";

    /// <summary>创建 DXGI 采集器（工厂 + D3D11 设备）。失败返回 null（原因由调用方并入降级链）。</summary>
    public static DxgiCapture? TryCreate(out string error)
    {
        using var com = ComHelper.TryInitializeMta();
        IntPtr factory = D3DInterop.CreateFactory1(out int hrF);
        if (factory == IntPtr.Zero)
        {
            error = $"CreateDXGIFactory1 失败（0x{hrF:X8}）";
            return null;
        }

        IntPtr device = D3DInterop.CreateHardwareDevice(out int hrD);
        if (device == IntPtr.Zero)
        {
            Marshal.Release(factory);
            error = $"D3D11CreateDevice 失败（0x{hrD:X8}）";
            return null;
        }

        var d3dDevice = (D3DInterop.ID3D11Device)Marshal.GetObjectForIUnknown(device);
        d3dDevice.GetImmediateContext(out IntPtr context);
        error = string.Empty;
        return new DxgiCapture(factory, device, context);
    }

    /// <summary>采集虚拟屏矩形（含负坐标）。不包含光标（服务层统一合成）。</summary>
    public bool Capture(PixelRect bounds, out RawFrame? frame, out string error)
    {
        frame = null;
        if (bounds.IsEmpty)
        {
            error = "采集矩形为空";
            return false;
        }

        using var com = ComHelper.TryInitializeMta();
        var parts = new List<(RawFrame Frame, PixelRect Rect)>();
        var failures = new List<string>();
        try
        {
            var outputs = EnumerateOutputs();
            if (outputs.Count == 0)
            {
                error = "未找到与采集矩形相交的显示输出";
                return false;
            }

            foreach (var (outputPtr, desc) in outputs)
            {
                var outputRect = PixelRect.FromLTRB(desc.DesktopLeft, desc.DesktopTop, desc.DesktopRight, desc.DesktopBottom);
                if (outputRect.Intersect(bounds).IsEmpty)
                {
                    continue;
                }

                try
                {
#pragma warning disable CA2000 // 所有权转移：part 加入 parts 列表，由成功/失败路径统一释放
                    if (!TryCaptureOutput(outputPtr, outputRect, bounds, out var part, out string partError))
                    {
                        failures.Add(partError);
                        continue;
                    }
#pragma warning restore CA2000
                    if (part is null)
                    {
                        failures.Add("DXGI 输出返回空帧");
                        continue;
                    }
                    parts.Add((part, outputRect));
                }
                finally
                {
                    // EnumerateOutputs 持有的 raw ref 在此释放（RCW 另有自己的引用计数，GC 时自清）。
                    Marshal.Release(outputPtr);
                }
            }

            if (parts.Count == 0)
            {
                error = failures.Count > 0 ? string.Join("；", failures) : "所有显示输出采集失败";
                return false;
            }

            // 合成到虚拟屏矩形。
            var target = RawFrame.Allocate(bounds, parts[0].Frame.Format);
            bool mixedFormats = false;
            foreach (var (part, rect) in parts)
            {
                if (part.Format != target.Format)
                {
                    mixedFormats = true;
                    continue;
                }
                FrameOps.CompositeBgra(target, part, rect.X - bounds.X, rect.Y - bounds.Y);
            }

            foreach (var (part, _) in parts)
            {
                part.Dispose();
            }

            if (mixedFormats)
            {
                target.Dispose();
                error = "多输出像素格式不一致（HDR/SDR 混合），无法合成";
                return false;
            }

            frame = target;
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            foreach (var (part, _) in parts)
            {
                part.Dispose();
            }
            error = $"DXGI 采集异常：{ex.Message}";
            return false;
        }
    }

    private List<(IntPtr Output, D3DInterop.DXGI_OUTPUT_DESC Desc)> EnumerateOutputs()
    {
        var result = new List<(IntPtr, D3DInterop.DXGI_OUTPUT_DESC)>();
        var factory = (D3DInterop.IDXGIFactory1)Marshal.GetObjectForIUnknown(_factory);
        for (uint adapter = 0; ; adapter++)
        {
            int hr;
            IntPtr adapterPtr;
            try
            {
                hr = factory.EnumAdapters1(adapter, out adapterPtr);
            }
            catch (Exception)
            {
                // 个别环境（如 IDD 显示驱动栈）DXGI 工厂行为异常：放弃该后端，交由降级链。
                break;
            }
            if (hr != 0)
            {
                break;
            }

            try
            {
                // 枚举到的适配器可能只支持 IDXGIAdapter（无 IDXGIAdapter1，典型：IDD 显示驱动）：
                // 单个适配器失败只跳过该适配器，不中断整体枚举（首个适配器为 IDD 时仍能采到真实 GPU）。
                var dxgiAdapter = (D3DInterop.IDXGIAdapter1)Marshal.GetObjectForIUnknown(adapterPtr);
                for (uint output = 0; ; output++)
                {
                    int ohr;
                    IntPtr outputPtr;
                    try
                    {
                        ohr = dxgiAdapter.EnumOutputs(output, out outputPtr);
                    }
                    catch (Exception)
                    {
                        break;
                    }
                    if (ohr != 0)
                    {
                        break;
                    }
                    try
                    {
                        var dxgiOutput = (D3DInterop.IDXGIOutput)Marshal.GetObjectForIUnknown(outputPtr);
                        dxgiOutput.GetDesc(out var desc);
                        result.Add((outputPtr, desc));
                    }
                    catch
                    {
                        Marshal.Release(outputPtr);
                    }
                }
            }
            catch (Exception ex)
            {
                CaptureLog.Warn($"DXGI 适配器 {adapter} 不可用，跳过：{ex.Message}");
            }
            finally
            {
                Marshal.Release(adapterPtr);
            }
        }
        return result;
    }

    private bool TryCaptureOutput(IntPtr outputPtr, PixelRect outputRect, PixelRect bounds, out RawFrame? frame, out string error)
    {
        frame = null;
        error = string.Empty;

        var output = (D3DInterop.IDXGIOutput)Marshal.GetObjectForIUnknown(outputPtr);
        int hr = output.DuplicateOutput(_device, out IntPtr dupPtr);
        if (hr == D3DInterop.DXGI_ERROR_NOT_CURRENTLY_AVAILABLE)
        {
            error = "桌面复制当前不可用（远程桌面/安全桌面）";
            return false;
        }
        if (hr != 0)
        {
            error = $"DuplicateOutput 失败（0x{hr:X8}）";
            return false;
        }

        var duplication = (D3DInterop.IDXGIOutputDuplication)Marshal.GetObjectForIUnknown(dupPtr);
        IntPtr desktopResource = IntPtr.Zero;
        IntPtr texture = IntPtr.Zero;
        IntPtr staging = IntPtr.Zero;
        try
        {
            // AcquireNextFrame：等待下一帧（最长 1s）。ACCESS_LOST（分辨率/桌面切换）→ 重试一次重建。
            hr = duplication.AcquireNextFrame(1000, out var frameInfo, out desktopResource);
            if (hr == D3DInterop.DXGI_ERROR_ACCESS_LOST)
            {
                Marshal.Release(dupPtr);
                duplication = null!;
                hr = output.DuplicateOutput(_device, out dupPtr);
                if (hr != 0)
                {
                    error = $"桌面复制重建失败（0x{hr:X8}）";
                    return false;
                }
                duplication = (D3DInterop.IDXGIOutputDuplication)Marshal.GetObjectForIUnknown(dupPtr);
                hr = duplication.AcquireNextFrame(1000, out frameInfo, out desktopResource);
            }
            if (hr == D3DInterop.DXGI_ERROR_NOT_CURRENTLY_AVAILABLE)
            {
                error = "桌面复制当前不可用（远程桌面/安全桌面）";
                return false;
            }
            if (hr != 0)
            {
                error = $"AcquireNextFrame 失败（0x{hr:X8}）";
                return false;
            }

            if (desktopResource == IntPtr.Zero)
            {
                error = "AcquireNextFrame 返回空资源";
                return false;
            }

            // 桌面纹理 → ID3D11Texture2D → staging → Map 读回。
            IntPtr texPtr = D3DInterop.QueryInterface(desktopResource, D3DInterop.IID_ID3D11Texture2D);
            if (texPtr == IntPtr.Zero)
            {
                error = "桌面资源 QI ID3D11Texture2D 失败";
                return false;
            }
            texture = texPtr;
            var tex = (D3DInterop.ID3D11Texture2D)Marshal.GetObjectForIUnknown(texPtr);
            tex.GetDesc(out var texDesc);

            var stagingDesc = texDesc;
            stagingDesc.Usage = D3DInterop.D3D11_USAGE_STAGING;
            stagingDesc.BindFlags = 0;
            stagingDesc.CPUAccessFlags = D3DInterop.D3D11_CPU_ACCESS_READ;
            stagingDesc.MiscFlags = 0;
            stagingDesc.MipLevels = 1;
            stagingDesc.ArraySize = 1;
            var device = (D3DInterop.ID3D11Device)Marshal.GetObjectForIUnknown(_device);
            int chr = device.CreateTexture2D(ref stagingDesc, IntPtr.Zero, out staging);
            if (chr != 0)
            {
                error = $"CreateTexture2D(staging) 失败（0x{chr:X8}）";
                return false;
            }

            var context = (D3DInterop.ID3D11DeviceContext)Marshal.GetObjectForIUnknown(_context);
            context.CopyResource(staging, texture);

            int mhr = context.Map(staging, 0, D3DInterop.D3D11_MAP_READ, 0, out var mapped);
            if (mhr != 0)
            {
                error = $"Map(staging) 失败（0x{mhr:X8}）";
                return false;
            }

            try
            {
                RawFrameFormat format = texDesc.Format switch
                {
                    D3DInterop.DXGI_FORMAT_R16G16B16A16_FLOAT => RawFrameFormat.RgbaF16,
                    D3DInterop.DXGI_FORMAT_R8G8B8A8_UNORM or D3DInterop.DXGI_FORMAT_B8G8R8A8_UNORM => RawFrameFormat.Bgra32,
                    _ => RawFrameFormat.Bgra32,
                };

                // 输出矩形可能大于 bounds：整输出读回后由合成裁剪，行拷贝只拷贝输出内与 bounds 相交部分。
                var outputFrame = RawFrame.Allocate(outputRect, format);
                int bpp = RawFrame.BytesPerPixel(format);
                int copyWidth = (int)Math.Min(texDesc.Width, (uint)outputRect.Width);
                int copyHeight = (int)Math.Min(texDesc.Height, (uint)outputRect.Height);
                bool swapRb = texDesc.Format == D3DInterop.DXGI_FORMAT_R8G8B8A8_UNORM;

                unsafe
                {
                    for (int y = 0; y < copyHeight; y++)
                    {
                        byte* src = (byte*)mapped.PData + (nint)y * mapped.RowPitch;
                        byte* dst = (byte*)outputFrame.Pixels + (nint)y * outputFrame.Stride;
                        if (swapRb)
                        {
                            for (int x = 0; x < (int)copyWidth; x++)
                            {
                                dst[x * 4] = src[x * 4 + 2];
                                dst[x * 4 + 1] = src[x * 4 + 1];
                                dst[x * 4 + 2] = src[x * 4];
                                dst[x * 4 + 3] = src[x * 4 + 3];
                            }
                        }
                        else
                        {
                            new Span<byte>(src, copyWidth * bpp).CopyTo(new Span<byte>(dst, copyWidth * bpp));
                        }
                    }
                }

                frame = outputFrame;
                return true;
            }
            finally
            {
                context.Unmap(staging, 0);
            }
        }
        catch (Exception ex)
        {
            error = $"DXGI 输出采集异常：{ex.Message}";
            return false;
        }
        finally
        {
            if (texture != IntPtr.Zero)
            {
                Marshal.Release(texture);
            }
            if (staging != IntPtr.Zero)
            {
                Marshal.Release(staging);
            }
            if (desktopResource != IntPtr.Zero)
            {
                Marshal.Release(desktopResource);
            }
            if (duplication != null)
            {
                duplication.ReleaseFrame();
            }
            if (dupPtr != IntPtr.Zero)
            {
                Marshal.Release(dupPtr);
            }
        }
    }

    public void Dispose()
    {
        using var com = ComHelper.TryInitializeMta();
        if (_context != IntPtr.Zero)
        {
            Marshal.Release(_context);
        }
        if (_device != IntPtr.Zero)
        {
            Marshal.Release(_device);
        }
        if (_factory != IntPtr.Zero)
        {
            Marshal.Release(_factory);
        }
    }
}
