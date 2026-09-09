using System.IO;
using BetterDesktop.Shell.Convert.Contracts;
using BetterDesktop.Shell.Convert.Services;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace BetterDesktop.Shell.Convert.Services.Engines;

/// <summary>
/// PDF 组合引擎（PDFsharp 6.2.4，MIT——红线 11 许可证白名单，选型已写入 csproj 注释；2026-09-08 C7 替换 PdfSharpCore）：
/// pdf-merge 多 PDF 合并（≥2）；pdf-compose 图片合成 PDF（每图一页，页面尺寸=图片尺寸）；
/// pdf-split 拆分（每页一文件）。产物只写 TempDir，安全输出由服务层统一实施。
/// webp 不被 XImage 支持合成页——先经 Skia 转 png 中转（诚实能力，不静默失败）。
/// </summary>
public sealed class PdfComposeEngine : IConversionEngine
{
    public EngineKind Kind => EngineKind.PdfCompose;

    public string Name => "pdf-compose";

    public bool CanHandle(IReadOnlyList<string> sources, ConversionTarget target) =>
        (target.Prefer == EngineKind.PdfCompose || target.Fallback == EngineKind.PdfCompose)
        && target.Filter switch
        {
            ConversionTarget.MergePdfMarker => sources.Count >= 2
                && sources.All(p => Path.GetExtension(p).Equals(".pdf", StringComparison.OrdinalIgnoreCase)),
            ConversionTarget.ComposePdfMarker => sources.Count >= 1
                && sources.All(p => ConversionMatrix.ImageExtensions.Contains(Path.GetExtension(p))),
            ConversionTarget.SplitPdfMarker => sources.Count == 1
                && Path.GetExtension(sources[0]).Equals(".pdf", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };

    public EngineAvailability Probe() => EngineAvailability.Ok("PDFsharp 6.2.4");

    public Task<IReadOnlyList<string>> RunAsync(ConversionJob job, CancellationToken ct)
    {
        IReadOnlyList<string> products = job.Target.Filter switch
        {
            ConversionTarget.MergePdfMarker => Merge(job),
            ConversionTarget.ComposePdfMarker => Compose(job),
            ConversionTarget.SplitPdfMarker => Split(job),
            _ => throw new ConvertException(ConvertError.InputInvalid, "未知的 PDF 组合操作"),
        };
        return Task.FromResult(products);
    }

    private static IReadOnlyList<string> Merge(ConversionJob job)
    {
        var product = Path.Combine(job.TempDir, "merged.pdf");
        using var output = new PdfDocument();
        foreach (var source in job.Sources)
        {
            using var input = PdfReader.Open(source, PdfDocumentOpenMode.Import);
            for (var i = 0; i < input.PageCount; i++)
            {
                output.AddPage(input.Pages[i]);
            }
        }
        output.Save(product);
        return [product];
    }

    private static IReadOnlyList<string> Compose(ConversionJob job)
    {
        var product = Path.Combine(job.TempDir, "composed.pdf");
        using var output = new PdfDocument();
        foreach (var source in job.Sources)
        {
            var image = source;
            var tempConverted = false;
            if (Path.GetExtension(source).Equals(".webp", StringComparison.OrdinalIgnoreCase))
            {
                // webp → png 中转（XImage 不支持 webp；Skia 解码诚实转换）
                image = Path.Combine(job.TempDir, Path.GetFileNameWithoutExtension(source) + "-compose.png");
                ConvertWebpToPng(source, image);
                tempConverted = true;
            }
            try
            {
                using var ximage = XImage.FromFile(image);
                var page = output.AddPage();
                page.Width = XUnit.FromPoint(ximage.PixelWidth);
                page.Height = XUnit.FromPoint(ximage.PixelHeight);
                using var graphics = XGraphics.FromPdfPage(page);
                graphics.DrawImage(ximage, 0, 0, page.Width.Point, page.Height.Point);
            }
            finally
            {
                if (tempConverted)
                {
                    try { File.Delete(image); } catch { /* 中转清理失败不阻断 */ }
                }
            }
        }
        output.Save(product);
        return [product];
    }

    private static IReadOnlyList<string> Split(ConversionJob job)
    {
        using var input = PdfReader.Open(job.PrimarySource, PdfDocumentOpenMode.Import);
        if (input.PageCount < 2)
        {
            throw new ConvertException(ConvertError.ConversionFailed, "PDF 仅 1 页，无需拆分");
        }
        var products = new List<string>(input.PageCount);
        for (var i = 0; i < input.PageCount; i++)
        {
            var product = Path.Combine(job.TempDir, $"page-{i + 1}.pdf");
            using var output = new PdfDocument();
            output.AddPage(input.Pages[i]);
            output.Save(product);
            products.Add(product);
        }
        return products;
    }

    private static void ConvertWebpToPng(string source, string target)
    {
        using var bitmap = SkiaSharp.SKBitmap.Decode(source)
            ?? throw new ConvertException(ConvertError.ConversionFailed, "webp 解码失败（SkiaSharp）");
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100)
            ?? throw new ConvertException(ConvertError.ConversionFailed, "png 中转编码失败");
        using var stream = File.OpenWrite(target);
        data.SaveTo(stream);
    }
}
