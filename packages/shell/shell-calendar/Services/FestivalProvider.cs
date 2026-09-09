using System;
using System.Collections.Generic;
using BetterDesktop.Shell.Calendar.Contracts;

namespace BetterDesktop.Shell.Calendar.Services;

/// <summary>
/// 传统节日 / 公历节日（纯规则，无外部数据）：
/// - 农历固定日期：春节、元宵、龙抬头、端午、七夕、中元、中秋、重阳、腊八，以及"腊月最后一天=除夕"；
/// - 公历固定日期：元旦、劳动节、国庆等；
/// - 第 N 个星期几：母亲节（5 月第 2 个周日）、父亲节（6 月第 3 个周日）、感恩节（11 月第 4 个周四）。
/// 每日最多产出 1 条主节日（避免格子被挤爆），额外节日进日详情。
/// </summary>
public sealed class FestivalProvider : ICalendarEntryProvider
{
    // 农历固定：(月, 日) → 名称
    private static readonly Dictionary<(int Month, int Day), string> LunarFestivals = new()
    {
        [(1, 1)] = "春节",
        [(1, 15)] = "元宵节",
        [(2, 2)] = "龙抬头",
        [(5, 5)] = "端午节",
        [(7, 7)] = "七夕",
        [(7, 15)] = "中元节",
        [(8, 15)] = "中秋节",
        [(9, 9)] = "重阳节",
        [(12, 8)] = "腊八节"
    };

    // 公历固定：(月, 日) → 名称
    private static readonly Dictionary<(int Month, int Day), string> GregorianFestivals = new()
    {
        [(1, 1)] = "元旦",
        [(2, 14)] = "情人节",
        [(3, 8)] = "妇女节",
        [(3, 12)] = "植树节",
        [(4, 1)] = "愚人节",
        [(5, 1)] = "劳动节",
        [(5, 4)] = "青年节",
        [(6, 1)] = "儿童节",
        [(7, 1)] = "建党节",
        [(8, 1)] = "建军节",
        [(9, 10)] = "教师节",
        [(10, 1)] = "国庆节",
        [(10, 31)] = "万圣节",
        [(11, 11)] = "光棍节",
        [(12, 25)] = "圣诞节"
    };

    // 第 N 个星期几：(月, 第N个, 星期) → 名称
    private static readonly (int Month, int Nth, DayOfWeek Day, string Name)[] FloatingFestivals =
    {
        (5, 2, DayOfWeek.Sunday, "母亲节"),
        (6, 3, DayOfWeek.Sunday, "父亲节"),
        (11, 4, DayOfWeek.Thursday, "感恩节")
    };

    public string Id => "festival";

    public string DisplayName => "节日";

    public bool IsEnabled => true;

    // 节日由固定规则推算，运行期不会变化：事件仅为满足 ICalendarEntryProvider 契约。
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
            for (var date = from; date <= to; date = date.AddDays(1))
            {
                foreach (var entry in ForDate(date))
                {
                    result.Add(entry);
                }
            }
        }
        catch
        {
            return Array.Empty<CalendarEntry>();
        }

        return result;
    }

    private static IEnumerable<CalendarEntry> ForDate(DateOnly date)
    {
        var dateTime = date.ToDateTime(TimeOnly.MinValue);

        // 1) 农历节日（含除夕 = 腊月最后一天）
        var lunar = LunarInfo.Get(dateTime);
        if (lunar is not null)
        {
            if (LunarFestivals.TryGetValue((lunar.Month, lunar.Day), out var lunarName) && !lunar.IsLeapMonth)
            {
                yield return Make(date, lunarName, $"农历{lunar.MonthText}{lunar.DayText}");
            }
            else if (lunar.Month == 12 && !lunar.IsLeapMonth
                     && lunar.Day == LunarInfo.DaysInLunarMonth(dateTime))
            {
                yield return Make(date, "除夕", $"农历{lunar.MonthText}{lunar.DayText}");
            }
        }

        // 2) 公历固定节日
        if (GregorianFestivals.TryGetValue((date.Month, date.Day), out var gregorianName))
        {
            yield return Make(date, gregorianName, null);
        }

        // 3) 第 N 个星期几
        foreach (var (month, nth, dayOfWeek, name) in FloatingFestivals)
        {
            if (date.Month == month && date.DayOfWeek == dayOfWeek && IsNthOfWeekday(date, nth))
            {
                yield return Make(date, name, null);
            }
        }
    }

    private static CalendarEntry Make(DateOnly date, string name, string? subtitle) =>
        new(date, CalendarEntryKind.Festival, name, subtitle) { Source = "festival" };

    /// <summary>是否为本月第 nth 个该星期几（1 起算）。</summary>
    private static bool IsNthOfWeekday(DateOnly date, int nth)
    {
        var occurrence = (date.Day - 1) / 7 + 1;
        return occurrence == nth;
    }
}
