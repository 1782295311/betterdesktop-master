// BetterDesktop.Shell.Status — 电池/电源监控（采集 + 语义状态机）
// 状态机：无电池 / 充电中 / 已接电源（满或未知）/ 使用电池（剩余比例 + 估算时长）/ 读取失败。
// 电量阈值：<20% 警告，<10% 严重。

using BetterDesktop.Shell.Status.Contracts;
using BetterDesktop.Shell.Status.Native;

namespace BetterDesktop.Shell.Status.Services;

/// <summary>电源 / 电池状态监控实现。</summary>
public sealed class BatteryMonitor : IBatteryMonitor, IStatusChangeSource
{
    private readonly ISystemSource _source;

    // 速率平滑滤波（最近 3 次移动平均），避免充电/放电功率跳变导致剩余时间抖动。
    private readonly Queue<int> _rateHistory = new();
    private const int RateSmoothWindow = 3;

    public BatteryMonitor(ISystemSource source)
    {
        _source = source;
    }

    public string MonitorId => "battery";

    // 电池/电源变化较慢，3s 采一次足够且省资源。
    public int PollIntervalMilliseconds => 3000;

    public event EventHandler<StatusSnapshot>? Changed;

    public void RaiseChanged(StatusSnapshot snapshot) => Changed?.Invoke(this, snapshot);

    public StatusSnapshot GetSnapshot()
    {
        // 优先用 IOCTL 直读的详细信息（健康度/功率/自算剩余时间）。
        var detail = PowerCoreNative.ReadBatteryDetail();
        if (detail.HasBattery)
        {
            return BuildFromDetail(detail);
        }

        // 无电池或原生层不可用：降级到旧的 GetSystemPowerStatus。
        var info = _source.ReadPower();
        if (info is null)
        {
            return Fail();
        }
        return BuildFromLegacy(info.Value);
    }

    /// <summary>基于 IOCTL 直读的详细数据构建快照。</summary>
    private StatusSnapshot BuildFromDetail(BatteryDetailNative detail)
    {
        // 速率平滑（3 点移动平均）。
        _rateHistory.Enqueue(detail.RateMw);
        while (_rateHistory.Count > RateSmoothWindow)
        {
            _rateHistory.Dequeue();
        }
        int smoothRate = detail.RateMw;
        if (_rateHistory.Count > 0)
        {
            int total = 0;
            foreach (var r in _rateHistory) total += r;
            smoothRate = total / _rateHistory.Count;
        }

        // 健康度详情（可选展示）。
        string? healthDetail = detail.HealthPercent >= 0
            ? $"电池健康度 {detail.HealthPercent}%"
            : null;

        // 接电状态（含充电中 / 已充满）。
        if (detail.AcOnline)
        {
            if (detail.Charging && detail.Percent >= 0 && detail.Percent < 100)
            {
                int chargeW = Math.Abs(smoothRate) / 1000; // 充电功率 W
                return new StatusSnapshot
                {
                    MonitorId = MonitorId,
                    ShortText = $"⚡{detail.Percent}%",
                    HumanText = $"正在充电 {detail.Percent}%" + (chargeW > 0 ? $"（{chargeW}W）" : string.Empty),
                    Severity = StatusSeverity.Info,
                    Progress = detail.Percent,
                    IconKey = "battery-charging",
                    Detail = healthDetail
                };
            }

            return new StatusSnapshot
            {
                MonitorId = MonitorId,
                ShortText = "已接电源",
                HumanText = "已接入电源",
                Severity = StatusSeverity.Normal,
                Progress = detail.Percent >= 0 ? detail.Percent : 100,
                IconKey = "battery-ac",
                Detail = healthDetail
            };
        }

        // 使用电池：自算剩余时间（用平滑后的放电速率）。
        int remainingSec = -1;
        if (smoothRate > 0 && detail.CurrentCapacityMwh > 0)
        {
            remainingSec = (int)((long)detail.CurrentCapacityMwh * 3600 / smoothRate);
        }
        string timeText = remainingSec >= 0
            ? HumanFormatter.FormatDuration((ulong)remainingSec)
            : "剩余时长未知";

        var severity = detail.Percent switch
        {
            < 10 => StatusSeverity.Critical,
            < 20 => StatusSeverity.Warning,
            _ => StatusSeverity.Normal
        };

        var hint = severity switch
        {
            StatusSeverity.Critical => "，请立即接通电源",
            StatusSeverity.Warning => "，建议尽快充电",
            _ => string.Empty
        };

        return new StatusSnapshot
        {
            MonitorId = MonitorId,
            ShortText = detail.Percent >= 0 ? $"{detail.Percent}%" : "电池",
            HumanText = detail.Percent >= 0
                ? $"剩余电量 {detail.Percent}%，{timeText}{hint}"
                : $"正在使用电池，{timeText}",
            Severity = severity,
            Progress = detail.Percent >= 0 ? detail.Percent : -1,
            IconKey = severity switch
            {
                StatusSeverity.Critical => "battery-critical",
                StatusSeverity.Warning => "battery-low",
                _ => "battery"
            },
            Detail = healthDetail is not null
                ? $"{healthDetail} · {timeText}"
                : timeText
        };
    }

