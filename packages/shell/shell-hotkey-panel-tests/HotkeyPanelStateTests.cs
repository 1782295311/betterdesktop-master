using Xunit;

namespace BetterDesktop.Shell.HotkeyPanel.Tests;

/// <summary>
/// 侧板交互状态机测试（P2-3）：门控翻转 / 刷新节流 / 忽略恢复——
/// P1-1（停表）、P1-3（仅只读可进可操作）、P0-1（可操作态全量）由纯模型层兜底。
/// 改键已移出侧板（用户裁定"侧板改键反人类"，改键在设置中心"热键"分节），状态机不再含录键分支。
/// </summary>
public class HotkeyPanelStateTests
{
    [Fact]
    public void Starts_read_only_and_only_readonly_can_enter_interactive()
    {
        var s = new HotkeyPanelState();
        Assert.Equal(HotkeyPanelMode.ReadOnly, s.Mode);
        Assert.False(s.IsInteractive);
        Assert.True(s.CanTimerRefresh);

        Assert.True(s.EnterInteractive());
        Assert.Equal(HotkeyPanelMode.Interactive, s.Mode);
        Assert.True(s.IsInteractive);
        Assert.False(s.CanTimerRefresh); // P1-1：交互中停表

        // 重复进入被拒（已在可操作态）
        Assert.False(s.EnterInteractive());
    }

    [Fact]
    public void Exit_to_readonly_closes_manage()
    {
        var s = new HotkeyPanelState();
        s.EnterInteractive();
        s.ToggleManage();
        Assert.Equal(HotkeyPanelMode.Manage, s.Mode);
        Assert.False(s.CanTimerRefresh); // P1-1：管理中停表

        s.ExitToReadOnly(); // 松开门控 → 强制回只读
        Assert.Equal(HotkeyPanelMode.ReadOnly, s.Mode);
    }

    [Fact]
    public void Toggle_manage_round_trips()
    {
        var s = new HotkeyPanelState();
        s.EnterInteractive();
        s.ToggleManage();
        Assert.Equal(HotkeyPanelMode.Manage, s.Mode);
        Assert.True(s.ShowsAllRows); // P0-1：管理也列全量

        // 退出回进入前状态（Interactive）
        s.ToggleManage();
        Assert.Equal(HotkeyPanelMode.Interactive, s.Mode);

        // 从只读进管理 → 退出回只读
        s.ExitToReadOnly();
        s.ToggleManage();
        Assert.Equal(HotkeyPanelMode.Manage, s.Mode);
        s.ToggleManage();
        Assert.Equal(HotkeyPanelMode.ReadOnly, s.Mode);
    }

    [Fact]
    public void Leave_manage_when_all_restored_returns_to_previous_mode()
    {
        var s = new HotkeyPanelState();
        s.ToggleManage(); // 从只读进管理
        Assert.Equal(HotkeyPanelMode.Manage, s.Mode);
        s.LeaveManage(); // hidden==0 自动退出
        Assert.Equal(HotkeyPanelMode.ReadOnly, s.Mode);
    }

    [Fact]
    public void Shows_all_rows_in_interactive_and_manage_only()
    {
        var s = new HotkeyPanelState();
        Assert.False(s.ShowsAllRows); // 只读态按上下文过滤（P0-1）
        s.EnterInteractive();
        Assert.True(s.ShowsAllRows);
        s.ExitToReadOnly();
        Assert.False(s.ShowsAllRows);
        s.ToggleManage();
        Assert.True(s.ShowsAllRows);
    }
}
