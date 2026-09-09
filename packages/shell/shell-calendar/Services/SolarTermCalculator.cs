using System;
using System.Collections.Generic;

namespace BetterDesktop.Shell.Calendar.Services;

/// <summary>
/// 二十四节气时刻计算（Meeus《Astronomical Algorithms》太阳视黄经法，非查表常数）。
/// 原理：节气 = 太阳视黄经 λ 到达 315°+15°×k 的瞬间（立春起算），用牛顿迭代解 JD，
/// 再按中国标准时（UTC+8）落到具体日期。精度约 ±15 分钟，对"哪天是立春"足够可靠。
///
/// 为什么不用"通用公式 + 逐年常数表"：常数表只覆盖有限年份且易记错一位导致日期错一天；
/// 天文算法长期有效，且可用二分二至（春分≈3/20、夏至≈6/21、秋分≈9/23、冬至≈12/21）单测校验。
/// </summary>
public static class SolarTermCalculator
{
    /// <summary>节气名（立春起，黄经 315° 起每隔 15°）。</summary>
    public static readonly string[] Names =
    {
        "立春", "雨水", "惊蛰", "春分", "清明", "谷雨",
        "立夏", "小满", "芒种", "夏至", "小暑", "大暑",
        "立秋", "处暑", "白露", "秋分", "寒露", "霜降",
        "立冬", "小雪", "大雪", "冬至", "小寒", "大寒"
    };

    private const double ChinaStandardTimeHours = 8.0;

    /// <summary>某年全部 24 个节气（按时间升序，已换算到 UTC+8）。异常年份返回空列表。</summary>
    public static IReadOnlyList<(string Name, DateTime Moment)> ForYear(int year)
    {
        var result = new List<(string, DateTime)>(24);
        for (var i = 0; i < 24; i++)
        {
            var moment = MomentOf(year, i);
            if (moment is null)
            {
                return Array.Empty<(string, DateTime)>();
            }
            result.Add((Names[i], moment.Value));
        }
        return result;
    }

    /// <summary>某年第 index 个节气的时刻（0=立春）。失败返回 null。</summary>
    public static DateTime? MomentOf(int year, int index)
    {
        if (index < 0 || index >= 24 || year < 1900 || year > 3000)
        {
            return null;
        }

        try
        {
            // 目标黄经：立春 315°，之后每节气 +15°
            var target = NormalizeDegrees(315 + 15.0 * index);

            // 初值：当年 1 月 1 日 UTC 的黄经出发，按 ~0.9856°/天 线性外推
            var jd0 = ToJulianDay(new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var lambda0 = ApparentSolarLongitude(jd0);
            var delta = NormalizeDegrees(target - lambda0);
            var jd = jd0 + delta / 0.9856;

            // 牛顿迭代：f(jd) = 有符号黄经差，导数取中心差分
            for (var iter = 0; iter < 8; iter++)
            {
                var diff = SignedDiff(ApparentSolarLongitude(jd), target);
                if (Math.Abs(diff) < 1e-7)
                {
                    break;
                }
                var derivative = (ApparentSolarLongitude(jd + 0.5) - ApparentSolarLongitude(jd - 0.5)) / 1.0;
                if (Math.Abs(derivative) < 1e-9)
                {
                    break;
                }
                jd -= diff / derivative;
            }

            return FromJulianDay(jd).AddHours(ChinaStandardTimeHours);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>太阳视黄经（度，已归一化到 [0,360)）。Meeus ch.25。</summary>
    internal static double ApparentSolarLongitude(double julianDay)
    {
        var t = (julianDay - 2451545.0) / 36525.0;
        var l0 = 280.46646 + 36000.76983 * t + 0.0003032 * t * t;
        var m = 357.52911 + 35999.05029 * t - 0.0001537 * t * t;
        var c = (1.914602 - 0.004817 * t - 0.000014 * t * t) * SinDeg(m)
              + (0.019993 - 0.000101 * t) * SinDeg(2 * m)
              + 0.000289 * SinDeg(3 * m);
        var trueLongitude = l0 + c;
        var omega = 125.04 - 1934.136 * t;
        return NormalizeDegrees(trueLongitude - 0.00569 - 0.00478 * SinDeg(omega));
    }

    /// <summary>有符号最短角差（度，(-180,180]）。</summary>
    private static double SignedDiff(double actual, double target)
    {
        var d = NormalizeDegrees(actual - target);
        return d > 180 ? d - 360 : d;
    }

    private static double NormalizeDegrees(double degrees)
    {
        var d = degrees % 360.0;
        return d < 0 ? d + 360.0 : d;
    }

    private static double SinDeg(double degrees) => Math.Sin(degrees * Math.PI / 180.0);

    // ---- 儒略日换算（Gregorian，标准 Fliegel/Meeus 公式） ----

    internal static double ToJulianDay(DateTime utc)
    {
        var year = utc.Year;
        var month = utc.Month;
        var day = utc.Day + (utc.Hour + (utc.Minute + utc.Second / 60.0) / 60.0) / 24.0;

        var a = (14 - month) / 12;
        var y = year + 4800 - a;
        var m = month + 12 * a - 3;
        var jdn = (int)day + (153 * m + 2) / 5 + 365 * y + y / 4 - y / 100 + y / 400 - 32045;
        return jdn + (day - (int)day) - 0.5;
    }

    internal static DateTime FromJulianDay(double jd)
    {
        var z = (int)Math.Floor(jd + 0.5);
        var f = jd + 0.5 - z;

        var alpha = (int)Math.Floor((z - 1867216.25) / 36524.25);
        var a = z + 1 + alpha - alpha / 4;
        var b = a + 1524;
        var c = (int)Math.Floor((b - 122.1) / 365.25);
        var d = (int)Math.Floor(365.25 * c);
        var e = (int)Math.Floor((b - d) / 30.6001);

        var day = b - d - (int)Math.Floor(30.6001 * e) + f;
        var month = e < 14 ? e - 1 : e - 13;
        var year = month > 2 ? c - 4716 : c - 4715;

        var dayInt = (int)Math.Floor(day);
        var fraction = day - dayInt;
        var seconds = (int)Math.Round(fraction * 86400.0);
        if (seconds >= 86400)
        {
            seconds -= 86400;
            dayInt += 1;
        }

        return new DateTime(year, month, dayInt, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds);
    }
}
