// BetterDesktop.Kernel — ResourceGovernor 内存治理器
// 周期采样受控对象内存，按 Warn → Isolate → Kill&Restart 升级，
// 连续内存崩溃达上限则 Quarantine 熔断（停止自动动作，等人工干预）。

using System.Threading;
using BetterDesktop.Kernel.Contracts;

namespace BetterDesktop.Kernel.Core;

/// <summary>
/// 内核级内存治理器：主动隔离内存出问题的功能，必要时杀掉自重启。
/// 零破坏现有插件契约——仅观察 IResourceSubject，不耦合具体插件类型。
/// </summary>
public sealed class ResourceGovernor : IDisposable
{
    private readonly IKernelLogger _logger;
    private ResourceGovernorOptions _options;
    private readonly List<IResourceSubject> _subjects = new();
    private readonly Dictionary<string, int> _killCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastKillAt = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly Timer _timer;
    private int _disposed;

    /// <summary>
    /// 进程级持续超致命阈值的兜底回调。治理器不擅自逐个杀 in-process 插件
    /// （共享 GC 堆无法精准归因），而是当进程整体内存持续超 KillBytes 超过宽限期后，
    /// 触发此回调由宿主执行 C1 自重启（"超时我们自主决策"）。宿主在装配时挂接。
    /// </summary>
    public Action<string>? OnProcessCritical { get; set; }

    // 进程级超致命阈值的首次观测时刻（用于宽限计时）；null 表示当前未超。
    private DateTimeOffset? _firstCriticalUtc;

    // 每个受控对象的稳态基线（稳定窗口内的最大采样值）；窗口期内只记录不触发。
    private readonly Dictionary<string, long> _baselines = new(StringComparer.Ordinal);

    // 治理器启动时刻（稳定窗口起点）。
    private readonly DateTimeOffset _startUtc = DateTimeOffset.UtcNow;

    /// <summary>构造：按 options 启动周期采样（options 未解析时自动按物理内存解析比例阈值）。</summary>
    public ResourceGovernor(IKernelLogger logger, ResourceGovernorOptions? options = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? new ResourceGovernorOptions();
        _options.Resolve();
        if (_options.Enabled)
        {
            _timer = new Timer(_ => _ = SampleAsync(), null, _options.SampleIntervalMs, _options.SampleIntervalMs);
        }
        else
        {
            _timer = new Timer(_ => { }, null, Timeout.Infinite, Timeout.Infinite);
        }
    }

    /// <summary>注册受控对象（HMR 插件 / 外部进程适配器）。</summary>
    public void Register(IResourceSubject subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        lock (_gate)
        {
            if (_subjects.FindIndex(s => s.Id == subject.Id) >= 0)
            {
                return;
            }
            _subjects.Add(subject);
        }
    }

    /// <summary>
    /// 热更新阈值配置（设置界面改值后调用）。仅替换阈值快照并重解析，
    /// 不重建 Timer、不中断正在进行的采样——治理器生命周期保持不变。
    /// </summary>
    public void UpdateOptions(ResourceGovernorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Resolve();
        lock (_gate)
        {
            _options = options;
        }
    }

    /// <summary>注销受控对象（对象自身已卸载时调用）。</summary>
    public void Unregister(string id)
    {
        lock (_gate)
        {
            _subjects.RemoveAll(s => s.Id == id);
            _killCounts.Remove(id);
            _lastKillAt.Remove(id);
            _baselines.Remove(id);
        }
    }

    /// <summary>
    /// 重置稳态基线。插件重载/重启后调用，使其在下一个稳定窗口重新采集基线，
    /// 避免用旧基线（重启前的内存）误判新进程。
    /// </summary>
    public void ResetBaseline(string id)
    {
        lock (_gate)
        {
            _baselines.Remove(id);
        }
    }

    /// <summary>手动触发一次采样（测试 / 诊断用）。</summary>
    public async Task SampleAsync()
    {
        if (Volatile.Read(ref _disposed) != 0 || !_options.Enabled)
        {
            return;
        }
        List<IResourceSubject> snapshot;
        lock (_gate)
        {
            snapshot = _subjects.ToList();
        }
        // 先逐对象评估（含单插件报警/自愈），再统一做进程级持续超阈值检测。
        await Task.WhenAll(snapshot.Select(EvaluateAsync)).ConfigureAwait(false);
        DetectProcessCritical(snapshot);
        return;
    }

