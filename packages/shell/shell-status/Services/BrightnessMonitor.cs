// BetterDesktop.Shell.Status — 显示器亮度监控（采集 + 语义 + 写入）
// 统一封装 DisplayCore.dll（dxva2 + WMI 双路径）的亮度读写，作为"亮度"唯一数据源：
// 控制中心、主题等所有面板都绑定同一个 IBrightnessMonitor 实例，从而读/写/同步一致。
// 亮度由用户手动调整驱动，无系统事件，因此不挂到固定轮询列表（PollIntervalMilliseconds=0）。

using BetterDesktop.Shell.Status.Contracts;
using BetterDesktop.Shell.Status.Native;

namespace BetterDesktop.Shell.Status.Services;

/// <summary>显示器亮度监控实现。写入成功后引发 Changed，供已打开的面板同步滑块。</summary>
public sealed class BrightnessMonitor : IBrightnessMonitor, IStatusChangeSource
{
    public string MonitorId => "brightness";

    // 亮度无系统事件，手动调节驱动；不进行固定轮询。
    public int PollIntervalMilliseconds => 0;

    public event EventHandler<StatusSnapshot>? Changed;

    public void RaiseChanged(StatusSnapshot snapshot) => Changed?.Invoke(this, snapshot);

    public bool IsBrightnessSupported => TryGetRange(out _, out _, out _);

    public bool TryGetRange(out int min, out int current, out int max)
    {
        var snap = DisplayCoreNative.GetBrightness();
        if (!snap.Ok || snap.Max <= snap.Min)
        {
            min = 0; current = 0; max = 0;
            return false;
        }
        min = snap.Min; current = snap.Current; max = snap.Max;
        return true;
    }

    public void SetValue(int value)
    {
        if (!TryGetRange(out var mn, out _, out var mx))
        {
            return;
        }
        int v = Math.Clamp(value, mn, mx);
        DisplayCoreNative.SetBrightness(v);
        RaiseChanged(BuildSnapshot(mn, v, mx));
    }

    public StatusSnapshot GetSnapshot()
    {
        if (!TryGetRange(out var mn, out var cur, out var mx))
        {
            return new StatusSnapshot
            {
                MonitorId = MonitorId,
                ShortText = "—",
                HumanText = "亮度调节不可用",
                Severity = StatusSeverity.Info,
                Progress = -1,
                IconKey = "brightness-disabled"
            };
        }
        return BuildSnapshot(mn, cur, mx);
    }

    private static StatusSnapshot BuildSnapshot(int mn, int cur, int mx)
    {
        int span = Math.Max(1, mx - mn);
        int pct = (int)Math.Round((cur - mn) * 100.0 / span);
        pct = Math.Clamp(pct, 0, 100);
        return new StatusSnapshot
        {
            MonitorId = "brightness",
            ShortText = $"{pct}%",
            HumanText = $"亮度 {pct}%",
            Severity = StatusSeverity.Normal,
            Progress = pct,
            IconKey = "brightness"
        };
    }
}