// 崩溃计数器单测：归因源只有一条（进程外 broker 的非 0 退出码），故直接用 RecordExternalCrash 模拟。
// 注意：HandlerCrashGuard 持有进程级静态状态（状态目录可被测试重定向）——凡会触碰它的测试类必须同属
// 一个 xUnit Collection（见类特性），否则并行执行时静态目录被改会出现假失败。

using System.IO;
using BetterDesktop.Shell.ContextMenu.Tests.Support;
using BetterDesktop.Shell.ContextMenus.Services;
using Xunit;

namespace BetterDesktop.Shell.ContextMenu.Tests;

[Collection("shellmenu-static-seams")]
public class HandlerCrashGuardTests : IDisposable
{
    private const string ClsidA = "{11111111-1111-1111-1111-111111111111}";
    private const string ClsidB = "{22222222-2222-2222-2222-222222222222}";

    private readonly TempFileScope _temp = new();

    public HandlerCrashGuardTests()
        => HandlerCrashGuard.OverrideStoreRoot(Path.Combine(_temp.CreateDirectory(), "guard"));

    public void Dispose()
    {
        HandlerCrashGuard.OverrideStoreRoot(null);
        _temp.Dispose();
    }

    [Fact]
    public void ThirdConsecutiveCrashReachesThreshold()
    {
        Assert.Equal(1, HandlerCrashGuard.RecordExternalCrash(ClsidA, "毒源扩展").Consecutive);
        Assert.Equal(2, HandlerCrashGuard.RecordExternalCrash(ClsidA, "毒源扩展").Consecutive);

        var third = HandlerCrashGuard.RecordExternalCrash(ClsidA, "毒源扩展");

        Assert.Equal(3, third.Consecutive);
        Assert.True(third.ReachedThreshold);
        Assert.Equal("毒源扩展", third.DisplayName);

        var pending = HandlerCrashGuard.Pending();
        Assert.Single(pending);
        Assert.Equal(HandlerCrashGuard.Normalize(ClsidA), pending[0].Clsid);
    }

    [Fact]
    public void SuccessResetsConsecutiveCount()
    {
        HandlerCrashGuard.RecordExternalCrash(ClsidA, null);
        HandlerCrashGuard.RecordExternalCrash(ClsidA, null);
        Assert.Equal(2, HandlerCrashGuard.ConsecutiveCount(ClsidA));

        HandlerCrashGuard.NoteSuccess(ClsidA); // 成功一次 = 洗清嫌疑（只有"连续"才熔断）

        Assert.Equal(0, HandlerCrashGuard.ConsecutiveCount(ClsidA));
        Assert.Equal(1, HandlerCrashGuard.RecordExternalCrash(ClsidA, null).Consecutive);
    }

    [Fact]
    public void DifferentClsidsAreCountedSeparately()
    {
        HandlerCrashGuard.RecordExternalCrash(ClsidA, null);
        HandlerCrashGuard.RecordExternalCrash(ClsidB, null);
        HandlerCrashGuard.RecordExternalCrash(ClsidA, null);

        Assert.Empty(HandlerCrashGuard.Pending()); // A=2、B=1，均未达阈值

        Assert.True(HandlerCrashGuard.RecordExternalCrash(ClsidA, null).ReachedThreshold);

        var pending = HandlerCrashGuard.Pending();
        Assert.Single(pending);
        Assert.Equal(HandlerCrashGuard.Normalize(ClsidA), pending[0].Clsid);
    }

    [Fact]
    public void AcknowledgeClearsSuspectAfterAutoDisable()
    {
        HandlerCrashGuard.RecordExternalCrash(ClsidA, null);
        HandlerCrashGuard.RecordExternalCrash(ClsidA, null);
        Assert.True(HandlerCrashGuard.RecordExternalCrash(ClsidA, null).ReachedThreshold);

        HandlerCrashGuard.Acknowledge(ClsidA);
        Assert.Empty(HandlerCrashGuard.Pending());

        // 停用后清零：用户手动恢复时不会被立刻再停用
        Assert.Equal(1, HandlerCrashGuard.RecordExternalCrash(ClsidA, null).Consecutive);
    }

    [Fact]
    public void NormalizeUnifiesClsidFormats()
    {
        Assert.Equal(
            HandlerCrashGuard.Normalize("{11111111-1111-1111-1111-111111111111}"),
            HandlerCrashGuard.Normalize("11111111-1111-1111-1111-111111111111"));
        Assert.Equal(HandlerCrashGuard.Normalize(ClsidA), HandlerCrashGuard.Normalize(ClsidA.ToUpperInvariant()));
        Assert.Equal(string.Empty, HandlerCrashGuard.Normalize(null));
    }
}
