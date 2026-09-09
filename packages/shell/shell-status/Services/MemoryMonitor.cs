// BetterDesktop.Shell.Status — 内存监控（采集 + 语义）
// 把裸内存值归一为：占用百分比短文本 + 人话描述 + 严重级别（<70 正常 / 70-90 偏高 / ≥90 已满）。

using BetterDesktop.Shell.Status.Contracts;
using BetterDesktop.Shell.Status.Native;

namespace BetterDesktop.Shell.Status.Services;

/// <summary>物理内存占用监控实现。</summary>
public sealed class MemoryMonitor : IMemoryMonitor, IStatusChangeSource
{
    private readonly ISystemSource _source;

    public MemoryMonitor(ISystemSource source)
    {
        _source = source;
    }

    public string MonitorId => "memory";

    // 内存占用高频变化，1s 才有平滑感。
    public int PollIntervalMilliseconds => 1000;

    public event EventHandler<StatusSnapshot>? Changed;

    public void RaiseChanged(StatusSnapshot snapshot) => Changed?.Invoke(this, snapshot);

    public StatusSnapshot GetSnapshot()
    {
        var info = _source.ReadMemory();
        if (info is null)
        {
            // M10 降级：读取失败时给出可读的状态而非崩溃。
            return new StatusSnapshot
            {
                MonitorId = MonitorId,
                ShortText = "—",
                HumanText = "内存信息读取失败",
                Severity = StatusSeverity.Info,
                Progress = -1,
                IconKey = "memory-unknown"
            };
        }

        var usagePercent = info.Value.dwMemoryLoad;
        var total = HumanFormatter.FormatBytes(info.Value.ullTotalPhys);
        var avail = HumanFormatter.FormatBytes(info.Value.ullAvailPhys);
        var severity = usagePercent switch
        {
            < 70 => StatusSeverity.Normal,
            < 90 => StatusSeverity.Warning,
            _ => StatusSeverity.Critical
        };

        var hint = severity switch
        {
            StatusSeverity.Normal => string.Empty,
            StatusSeverity.Warning => "，建议关闭部分后台程序",
            _ => "，内存已近饱和，请及时释放"
        };

        return new StatusSnapshot
        {
            MonitorId = MonitorId,
            ShortText = $"{usagePercent}%",
            HumanText = $"内存占用 {usagePercent}%（共 {total}，可用 {avail}）{hint}",
            Severity = severity,
            Progress = usagePercent,
            IconKey = severity switch
            {
                StatusSeverity.Critical => "memory-critical",
                StatusSeverity.Warning => "memory-warning",
                _ => "memory"
            }
        };
    }
}
