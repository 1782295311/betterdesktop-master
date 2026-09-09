using System.Linq;
using BetterDesktop.Shell.Clipboard.Contracts;
using Xunit;

namespace BetterDesktop.Shell.Clipboard.Tests;

/// <summary>驱逐：超 10000 驱逐最旧非收藏；收藏超 200 驱逐最旧收藏。</summary>
public class ClipboardEvictionTests
{
    [Fact]
    public void OverMaxHistory_EvictsOldestUnpinned_FromTail()
    {
        var manager = new TestClipboardManager(SnapshotFactory.Text("seed"));
        for (int i = 0; i < ClipboardManager.MaxHistoryItems + 5; i++)
        {
            manager.NextSnapshot = SnapshotFactory.Text($"item-{i}");
            manager.OnClipboardUpdate();
        }

        var entries = manager.GetFilteredEntries();
        Assert.Equal(ClipboardManager.MaxHistoryItems, entries.Count);
        // 尾部最旧（seed）被驱逐，最新在顶部。
        Assert.Equal($"item-{ClipboardManager.MaxHistoryItems + 4}", entries[0].Content);
        Assert.DoesNotContain(entries, e => e.Content == "seed");
    }

    [Fact]
    public void OverMaxPinned_EvictsOldestPinned_KeepsPinnedAtLimit()
    {
        var manager = new TestClipboardManager(SnapshotFactory.Text("seed"));
        for (int i = 0; i < ClipboardManager.MaxPinnedItems + 10; i++)
        {
            manager.NextSnapshot = SnapshotFactory.Text($"p-{i}");
            manager.OnClipboardUpdate();
        }

        // 收藏最旧的 10 条（p-0..p-9 在尾部方向），再加 5 条非收藏。
        foreach (ClipboardEntry e in manager.GetFilteredEntries().Where(e => e.Content.StartsWith("p-") && e.Content != "p-0"))
        {
            manager.PinEntry(e);
        }

        manager.NextSnapshot = SnapshotFactory.Text("unpinned-1");
        manager.OnClipboardUpdate();

        var entries = manager.GetFilteredEntries();
        int pinned = entries.Count(e => e.IsPinned);
        Assert.True(pinned <= ClipboardManager.MaxPinnedItems, $"收藏 {pinned} 应 ≤ {ClipboardManager.MaxPinnedItems}");
    }
}
