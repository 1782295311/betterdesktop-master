using System;
using System.Runtime.InteropServices;
using BetterDesktop.Shell.Capture.Native;

namespace BetterDesktop.Shell.Capture.Core;

/// <summary>
/// 剪贴板图片写入：CF_DIB + "PNG" 注册格式（引擎按 CF_DIB 优先捕获，PNG 供现代应用与引擎的
/// 像素指纹去重；不写 CF_UNICODETEXT——引擎快照优先级里文本高于图片，写了会误判为文本条目）。
/// </summary>
public static class ClipboardImageWriter
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
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

    private const uint BI_RGB = 0;

    /// <summary>把 PNG 字节写入剪贴板（CF_DIB + "PNG"）。返回是否成功与可读原因。</summary>
    public static bool WritePng(byte[] pngBytes, out string error)
    {
        error = string.Empty;
        if (pngBytes.Length == 0)
        {
            error = "PNG 字节为空";
            return false;
        }

        // CA2000：frame 所有权在下方 try/finally 统一释放（所有路径）
#pragma warning disable CA2000
        if (!PngCodec.TryDecodeBgra(pngBytes, out var frame, out string decodeError))
        {
            error = decodeError;
            return false;
        }
#pragma warning restore CA2000

        try
        {
            byte[] dib = BuildDibBottomUp(frame);
            uint pngFormat = NativeClipboard.PngFormat();
            if (pngFormat == 0)
            {
                error = "RegisterClipboardFormatW(PNG) 失败";
                return false;
            }

            if (!NativeClipboard.WriteFormats((pngFormat, pngBytes), (NativeClipboard.CF_DIB, dib)))
            {
                error = "写剪贴板失败（可能被其他程序占用）";
                return false;
            }
            return true;
        }
        finally
        {
            frame.Dispose();
        }
    }

    /// <summary>
    /// BGRA8 顶向下帧 → DIB（BITMAPINFOHEADER 40 + BGRA 像素，底向上；对齐引擎 png_to_dib 口径）。
    /// </summary>
    public static byte[] BuildDibBottomUp(RawFrame frame)
    {
        if (frame.Format != RawFrameFormat.Bgra32)
        {
            throw new ArgumentException("DIB 要求 BGRA8 帧", nameof(frame));
        }

        int w = frame.Width;
        int h = frame.Height;
        var header = new BITMAPINFOHEADER
        {
            BiSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            BiWidth = w,
            BiHeight = h, // 正值 = 底向上
            BiPlanes = 1,
            BiBitCount = 32,
            BiCompression = BI_RGB,
            BiSizeImage = (uint)(w * h * 4),
        };

        byte[] dib = new byte[Marshal.SizeOf<BITMAPINFOHEADER>() + w * h * 4];
        int headerSize = Marshal.SizeOf<BITMAPINFOHEADER>();
        unsafe
        {
            fixed (byte* dibPtr = dib)
            {
                Marshal.StructureToPtr(header, (IntPtr)dibPtr, false);
                int rowBytes = w * 4;
                for (int y = 0; y < h; y++)
                {
                    byte* src = (byte*)frame.Pixels + (nint)y * frame.Stride;
                    // 底向上：目标第 (h-1-y) 行 ← 源第 y 行
                    byte* dst = dibPtr + headerSize + (nint)(h - 1 - y) * rowBytes;
                    new Span<byte>(src, rowBytes).CopyTo(new Span<byte>(dst, rowBytes));
                }
            }
        }
        return dib;
    }
}
