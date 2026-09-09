// PdfEnginesTests — PDF 引擎内核单测（2026-09-08 C7 修复补锁：PdfSharpCore 1.3.2 → PDFsharp 6.2.4）。
// 覆盖 PdfComposeEngine（合并/合成/拆分）与 PdfSecurityEngine（加密/解密）的真实产物行为。
// 产物只写系统临时目录（同转换服务 TempDir 语义），测试结束清理；不触碰仓库任何路径。
// 正常/边界/异常 case：多页拆分、单页拒绝、加密往返、无密码拒绝。

using BetterDesktop.Shell.Convert.Contracts;
using BetterDesktop.Shell.Convert.Services.Engines;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace BetterDesktop.Shell.Convert.Tests;

public sealed class PdfEnginesTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "bd-pdfenginetests-" + Guid.NewGuid().ToString("N"));

    public PdfEnginesTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* 清理失败不阻断 */ }
    }

    private string WriteSinglePagePdf(string name)
    {
        var path = Path.Combine(_tempDir, name);
        using var doc = new PdfDocument();
        doc.AddPage();
        doc.Save(path);
        return path;
    }

    private string WriteTwoPagePdf(string name)
    {
        var path = Path.Combine(_tempDir, name);
        using var doc = new PdfDocument();
        doc.AddPage();
        doc.AddPage();
        doc.Save(path);
        return path;
    }

    private static string WritePng(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, System.Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="));
        return path;
    }

    private static ConversionTarget Target(string marker) => new(
        "pdf", "PDF 文档", marker, 1, EngineKind.PdfCompose);

    private static ConversionJob Job(IReadOnlyList<string> sources, string marker, string tempDir) =>
        new(sources, Target(marker), tempDir);

    private static ConversionTarget SecurityTarget(string marker) => new(
        "pdf", "PDF 文档", marker, 1, EngineKind.PdfSecurity);

    [Fact]
    public async Task PdfCompose_Merge_多PDF合并为多页()
    {
        var a = WriteSinglePagePdf("a.pdf");
        var b = WriteSinglePagePdf("b.pdf");
        var engine = new PdfComposeEngine();
        var products = await engine.RunAsync(Job([a, b], ConversionTarget.MergePdfMarker, _tempDir), CancellationToken.None);

        Assert.Single(products);
        Assert.True(File.Exists(products[0]));
        using var doc = PdfReader.Open(products[0], PdfDocumentOpenMode.Import);
        Assert.Equal(2, doc.PageCount);
    }

    [Fact]
    public async Task PdfCompose_Compose_图片合成单页且尺寸等于图片()
    {
        var png = WritePng(_tempDir, "t.png");
        var engine = new PdfComposeEngine();
        var products = await engine.RunAsync(Job([png], ConversionTarget.ComposePdfMarker, _tempDir), CancellationToken.None);

        Assert.Single(products);
        using var doc = PdfReader.Open(products[0], PdfDocumentOpenMode.Import);
        Assert.Equal(1, doc.PageCount);
        Assert.Equal(1, doc.Pages[0].Width.Point); // 1×1 图片 → 1×1 点页面
        Assert.Equal(1, doc.Pages[0].Height.Point);
    }

    [Fact]
    public async Task PdfCompose_Split_多页拆分为每页一文件()
    {
        var two = WriteTwoPagePdf("two.pdf");
        var engine = new PdfComposeEngine();
        var products = await engine.RunAsync(Job([two], ConversionTarget.SplitPdfMarker, _tempDir), CancellationToken.None);

        Assert.Equal(2, products.Count);
        foreach (var p in products)
        {
            using var doc = PdfReader.Open(p, PdfDocumentOpenMode.Import);
            Assert.Equal(1, doc.PageCount);
        }
    }

    [Fact]
    public async Task PdfCompose_Split_单页拒绝拆分()
    {
        var one = WriteSinglePagePdf("one.pdf");
        var engine = new PdfComposeEngine();
        var ex = await Assert.ThrowsAsync<ConvertException>(() =>
            engine.RunAsync(Job([one], ConversionTarget.SplitPdfMarker, _tempDir), CancellationToken.None));

        Assert.Equal(ConvertError.ConversionFailed, ex.Error);
    }

    [Fact]
    public async Task PdfSecurity_Encrypt_带密码可开无密码拒绝()
    {
        var src = WriteSinglePagePdf("plain.pdf");
        var engine = new PdfSecurityEngine("pw-123");
        var products = await engine.RunAsync(
            new ConversionJob([src], SecurityTarget(ConversionTarget.EncryptPdfMarker), _tempDir),
            CancellationToken.None);

        Assert.Single(products);
        using (var opened = PdfReader.Open(products[0], "pw-123", PdfDocumentOpenMode.Import))
        {
            Assert.Equal(1, opened.PageCount);
        }
        Assert.Throws<PdfReaderException>(() => PdfReader.Open(products[0], PdfDocumentOpenMode.Import));
    }

    [Fact]
    public async Task PdfSecurity_Decrypt_加密后解密可无密码打开()
    {
        var src = WriteSinglePagePdf("plain.pdf");
        var enc = new PdfSecurityEngine("pw-123");
        var encrypted = (await enc.RunAsync(
            new ConversionJob([src], SecurityTarget(ConversionTarget.EncryptPdfMarker), _tempDir),
            CancellationToken.None))[0];

        var dec = new PdfSecurityEngine("pw-123");
        var decrypted = (await dec.RunAsync(
            new ConversionJob([encrypted], SecurityTarget(ConversionTarget.DecryptPdfMarker), _tempDir),
            CancellationToken.None))[0];

        using var doc = PdfReader.Open(decrypted, PdfDocumentOpenMode.Import);
        Assert.Equal(1, doc.PageCount);
    }
}
