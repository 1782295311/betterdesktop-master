using System;

namespace BetterDesktop.Capture.Contracts;

/// <summary>截图模式（与剪贴板扩展功能同域；Scrolling 为预留扩展点，见计划 §7 开放问题 1）。</summary>
public enum CaptureMode
{
    /// <summary>整个虚拟屏（多显示器并集）。</summary>
    FullScreen,

    /// <summary>交互式选区（覆盖层拖选；由调用方先触发全屏采集再裁剪）。</summary>
    Region,

    /// <summary>指定窗口（<see cref="CaptureRequest.TargetWindow"/> 或交互式点选）。</summary>
    Window,

    /// <summary>长截图（滚动拼接，预留，当前不可用）。</summary>
    Scrolling,
}

/// <summary>实际使用的采集后端（降级链：WGC → DXGI → BitBlt）。</summary>
public enum CaptureBackend
{
    /// <summary>Windows.Graphics.Capture（Win11 22H2+，逐显示器/逐窗口，可关黄框）。</summary>
    Wgc,

    /// <summary>DXGI Desktop Duplication（Win8+，无边框可靠路径）。</summary>
    Dxgi,

    /// <summary>GDI BitBlt（最后兜底；HDR 屏颜色失真，命中必须标注降级）。</summary>
    BitBlt,
}

/// <summary>像素矩形（物理像素坐标，虚拟屏坐标系，左上原点，可为负——副屏在左侧）。</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public int Area => Width * Height;

    public int CenterX => X + Width / 2;

    public int CenterY => Y + Height / 2;

    public System.Drawing.Point Location => new(X, Y);

    public static PixelRect FromLTRB(int left, int top, int right, int bottom) =>
        new(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));

    public bool Contains(int px, int py) => px >= X && px < Right && py >= Y && py < Bottom;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>与另一矩形求交（空矩形返回空矩形；用于裁剪到边界）。</summary>
    public PixelRect Intersect(PixelRect other)
    {
        int left = Math.Max(X, other.X);
        int top = Math.Max(Y, other.Y);
        int right = Math.Min(Right, other.Right);
        int bottom = Math.Min(Bottom, other.Bottom);
        return right > left && bottom > top
            ? FromLTRB(left, top, right, bottom)
            : default;
    }

    public override string ToString() => $"{Width}×{Height} @({X},{Y})";
}

/// <summary>截图请求（契约面只暴露"采集到 PNG 路径"，不暴露帧字节——字节不进托管堆也不进 IPC）。</summary>
/// <param name="Mode">截图模式。</param>
/// <param name="TargetWindow">Window 模式的目标窗口句柄；0 = 交互式点选。</param>
/// <param name="Region">Region 模式的目标像素矩形（虚拟屏坐标）；null = 交互式拖选。</param>
/// <param name="IncludeCursor">是否包含光标。</param>
/// <param name="HdrTonemap">HDR 屏是否做 scRGB→sRGB 色调映射（false = 原样输出，仅在诊断场景使用）。</param>
public sealed record CaptureRequest(
    CaptureMode Mode,
    IntPtr TargetWindow,
    PixelRect? Region,
    bool IncludeCursor,
    bool HdrTonemap)
{
    public static CaptureRequest FullScreen(bool includeCursor = true, bool hdrTonemap = true) =>
        new(CaptureMode.FullScreen, IntPtr.Zero, null, includeCursor, hdrTonemap);

    public static CaptureRequest RegionOf(PixelRect region, bool includeCursor = true, bool hdrTonemap = true) =>
        new(CaptureMode.Region, IntPtr.Zero, region, includeCursor, hdrTonemap);

    public static CaptureRequest WindowOf(IntPtr hwnd, bool includeCursor = true, bool hdrTonemap = true) =>
        new(CaptureMode.Window, hwnd, null, includeCursor, hdrTonemap);
}

/// <summary>截图结果。</summary>
/// <param name="Success">是否成功。失败时其余字段可能为空/默认，必须读 <see cref="Error"/>。</param>
/// <param name="Error">失败原因（fail-visible；成功时为空）。</param>
/// <param name="PngPath">产物 PNG 路径（temp 目录；调用方用完负责删除或交给 TempFileManager 统一清理）。</param>
/// <param name="Width">产物宽度（px）。</param>
/// <param name="Height">产物高度（px）。</param>
/// <param name="VirtualScreenRect">本次采集覆盖的虚拟屏像素矩形（多屏并集，可为负坐标）。</param>
/// <param name="BackendUsed">实际使用的后端。</param>
/// <param name="DegradeReason">降级原因（命中 BitBlt / HDR 未映射 / 受保护内容等时非空；日志与 UI 必须可见）。</param>
public sealed record CaptureResult(
    bool Success,
    string? Error,
    string? PngPath,
    int Width,
    int Height,
    PixelRect VirtualScreenRect,
    CaptureBackend BackendUsed,
    string? DegradeReason)
{
    public static CaptureResult Failed(string error, CaptureBackend backend, string? degradeReason = null) =>
        new(false, error, null, 0, 0, default, backend, degradeReason);
}
