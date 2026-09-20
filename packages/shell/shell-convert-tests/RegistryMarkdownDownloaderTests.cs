using System.IO;
using BetterDesktop.Shell.Convert.Contracts;
using BetterDesktop.Shell.Convert.Services;
using BetterDesktop.Shell.Convert.Services.Engines;
using BetterDesktop.Shell.Convert.Services.Markdown;

namespace BetterDesktop.Shell.Convert.Tests;

/// <summary>引擎选择（Prefer→Fallback 候选链）+ Markdown 纯托管链 + 下载器诚实红线。</summary>
public class RegistryMarkdownDownloaderTests
{
    [Fact]
    public void 候选链_Prefer缺失回退Fallback()
    {
        var registry = new EngineRegistry()
            .Add(new FakeEngine(EngineKind.Soffice, available: false))
            .Add(new FakeEngine(EngineKind.ComPdf, available: true, name: "com"));
        // 2026-09-20 convert-lite 迁移：.docx→pdf 已改由 Lite 负责（无 fallback），这里换一条**仍保留
        // Prefer→Fallback 链**的边来测候选链语义：.wps 是 lite 没有 reader 的老格式 ⇒ 仍是 soffice 主 + COM 兜底。
        var pdf = ConversionMatrix.Find(".wps", "pdf")!;

        var resolved = registry.Resolve(["C:\\x.wps"], pdf);

        Assert.NotNull(resolved);
        Assert.Equal("com", resolved.Name);
    }

    [Fact]
    public void 候选链_全缺失返回null()
    {
        var registry = new EngineRegistry().Add(new FakeEngine(EngineKind.Soffice, available: false));
        var pdf = ConversionMatrix.Find(".docx", "pdf")!;
        Assert.Null(registry.Resolve(["C:\\x.docx"], pdf));
    }

    [Fact]
    public void 纯托管链_无soffice也可独立解析()
    {
        // 机器无 LibreOffice 也不影响 md→html/txt（纯托管必须独立可用，计划 6.3）
        var registry = new EngineRegistry()
            .Add(new FakeEngine(EngineKind.Soffice, available: false))
            .Add(new FakeEngine(EngineKind.Managed, available: true));
        Assert.NotNull(registry.Resolve(["C:\\x.md"], ConversionMatrix.Find(".md", "html")!));
        Assert.NotNull(registry.Resolve(["C:\\x.md"], ConversionMatrix.Find(".md", "txt")!));
        // 两跳依赖 soffice：缺失即隐藏
        Assert.Null(registry.Resolve(["C:\\x.md"], ConversionMatrix.Find(".md", "pdf")!));
    }

    [Fact]
    public void FrontMatter_默认剥离_可转标题区()
    {
        const string md = "---\ntitle: 测试\nauthor: 张三\n---\n\n# 正文\n";
        var (body, had) = MarkdownTransformer.StripFrontMatter(md);
        Assert.True(had);
        Assert.DoesNotContain("title", body);
        Assert.Contains("正文", body);

        var heading = MarkdownTransformer.ApplyFrontMatter(md, FrontMatterMode.Heading);
        Assert.Contains("**title**: 测试", heading);
        Assert.Contains("正文", heading);
    }

    [Fact]
    public void 无FrontMatter_原样返回()
    {
        const string md = "# 标题\n\n段落\n";
        var (body, had) = MarkdownTransformer.StripFrontMatter(md);
        Assert.False(had);
        Assert.Equal(md, body);
    }

    [Fact]
    public void Md转Html_表格任务列表样式齐备()
    {
        const string md = "| a | b |\n|---|---|\n| 1 | 2 |\n\n- [x] 完成\n";
        var html = MarkdownTransformer.BuildStandaloneHtml("t", MarkdownTransformer.ToHtmlBody(md));
        Assert.Contains("<table>", html);
        Assert.Contains("checkbox", html);
        Assert.Contains("Microsoft YaHei", html); // CJK 字体栈（红线 8）
        Assert.Contains("<style>", html);        // 完整独立页（内嵌 CSS）
    }

    [Fact]
    public void Html转Md_往返结构保留()
    {
        const string html = "<h1>标题</h1><p><strong>粗体</strong>与文本</p>";
        var md = new ReverseMarkdown.Converter().Convert(html);
        Assert.Contains("# 标题", md);
        Assert.Contains("**粗体**", md);
    }

