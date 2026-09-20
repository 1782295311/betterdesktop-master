using System;
using System.Runtime.InteropServices;
using BetterDesktop.Shell.Capture.Native;

namespace BetterDesktop.Shell.Capture.Core;

/// <summary>
/// 光标合成：WGC/DXGI 桌面复制不包含指针，采集后统一用 GDI DrawIconEx 把系统光标
/// 渲染进独立 DIB 再 alpha 合成进帧（各后端共用一条路径，行为一致）。
/// </summary>
internal static class CursorRenderer
{
    private const uint CURSOR_SHOWING = 0x00000001;
    private const uint DI_NORMAL = 0x0003;
    private const int BI_RGB = 0;
    private const uint DIB_RGB_COLORS = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public uint CbSize;
        public uint Flags;
        public IntPtr HCursor;
        public POINT PtScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public int FIcon;
        public uint XHotspot;
        public uint YHotspot;
        public IntPtr HbmMask;
        public IntPtr HbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int BmType;
        public int BmWidth;
        public int BmHeight;
        public int BmWidthBytes;
        public ushort BmPlanes;
        public ushort BmBitsPixel;
        public IntPtr BmBits;
    }

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

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorInfo(ref CURSORINFO pci);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr ho);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DrawIconEx(IntPtr hdc, int xLeft, int yTop, IntPtr hIcon, int cxWidth, int cyWidth, uint istepIfAniCur, IntPtr hbrFlickerFreeDraw, uint diFlags);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int GetObject(IntPtr h, int c, out BITMAP bmp);

    /// <summary>把系统光标合成进帧（光标位置落在帧内才绘制）。返回是否绘制。</summary>
    public static bool DrawCursor(RawFrame frame, bool includeCursor)
    {
        if (!includeCursor || frame.Format != RawFrameFormat.Bgra32)
        {
            return false;
        }

        var info = new CURSORINFO { CbSize = (uint)Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref info) || (info.Flags & CURSOR_SHOWING) == 0 || info.HCursor == IntPtr.Zero)
        {
            return false;
        }

        if (!GetIconInfo(info.HCursor, out var icon))
        {
            return false;
        }

        try
        {
            IntPtr sizeBitmap = icon.HbmColor != IntPtr.Zero ? icon.HbmColor : icon.HbmMask;
            if (sizeBitmap == IntPtr.Zero || GetObject(sizeBitmap, Marshal.SizeOf<BITMAP>(), out var bmp) == 0)
            {
                return false;
            }

            int w = bmp.BmWidth;
            int h = icon.HbmColor != IntPtr.Zero ? bmp.BmHeight : bmp.BmHeight / 2;
            if (w <= 0 || h <= 0 || w > 512 || h > 512)
            {
                return false;
            }

            int cursorLeft = info.PtScreenPos.X - (int)icon.XHotspot;
            int cursorTop = info.PtScreenPos.Y - (int)icon.YHotspot;

            // 目标重叠区域裁剪到帧内。
            int left = Math.Max(frame.Rect.X, cursorLeft);
            int top = Math.Max(frame.Rect.Y, cursorTop);
            int right = Math.Min(frame.Rect.Right, cursorLeft + w);
            int bottom = Math.Min(frame.Rect.Bottom, cursorTop + h);
            if (right <= left || bottom <= top)
            {
                return false;
            }

            RenderIconIntoFrame(frame, info.HCursor, w, h, cursorLeft, cursorTop, left, top, right, bottom);
            return true;
        }
        finally
        {
            if (icon.HbmMask != IntPtr.Zero)
            {
                DeleteObject(icon.HbmMask);
            }
            if (icon.HbmColor != IntPtr.Zero)
            {
                DeleteObject(icon.HbmColor);
            }
        }
    }

    private static void RenderIconIntoFrame(RawFrame frame, IntPtr hCursor, int w, int h, int cursorLeft, int cursorTop, int clipLeft, int clipTop, int clipRight, int clipBottom)
    {
        IntPtr hdc = CreateCompatibleDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero)
        {
            return;
        }

        var bi = new BITMAPINFOHEADER
        {
            BiSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            BiWidth = w,
            BiHeight = -h, // 顶向下 → bits 即 BGRA 顶向下行序
            BiPlanes = 1,
            BiBitCount = 32,
            BiCompression = BI_RGB,
        };

        IntPtr hbmp = CreateDIBSection(hdc, ref bi, DIB_RGB_COLORS, out IntPtr bits, IntPtr.Zero, 0);
        if (hbmp == IntPtr.Zero)
        {
            DeleteDC(hdc);
            return;
        }

        IntPtr old = SelectObject(hdc, hbmp);
        try
        {
            // 透明区域 = 全零（GDI 的 AND mask 不写这些像素，必须清底）。
            unsafe
            {
                new Span<byte>((void*)bits, w * h * 4).Clear();
            }
            // 图标左上角对齐位图 (0,0)：热点自然落在 (xHotspot, yHotspot)，与屏幕坐标换算一致。
            DrawIconEx(hdc, 0, 0, hCursor, w, h, 0, IntPtr.Zero, DI_NORMAL);
            CompositeAlpha(frame, bits, w, h, cursorLeft, cursorTop, clipLeft, clipTop, clipRight, clipBottom);
        }
        finally
        {
            if (old != IntPtr.Zero)
            {
                SelectObject(hdc, old);
            }
            DeleteObject(hbmp);
            DeleteDC(hdc);
        }
    }

    /// <summary>alpha 合成（src over dst）。GDI 不写 alpha：透明区为 0x00000000（跳过），
    /// 不透明区 RGB 非零但 alpha=0（按不透明处理）——经典 GDI 光标取数补偿。</summary>
    private static void CompositeAlpha(RawFrame frame, IntPtr cursorPixels, int cw, int ch, int cursorLeft, int cursorTop, int clipLeft, int clipTop, int clipRight, int clipBottom)
    {
        unsafe
        {
            byte* src = (byte*)cursorPixels;
            for (int y = clipTop; y < clipBottom; y++)
            {
                int sy = y - cursorTop;
                if (sy < 0 || sy >= ch)
                {
                    continue;
                }
                byte* dst = (byte*)frame.Pixels + (nint)(y - frame.Rect.Y) * frame.Stride + (nint)(clipLeft - frame.Rect.X) * 4;
                for (int x = clipLeft; x < clipRight; x++)
                {
                    int sx = x - cursorLeft;
                    if (sx < 0 || sx >= cw)
                    {
                        dst += 4;
                        continue;
                    }

                    byte* sp = src + (nint)sy * cw * 4 + (nint)sx * 4;
                    byte sa = sp[3];
                    if (sa == 0)
                    {
                        // GDI mask 补偿：RGB 非零 → 按不透明处理
                        if ((sp[0] | sp[1] | sp[2]) == 0)
                        {
                            dst += 4;
                            continue;
                        }
                        sa = 255;
                    }

                    int inv = 255 - sa;
                    dst[0] = (byte)((sp[0] * sa + dst[0] * inv + 127) / 255);
                    dst[1] = (byte)((sp[1] * sa + dst[1] * inv + 127) / 255);
                    dst[2] = (byte)((sp[2] * sa + dst[2] * inv + 127) / 255);
                    dst[3] = 255;
                    dst += 4;
                }
            }
        }
    }
}
