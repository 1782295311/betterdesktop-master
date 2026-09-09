using BetterDesktop.Shell.Convert.Contracts;
using BetterDesktop.Shell.Convert.Services;
using BetterDesktop.Shell.Convert.Services.Engines;

namespace BetterDesktop.Shell.Convert.Tests;

/// <summary>矩阵纯逻辑（计划 §8 核心覆盖：目标集/引擎指派/两跳登记/多输入）。</summary>
public class ConversionMatrixTests
{
    [Fact]
    public void Word_目标集_含pdf与文档族()
    {
        var formats = ConversionMatrix.GetTargets(".docx").Select(t => t.Format).ToList();
        Assert.Contains("pdf", formats);
        Assert.Contains("odt", formats);
        Assert.Contains("txt", formats);
        Assert.Contains("html", formats);
        Assert.Contains("epub", formats);
        Assert.DoesNotContain("xlsx", formats);
    }

    [Fact]
    public void Pdf目标_主soffice兜底COM()
    {
        var pdf = ConversionMatrix.Find(".doc", "pdf")!;
        Assert.Equal(EngineKind.Soffice, pdf.Prefer);
        Assert.Equal(EngineKind.ComPdf, pdf.Fallback);
    }

    [Fact]
    public void Excel_csv走UTF8filter常量()
    {
        var csv = ConversionMatrix.Find(".xlsx", "csv")!;
        Assert.Contains("44,34,76", ConvertServiceConstants.CsvUtf8Filter); // 代码页 76 = UTF-8（红线 6）
        Assert.Equal(ConvertServiceConstants.CsvUtf8Filter, csv.Filter);
    }

    [Fact]
    public void Md_两跳登记与纯托管()
    {
        var html = ConversionMatrix.Find(".md", "html")!;
        Assert.Equal(EngineKind.Managed, html.Prefer);
        Assert.Equal(1, html.Hops);

        var docx = ConversionMatrix.Find(".md", "docx")!;
        Assert.Equal(2, docx.Hops); // 两跳显式登记
        Assert.Equal(EngineKind.Pandoc, docx.Prefer);
        Assert.Equal(EngineKind.TwoHop, docx.Fallback); // P3 pandoc 到位自动切主链，调用方无感

        var pdf = ConversionMatrix.Find(".md", "pdf")!;
        Assert.Equal(2, pdf.Hops);
        Assert.Equal(EngineKind.TwoHop, pdf.Prefer);
    }

    [Fact]
    public void 图片_同义格式去重与合成pdf()
    {
        var formats = ConversionMatrix.GetTargets(".png").Select(t => t.Format).ToList();
        Assert.Contains("jpg", formats);
        Assert.DoesNotContain("jpeg", formats); // jpg/jpeg 归一
        Assert.DoesNotContain("png", formats);  // 不含自身
        Assert.Contains("pdf", formats);        // 单图合成 pdf
        Assert.True(ConversionMatrix.AllImages(["a.png", "b.jpg", "c.webp"]));
    }

    [Fact]
    public void Pdf源_Poppler目标登记()
    {
        // A1 修复：2026-09-07 矩阵已扩展——pdf 源除 Poppler 图片/文本外，新增 PdfText 文本提取到 docx/xlsx。
        var formats = ConversionMatrix.GetTargets(".pdf").Select(t => t.Format).ToList();
        Assert.Equal(["png", "jpg", "txt", "docx", "xlsx"], formats);

        var targets = ConversionMatrix.GetTargets(".pdf").ToList();
        // 前三项（png/jpg/txt）走 Poppler 渲染/文本提取
        Assert.All(targets.Take(3), t => Assert.Equal(EngineKind.Poppler, t.Prefer));
        // 后两项（docx/xlsx）走 PdfText 纯文本提取（无版式，菜单已标注）
        Assert.All(targets.Skip(3), t => Assert.Equal(EngineKind.PdfText, t.Prefer));
    }

    [Fact]
    public void 未登记扩展名_返回空()
    {
        Assert.Empty(ConversionMatrix.GetTargets(".exe"));
        Assert.False(ConversionMatrix.IsConvertible(".dll"));
    }
}
