using BetterDesktop.Shell.IndexIpc;
using Xunit;

namespace BetterDesktop.Shell.IndexIpc.Tests;

/// <summary>
/// 索引引擎启动请求的边界测试。
/// </summary>
/// <remarks>
/// <para>
/// 【2026-09-20 重写】旧用例测的是"定位候选顺序"与"自己 Process.Start 拉起"——
/// 那两件事**都已经不是本类的职责**（exe 定位在 core 的 <c>process::resolve_exe</c>，
/// 拉起在 core 的 supervisor）。继续测它们等于给已删除的第二所有者写回归保护。
/// </para>
/// <para>
/// 【现在测什么】本类只剩一个契约：把"确保引擎在跑"翻译成**对 core 的一次 start 请求**，
/// 并且把结果如实转达（不吞、不反过来自己拉起）。故用注入缝替换掉真实发请求的那一步，
/// 断言"发了什么、怎么回报"，不依赖真机 core 与进程状态。
/// </para>
/// </remarks>
public sealed class IndexEngineLauncherTests
{
    /// <summary>组件名必须与 core/components.json 的 name 逐字一致（写错的表现是运行时 unknown-component）。</summary>
    [Fact]
    public void ComponentNameMatchesComponentsJson()
    {
        Assert.Equal("index-engine", IndexEngineLauncher.ComponentName);
    }

    [Fact]
    public void EnsureEngineAsksCoreForTheIndexEngineComponent()
    {
        string? asked = null;
        var logs = new List<string>();

        bool ok = IndexEngineLauncher.EnsureEngine(
            logs.Add,
            start: (component, log) => { asked = component; log?.Invoke("stub"); return true; });

        Assert.True(ok);
        Assert.Equal("index-engine", asked);
        Assert.Single(logs, "stub");
    }

    /// <summary>
    /// 【回归钉子】core 拒绝或不可达时**必须如实返回 false**，绝不"再自己拉起一次"兜底 ——
    /// 那会把刚收敛掉的第二个生命周期所有者请回来（本文件曾在 2026-09-20 之前就是那个所有者）。
    /// </summary>
    [Fact]
    public void EnsureEngineReportsFailureWithoutFallingBackToItsOwnLaunch()
    {
        var logs = new List<string>();

        bool ok = IndexEngineLauncher.EnsureEngine(
            logs.Add,
            start: (component, log) => { log?.Invoke($"{component}: core 拒绝（gate-closed）"); return false; });

        Assert.False(ok);
        Assert.Single(logs);
        Assert.Contains("gate-closed", logs[0]);
    }

    /// <summary>日志回调可以为 null（生产路径里调用方有不传的场景），不得抛。</summary>
    [Fact]
    public void EnsureEngineToleratesNullLogCallback()
    {
        bool ok = IndexEngineLauncher.EnsureEngine(null, start: (_, _) => true);
        Assert.True(ok);
    }
}
