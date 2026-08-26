// BetterDesktop.Kernel.Tests — ResourceGovernor 内存治理策略测试
// 验证 Warn/Isolate/Kill&Restart 三级升级与连续失败熔断。

using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Kernel.Core;
using Xunit;

namespace BetterDesktop.Kernel.Tests;

/// <summary>可控内存的受控对象，用于驱动治理器策略。</summary>
internal sealed class FakeSubject : IResourceSubject
{
    private Func<long> _sampler;
    public FakeSubject(string id, Func<long> sampler)
    {
        Id = id;
        _sampler = sampler;
    }

    public string Id { get; }
    public string? DisplayName => Id;
    public bool IsSelfHealable => true;
    public bool IsQuarantined { get; private set; }
    public int IsolateCalls { get; private set; }
    public int KillRestartCalls { get; private set; }
    public int PressureCalls { get; private set; }

    public void SetSampler(Func<long> sampler) => _sampler = sampler;

    public long SampleMemoryBytes() => _sampler();
    public Task IsolateAsync(CancellationToken cancellationToken = default)
    {
        IsolateCalls++;
        return Task.CompletedTask;
    }
    public Task KillAndRestartAsync(CancellationToken cancellationToken = default)
    {
        KillRestartCalls++;
        return Task.CompletedTask;
    }
    public void Quarantine() => IsQuarantined = true;
    public void RecordPressure() => PressureCalls++;
}

public class ResourceGovernorTests
{
    private static ResourceGovernor CreateGovernor(out List<string> logs, ResourceGovernorOptions? opts = null)
    {
        logs = new List<string>();
        var logger = new LambdaLogger(logs);
        return new ResourceGovernor(logger, opts ?? new ResourceGovernorOptions
        {
            SampleIntervalMs = Timeout.Infinite,
            WarnBytesOverride = 100,
            IsolateBytesOverride = 200,
            KillBytesOverride = 300,
            MaxKillBeforeQuarantine = 3,
            KillWindowMs = 60_000
        });
    }

    private sealed class LambdaLogger : IKernelLogger
    {
        private readonly List<string> _logs;
        public LambdaLogger(List<string> logs) => _logs = logs;
        public void Log(LogLevel level, string message) => _logs.Add($"{level}:{message}");
        public void Info(string message) => _logs.Add($"Info:{message}");
        public void Warn(string message) => _logs.Add($"Warn:{message}");
        public void Error(string message) => _logs.Add($"Error:{message}");
    }

    [Fact]
    public async Task Warn_Only_Logs_No_Action()
    {
        using var gov = CreateGovernor(out var logs);
        var s = new FakeSubject("p1", () => 150);
        gov.Register(s);
        await gov.SampleAsync();
        Assert.Equal(0, s.IsolateCalls);
        Assert.Equal(0, s.KillRestartCalls);
        Assert.Contains(logs, l => l.StartsWith("Warn:"));
    }

    [Fact]
    public async Task Isolate_On_Hard_Threshold()
    {
        using var gov = CreateGovernor(out _);
        var s = new FakeSubject("p1", () => 250);
        gov.Register(s);
        await gov.SampleAsync();
        Assert.Equal(1, s.IsolateCalls);
        Assert.Equal(0, s.KillRestartCalls);
    }

    [Fact]
    public async Task KillAndRestart_On_Fatal_Threshold()
    {
        using var gov = CreateGovernor(out _);
        var s = new FakeSubject("p1", () => 350);
        gov.Register(s);
        await gov.SampleAsync();
        Assert.Equal(1, s.KillRestartCalls);
        Assert.Equal(0, s.IsolateCalls);
    }

    [Fact]
    public async Task Quarantine_After_Consecutive_Kills()
    {
        using var gov = CreateGovernor(out _);
        var s = new FakeSubject("p1", () => 350);
        gov.Register(s);
        for (var i = 0; i < 3; i++)
        {
            await gov.SampleAsync();
        }
        Assert.True(s.IsQuarantined);
        // 熔断后不再动作
        var before = s.KillRestartCalls;
        await gov.SampleAsync();
        Assert.Equal(before, s.KillRestartCalls);
    }

