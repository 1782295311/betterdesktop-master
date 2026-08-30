// BetterDesktop.Shell.Status — 麦克风监控（采集 + 语义）
// 输入端点静音态：静音归为警告档次（提示用户麦克风当前未启用）。

using BetterDesktop.Shell.Status.Contracts;

namespace BetterDesktop.Shell.Status.Services;

/// <summary>输入端点（麦克风）状态监控实现。</summary>
public sealed class MicrophoneMonitor : IMicrophoneMonitor, IStatusChangeSource
{
    private readonly ISystemSource _source;

    public MicrophoneMonitor(ISystemSource source)
    {
        _source = source;
    }

    public string MonitorId => "microphone";

    // 麦克风需要即时反馈：原生回调接入前用 500ms 兜底轮询。
    public int PollIntervalMilliseconds => 500;

    public event EventHandler<StatusSnapshot>? Changed;

    public void RaiseChanged(StatusSnapshot snapshot) => Changed?.Invoke(this, snapshot);

    public StatusSnapshot GetSnapshot()
    {
        var endpoint = _source.ReadMicrophoneEndpoint();
        if (!endpoint.Ok)
        {
            return new StatusSnapshot
            {
                MonitorId = MonitorId,
                ShortText = string.Empty,
                HumanText = "没有可用的麦克风设备",
                Severity = StatusSeverity.Info,
                Progress = -1,
                IconKey = "mic-none"
            };
        }

        if (endpoint.IsMuted)
        {
            return new StatusSnapshot
            {
                MonitorId = MonitorId,
                ShortText = "已关闭",
                HumanText = "麦克风当前为静音状态",
                Severity = StatusSeverity.Warning,
                Progress = -1,
                IconKey = "mic-muted"
            };
        }

        return new StatusSnapshot
        {
            MonitorId = MonitorId,
            ShortText = string.Empty,
            HumanText = "麦克风正常（可录音）",
            Severity = StatusSeverity.Normal,
            Progress = -1,
            IconKey = "mic"
        };
    }
}