    /// <summary>
    /// 进程级兜底检测：in-process 插件共享 GC 堆，任一对象采样值即进程整体内存。
    /// 若整体持续超致命阈值超过宽限期，触发 OnProcessCritical（宿主 C1 自重启），
    /// 而非治理器擅自逐个杀插件。首次观测记时，回落则清零。
    /// </summary>
    private void DetectProcessCritical(IReadOnlyList<IResourceSubject> snapshot)
    {
        if (OnProcessCritical is null)
        {
            return;
        }
        // 稳定窗口内不评估进程级兜底：启动早期内存波动属常态，不应把常态当异常触发复位。
        var now = DateTimeOffset.UtcNow;
        if ((now - _startUtc).TotalMilliseconds < _options.BaselineWarmupMs)
        {
            return;
        }
        var critical = snapshot.Any(s => !s.IsQuarantined && s.SampleMemoryBytes() >= _options.KillBytes);
        if (critical)
        {
            // 记录首次观测时刻（用于宽限计时）；GraceMs=0 时首次即满足阈值直接触发。
            if (_firstCriticalUtc is null)
            {
                _firstCriticalUtc = now;
            }
            if ((now - _firstCriticalUtc.Value).TotalMilliseconds >= _options.ProcessCriticalGraceMs)
            {
                _logger.Error($"[Governor] 进程级内存持续超致命阈值超 {_options.ProcessCriticalGraceMs / 1000}s，触发宿主自重启兜底（C1）");
                var cb = OnProcessCritical;
                cb("Governor.ProcessCritical");
                _firstCriticalUtc = null;
            }
            else
            {
                _logger.Error($"[Governor] 进程级内存超致命阈值（{_options.KillBytes / 1024 / 1024}MB），宽限计时中（{_options.ProcessCriticalGraceMs / 1000}s，仅报警，不擅自杀插件）");
            }
        }
        else
        {
            _firstCriticalUtc = null;
        }
    }

    /// <summary>
    /// 计算受控对象的相对阈值：基线 × 涨幅系数 与 绝对上限 取较小者。
    /// 基线未建立（仍在稳定窗口）时返回 ready=false，表示该对象暂不参与预警。
    /// </summary>
    private (long warn, long isolate, long kill, bool ready) ResolveThresholds(string id)
    {
        long baseline;
        lock (_gate)
        {
            if (!_baselines.TryGetValue(id, out baseline))
            {
                return (0, 0, 0, false);
            }
        }
        if (baseline <= 0)
        {
            return (0, 0, 0, false);
        }
        var warn = (long)(baseline * _options.WarnMultiplier);
        var isolate = (long)(baseline * _options.IsolateMultiplier);
        var kill = (long)(baseline * _options.KillMultiplier);
        // 绝对上限兜底：防止基线本身就极高时相对阈值失控，或基线极低时永远不触发。
        warn = Math.Min(warn, _options.WarnBytes);
        isolate = Math.Min(isolate, _options.IsolateBytes);
        kill = Math.Min(kill, _options.KillBytes);
        // 单调保证（相对系数已保证 isolate>warn、kill>isolate；与绝对上限取 min 后可能塌缩，补强）
        if (isolate <= warn) isolate = warn + 1;
        if (kill <= isolate) kill = isolate + 1;
        return (warn, isolate, kill, true);
    }

    private async Task EvaluateAsync(IResourceSubject subject)
    {
        if (subject.IsQuarantined)
        {
            return;
        }
        long bytes;
        try
        {
            bytes = subject.SampleMemoryBytes();
        }
        catch (Exception ex)
        {
            _logger.Warn($"[Governor] 采样 {subject.Id} 内存失败（跳过）：{ex.Message}");
            return;
        }
        if (bytes <= 0)
        {
            return;
        }

        // 手动指定绝对值阈值（override）时，跳过基线、直接走绝对阈值（旧语义，兼容设置界面手动值）。
        bool useAbsolute = _options.WarnBytesOverride > 0 || _options.IsolateBytesOverride > 0 || _options.KillBytesOverride > 0;
        if (useAbsolute)
        {
            await EvaluateAbsolute(subject, bytes);
            return;
        }

        // 稳定窗口内仅采集基线（取最大值），不触发任何动作。
        var now = DateTimeOffset.UtcNow;
        var inWarmup = (now - _startUtc).TotalMilliseconds < _options.BaselineWarmupMs;
        if (inWarmup)
        {
            lock (_gate)
            {
                if (!_baselines.TryGetValue(subject.Id, out var cur) || bytes > cur)
                {
                    _baselines[subject.Id] = bytes;
                }
            }
            return;
        }

        // 窗口结束后若尚未建立基线（例如窗口内从未采样成功），用当前值补建，避免永不触发。
        var (warn, isolate, kill, ready)  = ResolveThresholds(subject.Id);
        if (!ready)
        {
            lock (_gate)
            {
                _baselines[subject.Id] = bytes;
            }
            return;
        }

        await EvaluateRelative(subject, bytes, warn, isolate, kill);
    }

