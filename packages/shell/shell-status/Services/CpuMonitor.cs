// BetterDesktop.Shell.Status — CPU 利用率监控（采集 + 语义）
// 输出端点主音量百分比 + 静音态：静音归为警告档次。
//
using BetterDesktop.Shell.Status.Contracts;
using BetterDesktop.Shell.Status.Native;

namespace BetterDesktop.Shell.Status.Services;

/// <summary>CPU 利用率监控实现。</summary>
public sealed class CpuMonitor : ICpuMonitor, IStatusChangeSource
{
    private readonly object _lock = new();

    public string MonitorId => "cpu";

    // CPU 占用变化较快，1s 兜底轮询足够平滑。
    public int PollIntervalMilliseconds => 1000;

    public event EventHandler<StatusSnapshot>? Changed;

    public void RaiseChanged(StatusSnapshot snapshot) => Changed?.Invoke(this, snapshot);

    public StatusSnapshot GetSnapshot()
    {
        var util = CpuCoreNative.ReadUtilization();
        if (!util.Ok)
        {
            return new StatusSnapshot
            {
                MonitorId = MonitorId,
                ShortText = "—",
                HumanText = "CPU 利用率读取失败",
                Severity = StatusSeverity.Info,
                Progress = -1,
                IconKey = "cpu-unknown"
            };
        }

        int pct = util.Utilization;
        var sev = pct switch
        {
            < 30 => StatusSeverity.Normal,
            < 70 => StatusSeverity.Info,
            < 90 => StatusSeverity.Warning,
            _ => StatusSeverity.Critical
        };

        // 温度信息：优先来自 CpuCoreNative.ReadTemperature，不可用时 detail 不显示温度
        var temp = CpuCoreNative.ReadTemperature();
        string? detail = null;
        if (temp.HasValue)
        {
            detail = $"使用率 {pct}% · 温度 {temp.Value}°C";
        }
        else
        {
            detail = $"使用率 {pct}%";
        }

        return new StatusSnapshot
        {
            MonitorId = MonitorId,
            ShortText = $"{pct}%",
            HumanText = detail,
            Severity = sev,
            Progress = pct,
            IconKey = sev switch
            {
                StatusSeverity.Critical => "cpu-critical",
                StatusSeverity.Warning => "cpu-warning",
                StatusSeverity.Info => "cpu-info",
                _ => "cpu"
            },
            Detail = detail
        };
    }
}