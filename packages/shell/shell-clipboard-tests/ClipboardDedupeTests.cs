using BetterDesktop.Shell.Clipboard.Contracts;
using Xunit;

namespace BetterDesktop.Shell.Clipboard.Tests;

/// <summary>去重：同内容复制置顶不新增；不同内容新增；HTML 与文本同源优先 HTML 单条。</summary>
public class ClipboardDedupeTests
{
    [Fact]
    public void SameContent_CopiedTwice_NotDuplicated()
    {
        var manager = new TestClipboardManager(SnapshotFactory.Text("same"));
        manager.OnClipboardUpdate();
        manager.OnClipboardUpdate();

        // 内容对比双保险（探索版机制）：同内容重复更新被跳过，不新增不计数。
        Assert.Single(manager.GetFilteredEntries());
    }

    [Fact]
    public void DifferentContent_AppendsNewEntry()
    {
        var manager = new TestClipboardManager(SnapshotFactory.Text("first"));
        manager.OnClipboardUpdate();

        manager.NextSnapshot = SnapshotFactory.Text("second");
        manager.OnClipboardUpdate();

        var entries = manager.GetFilteredEntries();
        Assert.Equal(2, entries.Count);
        Assert.Equal("second", entries[0].Content);
        Assert.Equal("first", entries[1].Content);
    }

    [Fact]
    public void SameHtml_ReCaptured_NotDuplicated()
    {
        var manager = new TestClipboardManager(SnapshotFactory.Html("<p>Hello</p>", "Hello"));
        manager.OnClipboardUpdate();
        manager.OnClipboardUpdate();

        Assert.Single(manager.GetFilteredEntries());
    }

    [Fact]
    public void DifferentTypes_SameText_AreDistinctEntries()
    {
        // 探索版源码语义：去重限定同类型（ContentType + Content），HTML 与纯文本同源为两条独立记录。
        var manager = new TestClipboardManager(SnapshotFactory.Html("<p>Hello</p>", "Hello"));
        manager.OnClipboardUpdate();

        manager.NextSnapshot = SnapshotFactory.Text("Hello");
        manager.OnClipboardUpdate();

        var entries = manager.GetFilteredEntries();
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.ContentType == ClipboardItemKind.Html);
        Assert.Contains(entries, e => e.ContentType == ClipboardItemKind.Text);
    }

    [Fact]
    public void Dedupe_KeepsPinnedState()
    {
        var manager = new TestClipboardManager(SnapshotFactory.Text("pin-me"));
        manager.OnClipboardUpdate();

        var entry = Assert.Single(manager.GetFilteredEntries());
        manager.PinEntry(entry);

        manager.OnClipboardUpdate();

        var entries = manager.GetFilteredEntries();
        Assert.Single(entries);
        Assert.True(entries[0].IsPinned);
    }
}
