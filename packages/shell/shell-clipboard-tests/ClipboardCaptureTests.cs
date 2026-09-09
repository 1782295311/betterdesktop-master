using System.IO;
using BetterDesktop.Shell.Clipboard.Contracts;
using Xunit;

namespace BetterDesktop.Shell.Clipboard.Tests;

/// <summary>快照接缝注入四类内容 → 正确建条目；空快照不记录。</summary>
public class ClipboardCaptureTests
{
    [Fact]
    public void TextSnapshot_BuildsTextEntry()
    {
        var manager = new TestClipboardManager(SnapshotFactory.Text("plain text"));

        manager.OnClipboardUpdate();

        ClipboardEntry entry = Assert.Single(manager.GetFilteredEntries());
        Assert.Equal(ClipboardItemKind.Text, entry.ContentType);
        Assert.Equal("plain text", entry.Content);
        Assert.Equal(ContentCategory.Text, entry.Category);
    }

    [Fact]
    public void HtmlSnapshot_BuildsHtmlEntry_WithContentAndPlainText()
    {
        var manager = new TestClipboardManager(SnapshotFactory.Html("<p>Hello <b>World</b></p>", "Hello World"));

        manager.OnClipboardUpdate();

        ClipboardEntry entry = Assert.Single(manager.GetFilteredEntries());
        Assert.Equal(ClipboardItemKind.Html, entry.ContentType);
        Assert.Equal("<p>Hello <b>World</b></p>", entry.HtmlContent);
        Assert.Equal("Hello World", entry.Content);
    }

    [Fact]
    public void RichTextSnapshot_BuildsRichTextEntry()
    {
        var manager = new TestClipboardManager(SnapshotFactory.RichText("<p>Hi</p>", "{\\rtf1 Hi}", "Hi"));

        manager.OnClipboardUpdate();

        ClipboardEntry entry = Assert.Single(manager.GetFilteredEntries());
        Assert.Equal(ClipboardItemKind.RichText, entry.ContentType);
        Assert.Equal("{\\rtf1 Hi}", entry.RtfContent);
    }

    [Fact]
    public void ImageSnapshot_PersistsPng_AndSetsSize()
    {
        string dir = Path.Combine(Path.GetTempPath(), "bdt-clipboard-tests", System.Guid.NewGuid().ToString("N"));
        var manager = new TestClipboardManager(SnapshotFactory.Image(SnapshotFactory.FakePng(32, 48)), dir);

        manager.OnClipboardUpdate();

        ClipboardEntry entry = Assert.Single(manager.GetFilteredEntries());
        Assert.Equal(ClipboardItemKind.Image, entry.ContentType);
        Assert.False(string.IsNullOrEmpty(entry.ImagePath));
        Assert.Equal(32, entry.ImageWidth);
        Assert.Equal(48, entry.ImageHeight);
        Assert.True(File.Exists(Path.Combine(dir, entry.ImagePath)));
    }

    [Fact]
    public void FilesSnapshot_BuildsFileEntry()
    {
        var manager = new TestClipboardManager(SnapshotFactory.Files(@"C:\a.txt", @"C:\b.txt"));

        manager.OnClipboardUpdate();

        ClipboardEntry entry = Assert.Single(manager.GetFilteredEntries());
        Assert.Equal(ClipboardItemKind.Files, entry.ContentType);
        Assert.Equal(2, entry.FilePaths.Length);
        Assert.Equal(ContentCategory.File, entry.Category);
    }

    [Fact]
    public void EmptySnapshot_NotRecorded()
    {
        var manager = new TestClipboardManager(SnapshotFactory.Empty());

        manager.OnClipboardUpdate();

        Assert.Empty(manager.GetFilteredEntries());
    }
}
