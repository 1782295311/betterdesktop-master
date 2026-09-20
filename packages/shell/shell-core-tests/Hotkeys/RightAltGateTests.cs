using BetterDesktop.Shell.Core.Hotkeys;
using BetterDesktop.Shell.Core.Native;
using Xunit;

namespace BetterDesktop.Shell.Core.Tests.Hotkeys;

/// <summary>
/// 右 Alt 门控判定测试（纯函数；钩子跟踪本身无单测价值——只转发起落）。
/// </summary>
public class RightAltGateTests
{
    [Fact]
    public void Activates_only_when_gate_down_and_target_hit()
    {
        Assert.True(RightAltGate.ShouldSwitchToInteractive(
            gateKeyDown: true, ctrlHeld: false, targetHit: true, gateVk: NativeMethods.VK_RMENU));
        Assert.False(RightAltGate.ShouldSwitchToInteractive(
            gateKeyDown: false, ctrlHeld: false, targetHit: true, gateVk: NativeMethods.VK_RMENU));
        Assert.False(RightAltGate.ShouldSwitchToInteractive(
            gateKeyDown: true, ctrlHeld: false, targetHit: false, gateVk: NativeMethods.VK_RMENU));
    }

    [Fact]
    public void AltGr_layout_guard_blocks_right_alt_plus_ctrl()
    {
        // 欧语布局 AltGr = Ctrl + 右 Alt：按住右 Alt 同时按住 Ctrl 疑似输入字符 → 不激活
        Assert.False(RightAltGate.ShouldSwitchToInteractive(
            gateKeyDown: true, ctrlHeld: true, targetHit: true, gateVk: NativeMethods.VK_RMENU));
    }

    [Fact]
    public void Right_ctrl_gate_not_blocked_by_ctrl()
    {
        // 门控键为右 Ctrl：Ctrl 按住是门控本身，不做 AltGr 防护
        Assert.True(RightAltGate.ShouldSwitchToInteractive(
            gateKeyDown: true, ctrlHeld: true, targetHit: true, gateVk: NativeMethods.VK_RCONTROL));
    }

    [Fact]
    public void Right_shift_gate_works_independent_of_ctrl()
    {
        Assert.True(RightAltGate.ShouldSwitchToInteractive(
            gateKeyDown: true, ctrlHeld: false, targetHit: true, gateVk: NativeMethods.VK_RSHIFT));
    }
}
