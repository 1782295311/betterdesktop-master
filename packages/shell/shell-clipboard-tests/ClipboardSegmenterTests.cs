using System.Linq;
using Xunit;

namespace BetterDesktop.Shell.Clipboard.Tests;

/// <summary>自动分段器纯逻辑测试（Phase B D3 内核）：HTML 块级切分/内联保留/配对/降级；文本空行拆段。</summary>
public class ClipboardSegmenterTests
{
    // ---------- HTML ----------

    [Fact]
    public void SplitHtml_BlockEnds_SplitsIntoSegments()
    {
        var segments = ClipboardSegmenter.SplitHtml("<p>a</p><p>b</p>");
        Assert.Equal(2, segments.Count);
        Assert.False(segments[0].IsFallback);
        Assert.Equal("a", segments[0].Content);
        Assert.Equal("b", segments[1].Content);
    }

    [Fact]
    public void SplitHtml_InlineTags_PreservedWithinSegment()
    {
        var segments = ClipboardSegmenter.SplitHtml("<p><b>x</b></p><p>y</p>");
        Assert.Equal(2, segments.Count);
        Assert.Equal("<b>x</b>", segments[0].Content);
        Assert.Equal("y", segments[1].Content);
    }

    [Fact]
    public void SplitHtml_Br_SplitsSegments()
    {
        var segments = ClipboardSegmenter.SplitHtml("a<br>b");
        Assert.Equal(2, segments.Count);
        Assert.Equal("a", segments[0].Content);
        Assert.Equal("b", segments[1].Content);
    }

    [Fact]
    public void SplitHtml_NestedBlockOpenTags_StrippedAtSegmentEdges()
    {
        var segments = ClipboardSegmenter.SplitHtml("<div><p>a</p></div><div><p>b</p></div>");
        Assert.Equal(2, segments.Count);
        Assert.Equal("a", segments[0].Content);
        Assert.Equal("b", segments[1].Content);
        Assert.All(segments, s => Assert.False(s.IsFallback));
    }

    [Fact]
    public void SplitHtml_Unbalanced_FallbackToPlainText()
    {
        var segments = ClipboardSegmenter.SplitHtml("<p><b>x</p>");
        Assert.Single(segments);
        Assert.True(segments[0].IsFallback);
        Assert.Equal("x", segments[0].Content);
    }

    [Fact]
    public void SplitHtml_SingleSegment_NoSplit()
    {
        var segments = ClipboardSegmenter.SplitHtml("<p>just one</p>");
        Assert.Single(segments);
        Assert.False(segments[0].IsFallback);
    }

    [Fact]
    public void SplitHtml_ImageTag_VoidElement_DoesNotTriggerFallback()
    {
        var segments = ClipboardSegmenter.SplitHtml("<p>图<img src=\"x.png\">文</p>");
        Assert.Single(segments);
        Assert.False(segments[0].IsFallback);
    }

    // ---------- Text ----------

    [Fact]
    public void SplitText_BlankLines_SplitsSegments()
    {
        var segments = ClipboardSegmenter.SplitText("a\n\nb\n\nc");
        Assert.Equal(3, segments.Count);
        Assert.Equal("a", segments[0]);
        Assert.Equal("b", segments[1]);
        Assert.Equal("c", segments[2]);
    }

    [Fact]
    public void SplitText_LeadingTrailingBlankLines_Trimmed()
    {
        var segments = ClipboardSegmenter.SplitText("\n\na\n\nb\n\n");
        Assert.Equal(2, segments.Count);
        Assert.Equal("a", segments[0]);
        Assert.Equal("b", segments[1]);
    }

    [Fact]
    public void SplitText_NoBlankLine_SingleSegmentKeptAsIs()
    {
        var segments = ClipboardSegmenter.SplitText("a\nb");
        Assert.Single(segments);
        Assert.Equal("a\nb", segments[0]);
    }
}
