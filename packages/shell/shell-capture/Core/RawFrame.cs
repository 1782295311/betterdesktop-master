using System;
using BetterDesktop.Capture.Contracts;

namespace BetterDesktop.Shell.Capture.Core;

/// <summary>帧像素格式。</summary>
public enum RawFrameFormat
{
    /// <summary>BGRA8（SDR；BitBlt / WGC-SDR / 色调映射后）。每像素 4 字节。</summary>
    Bgra32,

    /// <summary>RGBA FP16 scRGB（HDR；WGC-HDR / DXGI-HDR 原帧）。每像素 8 字节。</summary>
    RgbaF16,
}

/// <summary>
/// 原始帧缓冲（非托管内存，避免 GC 压力——单帧 4K BGRA ≈ 33MB）。
/// 行优先、自上而下；Stride 可能大于 Width×bpp（对齐）。
/// </summary>
public sealed class RawFrame : IDisposable
{
    private IntPtr _pixels;

    /// <summary>帧在虚拟屏坐标系中的物理像素矩形。</summary>
    public PixelRect Rect { get; }

    /// <summary>行字节跨度。</summary>
    public int Stride { get; }

    public RawFrameFormat Format { get; }

    public int Width => Rect.Width;

    public int Height => Rect.Height;

    public bool IsDisposed { get; private set; }

    /// <summary>非托管像素首地址。仅在本对象存活期内有效。</summary>
    public IntPtr Pixels
    {
        get
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            return _pixels;
        }
    }

    private RawFrame(PixelRect rect, int stride, RawFrameFormat format, IntPtr pixels)
    {
        Rect = rect;
        Stride = stride;
        Format = format;
        _pixels = pixels;
    }

    public static int BytesPerPixel(RawFrameFormat format) => format == RawFrameFormat.RgbaF16 ? 8 : 4;

    public static int AlignUp(int value, int alignment) => (value + alignment - 1) & ~(alignment - 1);

    /// <summary>分配一个零初始化帧缓冲。</summary>
    public static unsafe RawFrame Allocate(PixelRect rect, RawFrameFormat format)
    {
        int bpp = BytesPerPixel(format);
        int stride = AlignUp(rect.Width * bpp, 16); // 16 字节对齐，便于 SIMD/编码器
        int size = stride * rect.Height;
        IntPtr ptr = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
        new Span<byte>((void*)ptr, size).Clear();
        return new RawFrame(rect, stride, format, ptr);
    }

    /// <summary>包装一个外部缓冲（调用方保证生命周期与对齐；本对象不负责释放）。</summary>
    public static RawFrame Wrap(PixelRect rect, int stride, RawFrameFormat format, IntPtr pixels) =>
        new(rect, stride, format, pixels);

    /// <summary>复制一行像素到目标地址（按最小行宽，不复制 padding）。</summary>
    public unsafe void CopyRowTo(int row, Span<byte> destination)
    {
        int rowBytes = Width * BytesPerPixel(Format);
        if (destination.Length < rowBytes)
        {
            throw new ArgumentException("destination 过小", nameof(destination));
        }
        new Span<byte>((void*)(Pixels + (nint)row * Stride), rowBytes).CopyTo(destination);
    }

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }
        IsDisposed = true;
        System.Runtime.InteropServices.Marshal.FreeHGlobal(_pixels);
        _pixels = IntPtr.Zero;
    }
}
