using BetterDesktop.Shell.Core.Native;
using BetterDesktop.Shell.Island.Services;
using Xunit;

namespace BetterDesktop.Shell.Island.Tests.Services;

/// <summary>
/// 抑制判定机检（计划 T3）：全屏/游戏/演示模式抑制，其余（含未知值）一律不抑制。
/// 保守方向很关键——错判为"抑制"会让用户的消息静默消失，错判为"不抑制"只是多弹一次。
/// </summary>
public class SuppressionPolicyTests
{
    [Theory]
    [InlineData(NativeMethods.QUNS_BUSY)]
    [InlineData(NativeMethods.QUNS_RUNNING_D3D_FULL_SCREEN)]
    [InlineData(NativeMethods.QUNS_PRESENTATION_MODE)]
    public void Fullscreen_like_states_suppress(int state) => Assert.True(SuppressionPolicy.ShouldSuppress(state));

    [Theory]
    [InlineData(NativeMethods.QUNS_ACCEPTS_NOTIFICATIONS)]
    [InlineData(NativeMethods.QUNS_QUIET_TIME)]
    [InlineData(NativeMethods.QUNS_NOT_PRESENT)]
    [InlineData(NativeMethods.QUNS_APP)]
    [InlineData(0)]
    [InlineData(99)]
    public void Unknown_or_normal_states_do_not_suppress(int state) => Assert.False(SuppressionPolicy.ShouldSuppress(state));

    [Fact]
    public void Accepts_notifications_is_five_not_one()
    {
        // 常量按 Windows SDK 取值固化：链路上"想当然写成 1"会让正常状态被判成静音
        Assert.Equal(5, NativeMethods.QUNS_ACCEPTS_NOTIFICATIONS);
        Assert.Equal(2, NativeMethods.QUNS_BUSY);
    }
}
