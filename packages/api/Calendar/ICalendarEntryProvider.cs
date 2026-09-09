using System;
using System.Collections.Generic;

namespace BetterDesktop.Shell.Calendar.Contracts;

/// <summary>
/// 日历条目提供者（日历的唯一扩展点）。
/// 已实现：节假日/调休、节气、传统节日、系统日程、天气（未接数据源时降级为空）。
/// **预留**：未来的便签（shell-quick-note）只需实现本接口，条目即自动出现在月视图与日详情里，UI 零改动。
///
/// 契约纪律：
/// - 取数按**区间**批量给（月视图一次约 42 天），禁止逐日调用；
/// - 任何异常内部消化并返回空列表（M10），绝不把异常抛给 UI；
/// - 数据变化（异步加载完成/用户编辑）发 <see cref="EntriesChanged"/>，由 <see cref="ICalendarService"/> 广播。
/// </summary>
public interface ICalendarEntryProvider
{
    /// <summary>提供者稳定标识（如 "holiday"、"solar-term"、"note"）。</summary>
    string Id { get; }

    /// <summary>展示名（日详情分组标题）。</summary>
    string DisplayName { get; }

    /// <summary>是否启用（权限被拒/无数据源时为 false，UI 不再显示该分组）。</summary>
    bool IsEnabled { get; }

    /// <summary>取 [from, to] 闭区间内的条目；越界/异常返回空列表。</summary>
    IReadOnlyList<CalendarEntry> GetEntries(DateOnly from, DateOnly to);

    /// <summary>条目发生变化（异步就绪/外部编辑）时触发。</summary>
    event EventHandler? EntriesChanged;
}

/// <summary>
/// 逐日信息（农历/干支/生肖/是否休息日），与条目列表分开：它每天必有，条目可为空。
/// </summary>
public sealed record CalendarDayInfo(
    DateOnly Date,
    int LunarYear, int LunarMonth, int LunarDay,
    bool IsLeapLunarMonth,
    string LunarMonthText,
    string LunarDayText,
    string GanZhiYearText,
    string? ZodiacText,
    bool IsWeekend,
    bool IsDayOff,
    bool IsMakeUpWorkday)
{
    /// <summary>该日是否需要上班：补班必上班；休息日/不上班的周末为 false。</summary>
    public bool IsWorkday => IsMakeUpWorkday || (!IsDayOff && !IsWeekend);
}

public interface ICalendarService
{
    /// <summary>某日全部条目（按种类与时间排序）。</summary>
    IReadOnlyList<CalendarEntry> GetEntries(DateOnly date);

    /// <summary>区间条目（月视图一次取 42 天，避免逐日跨层调用）。</summary>
    IReadOnlyList<CalendarEntry> GetEntries(DateOnly from, DateOnly to);

    /// <summary>某日逐日信息（农历/干支/是否休息）。</summary>
    CalendarDayInfo GetDayInfo(DateOnly date);

    /// <summary>已注册的提供者（诊断/设置页可列出）。</summary>
    IReadOnlyList<ICalendarEntryProvider> Providers { get; }

    /// <summary>注册新提供者（未来的便签在此挂载）。</summary>
    void Register(ICalendarEntryProvider provider);

    /// <summary>任一提供者条目变化时广播。</summary>
    event EventHandler? EntriesChanged;
}
