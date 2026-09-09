using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using BetterDesktop.Shell.Convert.Contracts;
using BetterDesktop.Shell.Convert.Services;
using SkiaSharp;

namespace BetterDesktop.Shell.Convert.Services.Engines;

/// <summary>
/// 图片互转引擎（红线 11 NuGet 选型，已写入 csproj 注释）：
/// 常规格式 System.Drawing.Common（BCL，Windows 支持：png/jpg/bmp/gif/tiff）；
/// webp 缺口由 SkiaSharp（MIT，带原生二进制，x64）补齐——webp 源先经 Skia 解码，
/// webp 目标由 Skia 编码（质量 90）。
/// </summary>
public sealed class ManagedImageEngine : IConversionEngine
{
    public EngineKind Kind => EngineKind.Image;

    public string Name => "image";

    public bool CanHandle(IReadOnlyList<string> sources, ConversionTarget target) =>
        sources.Count == 1
        && (target.Prefer == EngineKind.Image || target.Fallback == EngineKind.Image)
        && ConversionMatrix.ImageExtensions.Contains(Path.GetExtension(sources[0]))
        // tga 由 Ffmpeg 引擎处理（System.Drawing 不支持 tga 解码/编码，矩阵已指派 Ffmpeg）
        && !Path.GetExtension(sources[0]).Equals(".tga", StringComparison.OrdinalIgnoreCase);

    public EngineAvailability Probe() => EngineAvailability.Ok("System.Drawing/SkiaSharp");

    public Task<IReadOnlyList<string>> RunAsync(ConversionJob job, CancellationToken ct)
    {
        var input = job.PrimarySource;
        var srcExt = Path.GetExtension(input).ToLowerInvariant();
        var format = job.Target.Format;
        var product = Path.Combine(job.TempDir,
            Path.GetFileNameWithoutExtension(input) + "." + format);

        // 统一路径（2026-09-07 重构）：任意常规源 → GDI Bitmap（webp 经 Skia 中转；ico 源 GDI+ 可读）
        // → ImageTargetWriter 统一保存（png/jpg/bmp/gif/tif + webp/ico 特殊封装）。
        using var bitmap = DecodeToGdiBitmap(input, srcExt);
        ImageTargetWriter.Save(bitmap, product, format);

        IReadOnlyList<string> products = [product];
        return Task.FromResult(products);
    }

    /// <summary>常规源 → GDI Bitmap：webp 经 Skia 解码 → PNG 中转；其余（含 ico）GDI+ 直读。</summary>
    internal static Bitmap DecodeToGdiBitmap(string input, string srcExt)
    {
        if (srcExt == ".webp")
        {
            using var sk = SKBitmap.Decode(input)
                ?? throw new ConvertException(ConvertError.ConversionFailed, "webp 解码失败（SkiaSharp）");
            using var image = Image.FromStream(EncodeSkia(sk, SKEncodedImageFormat.Png, 100));
            return new Bitmap(image);
        }
        using var source = Image.FromFile(input);
        return new Bitmap(source); // 解除源文件锁
    }

    private static MemoryStream EncodeSkia(SKBitmap bitmap, SKEncodedImageFormat fmt, int quality)
    {
        using var image = SKImage.FromBitmap(bitmap);
        var data = image.Encode(fmt, quality) ?? throw new ConvertException(ConvertError.ConversionFailed, "Skia 编码失败");
        var stream = new MemoryStream();
        data.SaveTo(stream);
        stream.Position = 0;
        return stream;
    }
}
