// BetterDesktop.Shell.Dock.Tests — H2 自动隐藏轮询降频的 A/B 门槛测试。
// 设计方案：docs/design-proposals/2026-09-03-H2-Dock轮询降频.md

using System;
using BetterDesktop.Shell.Dock;
using Xunit;

namespace BetterDesktop.Shell.Dock.Tests;

public class DockTickPolicyTests
{
    [Fact]
    public void SteadyState_FarCursor_UsesSlowTier()
    {
        Assert.Equal(DockTickPolicy.SlowMs, DockTickPolicy.NextIntervalMs(statePending: false, cursorNearDock: false));
    }

    [Theory]
    [InlineData(false, true)]  // 光标在预唤出带/悬停
    [InlineData(true, true)]   // 隐藏 + 光标已靠近 → 快档（唤出及时性靠它）
    public void PendingOrNearCursor_UsesFastTier(bool statePending, bool cursorNearDock)
    {
        Assert.Equal(DockTickPolicy.FastMs, DockTickPolicy.NextIntervalMs(statePending, cursorNearDock));
    }

    [Fact]
    public void Hidden_FarCursor_UsesSlowTier()
    {
        // 【2026-09-18 电源管理修正】隐藏态**不再**升快档。
        // 原因：隐藏发生在系统空闲阈值之后（默认 20 分钟无输入）—— 也就是用户已经离开电脑、
        // 系统可能正在进入现代待机（S0ix）的时刻；此时升到 60ms 常驻轮询（每拍还有 P/Invoke
        // 乃至一次 COM 激活）与省电目标完全相反，是笔记本风扇长转的来源之一。
        Assert.Equal(
            DockTickPolicy.SlowMs,
            DockTickPolicy.NextIntervalMs(statePending: true, cursorNearDock: false));
    }

    [Fact]
    public void IdleHour_FarCursor_TickCountDropsByAtLeast76Percent()
    {
        // A/B 门槛：空闲 1 小时光标远离 → 治理前 60_000 次（固定 60ms），
        // 治理后 250ms 慢档 = 14_400 次（-76%）。UI 线程唤醒与每 tick P/Invoke 同比例下降。
        const long hourMs = 3_600_000;
        var oldTicks = hourMs / DockTickPolicy.FastMs;

        long newTicks = 0;
        long t = 0;
        while (t < hourMs)
        {
            var intervalMs = DockTickPolicy.NextIntervalMs(statePending: false, cursorNearDock: false);
            newTicks++;
            t += intervalMs;
        }

        Assert.Equal(hourMs / DockTickPolicy.SlowMs, newTicks);
        Assert.True(newTicks * 4 <= oldTicks, $"空闲 1h tick 数 {newTicks} 未达到 -76% 门槛（治理前 {oldTicks}）");
    }

    [Fact]
    public void SummonLatency_WorstCase_UnderPerceptibleThreshold()
    {
        // UX 契约：光标从远处进入贴底热区（慢档采样错过边沿的最坏情况）→
        // 一次慢档(250ms) 检出"进入预唤出带" → 升快档 → 一次快档(60ms) 内裁决显示 ≈ 310ms。
        var worstCaseMs = DockTickPolicy.SlowMs + DockTickPolicy.FastMs;
        Assert.True(worstCaseMs <= 400, $"唤出最坏延迟 {worstCaseMs}ms 超过 400ms 可感知门槛");
    }
}
