// BetterDesktop.Shell.MenuBar.Tests — ProcessAppInfo（H1b 静态缓存治理）A/B 门槛测试。
// 设计方案：docs/design-proposals/2026-09-03-H1-静态缓存治理.md
// 治理前：_cache/_icons 两个字典只增不逐（死 PID 永久驻留；_icons 无 TTL，PID 复用后图标张冠李戴）。
// 治理后：单字典 + 图标随条目同 TTL + 机会式过期清扫 + StartTime 复用防护。

using System.Diagnostics;
using BetterDesktop.Shell.MenuBar.Windows;
using Xunit;

namespace BetterDesktop.Shell.MenuBar.Tests;

public class ProcessAppInfoTests
{
    private static readonly string StalePath = "C:\\__stale_reuse__.exe";

    private static void ResetCache()
    {
        ProcessAppInfo.Cache.Clear();
        ProcessAppInfo.SweepEvery = 512;
        ProcessAppInfo.SweepAfterMs = 600_000;
    }

    [Fact]
    public void SweepExpired_RemovesDeadEntries_KeepsFresh()
    {
        ResetCache();
        var now = Environment.TickCount64;

        for (var i = 0; i < 300; i++)
        {
            ProcessAppInfo.Cache[900_000 + i] = new ProcessAppInfo.Info
            {
                Path = string.Empty,
                Name = "dead",
                Stamp = now - 700_000, // 700s 前查询：已超 600s 清扫线
            };
        }
        for (var i = 0; i < 5; i++)
        {
            ProcessAppInfo.Cache[950_000 + i] = new ProcessAppInfo.Info
            {
                Path = "C:\\alive.exe",
                Name = "alive",
                Stamp = now - 1_000,
            };
        }

        ProcessAppInfo.SweepExpired(now);

        Assert.Equal(5, ProcessAppInfo.Count);
    }

    [Fact]
    public void SimulatedHour_BoundedGrowth()
    {
        // A/B 门槛：模拟 1 小时面板运行（1 次/秒，每秒一个新 PID——真实世界的极端化上界）。
        // 治理前：条目数单调增长到 3600；治理后：清扫线 600s → 稳态驻留 ≤ 活跃窗口（~600+）。
        ResetCache();
        ProcessAppInfo.SweepEvery = 512;
        ProcessAppInfo.SweepAfterMs = 600_000;

        const long hourMs = 3_600_000;
        var opsSinceSweep = 0;
        var nextPid = 1_000_000;
        var now = Environment.TickCount64;
        for (var t = 0; t < hourMs; t += 1_000)
        {
            // 模拟时钟：条目落在「过去 1 小时内」的对应时刻（Stamp 随 t 老化），清扫比对真实 now
            ProcessAppInfo.Cache[nextPid++] = new ProcessAppInfo.Info
            {
                Path = "C:\\churn.exe",
                Name = "churn",
                Stamp = now - (hourMs - t),
            };
            if (++opsSinceSweep >= ProcessAppInfo.SweepEvery)
            {
                opsSinceSweep = 0;
                ProcessAppInfo.SweepExpired(now);
            }
        }
        // 收尾再清扫一次（对应"下一拍 tick 顺带清扫"），稳态应只剩 600s 活跃窗口内的 600 条
        ProcessAppInfo.SweepExpired(now);

        Assert.True(ProcessAppInfo.Count <= 650,
            $"1 小时模拟后驻留 {ProcessAppInfo.Count} 条（治理前 = 3600 单调增长）——清扫机制失效");
    }

    [Fact]
    public void PidReuse_TtlExpiry_ReresolvesAllFields()
    {
        // PID 复用语义：TTL 过期后重查，旧条目的路径/名称/启动时间必须全部被真实进程覆盖，
        // 不复用任何旧值（治理前 _icons 永不重解析是本测试要钉死的回归）。
        ResetCache();
        var pid = Process.GetCurrentProcess().Id;

        ProcessAppInfo.Cache[pid] = new ProcessAppInfo.Info
        {
            Path = StalePath,
            Name = "stale",
            Stamp = Environment.TickCount64 - 120_000, // TTL(60s) 已过期
            StartTick = 42,
        };

        var path = ProcessAppInfo.GetPath(pid);
        var entry = ProcessAppInfo.GetInfo(pid);

        Assert.NotEqual(StalePath, path);           // 旧路径被真实路径覆盖
        Assert.False(string.IsNullOrEmpty(path));   // 测试进程路径可解析
        Assert.NotEqual(42, entry.StartTick);       // 启动时间被真实值覆盖
    }

    [Fact]
    public void GetIcon_NoIndependentUnboundedDict()
    {
        // 结构性回归钉：图标必须随 Info 条目生命周期，不允许再出现独立的无 TTL 图标字典。
        ResetCache();
        var pid = Process.GetCurrentProcess().Id;

        _ = ProcessAppInfo.GetIcon(pid);
        _ = ProcessAppInfo.GetIcon(pid);

        Assert.Equal(1, ProcessAppInfo.Count); // 两次查询仍只有一个条目（图标在条目内）
        var iconsDict = typeof(ProcessAppInfo).GetFields(
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            .FirstOrDefault(f => f.Name.Contains("icon", StringComparison.OrdinalIgnoreCase));
        Assert.True(iconsDict is null, "不允许恢复独立的 _icons 静态字典（H1 治理回归）");
    }
}
