// BetterDesktop.Shell.Status — 音量监控（采集 + 语义）
// 输出端点主音量百分比 + 静音态：静音归为警告档次。

using BetterDesktop.Shell.Status.Contracts;

namespace BetterDesktop.Shell.Status.Services;

/// <summary>输出端点（扬声器/耳机）音量监控实现。</summary>
public sealed class VolumeMonitor : IVolumeMonitor, IStatusChangeSource
{
    private readonly ISystemSource _source;

    public VolumeMonitor(ISystemSource source)
    {
        _source = source;
    }

    public string MonitorId => "volume";

    // 音量需要即时反馈：原生回调接入前用 500ms 兜底轮询，回调接入后改为事件驱动。
    public int PollIntervalMilliseconds => 500;

    public event EventHandler<StatusSnapshot>? Changed;

    public void RaiseChanged(StatusSnapshot snapshot) => Changed?.Invoke(this, snapshot);

    public StatusSnapshot GetSnapshot()
    {
        var endpoint = _source.ReadVolumeEndpoint();
        if (!endpoint.Ok)
        {
            return new StatusSnapshot
            {
                MonitorId = MonitorId,
                ShortText = string.Empty,
                HumanText = "没有可用的音频输出设备",
                Severity = StatusSeverity.Info,
                Progress = -1,
                IconKey = "volume-none"
            };
        }

        if (endpoint.IsMuted)
        {
            return new StatusSnapshot
            {
                MonitorId = MonitorId,
                ShortText = "已静音",
                HumanText = "音量已静音",
                Severity = StatusSeverity.Warning,
                Progress = 0,
                IconKey = "volume-muted"
            };
        }

        var percent = (int)Math.Round(endpoint.Volume * 100, MidpointRounding.AwayFromZero);
        return new StatusSnapshot
        {
            MonitorId = MonitorId,
            ShortText = $"{percent}%",
            HumanText = $"音量 {percent}%",
            Severity = StatusSeverity.Normal,
            Progress = percent,
            IconKey = percent switch
            {
                <= 0 => "volume-muted",
                < 40 => "volume-low",
                < 75 => "volume-mid",
                _ => "volume-high"
            }
        };
    }
}