    /// <summary>降级：基于 GetSystemPowerStatus 的旧逻辑构建快照。</summary>
    private StatusSnapshot BuildFromLegacy(SystemPowerStatus power)
    {
        // 无电池（台式机/未知）：无电池标志位 128 或百分比为未知哨兵且未接电都视为无电池。
        var hasNoBattery = (power.BatteryFlag & 128) != 0;
        var percentKnown = power.BatteryLifePercent <= 100;
        if (hasNoBattery)
        {
            return new StatusSnapshot
            {
                MonitorId = MonitorId,
                ShortText = string.Empty,
                HumanText = "未检测到电池",
                Severity = StatusSeverity.Normal,
                Progress = -1,
                IconKey = "battery-none"
            };
        }

        var onAc = power.ACLineStatus == 1;

        // 接电状态（含充电中 / 已充满）。
        if (onAc)
        {
            var charging = (power.BatteryFlag & 8) != 0;
            if (percentKnown && power.BatteryLifePercent < 100)
            {
                return new StatusSnapshot
                {
                    MonitorId = MonitorId,
                    ShortText = $"⚡{power.BatteryLifePercent}%",
                    HumanText = $"正在充电，{power.BatteryLifePercent}%",
                    Severity = charging ? StatusSeverity.Info : StatusSeverity.Normal,
                    Progress = power.BatteryLifePercent,
                    IconKey = charging ? "battery-charging" : "battery-ac"
                };
            }

            return new StatusSnapshot
            {
                MonitorId = MonitorId,
                ShortText = "已接电源",
                HumanText = "已接入电源",
                Severity = StatusSeverity.Normal,
                Progress = percentKnown ? power.BatteryLifePercent : 100,
                IconKey = "battery-ac"
            };
        }

        // 使用电池：剩余比例 + 估算时长；百分比未知则给出时长描述。
        if (!percentKnown)
        {
            return new StatusSnapshot
            {
                MonitorId = MonitorId,
                ShortText = "电池",
                HumanText = "正在使用电池，剩余电量未知",
                Severity = StatusSeverity.Warning,
                Progress = -1,
                IconKey = "battery-low"
            };
        }

        var remainingTime = HumanFormatter.IsUnknownTime(power.BatteryLifeTime)
            ? "，剩余时长未知"
            : $"，{HumanFormatter.FormatDuration(power.BatteryLifeTime)}";

        var severity = power.BatteryLifePercent switch
        {
            < 10 => StatusSeverity.Critical,
            < 20 => StatusSeverity.Warning,
            _ => StatusSeverity.Normal
        };

        var hint = severity switch
        {
            StatusSeverity.Critical => "，请立即接通电源",
            StatusSeverity.Warning => "，建议尽快充电",
            _ => string.Empty
        };

        return new StatusSnapshot
        {
            MonitorId = MonitorId,
            ShortText = $"{power.BatteryLifePercent}%",
            HumanText = $"剩余电量 {power.BatteryLifePercent}%{remainingTime}{hint}",
            Severity = severity,
            Progress = power.BatteryLifePercent,
            IconKey = severity switch
            {
                StatusSeverity.Critical => "battery-critical",
                StatusSeverity.Warning => "battery-low",
                _ => "battery"
            },
            Detail = HumanFormatter.IsUnknownTime(power.BatteryLifeTime) ? null : HumanFormatter.FormatDuration(power.BatteryLifeTime)
        };
    }

    private static StatusSnapshot Fail() => new()
    {
        MonitorId = "battery",
        ShortText = "—",
        HumanText = "电源状态读取失败",
        Severity = StatusSeverity.Info,
        Progress = -1,
        IconKey = "battery-unknown"
    };
}
