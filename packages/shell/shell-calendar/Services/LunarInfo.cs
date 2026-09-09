using System;
using System.Globalization;

namespace BetterDesktop.Shell.Calendar.Services;

/// <summary>某天的农历/干支/生肖（纯计算，复用 .NET ChineseLunisolarCalendar，零查表数据）。</summary>
public sealed record LunarDate(
    int Year,
    int Month,
    int Day,
    bool IsLeapMonth,
    string MonthText,
    string DayText,
    string GanZhiYearText,
    string ZodiacText);

/// <summary>
/// 农历与干支生肖计算。中文数字/天干地支是**固定序常量**（算法常量，不是逐年数据），
/// 因此不会随年份失效，也不需要外部数据源。
/// </summary>
public static class LunarInfo
{
    private static readonly ChineseLunisolarCalendar Calendar = new();

    private static readonly string[] ChineseNumbers = { "一", "二", "三", "四", "五", "六", "七", "八", "九", "十" };
    private static readonly string[] Tiangan = { "甲", "乙", "丙", "丁", "戊", "己", "庚", "辛", "壬", "癸" };
    private static readonly string[] Dizhi = { "子", "丑", "寅", "卯", "辰", "巳", "午", "未", "申", "酉", "戌", "亥" };
    private static readonly string[] Zodiacs = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };

    /// <summary>取某天的农历信息；失败（越界等）返回 null。</summary>
    public static LunarDate? Get(DateTime date)
    {
        try
        {
            var lunarYear = Calendar.GetYear(date);
            var month = Calendar.GetMonth(date);
            // 闰月判定必须用**农历年**（GetYear 返回值），传公历年会判错
            var isLeap = Calendar.IsLeapMonth(lunarYear, month);
            var day = Calendar.GetDayOfMonth(date);
            var sexagenary = Calendar.GetSexagenaryYear(date);
            var branchIndex = ((sexagenary - 1) % 12 + 12) % 12;

            return new LunarDate(
                lunarYear,
                month,
                day,
                isLeap,
                ToChineseMonth(month, isLeap),
                ToChineseDay(day),
                ToGanZhi(sexagenary),
                Zodiacs[branchIndex]);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>该农历月的天数（算除夕等"月最后一天"用）。</summary>
    public static int DaysInLunarMonth(DateTime dateInMonth)
    {
        try
        {
            var year = Calendar.GetYear(dateInMonth);
            var month = Calendar.GetMonth(dateInMonth);
            return Calendar.GetDaysInMonth(year, month);
        }
        catch
        {
            return 30;
        }
    }

    private static string ToChineseMonth(int month, bool isLeap)
    {
        var name = month switch
        {
            1 => "正",
            11 => "冬",
            12 => "腊",
            <= 10 => ChineseNumbers[month - 1],
            _ => month.ToString()
        };
        return (isLeap ? "闰" : string.Empty) + name + "月";
    }

    private static string ToChineseDay(int day)
    {
        if (day == 10) return "初十";
        if (day == 20) return "二十";
        if (day == 30) return "三十";
        if (day < 10) return $"初{ChineseNumbers[day - 1]}";
        if (day < 20) return $"十{ChineseNumbers[day - 10 - 1]}";
        if (day < 30) return $"廿{ChineseNumbers[day - 20 - 1]}";
        return day.ToString();
    }

    private static string ToGanZhi(int sexagenaryIndex)
    {
        var i = ((sexagenaryIndex - 1) % 60 + 60) % 60;
        return $"{Tiangan[i % 10]}{Dizhi[i % 12]}";
    }
}
