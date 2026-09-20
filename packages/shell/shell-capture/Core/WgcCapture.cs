using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using BetterDesktop.Capture.Contracts;
using BetterDesktop.Shell.Capture.Native;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.UI;
using WinRT;

namespace BetterDesktop.Shell.Capture.Core;

/// <summary>
/// Windows.Graphics.Capture 采集（后端 #1：Win11 22H2+ 逐显示器/逐窗口采集，可关黄框）。
/// 显示器走 TryCreateFromDisplayId（DisplayId = HMONITOR 直接包装，实测映射成立），
/// 窗口走 TryCreateFromWindowId（WindowId = HWND 直接包装）；帧表面经
/// IDirect3DDxgiInterfaceAccess → 原生 IDXGISurface 读像素。
/// HDR 屏用 FP16 scRGB 池（保留高光数据，服务层色调映射），SDR 用 BGRA8。
/// 受保护内容检测：本投影无 IsProtected API，由服务层用「WGC 帧 vs BitBlt 帧亮度对照」启发式兜底。
/// </summary>
public sealed class WgcCapture : IBackendCapture
{
    private const int FrameWaitMs = 3000;
    private const int MaxFrameTries = 3;

    private readonly IntPtr _nativeDevice;   // ID3D11Device（保持存活）
    private readonly IntPtr _dxgiDevice;     // IDXGIDevice（QI 自 native device）
    private readonly IDirect3DDevice _device;

    /// <summary>HDR 池开关（服务层在采集前按目标显示器 HDR 状态设置）。</summary>
    public bool HdrEnabled { get; set; }

    private WgcCapture(IntPtr nativeDevice, IntPtr dxgiDevice, IDirect3DDevice device)
    {
        _nativeDevice = nativeDevice;
        _dxgiDevice = dxgiDevice;
        _device = device;
    }

    public CaptureBackend Kind => CaptureBackend.Wgc;

    public string Name => "WGC";

    /// <summary>创建 WGC 采集器（D3D11 设备 + WinRT 包装）。失败返回 null（原因并入降级链）。</summary>
    public static WgcCapture? TryCreate(out string error)
    {
        try
        {
            using var com = ComHelper.TryInitializeMta();
            IntPtr device = D3DInterop.CreateHardwareDevice(out int hrD);
            if (device == IntPtr.Zero)
            {
                error = $"WGC: D3D11CreateDevice 失败（0x{hrD:X8}）";
                return null;
            }

            IntPtr dxgiDevice = D3DInterop.QueryInterface(device, D3DInterop.IID_IDXGIDevice);
            if (dxgiDevice == IntPtr.Zero)
            {
                Marshal.Release(device);
                error = "WGC: QI IDXGIDevice 失败";
                return null;
            }

            IntPtr winrtDevicePtr = WgcInterop.CreateDirect3D11DeviceFromDxgiDevice(dxgiDevice, out int hrW);
            if (winrtDevicePtr == IntPtr.Zero)
            {
                Marshal.Release(dxgiDevice);
                Marshal.Release(device);
                error = $"WGC: CreateDirect3D11DeviceFromDXGIDevice 失败（0x{hrW:X8}）";
                return null;
            }

            // FromAbi 接管 winrtDevicePtr 的生命周期（GC 时释放）。
            var winrtDevice = MarshalInspectable<IDirect3DDevice>.FromAbi(winrtDevicePtr);
            error = string.Empty;
            return new WgcCapture(device, dxgiDevice, winrtDevice);
        }
        catch (Exception ex)
        {
            // 【2026-09-15】部分 Windows 版本（实测 Win11 25H2 精简镜像）缺
            // windows.graphics.directx.direct3d11.interop.dll → DllNotFound。
            // 属环境性缺失：按降级链处理（DXGI/BitBlt 兜底），不阻断截图主链路。
            error = $"WGC: 运行时不可用（{ex.GetType().Name}: {ex.Message}）";
            return null;
        }
    }

