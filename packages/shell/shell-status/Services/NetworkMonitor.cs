// BetterDesktop.Shell.Status — 网络监控（采集 + 语义）
// 综合：是否有可用链路（有线/无线）+ 无线已连接数量。
// 未连接 → 警告；无线已连接 → 显示 Wi-Fi；仅有线 → 显示以太网。

using BetterDesktop.Shell.Status.Contracts;

namespace BetterDesktop.Shell.Status.Services;

/// <summary>网络连接状态监控实现。</summary>
public sealed class NetworkMonitor : INetworkMonitor, IStatusChangeSource
{
    private readonly ISystemSource _source;

    public NetworkMonitor(ISystemSource source)
    {
        _source = source;
    }

    public string MonitorId => "network";

    // 网络速率高频变化，1s 才有平滑感。
    public int PollIntervalMilliseconds => 1000;

    public event EventHandler<StatusSnapshot>? Changed;

    public void RaiseChanged(StatusSnapshot snapshot) => Changed?.Invoke(this, snapshot);

    public StatusSnapshot GetSnapshot()
    {
        if (!_source.HasActiveConnection())
        {
            return new StatusSnapshot
            {
                MonitorId = MonitorId,
                ShortText = "未连接",
                HumanText = "当前未连接到网络",
                Severity = StatusSeverity.Warning,
                Progress = -1,
                IconKey = "network-offline"
            };
        }

        // 有链路连通：进一步判断是否走无线（用于展示 Wi-Fi / 以太网图标）。
        var wirelessConnected = 0;
        try
        {
            foreach (var adapter in _source.ReadWirelessAdapters())
            {
                if (adapter.IsConnected)
                {
                    wirelessConnected++;
                }
            }
        }
        catch
        {
            wirelessConnected = 0; // 无线信息不可用则按普通已连接展示
        }

        var isWifi = wirelessConnected > 0;
        return new StatusSnapshot
        {
            MonitorId = MonitorId,
            ShortText = isWifi ? "Wi-Fi" : "有线",
            HumanText = isWifi
                ? $"已连接 Wi-Fi（{wirelessConnected} 个无线适配器在线）"
                : "已通过有线网络连接",
            Severity = StatusSeverity.Normal,
            Progress = -1,
            IconKey = isWifi ? "network-wifi" : "network-ethernet"
        };
    }
}