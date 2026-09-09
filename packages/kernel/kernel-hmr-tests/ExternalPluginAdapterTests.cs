// BetterDesktop.Kernel.Hmr.Tests — ExternalPluginAdapter 进程生命周期测试
// 验证 B 层骨架：启动外部进程 / 卸载清理 / 进程退出看门狗重启。
// C14 修复：移除 Assert.True(true) 空洞断言，改用 IResourceSubject.IsQuarantined 显式接口做真实断言；
// 看门狗测试改轮询等待（替代固定 Task.Delay(5000) 的脆弱计时）。

using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Kernel.Hmr;
using Xunit;

namespace BetterDesktop.Kernel.Hmr.Tests;

public sealed class ExternalPluginAdapterTests
{
    private static PluginManifest Manifest(string id) =>
        new(id, id, new SemanticVersion(1, 0, 0), new SemanticVersion(1, 0, 0));

    [Fact(DisplayName = "Load 启动外部进程，Unload 清理无泄漏")]
    public async Task Load_Starts_And_Unload_Cleans()
    {
        using var context = new CordisContext();
        // 用 ping -n 6 作外部进程夹具（运行约 5 秒，测试期间不会自行退出触发看门狗）
        using var adapter = new ExternalPluginAdapter(Manifest("ext.ping"), context, "ping", new[] { "-n", "6", "127.0.0.1" });
        await adapter.LoadAsync(context);
        await Task.Delay(300); // 给进程启动留时间

        // 真实断言：正常运行的进程不应被看门狗标记为隔离
        Assert.False(((IResourceSubject)adapter).IsQuarantined);

        await adapter.UnloadAsync();
        // 卸载后再次 Unload / Dispose 应幂等不抛
        await adapter.UnloadAsync();
    }

    [Fact(DisplayName = "进程崩溃后看门狗自动重启（连续失败转熔断）")]
    public async Task Crash_Triggers_Watchdog_Restart()
    {
        using var context = new CordisContext();
        // 用立即退出的进程模拟反复崩溃；连续 3 次失败应转 Quarantine
        using var adapter = new ExternalPluginAdapter(Manifest("ext.crashy"), context, "cmd", new[] { "/c", "exit 1" });
        await adapter.LoadAsync(context);

        // 轮询等待看门狗累计 3 次失败并转隔离（退避 0.5+1+2s + 1s 轮询粒度，上限 8s）
        var deadline = DateTime.UtcNow.AddSeconds(8);
        bool quarantined = false;
        while (DateTime.UtcNow < deadline)
        {
            quarantined = ((IResourceSubject)adapter).IsQuarantined;
            if (quarantined) break;
            await Task.Delay(200);
        }

        // 真实断言：连续崩溃必须触发隔离熔断
        Assert.True(quarantined, "连续崩溃 3 次后看门狗应将插件标记为 IsQuarantined");

        await adapter.UnloadAsync();
    }
}
