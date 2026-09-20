using System;
using System.Buffers;
using BetterDesktop.Capture.Contracts;
using SkiaSharp;

namespace BetterDesktop.Shell.Capture.Core;

/// <summary>PNG 编解码（SkiaSharp：绘制/编解码/滤镜统一在依赖面，不新增图像库）。</summary>
public static class PngCodec
{
    /// <summary>
    /// BGRA8 帧 → PNG 字节。压缩档位取「性能与体积平衡」：截图行内冗余大，
    /// 无需 filter 也能压到可用体积；filter 是 PNG 编码的主要 CPU 开销。
    /// </summary>
    public static byte[] EncodeBgra(RawFrame frame)
    {
        if (frame.Format != RawFrameFormat.Bgra32)
        {
            throw new ArgumentException("PNG 编码要求 BGRA8 帧", nameof(frame));
        }

        var info = new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var bmp = new SKBitmap(info);
        unsafe
        {
            // SKBitmap.Pixels 为连续行缓冲；逐行拷贝（frame stride 可能大于 width*4）。
            byte* dst = (byte*)bmp.GetPixels();
            int rowBytes = frame.Width * 4;
            for (int y = 0; y < frame.Height; y++)
            {
                byte* src = (byte*)frame.Pixels + (nint)y * frame.Stride;
                new Span<byte>(src, rowBytes).CopyTo(new Span<byte>(dst + (nint)y * bmp.RowBytes, rowBytes));
            }
        }

        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        if (data is null || data.Size == 0)
        {
            throw new InvalidOperationException("PNG 编码结果为空");
        }
        return data.ToArray();
    }

    /// <summary>PNG 字节 → BGRA8 像素（含解码失败可读原因；供编辑器/标注/OCR 复用）。</summary>
    public static bool TryDecodeBgra(byte[] png, out RawFrame frame, out string error)
    {
        frame = null!;
        error = string.Empty;
        try
        {
            using var stream = new SKMemoryStream(png);
            using var codec = SKCodec.Create(stream);
            if (codec is null)
            {
                error = "PNG 解码失败（无法识别的图片格式）";
                return false;
            }

            var info = codec.Info;
            var bgra = new SKImageInfo(info.Width, info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var bmp = new SKBitmap(bgra);
            var result = codec.GetPixels(bmp.Info, bmp.GetPixels());
            if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
            {
                error = $"PNG 解码失败（{result}）";
                return false;
            }

            var rect = new PixelRect(0, 0, info.Width, info.Height);
            var target = RawFrame.Allocate(rect, RawFrameFormat.Bgra32);
            unsafe
            {
                byte* src = (byte*)bmp.GetPixels();
                int rowBytes = info.Width * 4;
                for (int y = 0; y < info.Height; y++)
                {
                    byte* dst = (byte*)target.Pixels + (nint)y * target.Stride;
                    new Span<byte>(src + (nint)y * bmp.RowBytes, rowBytes).CopyTo(new Span<byte>(dst, rowBytes));
                }
            }
            frame = target;
            return true;
        }
        catch (Exception ex)
        {
            error = $"PNG 解码异常：{ex.Message}";
            return false;
        }
    }

    /// <summary>PNG 裁剪（交互截图复用同一帧：解码 → 裁像素 → 重编码）。region 为物理像素矩形。</summary>
#pragma warning disable CA2000 // 所有权转移：frame/outFrame 在 finally / using 中显式释放
    public static bool CropPng(byte[] png, PixelRect region, out byte[] result, out string error)
    {
        result = Array.Empty<byte>();
        error = string.Empty;
        if (!TryDecodeBgra(png, out var frame, out string decodeError))
        {
            error = decodeError;
            return false;
        }

        try
        {
            var crop = region.Intersect(frame.Rect);
            if (crop.IsEmpty)
            {
                error = "裁剪区域与图片无交集";
                return false;
            }

            RawFrame outFrame;
            try
            {
                outFrame = FrameOps.Crop(frame, crop);
            }
            catch (ArgumentException ex)
            {
                error = ex.Message;
                return false;
            }

            using (outFrame)
            {
                result = EncodeBgra(outFrame);
                return true;
            }
        }
        finally
        {
            frame.Dispose();
        }
    }
#pragma warning restore CA2000
}
