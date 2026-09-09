using Xunit;

namespace BetterDesktop.Shell.Clipboard.Tests;

/// <summary>临时暂停语义（Phase A 收口补测）：暂停期跳过捕获；恢复后正常记录。</summary>
public class ClipboardPauseTests
{
    [Fact]
    public void PauseTemporarily_OnClipboardUpdate_SkipsCapture()
    {
        var manager = new TestClipboardManager(SnapshotFactory.Text("hello"));
        manager.PauseTemporarily(seconds: 60);

        manager.OnClipboardUpdate();

        // 暂停期：快照不被读取、历史不写入。
        Assert.Equal(0, manager.SnapshotReadCount);
        Assert.Empty(manager.GetFilteredEntries());
    }

    [Fact]
    public void Resume_AfterPause_RestoresCapture()
    {
        var manager = new TestClipboardManager(SnapshotFactory.Text("hello"));
        manager.PauseTemporarily(seconds: 60);

        manager.OnClipboardUpdate();
        Assert.Empty(manager.GetFilteredEntries());

        manager.Resume();
        manager.OnClipboardUpdate();

        Assert.Single(manager.GetFilteredEntries());
    }
}
