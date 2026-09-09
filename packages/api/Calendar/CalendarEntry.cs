using System;

namespace BetterDesktop.Shell.Calendar.Contracts;

/// <summary>日历条目种类（UI 按种类决定角标/圆点/配色，不传画刷，保持跨主题可读）。</summary>
public enum CalendarEntryKind
{
    /// <summary>法定节假日（放假）。</summary>
    Holiday,

    /// <summary>调休补班（周末但需上班）。</summary>
    Workday,

    /// <summary>二十四节气。</summary>
    SolarTerm,

    /// <summary>传统节日 / 公历节日。</summary>
    Festival,

    /// <summary>系统日程（WinRT AppointmentCalendar）。</summary>
    Event,

    /// <summary>天气概况。</summary>
    Weather,

    /// <summary>便签（**预留**：未来的 quick-note 实现 ICalendarEntryProvider 即可产出本类条目）。</summary>
    Note
}

/// <summary>
/// 日历上的一条信息。所有数据源（节假日/节气/节日/日程/天气/未来的便签）统一用这个模型，
/// UI 只按 Kind 渲染，新增数据源无需改 UI。
/// </summary>
public sealed record CalendarEntry(
    DateOnly Date,
    CalendarEntryKind Kind,
    string Title,
    string? Subtitle = null)
{
    /// <summary>是否"放假"（法定节假日）。</summary>
    public bool IsDayOff => Kind is CalendarEntryKind.Holiday;

    /// <summary>是否"调休补班"（周末/假日里要上班的一天）。</summary>
    public bool IsMakeUpWorkday => Kind is CalendarEntryKind.Workday;

    /// <summary>有具体时刻的条目（日程）可填，用于日详情排序。</summary>
    public TimeOnly? Time { get; init; }

    /// <summary>数据来源标识（诊断/未来冲突消解用，如 "holiday:2026"、"winrt"）。</summary>
    public string? Source { get; init; }
}