    [Fact]
    public void Html转Text_实体解码与换行()
    {
        var text = MarkdownTransformer.HtmlToText("<p>a&amp;b</p><p>第二段</p>");
        Assert.Contains("a&b", text);
        Assert.Contains("第二段", text);
    }

    [Fact]
    public void PlainText转Html_转义防注入()
    {
        var html = MarkdownTransformer.PlainTextToHtml("<script>alert(1)</script>");
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void 图片缺失_逐张告警不抛异常()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bd-conv-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var html = MarkdownTransformer.ResolveRelativeImageSrcs(@"<img src=""missing.png"">", dir);
            Assert.Contains("missing.png", html); // 保留原 src（告警不阻断，不静默残缺）
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task 下载器_校验和未配置显式拒绝()
    {
        Assert.False(EngineDownloader.IsSpecConfigured(EngineDownloader.PandocSpec));
        Assert.False(EngineDownloader.IsSpecConfigured(EngineDownloader.FfmpegSpec));
        // 无校验不下载（dependency-on-demand 红线 2）——直接抛异常且零网络行为
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => EngineDownloader.Shared.DownloadAsync(EngineDownloader.PandocSpec, CancellationToken.None));
    }

    [Fact]
    public void 同类引擎_注册序取第一个可用者_演示族两跳()
    {
        // 2026-09-10 收尾：ComPdfTwoHop 注册序在 TwoHopEngine 之前（演示族 png/jpg 两跳 COM 优先保真，
        // TwoHop(soffice 中间态) 兜底——COM 缺失时"高亮=成功"契约仍成立）
        // 2026-09-20 convert-lite 迁移：.pptx→png 已被 lite 收编（Lite 单条、无同类竞争）⇒ 换 .dps
        // （WPS 演示，lite 没有 reader）才能继续测"同类多引擎按注册序取第一个可用者"。
        var png = ConversionMatrix.Find(".dps", "png")!;
        var registry = new EngineRegistry()
            .Add(new FakeEngine(EngineKind.TwoHop, available: true, name: "com-two-hop"))
            .Add(new FakeEngine(EngineKind.TwoHop, available: true, name: "two-hop"));
        Assert.Equal("com-two-hop", registry.Resolve(["C:\\a.dps"], png)!.Name);

        // COM 缺失（第一不可用）→ 第二个（soffice 两跳）兜底
        var registry2 = new EngineRegistry()
            .Add(new FakeEngine(EngineKind.TwoHop, available: false, name: "com-two-hop"))
            .Add(new FakeEngine(EngineKind.TwoHop, available: true, name: "two-hop"));
        Assert.Equal("two-hop", registry2.Resolve(["C:\\a.dps"], png)!.Name);
    }

    [Fact]
    public void 菜单高亮_仅无损项高亮()
    {
        // 2026-09-10 高亮契约：只有可无损转换才高亮（加粗），有损项正常显示但不加粗
        var registry = new EngineRegistry()
            .Add(new FakeEngine(EngineKind.TwoHop, available: true, name: "two-hop"))
            .Add(new FakeEngine(EngineKind.Pandoc, available: true, name: "pandoc"))
            // 2026-09-20：.pptx→png 等边已改派 Lite ⇒ 注册表里必须有它，否则 IsEngineReady=false、
            // 整项隐藏，菜单里就找不到 convertTopng（用真实 LiteEngine：它恒可用，不需要打桩）。
            .Add(new LiteEngine());
        var service = new ConvertMenuService(registry, new ConversionService(registry));
        var dir = Path.Combine(Path.GetTempPath(), "bd-menu-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var pptx = Path.Combine(dir, "a.pptx");
            File.WriteAllBytes(pptx, [0x50, 0x4B]); // 占位（仅 File.Exists 判定）
            var items = service.BuildMenuItems([pptx]);
            Assert.True(items.Count > 0, $"items 为空（pptx 存在={File.Exists(pptx)}，targets={ConversionMatrix.GetTargets(".pptx").Count}）");
            var sub = items.FirstOrDefault(i => i.Id == "convertTo");
            Assert.NotNull(sub);
            Assert.True(sub.Children!.First(c => c.Id == "convertTopng").Highlighted); // 无损 → 高亮
            Assert.False(sub.Children!.First(c => c.Id == "convertTojpg").Highlighted); // 有损 → 不高亮
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