    [Fact]
    public async Task Kill_Count_Resets_Outside_Window()
    {
        using var gov = CreateGovernor(out _, new ResourceGovernorOptions
        {
            SampleIntervalMs = Timeout.Infinite,
            WarnBytesOverride = 100,
            IsolateBytesOverride = 200,
            KillBytesOverride = 300,
            MaxKillBeforeQuarantine = 3,
            KillWindowMs = 0
        });
        var s = new FakeSubject("p1", () => 350);
        gov.Register(s);
        await gov.SampleAsync();
        await gov.SampleAsync();
        // 窗口为 0，第三次 kill 时前两次已过期，不应熔断
        Assert.False(s.IsQuarantined);
        Assert.Equal(2, s.KillRestartCalls);
    }

    [Fact]
    public async Task UpdateOptions_HotSwaps_Threshold_Without_Rebuild()
    {
        using var gov = CreateGovernor(out _, new ResourceGovernorOptions
        {
            SampleIntervalMs = Timeout.Infinite,
            WarnBytesOverride = 100,
            IsolateBytesOverride = 200,
            KillBytesOverride = 300
        });
        var s = new FakeSubject("p1", () => 250);
        gov.Register(s);

        // 初始阈值：250 介于 isolate(200) 与 kill(300) → 触发隔离
        await gov.SampleAsync();
        Assert.Equal(1, s.IsolateCalls);
        Assert.Equal(0, s.KillRestartCalls);

        // 热更新阈值：把 kill 降到 220，使 250 超致命阈值 → 下次采样走杀掉自重启
        gov.UpdateOptions(new ResourceGovernorOptions
        {
            SampleIntervalMs = Timeout.Infinite,
            WarnBytesOverride = 100,
            IsolateBytesOverride = 200,
            KillBytesOverride = 220
        });
        await gov.SampleAsync();
        Assert.Equal(1, s.IsolateCalls);
        Assert.Equal(1, s.KillRestartCalls);
    }

    [Fact]
    public async Task ProcessCritical_Triggers_Callback_After_Grace()
    {
        // 进程级持续超致命阈值：宽限期（ProcessCriticalGraceMs）内仅报警，
        // 超过宽限则触发 OnProcessCritical（宿主 C1 自重启兜底），不擅自逐个杀 in-process 插件。
        var fired = new List<string>();
        using var gov = CreateGovernor(out _, new ResourceGovernorOptions
        {
            SampleIntervalMs = Timeout.Infinite,
            WarnBytesOverride = 100,
            IsolateBytesOverride = 200,
            KillBytesOverride = 300,
            ProcessCriticalGraceMs = 0, // 0 = 首次超即触发（测试用，真实默认 30s）
            BaselineWarmupMs = 0        // 跳过稳定窗口，让进程级兜底检测立即生效
        });
        gov.OnProcessCritical = src => fired.Add(src);
        var s = new FakeSubject("p1", () => 350);
        gov.Register(s);
        await gov.SampleAsync();
        Assert.Single(fired);
        Assert.Equal("Governor.ProcessCritical", fired[0]);
    }

    [Fact]
    public async Task ProcessCritical_Not_Fired_Before_Grace()
    {
        // 宽限期 > 0 时，单次采样超阈值不应触发回调（仅报警）。
        var fired = new List<string>();
        using var gov = CreateGovernor(out _, new ResourceGovernorOptions
        {
            SampleIntervalMs = Timeout.Infinite,
            WarnBytesOverride = 100,
            IsolateBytesOverride = 200,
            KillBytesOverride = 300,
            ProcessCriticalGraceMs = 60_000,
            BaselineWarmupMs = 0        // 跳过稳定窗口，让进程级兜底检测立即生效
        });
        gov.OnProcessCritical = src => fired.Add(src);
        var s = new FakeSubject("p1", () => 350);
        gov.Register(s);
        await gov.SampleAsync();
        Assert.Empty(fired);
    }

