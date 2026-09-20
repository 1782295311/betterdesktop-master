// BetterDesktop.Shell.Status — 状态轮询器
// 每个监控项按各自声明的 PollIntervalMilliseconds 独立调度（互不拖累）：
//   1) 采集到语义快照，检测到变化时：
//      - 触发该 monitor 的 Changed 事件（供直接订阅方）；
//      - 经 IEventBus 广播 "status.changed"（强类型载荷，UI 层订阅）。
// UI 只订阅事件，不自行轮询。

using System.Linq;
using BetterDesktop.Kernel.Contracts;

namespace BetterDesktop.Shell.Status.Services;

using Contracts;

/// <summary>统一状态轮询器实现（见 IStatusPoller）。</summary>
public sealed class StatusPoller : IStatusPoller
{
    /// <summary>状态变化广播事件名（约定：域/动作）。</summary>
    public const string StatusChangedEvent = "status.changed";

    private readonly IReadOnlyList<IStatusMonitor> _monitors;
    private readonly IEventBus _events;
    private readonly object _gate = new();
    private readonly Dictionary<string, StatusSnapshot> _last = new();
    private readonly List<MonitorTimer> _timerSlots = new();
    private SynchronizationContext? _capturedSyncContext;
    private WinEventForegroundPump? _foregroundPump;
    private PowerBroadcastHook? _powerHook;
    private bool _started;

    // 前台切换事件拉取的防抖：连续快速切换（打字点击）时避免每下都全量拉取，合并到 ≤100ms 一次。
    private const long EventDrivenCooldownMs = 100;
    private long _lastEventDrivenPollStamp;

    // 电源广播事件拉取的防抖：避免瞬时多次广播触发重复采集，合并到 ≤500ms 一次。
    private const long PowerEventCooldownMs = 500;
    private long _lastPowerPollStamp;

    /// <summary>一个监控项的独立定时器槽：Running 用 Interlocked 防止回调重入。</summary>
    private sealed class MonitorTimer
    {
        public required IStatusMonitor Monitor { get; init; }
        public Timer? Timer { get; set; }
        public int Running;
    }

    public StatusPoller(IReadOnlyList<IStatusMonitor> monitors, IEventBus events)
    {
        _monitors = monitors;
        _events = events;
    }

    /// <inheritdoc />
    public void Start(int intervalMilliseconds = 2000)
    {
        // 兜底周期：仅用于未单独声明周期的监控项。
        var fallback = Math.Max(100, intervalMilliseconds);
        // 记录加载那一刻的同步上下文（UI 线程）用于事件回抛；null 则保持回调线程触发。
        _capturedSyncContext ??= SynchronizationContext.Current;

        lock (_gate)
        {
            if (_started) return;
            _started = true;
        }

        foreach (var monitor in _monitors)
        {
            var period = monitor.PollIntervalMilliseconds > 0
                ? monitor.PollIntervalMilliseconds
                : fallback;
            period = Math.Max(100, period); // 最低 100ms，避免空转
            var slot = new MonitorTimer { Monitor = monitor };
            slot.Timer = new Timer(_ => OnTick(slot), null, 0, period);
            lock (_gate)
            {
                _timerSlots.Add(slot);
            }
        }

        // 事件驱动：若存在前台事件驱动的监控项（如 IME），启动后台 WinEvent 泵，前台切换时即时拉取。
        if (_monitors.Any(m => m is IEventDrivenMonitor))
        {
            _foregroundPump = new WinEventForegroundPump(PollEventDrivenMonitors);
            _foregroundPump.Start();
        }

        // 事件驱动：若存在电池监控项，启动电源广播钩子，电池状态（插拔电源/电量阈值变化）即时拉取。
        if (_monitors.Any(m => m.MonitorId == "battery"))
        {
            _powerHook = new PowerBroadcastHook(PollPowerMonitors);
            _powerHook.Start();
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        lock (_gate)
        {
            foreach (var slot in _timerSlots)
            {
                slot.Timer?.Dispose();
                slot.Timer = null;
            }
            _timerSlots.Clear();
            _started = false;
        }
        _foregroundPump?.Stop();
        _foregroundPump = null;
        _powerHook?.Dispose();
        _powerHook = null;
    }

    /// <inheritdoc />
    public void PollNow()
    {
        foreach (var monitor in _monitors)
        {
            PollOne(monitor);
        }
    }

    public void Dispose() => Stop();

    /// <summary>
    /// 前台 WinEvent 泵的拉取回调（泵线程上下文）：对事件驱动监控项各采集一次。
    /// 带防抖——连续前台切换时合并到 ≤100ms 一次，避免每下点击都全量拉取。
    /// </summary>
    private void PollEventDrivenMonitors()
    {
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastEventDrivenPollStamp) < EventDrivenCooldownMs) return;
        Interlocked.Exchange(ref _lastEventDrivenPollStamp, now);