    /// <summary>手动绝对值语义：直接按物理内存比例解析出的绝对阈值判定（兼容 override 与旧行为）。</summary>
    private async Task EvaluateAbsolute(IResourceSubject subject, long bytes)
    {
        if (bytes >= _options.KillBytes)
        {
            subject.RecordPressure();
            if (subject.IsSelfHealable)
            {
                await EscalateToKillAsync(subject, bytes);
            }
            else
            {
                _logger.Error($"[Governor] 插件 {subject.Id} 内存 {bytes / 1024 / 1024}MB 超致命阈值（不可自愈，仅告警；进程级持续超时将触发 C1 兜底）");
            }
        }
        else if (bytes >= _options.IsolateBytes)
        {
            subject.RecordPressure();
            if (subject.IsSelfHealable)
            {
                _logger.Error($"[Governor] 插件 {subject.Id} 内存 {bytes / 1024 / 1024}MB 超隔离阈值，主动隔离");
                try
                {
                    await subject.IsolateAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.Error($"[Governor] 隔离 {subject.Id} 失败：{ex.Message}");
                }
            }
            else
            {
                _logger.Warn($"[Governor] 插件 {subject.Id} 内存 {bytes / 1024 / 1024}MB 超隔离阈值（不可自愈，仅告警）");
            }
        }
        else if (bytes >= _options.WarnBytes)
        {
            subject.RecordPressure();
            _logger.Warn($"[Governor] 插件 {subject.Id} 内存 {bytes / 1024 / 1024}MB 偏高（软上限 {_options.WarnBytes / 1024 / 1024}MB）");
        }
    }

    /// < 摘要>相对基线语义：按基线 × 涨幅系数（同时受绝对上限兜底）判定。</summary>
    private async Task EvaluateRelative(IResourceSubject subject, long bytes, long warn, long isolate, long kill)
    {
        if (bytes >= kill)
        {
            subject.RecordPressure();
            if (subject.IsSelfHealable)
            {
                await EscalateToKillAsync(subject, bytes);
            }
            else
            {
                _logger.Error($"[Governor] 插件 {subject.Id} 内存 {bytes / 1024 / 1024}MB 超致命阈值（基线 {_baselines[subject.Id] / 1024 / 1024}MB，不可自愈，仅告警；进程级持续超时将触发 C1 兜底）");
            }
        }
        else if (bytes >= isolate)
        {
            subject.RecordPressure();
            if (subject.IsSelfHealable)
            {
                _logger.Error($"[Governor] 插件 {subject.Id} 内存 {bytes / 1024 / 1024}MB（基线 {_baselines[subject.Id] / 1024 / 1024}MB）超隔离阈值，主动隔离");
                try
                {
                    await subject.IsolateAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.Error($"[Governor] 隔离 {subject.Id} 失败：{ex.Message}");
                }
            }
            else
            {
                _logger.Warn($"[Governor] 插件 {subject.Id} 内存 {bytes / 1024 / 1024}MB（基线 {_baselines[subject.Id] / 1024 / 1024}MB）超隔离阈值（不可自愈，仅告警）");
            }
        }
        else if (bytes >= warn)
        {
            subject.RecordPressure();
            _logger.Warn($"[Governor] 插件 {subject.Id} 内存 {bytes / 1024 / 1024}MB（基线 {_baselines[subject.Id] / 1024 / 1024}MB）偏高，相对涨幅预警");
        }
    }

    private async Task EscalateToKillAsync(IResourceSubject subject, long bytes)
    {
        _logger.Error($"[Governor] 插件 {subject.Id} 内存 {bytes / 1024 / 1024}MB 超致命阈值，杀掉并自重启");
        var now = DateTimeOffset.UtcNow;
        bool quarantine;
        lock (_gate)
        {
            if (_lastKillAt.TryGetValue(subject.Id, out var last) && (now - last).TotalMilliseconds > _options.KillWindowMs)
            {
                _killCounts[subject.Id] = 0;
            }
            var count = (_killCounts.TryGetValue(subject.Id, out var c) ? c : 0) + 1;
            _killCounts[subject.Id] = count;
            _lastKillAt[subject.Id] = now;
            quarantine = count >= _options.MaxKillBeforeQuarantine;
        }
        if (quarantine)
        {
            _logger.Error($"[Governor] 插件 {subject.Id} 连续 {_options.MaxKillBeforeQuarantine} 次内存崩溃，熔断隔离（停止自动动作，需人工干预）");
            subject.Quarantine();
            return;
        }
        try
        {
            await subject.KillAndRestartAsync().ConfigureAwait(false);
            // 重启后应重新建立基线（内存回到初始值），避免用旧基线误判新进程。
            ResetBaseline(subject.Id);
        }
        catch (Exception ex)
        {
            _logger.Error($"[Governor] 杀掉自重启 {subject.Id} 失败：{ex.Message}");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _timer.Dispose();
    }
}
