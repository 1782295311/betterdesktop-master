// BetterDesktop.Shell.Convert — 图片目标统一保存器（2026-09-07：ManagedImageEngine / HeicEngine / RawDecodeEngine 共用）
// 输入 GDI Bitmap → 目标格式：png/jpg/bmp/gif/tif/tiff 走 GDI+；webp 走 Skia（质量 90）；
// ico 走 ≤256 内嵌 PNG 的 ICO 封装。避免三个图片类引擎重复实现保存逻辑。

using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using BetterDesktop.Shell.Convert.Contracts;
using SkiaSharp;

namespace BetterDesktop.Shell.Convert.Services.Engines;

/// <summary>图片目标统一保存（仅保存，不负责解码——调用方先得到 GDI Bitmap）。</summary>
internal static class ImageTargetWriter
{
    /// <summary>GDI Bitmap → 目标格式文件。</summary>
    public static void Save(Bitmap bitmap, string product, string format)
    {
        if (format == "ico")
        {
            SaveAsIco(bitmap, product);
            return;
        }
        if (format == "webp")
        {
            SaveAsWebp(bitmap, product);
            return;
        }
        using var bmp = new Bitmap(bitmap); // 解除调用方引用
        bmp.Save(product, ImageFormatOf(format));
    }

    /// <summary>GDI Bitmap → ICO（现代 ICO 直接内嵌 PNG；缩放到 ≤256，目录项 w/h=0 表示 256）。</summary>
    private static void SaveAsIco(Bitmap bitmap, string product)
    {
        using var pngStream = new MemoryStream();
        bitmap.Save(pngStream, ImageFormat.Png);
        pngStream.Position = 0;
        using var sk = SKBitmap.Decode(pngStream)
            ?? throw new ConvertException(ConvertError.ConversionFailed, "ico 编码前解码失败（SkiaSharp）");

        var max = Math.Max(sk.Width, sk.Height);
        var size = max <= 256 ? max : 256;
        using var scaled = max <= 256
            ? sk
            : sk.Resize(new SKImageInfo(size, size), SKFilterQuality.High);
        if (scaled is null)
        {
            throw new ConvertException(ConvertError.ConversionFailed, "ico 缩放失败（SkiaSharp）");
        }

        using var image = SKImage.FromBitmap(scaled);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new ConvertException(ConvertError.ConversionFailed, "ico PNG 封装编码失败");

        using var fs = File.Create(product);
        fs.WriteByte(0); fs.WriteByte(0);      // reserved
        fs.WriteByte(1); fs.WriteByte(0);      // type = icon
        fs.WriteByte(1); fs.WriteByte(0);      // image count = 1
        fs.WriteByte((byte)(size >= 256 ? 0 : size)); // width（0 = 256）
        fs.WriteByte((byte)(size >= 256 ? 0 : size)); // height（0 = 256）
        fs.WriteByte(0); fs.WriteByte(0);      // palette colors = 0, reserved = 0
        fs.WriteByte(1); fs.WriteByte(0);      // planes
        fs.WriteByte(32); fs.WriteByte(0);     // bits per pixel
        var len = (int)data.Size;
        fs.WriteByte((byte)(len & 0xFF)); fs.WriteByte((byte)((len >> 8) & 0xFF));
        fs.WriteByte((byte)((len >> 16) & 0xFF)); fs.WriteByte((byte)((len >> 24) & 0xFF));
        fs.WriteByte(22); fs.WriteByte(0); fs.WriteByte(0); fs.WriteByte(0); // offset = 6 + 16
        using (var png = data.AsStream())
        {
            png.CopyTo(fs);
        }
    }

    /// <summary>GDI Bitmap → webp（Skia 编码，质量 90）。</summary>
    private static void SaveAsWebp(Bitmap bitmap, string product)
    {
        using var pngStream = new MemoryStream();
        bitmap.Save(pngStream, ImageFormat.Png);
        pngStream.Position = 0;
        using var skBitmap = SKBitmap.Decode(pngStream)
            ?? throw new ConvertException(ConvertError.ConversionFailed, "webp 编码失败（SkiaSharp）");
        using var skImage = SKImage.FromBitmap(skBitmap);
        using var data = skImage.Encode(SKEncodedImageFormat.Webp, 90)
            ?? throw new ConvertException(ConvertError.ConversionFailed, "webp 编码失败（SkiaSharp）");
        using var stream = File.OpenWrite(product);
        data.SaveTo(stream);
    }

    private static ImageFormat ImageFormatOf(string format) => format switch
    {
        "png" => ImageFormat.Png,
        "jpg" => ImageFormat.Jpeg,
        "bmp" => ImageFormat.Bmp,
        "gif" => ImageFormat.Gif,
        "tif" or "tiff" => ImageFormat.Tiff,
        _ => throw new ConvertException(ConvertError.InputInvalid, $"不支持的图片目标格式: {format}"),
    };
}