    [Fact]
    public async Task Baseline_Warmup_Only_Collects_No_Action()
    {
        // 默认（无 override）进入稳定窗口：前 30s 仅采集基线，绝不触发动作（即便远超基线倍数也无所谓，因为窗口内不判定）。
        using var gov = CreateGovernor(out _, new ResourceGovernorOptions
        {
            SampleIntervalMs = Timeout.Infinite,
            BaselineWarmupMs = 30_000 // 默认；测试运行瞬间 < 30s，必在 warmup
        });
        var s = new FakeSubject("p1", () => 1000);
        gov.Register(s);
        await gov.SampleAsync();
        Assert.Equal(0, s.IsolateCalls);
        Assert.Equal(0, s.KillRestartCalls);
    }

    [Fact]
    public async Task Baseline_Relative_Triggers_After_Warmup()
    {
        // 模拟稳定窗口已过：通过 warmup=0 让首采样即进入判定，但首采样会补建基线并返回（不触发）。
        // 第二次采样采用 baseline=100，WarnMultiplier=1.5→150 / IsolateMultiplier=2.0→200 / KillMultiplier=3.0→300。
        using var gov = CreateGovernor(out _, new ResourceGovernorOptions
        {
            SampleIntervalMs = Timeout.Infinite,
            BaselineWarmupMs = 0,
            WarnMultiplier = 1.5,
            IsolateMultiplier = 2.0,
            KillMultiplier = 3.0
        });
        var s = new FakeSubject("p1", () => 100);
        gov.Register(s);
        await gov.SampleAsync();           // 补建 baseline=100，不触发
        Assert.Equal(0, s.IsolateCalls);
        Assert.Equal(0, s.KillRestartCalls);

        // Isolate：200 >= 100*2，触发隔离
        s.SetSampler(() => 200);
        await gov.SampleAsync();
        Assert.Equal(1, s.IsolateCalls);
        Assert.Equal(0, s.KillRestartCalls);

        // Kill：300 >= 100*3，触发杀掉自重启
        s.SetSampler(() => 300);
        await gov.SampleAsync();
        Assert.Equal(1, s.IsolateCalls);
        Assert.Equal(1, s.KillRestartCalls);
    }

    [Fact]
    public async Task Baseline_Absolute_Cap_Wins_When_Lower()
    {
        // 绝对值上限兜底：相对阈值（baseline*倍数）很高，但绝对上限（物理比例，含 256MB 安全下限）更低，
        // 二者取较小者 → 绝对上限生效。采样远超绝对上限即触发 Kill。
        using var gov = CreateGovernor(out _, new ResourceGovernorOptions
        {
            SampleIntervalMs = Timeout.Infinite,
            BaselineWarmupMs = 0,
            WarnRatio = 0.001,      // 绝对上限 ≈ 物理内存 * 0.001（远小于 baseline*倍数）
            IsolateRatio = 0.002,
            KillRatio = 0.003,
            WarnMultiplier = 100,   // 相对阈值本应很高（baseline*100）
            IsolateMultiplier = 200,
            KillMultiplier = 300
        });
        var s = new FakeSubject("p1", () => 100);
        gov.Register(s);
        await gov.SampleAsync(); // 补建 baseline=100
        // 相对阈值 baseline*300=30000，但绝对上限被 clamp 到安全下限 768MB；用 1000MB（>768MB）触发 Kill。
        s.SetSampler(() => 1000 * 1024 * 1024);
        await gov.SampleAsync();
        Assert.Equal(0, s.IsolateCalls);
        Assert.Equal(1, s.KillRestartCalls);
    }
}
