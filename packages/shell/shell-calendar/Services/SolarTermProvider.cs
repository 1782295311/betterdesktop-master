using System;
using System.Collections.Generic;
using BetterDesktop.Shell.Calendar.Contracts;

namespace BetterDesktop.Shell.Calendar.Services;

/// <summary>
/// 二十四节气条目（算法推算，无外部数据）。
/// 按年缓存：一次月视图只需算 1-2 个年份的 24 个节气，翻月不重复计算。
/// </summary>
public sealed class SolarTermProvider : ICalendarEntryProvider
{
    private readonly Dictionary<int, IReadOnlyList<(string Name, DateTime Moment)>> _cache = new();

    public string Id => "solar-term";

    public string DisplayName => "节气";

    public bool IsEnabled => true;

    // 节气由算法推算，运行期不会变化：事件仅为满足 ICalendarEntryProvider 契约（接口统一广播）。
#pragma warning disable CS0067 // 事件从未使用（静态数据提供者无变更源）
    public event EventHandler? EntriesChanged;
#pragma warning restore CS0067

    public IReadOnlyList<CalendarEntry> GetEntries(DateOnly from, DateOnly to)
    {
        var result = new List<CalendarEntry>();
        if (to < from)
        {
            return result;
        }

        try
        {
            for (var year = from.Year; year <= to.Year; year++)
            {
                if (!_cache.TryGetValue(year, out var terms))
                {
                    terms = SolarTermCalculator.ForYear(year);
                    _cache[year] = terms;
                }

                foreach (var (name, moment) in terms)
                {
                    var date = DateOnly.FromDateTime(moment);
                    if (date < from || date > to)
                    {
                        continue;
                    }
                    result.Add(new CalendarEntry(date, CalendarEntryKind.SolarTerm, name)
                    {
                        Subtitle = moment.ToString("HH:mm"),
                        Source = $"solar-term:{year}"
                    });
                }
            }
        }
        catch
        {
            return Array.Empty<CalendarEntry>(); // M10：算法异常不冒泡，退化为空条目
        }

        return result;
    }
}
