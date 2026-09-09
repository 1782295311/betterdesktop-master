using System;
using System.Collections.Generic;
using System.Linq;
using BetterDesktop.Shell.Calendar.Contracts;

namespace BetterDesktop.Shell.Calendar.Services;

/// <summary>
/// 日历聚合服务：把各 <see cref="ICalendarEntryProvider"/>（节假日/节气/节日/日程/天气/未来便签）
/// 的条目合并成"某日有什么"，并计算逐日的农历/干支/休息日信息。
/// 新增数据源只需 <see cref="Register"/>，UI 与服务本体都不用改（开闭原则）。
/// </summary>
public sealed class CalendarService : ICalendarService
{
    private readonly List<ICalendarEntryProvider> _providers = new();

    // 展示优先级：休/班 > 节气 > 节日 > 日程 > 天气 > 便签（格子只显示优先级最高的一条）
    private static readonly Dictionary<CalendarEntryKind, int> KindOrder = new()
    {
        [CalendarEntryKind.Holiday] = 0,
        [CalendarEntryKind.Workday] = 0,
        [CalendarEntryKind.SolarTerm] = 1,
        [CalendarEntryKind.Festival] = 2,
        [CalendarEntryKind.Event] = 3,
        [CalendarEntryKind.Weather] = 4,
        [CalendarEntryKind.Note] = 5
    };

    public event EventHandler? EntriesChanged;

    public IReadOnlyList<ICalendarEntryProvider> Providers => _providers.ToArray();

    public void Register(ICalendarEntryProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (_providers.Any(p => string.Equals(p.Id, provider.Id, StringComparison.OrdinalIgnoreCase)))
        {
            return; // 同 Id 不重复注册（便签未来多次装配也安全）
        }
        _providers.Add(provider);
        provider.EntriesChanged += OnProviderChanged;
    }

    public IReadOnlyList<CalendarEntry> GetEntries(DateOnly date)
        => GetEntries(date, date);

    /// <summary>区间取条目并按种类/时间排序（月视图一次取 42 天，避免逐日跨层调用）。</summary>
    public IReadOnlyList<CalendarEntry> GetEntries(DateOnly from, DateOnly to)
    {
        var result = new List<CalendarEntry>();
        foreach (var provider in _providers)
        {
            if (!provider.IsEnabled)
            {
                continue;
            }
            try
            {
                result.AddRange(provider.GetEntries(from, to));
            }
            catch
            {
                // 单个提供者失败不影响其它（M10）
            }
        }

        return result
            .OrderBy(e => e.Date)
            .ThenBy(e => KindOrder.TryGetValue(e.Kind, out var order) ? order : 99)
            .ThenBy(e => e.Time ?? TimeOnly.MaxValue)
            .ThenBy(e => e.Title, StringComparer.CurrentCulture)
            .ToArray();
    }

    public CalendarDayInfo GetDayInfo(DateOnly date)
    {
        var dateTime = date.ToDateTime(TimeOnly.MinValue);
        var lunar = LunarInfo.Get(dateTime);

        var isWeekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        var isDayOff = false;
        var isMakeUpWorkday = false;

        foreach (var entry in GetEntries(date))
        {
            if (entry.Kind == CalendarEntryKind.Holiday)
            {
                isDayOff = true;
            }
            else if (entry.Kind == CalendarEntryKind.Workday)
            {
                isMakeUpWorkday = true;
            }
        }

        return new CalendarDayInfo(
            date,
            lunar?.Year ?? 0,
            lunar?.Month ?? 0,
            lunar?.Day ?? 0,
            lunar?.IsLeapMonth ?? false,
            lunar?.MonthText ?? string.Empty,
            lunar?.DayText ?? string.Empty,
            lunar?.GanZhiYearText ?? string.Empty,
            lunar?.ZodiacText,
            isWeekend,
            isDayOff,
            isMakeUpWorkday);
    }

    private void OnProviderChanged(object? sender, EventArgs e) => EntriesChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>卸载：退订全部提供者事件，避免插件卸载后仍广播。</summary>
    public void Dispose()
    {
        foreach (var provider in _providers)
        {
            provider.EntriesChanged -= OnProviderChanged;
        }
        _providers.Clear();
    }
}