        foreach (var monitor in _monitors)
        {
            if (monitor is IEventDrivenMonitor)
            {
                PollOne(monitor);
            }
        }
    }

    /// <summary>
    /// 电源广播钩子的拉取回调（钩子线程上下文）：仅对电池监控项采集一次，其余监控项不受干扰。
    /// 带防抖——瞬时多次广播（充电临界抖动）合并到 ≤500ms 一次。
    /// </summary>
    private void PollPowerMonitors()
    {
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastPowerPollStamp) < PowerEventCooldownMs) return;
        Interlocked.Exchange(ref _lastPowerPollStamp, now);

        foreach (var monitor in _monitors)
        {
            if (monitor.MonitorId == "battery")
            {
                PollOne(monitor);
            }
        }
    }

    private void OnTick(MonitorTimer slot)
    {
        // 【2026-09-18 电源管理】挂起中 / 恢复冷却期跳过本轮采集。
        // 本采集常驻且频率高（音量 / 麦克风 / 输入法 500ms，CPU / 内存 / 网络 1s，合计约 9 次/秒原生调用：
        // CoreAudio 会话枚举、键盘布局注册表 + TSF 等）。现代待机（S0ix）下 CPU 仍会被这些定时器
        // 反复唤醒，是"合盖后风扇仍转"的来源之一；恢复瞬间设备/COM 正在重新枚举，采到的也多为无效值。
        // 冷却期（默认 5s）结束后自动恢复采集。
        if (BetterDesktop.Kernel.Core.SystemPowerMonitor.Current.ShouldPauseHighFrequencyWork)
        {
            return;
        }

        // 一次采集未结束前不再重入，避免慢采集在短周期下叠加。
        if (Interlocked.CompareExchange(ref slot.Running, 1, 0) != 0)
        {
            return;
        }
        try
        {
            PollOne(slot.Monitor);
        }
        finally
        {
            Interlocked.Exchange(ref slot.Running, 0);
        }
    }

    private void PollOne(IStatusMonitor monitor)
    {
        StatusSnapshot snapshot;
        try
        {
            snapshot = monitor.GetSnapshot();
        }
        catch (Exception ex)
        {
            // M10：单个采集项异常不得影响其余项；跳过本轮。
            _ = ex;
            return;
        }

        var changed = false;
        StatusSnapshot? previous = null;
        lock (_gate)
        {
            _last.TryGetValue(monitor.MonitorId, out previous);
            if (!SnapshotsEqual(previous, snapshot))
            {
                _last[monitor.MonitorId] = snapshot;
                changed = true;
            }
        }

        if (!changed)
        {
            return;
        }

        Publish(monitor.MonitorId, snapshot);
    }

    private void Publish(string monitorId, StatusSnapshot snapshot)
    {
        // 1) 直接订阅方（该 monitor 的 Changed 事件）。
        if (_capturedSyncContext is { } sc)
        {
            sc.Post(_ =>
            {
                RaiseChanged(monitorId, snapshot);
                _ = _events.EmitAsync(StatusChangedEvent, snapshot);
            }, null);
        }
        else
        {
            RaiseChanged(monitorId, snapshot);
            _ = _events.EmitAsync(StatusChangedEvent, snapshot); // 异步广播，不阻塞轮询
        }
    }

    private void RaiseChanged(string monitorId, StatusSnapshot snapshot)
    {
        // 每个 monitor 通过同程序集内部接口暴露变更触发点，poller 泛化调用，无需类型 switch。
        foreach (var monitor in _monitors)
        {
            if (monitor.MonitorId == monitorId && monitor is IStatusChangeSource source)
            {
                source.RaiseChanged(snapshot);
            }
        }
    }

    private static bool SnapshotsEqual(StatusSnapshot? a, StatusSnapshot? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }
        if (a is null || b is null)
        {
            return false;
        }
        return a.ShortText == b.ShortText
            && a.HumanText == b.HumanText
            && a.Severity == b.Severity
            && a.Progress.Equals(b.Progress)
            && a.IconKey == b.IconKey
            && a.Detail == b.Detail;
    }
}
