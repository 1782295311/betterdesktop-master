using System.IO;
using BetterDesktop.Shell.Convert.Contracts;
using BetterDesktop.Shell.Convert.Services;
using BetterDesktop.Shell.Convert.Services.Engines;

namespace BetterDesktop.Shell.Convert.Tests;

/// <summary>参数构造 + 安全输出（计划 §8：序号/输出≠输入/发布命名）。</summary>
public class SafeOutputAndArgsTests
{
    [Fact]
    public void Soffice参数_顺序与内容锁定()
    {
        var args = SofficeEngine.BuildArguments("pdf", @"C:\tmp", @"C:\in\a b.docx");
        // 首参 = 受管独立 profile（隔离 LibreOffice 首次启动版本面板，不写用户 %APPDATA%）
        Assert.StartsWith("-env:UserInstallation=file:///", args[0]);
        Assert.Equal(["--headless", "--norestore", "--convert-to", "pdf", "--outdir", @"C:\tmp", @"C:\in\a b.docx"],
            args.Skip(1));
    }

    [Fact]
    public void Soffice参数_编码filter含UTF8()
    {
        Assert.Contains("UTF8", ConvertServiceConstants.TxtUtf8Filter);
        Assert.Contains("76", ConvertServiceConstants.CsvUtf8Filter); // StarCalc 代码页 76 = UTF-8
    }

    [Fact]
    public void 文件名清理_非法字符()
    {
        Assert.Equal("a_b_c", BetterDesktop.Shell.Convert.Services.Markdown.MarkdownTransformer.SanitizeFileName(@"a/b\c"));
        Assert.Equal("output", BetterDesktop.Shell.Convert.Services.Markdown.MarkdownTransformer.SanitizeFileName("///"));
    }
}
