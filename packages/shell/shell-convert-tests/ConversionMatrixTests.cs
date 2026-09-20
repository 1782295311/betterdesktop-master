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
    public void Pdf目标_文档族走Lite()
    {
        // 2026-09-20 convert-lite 迁移：.doc→pdf 原为 soffice 主 + COM 兜底，现由**进程内 lite** 负责
        // （engines/libreoffice 已删；lite 能读 OLE 旧格式 .doc）⇒ Prefer=Lite、Fallback=null（不做双路径）。
        var pdf = ConversionMatrix.Find(".doc", "pdf")!;
        Assert.Equal(EngineKind.Lite, pdf.Prefer);
        Assert.Null(pdf.Fallback);
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

        // 2026-09-20：docx/pdf 目标改由 lite 负责（原 pandoc 主 + twohop 兜底）。
        // hops 仍为 2（表登记未动）：lite 内部按 md→docx→pdf 两跳完成，语义一致。
        var docx = ConversionMatrix.Find(".md", "docx")!;
        Assert.Equal(2, docx.Hops);
        Assert.Equal(EngineKind.Lite, docx.Prefer);
        Assert.Null(docx.Fallback); // 不做双路径

        var pdf = ConversionMatrix.Find(".md", "pdf")!;
        Assert.Equal(2, pdf.Hops);
        Assert.Equal(EngineKind.Lite, pdf.Prefer);
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
        // 2026-09-10 全列：渲染系 png/jpg/tiff（Poppler，bmp 被 26.09 移除）+ 文本提取系 txt/md/html/docx/xlsx/epub/odt/rtf/opendocument（PdfText）。
        var formats = ConversionMatrix.GetTargets(".pdf").Select(t => t.Format).ToList();
        Assert.Equal(
            ["png", "jpg", "tiff", "txt", "md", "html", "docx", "xlsx", "epub", "odt", "rtf", "opendocument"],
            formats);

        var targets = ConversionMatrix.GetTargets(".pdf").ToList();
        // 渲染系（png/jpg/tiff）走 Poppler；全部为有损（栅格化）
        Assert.All(targets.Take(3), t => Assert.Equal(EngineKind.Poppler, t.Prefer));
        Assert.All(targets.Take(3), t => Assert.False(t.Lossless));
        // 文本提取系（txt/md/html/docx/xlsx/epub/odt/rtf/opendocument）走 PdfText；全部为有损（无版式）
        Assert.All(targets.Skip(3), t => Assert.Equal(EngineKind.PdfText, t.Prefer));
        Assert.All(targets.Skip(3), t => Assert.False(t.Lossless));
    }

    [Fact]
    public void 未登记扩展名_返回空()
    {
        Assert.Empty(ConversionMatrix.GetTargets(".exe"));
        Assert.False(ConversionMatrix.IsConvertible(".dll"));
    }

    // ===== 2026-09-10：分类 + 无损元数据（计划 §4.2/§4.3） =====

    [Fact]
    public void Pandoc扩展_文本族目标全量登记()
    {
        var expected = new[]
        {
            "rtf", "odt", "tex", "rst", "org", "wiki", "adoc", "textile",
            "ipynb", "db", "man", "context", "texi", "opendocument", "plain",
        };
        foreach (var ext in new[] { ".md", ".txt", ".log", ".html", ".htm", ".epub", ".docx", ".odt", ".rtf", ".pptx" })
        {
            var formats = ConversionMatrix.GetTargets(ext).Select(t => t.Format).ToList();
            foreach (var format in expected)
            {
                if ($".{format}" == ext) continue; // 源==目标，矩阵跳自身（.odt 源无 odt 目标等）
                Assert.True(formats.Contains(format), $"{ext} 应含 pandoc 目标 {format}");
            }
        }
    }

    [Fact]
    public void Pptx源_文本系全列加两跳()
    {
        // 2026-09-10 PPT 主线：.pptx 源 pandoc 文本系全列（html/md/txt/docx/epub + PandocTextTargets），
        // pdf 走 soffice→ComPdf 兜底，png/jpg 两跳（soffice 缺→ComPdfTwoHop 按序兜底）。
        var formats = ConversionMatrix.GetTargets(".pptx").Select(t => t.Format).ToList();
        Assert.Contains("pdf", formats);
        Assert.Contains("md", formats);
        Assert.Contains("docx", formats);
        Assert.Contains("html", formats);
        Assert.Contains("epub", formats);
        Assert.Contains("png", formats);
        Assert.Contains("jpg", formats);
        Assert.Contains("rtf", formats);
        Assert.Contains("odt", formats);
        Assert.Contains("tex", formats);
        // pandoc 直连文本系 = 无损（内容保真）；png 两跳渲染 = 无损；jpg 重编码 = 有损
        Assert.True(ConversionMatrix.Find(".pptx", "docx")!.Lossless);
        Assert.True(ConversionMatrix.Find(".pptx", "md")!.Lossless);
        Assert.True(ConversionMatrix.Find(".pptx", "png")!.Lossless);
        Assert.False(ConversionMatrix.Find(".pptx", "jpg")!.Lossless);
        // pdf 渲染保真 = 无损
        Assert.True(ConversionMatrix.Find(".pptx", "pdf")!.Lossless);
        // 老演示格式无 pandoc reader：.ppt 不列 pandoc 文本系
        Assert.DoesNotContain("docx", ConversionMatrix.GetTargets(".ppt").Select(t => t.Format));
        Assert.DoesNotContain("md", ConversionMatrix.GetTargets(".ppt").Select(t => t.Format));
    }

    [Fact]
    public void 无损规则_文本系无损两跳有损()
    {
        Assert.True(ConversionMatrix.Find(".md", "rtf")!.Lossless);   // pandoc 直转=无损
        Assert.True(ConversionMatrix.Find(".md", "docx")!.Lossless);  // pandoc 主链=无损
        Assert.False(ConversionMatrix.Find(".md", "pdf")!.Lossless);  // 两跳样式有损
        Assert.False(ConversionMatrix.Find(".doc", "md")!.Lossless);  // 两跳（非 docx/odt/rtf）有损
        Assert.True(ConversionMatrix.Find(".docx", "pdf")!.Lossless); // soffice/COM 渲染保真=无损
    }

    [Fact]
    public void 无损规则_媒体重编码有损()
    {
        Assert.False(ConversionMatrix.Find(".png", "jpg")!.Lossless);  // 重编码
        Assert.True(ConversionMatrix.Find(".png", "bmp")!.Lossless);   // 同族无损
        Assert.True(ConversionMatrix.Find(".png", "tif")!.Lossless);   // 同族无损
        Assert.False(ConversionMatrix.Find(".png", "webp")!.Lossless); // 重编码
        // 2026-09-20 D3①：音视频目标已从矩阵摘掉（本轮不带 FFmpeg 依赖）⇒ 原先这些"有损/无损"断言的对象
        // 已不在矩阵里。改成**不存在性断言**：将来若有人把音视频放回来，这里会红，提醒同时恢复上面那批规则。
        Assert.Null(ConversionMatrix.Find(".wav", "mp3"));
        Assert.Null(ConversionMatrix.Find(".wav", "flac"));
        Assert.Null(ConversionMatrix.Find(".mp4", "mkv"));
        Assert.Null(ConversionMatrix.Find(".mp4", "mp3"));
        Assert.False(ConversionMatrix.Find(".pdf", "txt")!.Lossless);  // 文本提取
        Assert.False(ConversionMatrix.Find(".pdf", "png")!.Lossless);  // 渲染
    }

    [Fact]
    public void 目标分类_按类别归属()
    {
        Assert.Equal(TargetCategory.Text, ConversionMatrix.Find(".md", "rtf")!.Category);
        Assert.Equal(TargetCategory.Document, ConversionMatrix.Find(".md", "docx")!.Category);
        Assert.Equal(TargetCategory.Document, ConversionMatrix.Find(".md", "pptx")!.Category);
        Assert.Equal(TargetCategory.Spreadsheet, ConversionMatrix.Find(".md", "csv")!.Category);
        Assert.Equal(TargetCategory.Image, ConversionMatrix.Find(".png", "jpg")!.Category);
        // 2026-09-20 D3①：音视频目标已摘掉 ⇒ Audio/Video 类别在矩阵里不再出现（这里钉住这个事实）
        Assert.Null(ConversionMatrix.Find(".wav", "mp3"));
        Assert.Null(ConversionMatrix.Find(".mp4", "mkv"));
        Assert.Equal(TargetCategory.Other, ConversionMatrix.EncryptPdf.Category);
    }

    [Fact]
    public void 系统级联无损过滤_全部目标仅无损入级联()
    {
        // 级联过滤条件 = Lossless && IsResolvable（IsResolvable 属引擎探测，此处只断言 Lossless 语义）
        var mdLossless = ConversionMatrix.GetTargets(".md").Where(t => t.Lossless).ToList();
        Assert.DoesNotContain(mdLossless, t => t.Format == "pdf"); // 两跳 pdf 不进级联
        Assert.Contains(mdLossless, t => t.Format == "docx");
        Assert.Contains(mdLossless, t => t.Format == "rtf");
    }
}
