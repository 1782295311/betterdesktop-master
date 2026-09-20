using System;
using System.Runtime.InteropServices;
using BetterDesktop.Capture.Contracts;

namespace BetterDesktop.Shell.Capture.Core;

/// <summary>GDI BitBlt 采集（最后兜底；HDR 屏颜色失真由服务层标注降级）。</summary>
public sealed class BitBltCapture : IBackendCapture
{
    private const uint SRCCOPY_CAPTUREBLT = 0x00CC0020 | 0x40000000; // CAPTUREBLT 含分层窗口
    private const int BI_RGB = 0;
    private const uint DIB_RGB_COLORS = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint BiSize;
        public int BiWidth;
        public int BiHeight;
        public ushort BiPlanes;
        public ushort BiBitCount;
        public uint BiCompression;
        public uint BiSizeImage;
        public int BiXPelsPerMeter;
        public int BiYPelsPerMeter;
        public uint BiClrUsed;
        public uint BiClrImportant;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest, IntPtr hdcSrc, int xSrc, int ySrc, uint rop);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr ho);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);

    public CaptureBackend Kind => CaptureBackend.BitBlt;

    public string Name => "BitBlt";

    /// <summary>采集虚拟屏像素矩形为 BGRA8 顶向下帧（不包含光标，由服务层统一合成）。</summary>
    public bool Capture(PixelRect bounds, out RawFrame? frame, out string error)
    {
        frame = null;
        if (bounds.IsEmpty)
        {
            error = "采集矩形为空";
            return false;
        }

        IntPtr screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            error = "GetDC 失败";
            return false;
        }

        IntPtr memDc = CreateCompatibleDC(screenDc);
        if (memDc == IntPtr.Zero)
        {
            ReleaseDC(IntPtr.Zero, screenDc);
            error = "CreateCompatibleDC 失败";
            return false;
        }

        var bi = new BITMAPINFOHEADER
        {
            BiSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            BiWidth = bounds.Width,
            BiHeight = -bounds.Height, // 顶向下
            BiPlanes = 1,
            BiBitCount = 32,
            BiCompression = BI_RGB,
        };

        IntPtr hbmp = CreateDIBSection(memDc, ref bi, DIB_RGB_COLORS, out IntPtr bits, IntPtr.Zero, 0);
        if (hbmp == IntPtr.Zero)
        {
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
            error = "CreateDIBSection 失败";
            return false;
        }

        try
        {
            IntPtr old = SelectObject(memDc, hbmp);
            // 源坐标为虚拟屏绝对坐标（负坐标副屏同样有效）。
            bool ok = BitBlt(memDc, 0, 0, bounds.Width, bounds.Height, screenDc, bounds.X, bounds.Y, SRCCOPY_CAPTUREBLT);
            if (!ok)
            {
                error = $"BitBlt 失败（Win32 错误 {Marshal.GetLastWin32Error()}）";
                return false;
            }

            // DIB bits → RawFrame（拷贝行；stride = width*4，DIB 行恒 4 字节对齐）。
            var target = RawFrame.Allocate(bounds, RawFrameFormat.Bgra32);
            unsafe
            {
                for (int y = 0; y < bounds.Height; y++)
                {
                    byte* src = (byte*)bits + (nint)y * (nint)bounds.Width * 4;
                    byte* dst = (byte*)target.Pixels + (nint)y * target.Stride;
                    new Span<byte>(src, bounds.Width * 4).CopyTo(new Span<byte>(dst, bounds.Width * 4));
                }
            }

            if (old != IntPtr.Zero)
            {
                SelectObject(memDc, old);
            }
            frame = target;
            error = string.Empty;
            return true;
        }
        finally
        {
            DeleteObject(hbmp);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    public void Dispose()
    {
        // 无长期资源（每帧独立 GDI 句柄，已即时释放）。
    }
}
