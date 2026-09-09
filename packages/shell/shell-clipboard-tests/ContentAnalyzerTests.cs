using BetterDesktop.Shell.Clipboard.Contracts;
using Xunit;

namespace BetterDesktop.Shell.Clipboard.Tests;

/// <summary>内容语义分类（纯函数）：代码→Code；正文→Text；HTML 富文本；File/Image 优先级。</summary>
public class ContentAnalyzerTests
{
    [Fact]
    public void CodeLikeText_ClassifiedAsCode()
    {
        const string code = "public static int Add(int a, int b)\n{\n    int result = a + b;\n    return result;\n}";
        ContentProfile profile = ContentAnalyzer.Analyze(ClipboardItemKind.Text, string.Empty, code);
        Assert.Equal(ContentCategory.Code, profile.Category);
        Assert.True(profile.IsCode);
    }

    [Fact]
    public void PlainParagraph_ClassifiedAsText()
    {
        const string text = "今天天气很好，我们一起去公园散步。春天的花开得特别漂亮。";
        ContentProfile profile = ContentAnalyzer.Analyze(ClipboardItemKind.Text, string.Empty, text);
        Assert.Equal(ContentCategory.Text, profile.Category);
        Assert.False(profile.IsCode);
    }

    [Fact]
    public void HtmlWithoutImageTable_ClassifiedAsRichText()
    {
        ContentProfile profile = ContentAnalyzer.Analyze(
            ClipboardItemKind.Html,
            "<p style=\"color:red\">Hello <b>World</b></p>",
            "Hello World");
        Assert.Equal(ContentCategory.RichText, profile.Category);
        Assert.False(profile.HasImages);
        Assert.False(profile.HasTable);
    }

    [Fact]
    public void HtmlWithImage_SetsHasImagesFlag()
    {
        ContentProfile profile = ContentAnalyzer.Analyze(
            ClipboardItemKind.Html,
            "<html><body><img src=\"https://x/y.png\"/><p>图文</p></body></html>",
            "图文");
        Assert.Equal(ContentCategory.RichText, profile.Category);
        Assert.True(profile.HasImages);
        Assert.False(profile.HasTable);
    }

    [Fact]
    public void HtmlWithTable_SetsHasTableFlag()
    {
        ContentProfile profile = ContentAnalyzer.Analyze(
            ClipboardItemKind.Html,
            "<html><body><table><tr><td>A</td></tr></table></body></html>",
            "A");
        Assert.Equal(ContentCategory.RichText, profile.Category);
        Assert.True(profile.HasTable);
    }

    [Fact]
    public void FileAndImage_Kinds_TakePriority()
    {
        ContentProfile file = ContentAnalyzer.Analyze(ClipboardItemKind.Files, "<p>x</p>", "x");
        Assert.Equal(ContentCategory.File, file.Category);

        ContentProfile image = ContentAnalyzer.Analyze(ClipboardItemKind.Image, string.Empty, string.Empty);
        Assert.Equal(ContentCategory.Image, image.Category);
    }

    [Fact]
    public void ShortText_NeverClassifiedAsCode()
    {
        Assert.False(ContentAnalyzer.IsCodeText("if x"));
        Assert.False(ContentAnalyzer.IsCodeText(""));
    }
}
