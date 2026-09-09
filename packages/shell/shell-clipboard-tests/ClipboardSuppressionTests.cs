using System.Linq;
using BetterDesktop.Shell.Clipboard.Contracts;
using Xunit;

namespace BetterDesktop.Shell.Clipboard.Tests;

/// <summary>写回抑制回归（原版 ClipboardSuppressionTests 同款语义）：令牌置位 → 更新跳过；消费归零后可正常捕获。</summary>
public class ClipboardSuppressionTests
{
    [Fact]
    public void SuppressToken_Set_OnClipboardUpdate_SkipsCapture_And_TokenConsumed()
    {
        var manager = new TestClipboardManager(SnapshotFactory.Text("hello"));

        manager.SetSuppressTokenForTest();
        manager.OnClipboardUpdate();

        // 令牌置位：本次更新被跳过，历史不写入、快照不读取。
        Assert.Equal(0, manager.SnapshotReadCount);
        Assert.Empty(manager.GetFilteredEntries());

        // 令牌已被消费归零：下一次更新恢复正常捕获。
        manager.OnClipboardUpdate();
        Assert.Single(manager.GetFilteredEntries());
    }

    [Fact]
    public void SuppressToken_Set_NoHistoryChanged_Event()
    {
        var manager = new TestClipboardManager(SnapshotFactory.Text("hello"));
        int raised = 0;
        manager.HistoryChanged += _ => raised++;

        manager.SetSuppressTokenForTest();
        manager.OnClipboardUpdate();

        Assert.Equal(0, raised);
    }

    [Fact]
    public void NoSuppress_NormalCapture_RaisesHistoryChanged()
    {
        var manager = new TestClipboardManager(SnapshotFactory.Text("hello"));
        int raised = 0;
        ClipboardChangeKind? lastChange = null;
        manager.HistoryChanged += e =>
        {
            raised++;
            lastChange = e.Change;
        };

        manager.OnClipboardUpdate();

        Assert.Equal(1, raised);
        Assert.Equal(ClipboardChangeKind.Added, lastChange);
        Assert.Single(manager.GetFilteredEntries());
    }

    [Fact]
    public void SuppressToken_AfterCapture_DoesNotKillFutureCaptures()
    {
        var manager = new TestClipboardManager(SnapshotFactory.Text("a"));
        manager.OnClipboardUpdate();
        Assert.Single(manager.GetFilteredEntries());

        manager.SetSuppressTokenForTest();
        manager.OnClipboardUpdate();
        Assert.Single(manager.GetFilteredEntries());

        // 换内容后令牌已归零，正常捕获。
        var manager2 = new TestClipboardManager(SnapshotFactory.Text("b"));
        manager2.OnClipboardUpdate();
        Assert.Single(manager2.GetFilteredEntries());
    }
}
