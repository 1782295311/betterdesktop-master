// 原生链冒烟测试（natives/ 随 shell-status 输出分发，见 csproj None 项）。
// 只验证 DLL 加载、空/无效路径的降级行为；绝不触发真实按键或真实媒体命令
// （会切换用户输入法 / 控制用户正在播放的媒体），真实行为走 DoD 真机走查。
using BetterDesktop.Shell.Status.Native;
using Xunit;

namespace BetterDesktop.Shell.Status.Tests;

public class NativeSmokeTests
{
    [Fact]
    public void MediaCoreNative_IsAvailable_WhenNativesDistributed()
    {
        Assert.True(MediaCoreNative.IsAvailable);
    }

    [Fact]
    public void MediaCoreNative_GetSessions_DoesNotThrow_ReturnsList()
    {
        var sessions = MediaCoreNative.GetSessions();
        Assert.NotNull(sessions);
    }

    [Fact]
    public void MediaCoreNative_GetThumbnailBytes_InvalidId_ReturnsNull()
    {
        Assert.Null(MediaCoreNative.GetThumbnailBytes(123456789L));
    }

    [Fact]
    public void MediaCoreNative_SendControl_InvalidId_ReturnsFalse()
    {
        Assert.False(MediaCoreNative.SendControl(123456789L, 0));
    }

    [Fact]
    public void ImeCoreNative_IsAvailable_WhenNativesDistributed()
    {
        Assert.True(ImeCoreNative.IsAvailable);
    }
}
