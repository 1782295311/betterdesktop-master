using System;
using System.IO;
using System.Linq;
using BetterDesktop.Shell.Clipboard.Contracts;
using Xunit;

namespace BetterDesktop.Shell.Clipboard.Tests;

/// <summary>过期清理：90 天前非收藏被清（含图片文件），收藏保留。</summary>
public class ClipboardCleanupTests
{
    [Fact]
    public void ExpiredUnpinned_Removed_WithImageFile_Deleted()
    {
        string dir = Path.Combine(Path.GetTempPath(), "bdt-clipboard-tests", Guid.NewGuid().ToString("N"));
        var manager = new TestClipboardManager(SnapshotFactory.Image(SnapshotFactory.FakePng(8, 8)), dir);
        manager.OnClipboardUpdate();

        ClipboardEntry image = Assert.Single(manager.GetFilteredEntries());
        string imageFull = Path.Combine(dir, image.ImagePath);
        Assert.True(File.Exists(imageFull));

        image.Timestamp = DateTime.Now.AddDays(-100);
        manager.CleanupExpiredEntries();

        Assert.Empty(manager.GetFilteredEntries());
        Assert.False(File.Exists(imageFull));
    }

    [Fact]
    public void ExpiredPinned_Kept_WithImageFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "bdt-clipboard-tests", Guid.NewGuid().ToString("N"));
        var manager = new TestClipboardManager(SnapshotFactory.Image(SnapshotFactory.FakePng(8, 8)), dir);
        manager.OnClipboardUpdate();

        ClipboardEntry image = Assert.Single(manager.GetFilteredEntries());
        manager.PinEntry(image);
        image.Timestamp = DateTime.Now.AddDays(-100);

        manager.CleanupExpiredEntries();

        Assert.Single(manager.GetFilteredEntries());
        Assert.True(File.Exists(Path.Combine(dir, image.ImagePath)));
    }

    [Fact]
    public void RecentEntries_Kept()
    {
        var manager = new TestClipboardManager(SnapshotFactory.Text("fresh"));
        manager.OnClipboardUpdate();

        manager.CleanupExpiredEntries();

        Assert.Single(manager.GetFilteredEntries());
    }
}
