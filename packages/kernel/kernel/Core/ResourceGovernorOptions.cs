// BetterDesktop.Kernel — ResourceGovernorOptions 内存治理阈值配置
// 头号优先级（本地大模型/3D 渲染）：阈值默认按物理内存比例自适应，大模型机器自动放大。
// 【用户决策·已确认】：阈值不开放给用户设置，内核取经验值即可，避免误设导致误杀。
// 双层阈值：①稳态基线相对涨幅（Baseline × 系数）；②物理内存比例绝对上限兜底（取较小者）。

namespace BetterDesktop.Kernel.Core;

/// <summary>内存治理阈值与策略配置。比例阈值在构造时按物理内存解析为绝对值。</summary>
public sealed class ResourceGovernorOptions
{
    /// <summary>采样周期（毫秒）。默认 5000，与跨语言计划看门狗一致。</summary>
    public int SampleIntervalMs { get; set; } = 5000;

    /// <summary>
    /// 进程级持续超致命阈值（KillBytes）的宽限时长（毫秒）。默认 30000（30s）。
    /// 达到宽限后治理器触发 OnProcessCritical（宿主 C1 自重启兜底），而非擅自逐个杀插件。
    /// 对应"先报警，超时我们自主决策"的运维策略。
    /// </summary>
    public int ProcessCriticalGraceMs { get; set; } = 30_000;

    /// <summary>软上限比例（占物理内存）。默认 0.25（25%）。作为相对阈值的绝对上限兜底。</summary>
    public double WarnRatio { get; set; } = 0.25;

    /// <summary>隔离阈值比例（占物理内存）。默认 0.50（50%）。作为相对阈值的绝对上限兜底。</summary>
    public double IsolateRatio { get; set; } = 0.50;

    /// <summary>致命阈值比例（占物理内存）。默认 0.75（75%）。作为相对阈值的绝对上限兜底。</summary>
    public double KillRatio { get; set; } = 0.75;

    /// <summary>绝对值覆盖：非 0 时优先级高于比例（设置界面手动指定）。</summary>
    public long WarnBytesOverride { get; set; }

    /// <summary>绝对值覆盖：非 0 时优先级高于比例。</summary>
    public long IsolateBytesOverride { get; set; }

    /// <summary>绝对值覆盖：非 0 时优先级高于比例。</summary>
    public long KillBytesOverride { get; set; }

    /// <summary>
    /// 稳态基线相对涨幅系数（Warn）。默认 1.5：采样值 ≥ Baseline×1.5 触发软上限告警。
    /// 基线为程序稳定后采集的常规内存；相对涨幅比固定物理比例更能反映"异常膨胀"。
    /// </summary>
    public double WarnMultiplier { get; set; } = 1.5;

    /// <summary>稳态基线相对涨幅系数（Isolate）。默认 2.0：≥ Baseline×2 主动隔离。</summary>
    public double IsolateMultiplier { get; set; } = 2.0;

    /// <summary>稳态基线相对涨幅系数（Kill）。默认 3.0：≥ Baseline×3 杀掉自重启。</summary>
    public double KillMultiplier { get; set; } = 3.0;

    /// <summary>
    /// 基线稳定窗口（毫秒）。程序启动后前 N 毫秒仅采集基线、不触发任何动作，
    /// 取窗口内的最大值作为稳态基线；窗口结束后进入预警阶段。默认 30000（30s）。
    /// </summary>
    public int BaselineWarmupMs { get; set; } = 30_000;

    /// <summary>同一对象连续触发 Kill 次数上限，达到则 Quarantine 熔断。</summary>
    public int MaxKillBeforeQuarantine { get; set; } = 3;

    /// <summary>连续 Kill 计数窗口（毫秒）：超过此间隔无新 Kill 则重置计数。</summary>
    public int KillWindowMs { get; set; } = 60_000;

    /// <summary>是否启用治理器（默认 true）。</summary>
    public bool Enabled { get; set; } = true;

    // 解析后的绝对值（构造后固定，避免每次采样重复 P/Invoke）
    internal long WarnBytes { get; private set; }
    internal long IsolateBytes { get; private set; }
    internal long KillBytes { get; private set; }

    /// <summary>按物理内存解析比例阈值为绝对值；若 override 非 0 用 override（跳过安全下限，手动值优先）。</summary>
    public void Resolve()
    {
        var warnOverride = WarnBytesOverride > 0;
        var isolateOverride = IsolateBytesOverride > 0;
        var killOverride = KillBytesOverride > 0;

        WarnBytes = warnOverride ? WarnBytesOverride : SystemMemoryProbe.BytesFromPercent(WarnRatio);
        IsolateBytes = isolateOverride ? IsolateBytesOverride : SystemMemoryProbe.BytesFromPercent(IsolateRatio);
        KillBytes = killOverride ? KillBytesOverride : SystemMemoryProbe.BytesFromPercent(KillRatio);

        // 安全下限：仅比例模式生效，防极小内存机器阈值过低抖动；手动 override 必须被尊重（设置界面覆盖优先）
        if (!warnOverride && WarnBytes < 256L * 1024 * 1024) WarnBytes = 256L * 1024 * 1024;
        if (!isolateOverride && IsolateBytes < 512L * 1024 * 1024) IsolateBytes = 512L * 1024 * 1024;
        if (!killOverride && KillBytes < 768L * 1024 * 1024) KillBytes = 768L * 1024 * 1024;
        // 单调保证：Isolate > Warn，Kill > Isolate
        if (IsolateBytes <= WarnBytes) IsolateBytes = WarnBytes + 256L * 1024 * 1024;
        if (KillBytes <= IsolateBytes) KillBytes = IsolateBytes + 256L * 1024 * 1024;
    }
}
