using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using BetterDesktop.Shell.Clipboard.Contracts;
using BetterDesktop.Shell.Clipboard.Native;
using Xunit;

namespace BetterDesktop.Shell.Clipboard.Tests;

/// <summary>Phase B 内核纯逻辑测试：D3 自动分段判定、D5 合并拼接、L 按序粘贴状态机、CF_HTML 头包装。</summary>
public class ClipboardPhaseBTests
{
    private static ClipboardEntry TextEntry(string content, ContentCategory category = ContentCategory.Text) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        ContentType = ClipboardItemKind.Text,
        Content = content,
        Category = category,
    };

    private static ClipboardEntry HtmlEntry(string html, string plain) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        ContentType = ClipboardItemKind.Html,
        HtmlContent = html,
        Content = plain,
        Category = ContentCategory.RichText,
    };

    private static ClipboardEntry ImageEntry() => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        ContentType = ClipboardItemKind.Image,
        Category = ContentCategory.Image,
    };

    private static ClipboardEntry FilesEntry() => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        ContentType = ClipboardItemKind.Files,
        FilePaths = new[] { @"C:\a.txt" },
        Category = ContentCategory.File,
    };

    // ---------- D3 自动分段判定（GetSegments 纯判定，不触碰剪贴板） ----------

    [Fact]
    public void GetSegments_HtmlMultiBlock_SplitsWithFormat()
    {
        var m = new TestClipboardManager(null);
        var entry = HtmlEntry("<p>甲</p><p>乙</p><p>丙</p>", "甲乙丙");
        var segments = m.GetSegments(entry);

        Assert.NotNull(segments);
        Assert.Equal(3, segments!.Count);
        Assert.All(segments, s => Assert.True(s.IsHtml));
    }

    [Fact]
    public void GetSegments_HtmlSingleBlock_NoSplit()
    {
        var m = new TestClipboardManager(null);
        var entry = HtmlEntry("<p>只有一段</p>", "只有一段");
        Assert.Null(m.GetSegments(entry));
    }

    [Fact]
    public void GetSegments_TextBlankLines_SplitsPlain()
    {
        var m = new TestClipboardManager(null);
        var entry = TextEntry("a\n\nb\n\nc");
        var segments = m.GetSegments(entry);

        Assert.NotNull(segments);
        Assert.Equal(3, segments!.Count);
        Assert.All(segments, s => Assert.False(s.IsHtml));
        Assert.Equal(new[] { "a", "b", "c" }, segments.Select(s => s.Content).ToArray());
    }

    [Fact]
    public void GetSegments_TextSingleLine_NoSplit()
    {
        var m = new TestClipboardManager(null);
        Assert.Null(m.GetSegments(TextEntry("a\nb")));
    }

    [Fact]
    public void GetSegments_Code_NoSplit()
    {
        var m = new TestClipboardManager(null);
        var entry = TextEntry("void M()\n{\n    x();\n}\n\ny();", ContentCategory.Code);
        Assert.Null(m.GetSegments(entry));
    }

    [Fact]
    public void GetSegments_ImageAndFiles_NoSplit()
    {
        var m = new TestClipboardManager(null);
        Assert.Null(m.GetSegments(ImageEntry()));
        Assert.Null(m.GetSegments(FilesEntry()));
    }

    // ---------- D5 合并拼接（BuildMergeText 纯函数） ----------

    [Fact]
    public void BuildMergeText_JoinsWithSeparator()
    {
        var entries = new List<ClipboardEntry> { TextEntry("甲"), TextEntry("乙"), TextEntry("丙") };
        Assert.Equal("甲, 乙, 丙", ClipboardManager.BuildMergeText(entries, ", "));
        Assert.Equal("甲\n乙", ClipboardManager.BuildMergeText(entries.Take(2).ToList(), "\n"));
    }

    [Fact]
    public void BuildMergeText_EmptyList_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, ClipboardManager.BuildMergeText(new List<ClipboardEntry>(), "\n"));
        Assert.Equal(string.Empty, ClipboardManager.BuildMergeText(null!, "\n"));
    }

    // ---------- L 按序粘贴状态机（纯状态迁移，不触碰剪贴板） ----------

    [Fact]
    public void SequentialPaste_StateMachine_Transitions()
    {
        var m = new TestClipboardManager(null);
        var entries = new List<ClipboardEntry> { TextEntry("1"), TextEntry("2") };

        Assert.False(m.IsSequentialPasteActive);
        Assert.Equal(0, m.SequentialRemaining);

        m.BeginSequentialPaste(entries);
        Assert.True(m.IsSequentialPasteActive);
        Assert.Equal(2, m.SequentialRemaining);

        m.CancelSequentialPaste();
        Assert.False(m.IsSequentialPasteActive);
        Assert.Equal(0, m.SequentialRemaining);
    }

    [Fact]
    public void SequentialPaste_BeginOverwritesAndResetClears()
    {
        var m = new TestClipboardManager(null);
        m.BeginSequentialPaste(new List<ClipboardEntry> { TextEntry("1"), TextEntry("2") });
        m.BeginSequentialPaste(new List<ClipboardEntry> { TextEntry("a") });
        Assert.True(m.IsSequentialPasteActive);
        Assert.Equal(1, m.SequentialRemaining);

        m.ResetSequentialPaste();
        Assert.False(m.IsSequentialPasteActive);
    }

    [Fact]
    public void SequentialPaste_EmptyList_Throws()
    {
        var m = new TestClipboardManager(null);
        Assert.Throws<ArgumentException>(() => m.BeginSequentialPaste(new List<ClipboardEntry>()));
        Assert.Throws<ArgumentNullException>(() => m.BeginSequentialPaste(null!));
    }

    // ---------- CF_HTML 头包装（生死线 7 ②：偏移必须与真实长度一致） ----------

    [Fact]
    public void WrapHtmlForClipboard_OffsetsMatchActualLengths()
    {
        const string html = "<p>hello</p>";
        string wrapped = ClipboardNative.WrapHtmlForClipboard(html);

        var m = Regex.Match(wrapped, @"StartHTML:(\d+)\r\nEndHTML:(\d+)\r\nStartFragment:(\d+)\r\nEndFragment:(\d+)\r\n");
        Assert.True(m.Success);
        int startHtml = int.Parse(m.Groups[1].Value);
        int endHtml = int.Parse(m.Groups[2].Value);
        int startFragment = int.Parse(m.Groups[3].Value);
        int endFragment = int.Parse(m.Groups[4].Value);

        Assert.Equal(endHtml, wrapped.Length);
        Assert.Equal(startFragment, startHtml + "<html><body><!--StartFragment-->".Length);
        Assert.Equal(endFragment, startFragment + html.Length);
        Assert.Equal(html, wrapped.Substring(startFragment, endFragment - startFragment));
    }

    [Fact]
    public void WrapHtmlForClipboard_EmptyReturnsEmpty()
    {
        Assert.Equal(string.Empty, ClipboardNative.WrapHtmlForClipboard(string.Empty));
        Assert.Equal(string.Empty, ClipboardNative.WrapHtmlForClipboard(null!));
    }
}
