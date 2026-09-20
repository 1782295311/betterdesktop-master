using System;
using System.Threading;
using System.Threading.Tasks;
using BetterDesktop.Capture.Contracts;

namespace BetterDesktop.Shell.Capture.Core;

/// <summary>
/// 屏幕采集服务（IScreenCaptureService 实现）：降级链（WGC → DXGI → BitBlt）执行、
/// HDR 色调映射、裁剪、光标合成、PNG 编码落盘。失败不产出半成品文件（红线 3）；
/// 降级与诊断信息全部并入结果（红线 2，fail-visible）。
/// </summary>
public sealed class ScreenCaptureService : IScreenCaptureService, IDisposable
{
    private readonly object _sync = new();
    private WgcCapture? _wgc;
    private DxgiCapture? _dxgi;
    private BitBltCapture? _bitBlt;
    private bool _disposed;

    public Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken ct)
    {
        // 采集在 MTA 工作线程执行：不阻塞 UI 线程、避免 STA 与 COM/D3D 冲突。
        return Task.Run(() => CaptureCore(request, ct), ct);
    }

    private CaptureResult CaptureCore(CaptureRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // ---- 目标矩形解析 ----
        PixelRect target;
        bool windowCapture = false;
        switch (request.Mode)
        {
            case CaptureMode.FullScreen:
                target = VirtualScreenInfo.Query().Bounds;
                break;
            case CaptureMode.Region:
                if (request.Region is not { } region || region.IsEmpty)
                {
                    return CaptureResult.Failed("Region 模式缺少有效矩形", CaptureBackend.BitBlt);
                }
                target = region;
                break;
            case CaptureMode.Window:
                var wr = request.TargetWindow == IntPtr.Zero
                    ? null
                    : Native.WindowApi.GetWindowPixelRect(request.TargetWindow);
                if (wr is null || wr.Value.IsEmpty)
                {
                    return CaptureResult.Failed("Window 模式目标窗口句柄无效或窗口已关闭", CaptureBackend.BitBlt);
                }
                target = wr.Value;
                windowCapture = true;
                break;
            case CaptureMode.Scrolling:
                return CaptureResult.Failed("长截图（滚动拼接）尚未实现，属预留扩展点", CaptureBackend.BitBlt);
            default:
                return CaptureResult.Failed($"未知截图模式 {request.Mode}", CaptureBackend.BitBlt);
        }

        var screen = VirtualScreenInfo.Query();
        target = target.Intersect(screen.Bounds);
        if (target.IsEmpty)
        {
            return CaptureResult.Failed("目标矩形与虚拟屏无交集", CaptureBackend.BitBlt);
        }

        bool osSupportsWgc = Environment.OSVersion.Version.Build >= CaptureBackendSelector.WgcMinBuild;
        var plan = CaptureBackendSelector.Select(new SelectorInput(
            osSupportsWgc, screen.IsRemoteSession, screen.AnyAdvancedColor, null, true));

        var degradeReasons = new System.Collections.Generic.List<string>(plan.DegradeReasons);

        // ---- Window 模式优先走 WGC 窗口 item（受保护内容检测，红线 D4）----
        if (windowCapture && osSupportsWgc)
        {
            var wgc = GetOrCreateWgc(out string wgcCreateError);
            if (wgc is not null)
            {
                wgc.HdrEnabled = request.HdrTonemap && screen.AnyAdvancedColor;
                if (wgc.TryCaptureWindow(request.TargetWindow, out var windowFrame, out string wErr))
                {
                    // 受保护内容（DRM 等）在 WGC 帧里呈纯黑；若 BitBlt 对照有内容 → 显式拒绝（红线 D4）。
                    if (LooksNearBlack(windowFrame!))
                    {
#pragma warning disable CA2000 // 所有权转移：bbFrame 在下方对照后立即释放
                        if (TryCaptureWith(CaptureBackend.BitBlt, screen, request, out var bbFrame, out _) && bbFrame is not null)
                        {
                            bool bbAlsoBlack = LooksNearBlack(bbFrame, target);
                            bbFrame.Dispose();
                            if (!bbAlsoBlack)
                            {
                                windowFrame!.Dispose();
                                CaptureLog.Warn($"受保护窗口拒绝截图：{request.TargetWindow:X}（WGC 帧黑屏但 GDI 有内容）");
                                return CaptureResult.Failed("该窗口受系统保护，无法截取", CaptureBackend.Wgc);
                            }
                        }
#pragma warning restore CA2000
                    }
                    return FinishCapture(windowFrame!, target, request, CaptureBackend.Wgc, degradeReasons);
                }

                windowFrame?.Dispose();
                degradeReasons.Add($"WGC 窗口采集失败（{wErr}），降级全屏+裁剪");
            }
            else
            {
                degradeReasons.Add($"WGC 不可用（{wgcCreateError}）");
            }
        }

        // ---- 降级链：WGC → DXGI → BitBlt ----
        RawFrame? frame = null;
        CaptureBackend used = CaptureBackend.BitBlt;
        foreach (var backend in plan.Order)
        {
            ct.ThrowIfCancellationRequested();
#pragma warning disable CA2000 // 所有权转移：frame 由 FinishCapture 统一释放（含失败分支）
            if (!TryCaptureWith(backend, screen, request, out frame, out string runtimeError))
            {
                degradeReasons.Add($"{backend} 失败（{runtimeError}）");
                continue;
            }
#pragma warning restore CA2000
            used = backend;
            break;
        }

        if (frame is null)
        {
            return CaptureResult.Failed(string.Join("；", degradeReasons), CaptureBackend.BitBlt);
        }

        // ---- HDR 后端命中时的失真标注（红线 2：降级必须可见）----
        if (used == CaptureBackend.BitBlt && screen.AnyAdvancedColor)
        {
            degradeReasons.Add("BitBlt 后端在 HDR 屏上颜色失真");
        }

        return FinishCapture(frame!, target, request, used, degradeReasons);
    }

    /// <summary>
    /// 受保护内容启发式（本投影无 IsProtected API）：纯黑帧 = 亮度均值极低且方差小。
    /// 仅采样（每 32 行/列），4K 帧开销 &lt;1ms。误判风险（真黑窗口）由 BitBlt 对照消解。
    /// </summary>
    private static bool LooksNearBlack(RawFrame frame, PixelRect? subRect = null)
    {
        var region = subRect ?? frame.Rect;
        var clipped = region.Intersect(frame.Rect);
        if (clipped.IsEmpty)
        {
            return false;
        }

        long sum = 0;
        int count = 0;
        unsafe
        {
            byte* basePtr = (byte*)frame.Pixels;
            int bpp = RawFrame.BytesPerPixel(frame.Format);
            for (int y = clipped.Y; y < clipped.Bottom; y += 32)
            {
                int row = y - frame.Rect.Y;
                if (row < 0 || row >= frame.Height)
                {
                    continue;
                }
                byte* rowPtr = basePtr + (nint)row * frame.Stride;
                for (int x = clipped.X; x < clipped.Right; x += 32)
                {
                    int col = x - frame.Rect.X;
                    if (col < 0 || col >= frame.Width)
                    {
                        continue;
                    }
                    if (frame.Format == RawFrameFormat.RgbaF16)
                    {
                        // FP16 scRGB：取 RGB 通道近似亮度（范围 [0,1]）
                        Half* p = (Half*)(rowPtr + (nint)col * bpp);
                        float r = (float)p[0], g = (float)p[1], b = (float)p[2];
                        sum += (long)((r * 0.299f + g * 0.587f + b * 0.114f) * 255f);
                    }
                    else
                    {
                        byte* p = rowPtr + (nint)col * bpp;
                        sum += (p[0] * 299L + p[1] * 587L + p[2] * 114L) / 1000L;
                    }
                    count++;
                }
            }
        }

        if (count == 0)
        {
            return false;
        }
        double mean = (double)sum / count;
        return mean < 6.0; // 均值 &lt; 6/255 ≈ 纯黑
    }

    private bool TryCaptureWith(CaptureBackend backend, VirtualScreenInfo screen, CaptureRequest request, out RawFrame? frame, out string error)
    {
        frame = null;
        error = string.Empty;
        switch (backend)
        {
            case CaptureBackend.Wgc:
            {
                var wgc = GetOrCreateWgc(out error);
                if (wgc is null)
                {
                    return false;
                }
                // HDR 池：请求映射 && 屏 HDR → FP16（保留高光数据供色调映射）；否则 BGRA8。
                wgc.HdrEnabled = request.HdrTonemap && screen.AnyAdvancedColor;
                return wgc.Capture(screen.Bounds, out frame, out error);
            }
            case CaptureBackend.Dxgi:
            {
                var dxgi = GetOrCreateDxgi(out error);
                if (dxgi is null)
                {
                    return false;
                }
                return dxgi.Capture(screen.Bounds, out frame, out error);
            }
            case CaptureBackend.BitBlt:
            {
                var bitBlt = GetOrCreateBitBlt(out error);
                if (bitBlt is null)
                {
                    return false;
                }
                return bitBlt.Capture(screen.Bounds, out frame, out error);
            }
            default:
                error = $"未知后端 {backend}";
                return false;
        }
    }

    /// <summary>帧 → 产物：裁剪 → HDR 转换 → 光标合成 → PNG 落盘 → CaptureResult（接管 fullFrame 所有权）。</summary>
    private CaptureResult FinishCapture(
        RawFrame fullFrame, PixelRect target, CaptureRequest request,
        CaptureBackend backendUsed, System.Collections.Generic.List<string> degradeReasons)
    {
        try
        {
            RawFrame frame;
#pragma warning disable CA2000 // 所有权转移：Crop 全等返回原帧/否则新帧，均由下方统一释放
            try
            {
                // Crop 全等时返回 fullFrame 本身（所有权转移）；否则返回新帧。
                frame = FrameOps.Crop(fullFrame, target);
            }
            catch (ArgumentException ex)
            {
                fullFrame.Dispose();
                return CaptureResult.Failed(ex.Message, backendUsed);
            }
#pragma warning restore CA2000

            if (!ReferenceEquals(frame, fullFrame))
            {
                fullFrame.Dispose();
            }

            try
            {
                // FP16（HDR）→ BGRA8：色调映射或诊断模式（clamp，无伽马）。
                if (frame.Format == RawFrameFormat.RgbaF16)
                {
                    var bgra = RawFrame.Allocate(frame.Rect, RawFrameFormat.Bgra32);
                    try
                    {
                        if (request.HdrTonemap)
                        {
                            ToneMapper.ScRgbFp16ToBgra8(ToHalfSpan(frame), ToByteSpan(bgra));
                        }
                        else
                        {
                            ToneMapper.ScRgbFp16ToBgra8Clamp(ToHalfSpan(frame), ToByteSpan(bgra));
                            degradeReasons.Add("HDR 原始数据未做色调映射（诊断模式，输出偏暗）");
                        }
                    }
                    finally
                    {
                        frame.Dispose();
                    }
                    frame = bgra;
                }

                // 光标合成（统一 CPU 路径；位置在帧外自动跳过）。
                CursorRenderer.DrawCursor(frame, request.IncludeCursor);

                // PNG 编码 + 落盘（失败不残留半成品：WriteTempPng 失败时无文件产生）。
                string? pngPath;
                try
                {
                    byte[] png = PngCodec.EncodeBgra(frame);
                    pngPath = TempFileManager.WriteTempPng(png);
                }
                catch (Exception ex)
                {
                    return CaptureResult.Failed($"PNG 编码/落盘失败：{ex.Message}", backendUsed);
                }

                string degrade = degradeReasons.Count > 0 ? string.Join("；", degradeReasons) : string.Empty;
                CaptureLog.Info(
                    $"截图完成：{frame.Width}×{frame.Height}@{frame.Rect} 模式={request.Mode} 后端={backendUsed}" +
                    (degrade.Length > 0 ? $" 降级={degrade}" : string.Empty));
                return new CaptureResult(
                    true, null, pngPath, frame.Width, frame.Height, frame.Rect, backendUsed,
                    degrade.Length > 0 ? degrade : null);
            }
            finally
            {
                frame.Dispose();
            }
        }
        catch (Exception ex)
        {
            fullFrame.Dispose();
            return CaptureResult.Failed($"截图流程异常：{ex.Message}", backendUsed);
        }
    }

    private static unsafe System.Span<Half> ToHalfSpan(RawFrame f) =>
        new((void*)f.Pixels, f.Width * f.Height * 4);

    private static unsafe System.Span<byte> ToByteSpan(RawFrame f) =>
        new((void*)f.Pixels, f.Width * f.Height * 4);

    private WgcCapture? GetOrCreateWgc(out string error)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                error = "服务已释放";
                return null;
            }
            if (_wgcUnavailable)
            {
                // 本会话已确认 WGC 运行时不可用：不再反复尝试加载（降级链直接跳过）。
                error = _wgcError;
                return null;
            }
            if (_wgc is null)
            {
                _wgc = WgcCapture.TryCreate(out error);
                if (_wgc is null)
                {
                    _wgcUnavailable = true;
                    _wgcError = error;
                    CaptureLog.Warn($"WGC 不可用，后续截图直接降级：{error}");
                }
                return _wgc;
            }
            error = string.Empty;
            return _wgc;
        }
    }

    private bool _wgcUnavailable;
    private string _wgcError = string.Empty;

    private DxgiCapture? GetOrCreateDxgi(out string error)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                error = "服务已释放";
                return null;
            }
            if (_dxgi is null)
            {
                _dxgi = DxgiCapture.TryCreate(out error);
                return _dxgi;
            }
            error = string.Empty;
            return _dxgi;
        }
    }

    private BitBltCapture? GetOrCreateBitBlt(out string error)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                error = "服务已释放";
                return null;
            }
            _bitBlt ??= new BitBltCapture();
            error = string.Empty;
            return _bitBlt;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _wgc?.Dispose();
            _dxgi?.Dispose();
            _bitBlt?.Dispose();
            _wgc = null;
            _dxgi = null;
            _bitBlt = null;
        }
    }
}
