using BetterDesktop.Shell.Convert.Contracts;
using BetterDesktop.Shell.Convert.Services.Engines;
using Xunit;

namespace BetterDesktop.Shell.Convert.Tests;

/// <summary>
/// PandocEngine 能力契约：--version 首行解析（新旧格式兼容）+ pptx 版本门槛（高亮=成功契约）
/// + 2026-09-10 pandoc 文本系扩展白名单（源×目标）。
/// </summary>
public class PandocEngineTests
{
    private static ConversionTarget Target(string format, EngineKind prefer) =>
        new(format, format, null, 1, prefer);

    // ===== TryParseVersion：新旧格式兼容 =====

    [Theory]
    [InlineData("pandoc.exe 2.0.1.1", 2, 0, 1, 1)]  // 旧格式（2017）
    [InlineData("pandoc 3.6.4", 3, 6, 4, 0)]        // 新格式
    [InlineData("pandoc v3.1.2", 3, 1, 2, 0)]       // 带 v 前缀
    [InlineData("pandoc.exe 1.19.2.1", 1, 19, 2, 1)]
    public void TryParseVersion_RecognizesOldAndNewFormats(
        string firstLine, int major, int minor, int build, int revision)
    {
        var ok = PandocEngine.TryParseVersion(firstLine, out var version);

        Assert.True(ok);
        Assert.Equal(new Version(major, minor, build, revision), version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not pandoc at all")]
    [InlineData("libreoffice 7.6.4")]
    [InlineData("pandoc")]
    public void TryParseVersion_RejectsInvalidLines(string firstLine)
    {
        Assert.False(PandocEngine.TryParseVersion(firstLine, out _));
    }

    // ===== CanHandle：pptx 版本门槛（高亮=成功） =====

    [Fact]
    public void CanHandle_Pptx_RequiresVersionCapability()
    {
        var engine = new PandocEngine();

        try
        {
            PandocEngine.SupportsPptx = false; // 默认/旧版状态（2.0.1.1 实测失败）
            Assert.False(engine.CanHandle(["a.md"], Target("pptx", EngineKind.Pandoc)));

            PandocEngine.SupportsPptx = true; // 引擎升级到 3.0+ 后解锁
            Assert.True(engine.CanHandle(["a.md"], Target("pptx", EngineKind.Pandoc)));
        }
        finally
        {
            PandocEngine.SupportsPptx = false;
        }
    }

    [Theory]
    [InlineData("a.docx", "md", true)]
    [InlineData("a.md", "docx", true)]
    [InlineData("a.md", "epub", true)]
    [InlineData("a.docx", "pptx", false)] // 矩阵不声明 docx→pptx，CanHandle 不认
    [InlineData("a.txt", "md", true)] // 2026-09-10：txt/log 进 ReadableSources（pandoc markdown 解析）
    [InlineData("a.md", "pdf", false)]
    [InlineData("a.md", "mp4", false)] // 非 pandoc 目标
    public void CanHandle_MatrixPairs(string path, string format, bool expected)
    {
        var engine = new PandocEngine();
        Assert.Equal(expected, engine.CanHandle([path], Target(format, EngineKind.Pandoc)));
    }

    // ===== CanHandle：2026-09-10 pandoc 文本系扩展（用户拍板"当然要加"） =====

    [Theory]
    [InlineData("a.md", "rtf")]
    [InlineData("a.md", "odt")]
    [InlineData("a.md", "tex")]
    [InlineData("a.md", "rst")]
    [InlineData("a.md", "org")]
    [InlineData("a.md", "wiki")]
    [InlineData("a.md", "adoc")]
    [InlineData("a.md", "textile")]
    [InlineData("a.md", "ipynb")]
    [InlineData("a.md", "db")]
    [InlineData("a.md", "man")]
    [InlineData("a.md", "context")]
    [InlineData("a.md", "texi")]
    [InlineData("a.md", "opendocument")]
    [InlineData("a.md", "plain")]
    [InlineData("a.docx", "rtf")]
    [InlineData("a.odt", "tex")]
    [InlineData("a.rtf", "rst")]
    [InlineData("a.txt", "org")]
    [InlineData("a.log", "wiki")]
    [InlineData("a.html", "adoc")]
    [InlineData("a.htm", "ipynb")]
    [InlineData("a.epub", "opendocument")]
    public void CanHandle_PandocExtendedWriters(string path, string format)
    {
        var engine = new PandocEngine();
        Assert.True(engine.CanHandle([path], Target(format, EngineKind.Pandoc)));
    }

    [Fact]
    public void CanHandle_OnlyAcceptsSingleSource()
    {
        var engine = new PandocEngine();
        Assert.False(engine.CanHandle(["a.md", "b.md"], Target("docx", EngineKind.Pandoc)));
    }
}