    /// <summary>采集虚拟屏矩形（含负坐标）。逐显示器 item 采集后合成；不包含光标（服务层统一合成）。</summary>
    public bool Capture(PixelRect bounds, out RawFrame? frame, out string error)
    {
        frame = null;
        if (bounds.IsEmpty)
        {
            error = "采集矩形为空";
            return false;
        }

        using var com = ComHelper.TryInitializeMta();
        var screen = VirtualScreenInfo.Query();
        var parts = new List<(RawFrame Frame, PixelRect Rect)>();
        var failures = new List<string>();

        try
        {
            foreach (var monitor in screen.Monitors)
            {
                var monitorRect = monitor.Bounds;
                if (monitorRect.Intersect(bounds).IsEmpty)
                {
                    continue;
                }

                if (!TryCaptureMonitor(monitor.Handle, monitorRect, out var part, out string partError))
                {
                    failures.Add(partError);
                    continue;
                }
#pragma warning disable CA2000 // 所有权转移：part 加入 parts 列表，由成功/失败路径统一释放
                if (part is null)
                {
                    failures.Add("WGC 显示器返回空帧");
                    continue;
                }
#pragma warning restore CA2000
                parts.Add((part, monitorRect));
            }

            if (parts.Count == 0)
            {
                error = failures.Count > 0 ? string.Join("；", failures) : "WGC: 无显示器可采集";
                return false;
            }

            var target = RawFrame.Allocate(bounds, parts[0].Frame.Format);
            foreach (var (part, rect) in parts)
            {
                FrameOps.CompositeBgra(target, part, rect.X - bounds.X, rect.Y - bounds.Y);
                part.Dispose();
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
            error = $"WGC 采集异常：{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 逐窗口采集（Window 模式首选）。失败则调用方降级全屏+裁剪；
    /// 受保护内容由服务层用亮度对照启发式判定（本投影无 IsProtected API）。
    /// </summary>
    public bool TryCaptureWindow(IntPtr hwnd, out RawFrame? frame, out string error)
    {
        frame = null;
        error = string.Empty;

        var windowRect = WindowApi.GetWindowPixelRect(hwnd);
        if (windowRect is null || windowRect.Value.IsEmpty)
        {
            error = "窗口矩形不可用";
            return false;
        }

        using var com = ComHelper.TryInitializeMta();
        try
        {
            GraphicsCaptureItem? item;
            try
            {
                item = GraphicsCaptureItem.TryCreateFromWindowId(WindowIdFromHwnd(hwnd));
            }
            catch (Exception ex)
            {
                error = $"WGC 窗口 item 创建失败：{ex.Message}";
                return false;
            }

            if (item is null)
            {
                error = "WGC 窗口 item 不可用（窗口已关闭/受支持性限制）";
                return false;
            }

            // 注意：GraphicsCaptureItem 在该投影无 IClosable/Dispose，由 GC 释放。
            var rect = windowRect.Value;
            return TryCaptureItem(item, rect, out frame, out error);
        }
        catch (Exception ex)
        {
            error = $"WGC 窗口采集异常：{ex.Message}";
            return false;
        }
    }

    private bool TryCaptureMonitor(IntPtr hMonitor, PixelRect monitorRect, out RawFrame? frame, out string error)
    {
        using var com = ComHelper.TryInitializeMta();
        GraphicsCaptureItem? item;
        try
        {
            item = GraphicsCaptureItem.TryCreateFromDisplayId(DisplayIdFromMonitor(hMonitor));
        }
        catch (Exception ex)
        {
            error = $"WGC 显示器 item 创建失败：{ex.Message}";
            frame = null;
            return false;
        }

        if (item is null)
        {
            error = "WGC 显示器 item 不可用";
            frame = null;
            return false;
        }

        // 注意：GraphicsCaptureItem 在该投影无 IClosable/Dispose，由 GC 释放。
        return TryCaptureItem(item, monitorRect, out frame, out error);
    }

    private bool TryCaptureItem(GraphicsCaptureItem item, PixelRect targetRect, out RawFrame? frame, out string error)
    {
        frame = null;
        DirectXPixelFormat pixelFormat = HdrEnabled
            ? DirectXPixelFormat.R16G16B16A16Float
            : DirectXPixelFormat.B8G8R8A8UIntNormalized;
        RawFrameFormat format = HdrEnabled ? RawFrameFormat.RgbaF16 : RawFrameFormat.Bgra32;

        try
        {
            using var pool = Direct3D11CaptureFramePool.Create(_device, pixelFormat, 2, item.Size);
            using var session = pool.CreateCaptureSession(item);
            try
            {
                // Win11 22H2+：关黄框。更老系统属性 setter 抛异常 → 忽略（保留默认黄框，服务层记录降级）。
                session.IsBorderRequired = false;
            }
            catch
            {
                // 忽略；黄框与否不影响像素内容
            }

            session.IsCursorCaptureEnabled = false; // 光标由服务层统一合成

            using var ready = new SemaphoreSlim(0, 1);
            pool.FrameArrived += (_, _) =>
            {
                try
                {
                    ready.Release();
                }
                catch (SemaphoreFullException)
                {
                    // 上一帧尚未消费：忽略
                }
            };

            session.StartCapture();
            try
            {
                Direct3D11CaptureFrame? frameObj = null;
                for (int i = 0; i < MaxFrameTries && frameObj is null; i++)
                {
                    if (!ready.Wait(FrameWaitMs))
                    {
                        error = "WGC 采集超时（无新帧到达）";
                        return false;
                    }
                    frameObj = pool.TryGetNextFrame();
                }

                if (frameObj is null)
                {
                    error = "WGC 未取到帧";
                    return false;
                }

                using (frameObj)
                {
                    return CopyFrameSurface(frameObj, targetRect, format, out frame, out error);
                }
            }
            finally
            {
                // 会话释放即停止采集（19041 投影无 StopCapture 方法）。
            }
        }
        catch (Exception ex)
        {
            error = $"WGC 会话异常：{ex.Message}";
            return false;
        }
    }

    private static bool CopyFrameSurface(Direct3D11CaptureFrame frameObj, PixelRect targetRect, RawFrameFormat format, out RawFrame? frame, out string error)
    {
        frame = null;
        var access = (WgcInterop.IDirect3DDxgiInterfaceAccess)(object)frameObj.Surface;
        Guid iidSurface = D3DInterop.IID_IDXGISurface; // 静态只读 Guid 不能作 ref 实参，本地副本
        int hr = access.GetInterface(ref iidSurface, out IntPtr nativeSurface);
        if (hr != 0 || nativeSurface == IntPtr.Zero)
        {
            error = $"WGC 帧表面 QI IDXGISurface 失败（0x{hr:X8}）";
            return false;
        }

        try
        {
            var surface = (D3DInterop.IDXGISurface)Marshal.GetObjectForIUnknown(nativeSurface);
            int dhr = surface.GetDesc(out var desc);
            if (dhr != 0)
            {
                error = $"WGC 帧表面 GetDesc 失败（0x{dhr:X8}）";
                return false;
            }

            int mhr = surface.Map(D3DInterop.DXGI_MAP_READ, out var mapped);
            if (mhr != 0)
            {
                error = $"WGC 帧表面 Map 失败（0x{mhr:X8}）";
                return false;
            }

            try
            {
                // 表面尺寸与目标矩形尺寸可能有一帧延迟（分辨率变化），以实际表面为准，位置保持目标矩形左上角。
                int w = (int)desc.Width;
                int h = (int)desc.Height;
                if (w <= 0 || h <= 0)
                {
                    error = "WGC 帧表面尺寸无效";
                    return false;
                }

                var rect = new PixelRect(targetRect.X, targetRect.Y, w, h);
                var target = RawFrame.Allocate(rect, format);
                int bpp = RawFrame.BytesPerPixel(format);
                int copyWidth = Math.Min(w, rect.Width);
                int copyHeight = Math.Min(h, rect.Height);

                unsafe
                {
                    for (int y = 0; y < copyHeight; y++)
                    {
                        byte* src = (byte*)mapped.PBits + (nint)y * mapped.Pitch;
                        byte* dst = (byte*)target.Pixels + (nint)y * target.Stride;
                        new Span<byte>(src, copyWidth * bpp).CopyTo(new Span<byte>(dst, copyWidth * bpp));
                    }
                }

                frame = target;
                error = string.Empty;
                return true;
            }
            finally
            {
                surface.Unmap();
            }
        }
        finally
        {
            Marshal.Release(nativeSurface);
        }
    }

    /// <summary>DisplayId 直接包装 HMONITOR（实测映射成立：TryCreateFromDisplayId 可采到显示器）。</summary>
    private static DisplayId DisplayIdFromMonitor(IntPtr hMonitor)
    {
        ulong value = unchecked((ulong)hMonitor.ToInt64());
        return Unsafe.As<ulong, DisplayId>(ref value);
    }

    /// <summary>WindowId 直接包装 HWND（同 WindowId.CreateFromHwnd 语义；本投影无该静态工厂）。</summary>
    private static WindowId WindowIdFromHwnd(IntPtr hwnd)
    {
        ulong value = unchecked((ulong)hwnd.ToInt64());
        return Unsafe.As<ulong, WindowId>(ref value);
    }

    public void Dispose()
    {
        using var com = ComHelper.TryInitializeMta();
        // winrtDevice 由 MarshalInspectable 托管（GC 释放）；native/dxgi 指针由本对象释放。
        if (_dxgiDevice != IntPtr.Zero)
        {
            Marshal.Release(_dxgiDevice);
        }
        if (_nativeDevice != IntPtr.Zero)
        {
            Marshal.Release(_nativeDevice);
        }
    }
}
