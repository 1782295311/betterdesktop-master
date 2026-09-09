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
        Assert.Equal(["--headless", "--norestore", "--convert-to", "pdf", "--outdir", @"C:\tmp", @"C:\in\a b.docx"], args);
    }

    [Fact]
    public void Soffice参数_编码filter含UTF8()
    {
        Assert.Contains("UTF8", ConvertServiceConstants.TxtUtf8Filter);
        Assert.Contains("76", ConvertServiceConstants.CsvUtf8Filter); // StarCalc 代码页 76 = UTF-8
    }

    [Fact]
    public void UniqueTarget_重名序号不覆盖()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bd-conv-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Equal(Path.Combine(dir, "a.pdf"), ConversionService.UniqueTarget(dir, "a", "pdf"));
            File.WriteAllText(Path.Combine(dir, "a.pdf"), "x");
            Assert.Equal(Path.Combine(dir, "a (2).pdf"), ConversionService.UniqueTarget(dir, "a", "pdf"));
            File.WriteAllText(Path.Combine(dir, "a (2).pdf"), "x");
            Assert.Equal(Path.Combine(dir, "a (3).pdf"), ConversionService.UniqueTarget(dir, "a", "pdf"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void PublishAll_多产物按序号命名且不覆盖()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bd-conv-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var products = new List<string>();
            for (var i = 1; i <= 3; i++)
            {
                var p = Path.Combine(dir, $"tmp{i}.png");
                File.WriteAllText(p, "x");
                products.Add(p);
            }
            var input = Path.Combine(dir, "in.pdf");
            File.WriteAllText(input, "x");

            var outputs = ConversionService.PublishAll(products, "in", "png", [input]);

            Assert.Equal(3, outputs.Count);
            Assert.EndsWith("in-1.png", outputs[0]);
            Assert.EndsWith("in-2.png", outputs[1]);
            Assert.EndsWith("in-3.png", outputs[2]);
            Assert.All(outputs, o => Assert.True(File.Exists(o)));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void 文件名清理_非法字符()
    {
        Assert.Equal("a_b_c", BetterDesktop.Shell.Convert.Services.Markdown.MarkdownTransformer.SanitizeFileName(@"a/b\c"));
        Assert.Equal("output", BetterDesktop.Shell.Convert.Services.Markdown.MarkdownTransformer.SanitizeFileName("///"));
    }
}
