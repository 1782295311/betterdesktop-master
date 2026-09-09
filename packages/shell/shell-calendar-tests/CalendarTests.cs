using System;
using System.Linq;
using BetterDesktop.Shell.Calendar.Contracts;
using BetterDesktop.Shell.Calendar.Services;
using Xunit;

namespace BetterDesktop.Shell.Calendar.Tests;

/// <summary>
/// 节气算法校验（Meeus 太阳视黄经）。
/// 断言只用**天文常识**（二分二至的公历日期区间）与序列性质，不写死逐年数据：
/// 春分≈3/20、夏至≈6/21、秋分≈9/23、冬至≈12/21（各允许 ±1 天浮动），
/// 24 个节气严格递增、相邻间隔 15-16 天。
/// </summary>
public sealed class SolarTermCalculatorTests
{
    [Theory]
    [InlineData(2024, 3, 20)]
    [InlineData(2025, 3, 20)]
    [InlineData(2026, 3, 20)]
    [InlineData(2027, 3, 21)]
    public void VernalEquinox_FallsOnKnownDate(int year, int month, int day)
    {
        var moment = SolarTermCalculator.MomentOf(year, IndexOf("春分"));
        Assert.NotNull(moment);
        Assert.Equal(month, moment!.Value.Month);
        Assert.InRange(moment.Value.Day, day - 1, day + 1);
    }

    [Theory]
    [InlineData(2026, 6, 21)]
    [InlineData(2026, 9, 23)]
    [InlineData(2026, 12, 21)]
    public void SolsticesAndAutumnEquinox_FallOnKnownDates(int year, int month, int day)
    {
        var name = month switch { 6 => "夏至", 9 => "秋分", _ => "冬至" };
        var moment = SolarTermCalculator.MomentOf(year, IndexOf(name));
        Assert.NotNull(moment);
        Assert.Equal(month, moment!.Value.Month);
        Assert.InRange(moment.Value.Day, day - 1, day + 1);
    }

    [Theory]
    [InlineData(2026)]
    [InlineData(2030)]
    [InlineData(2035)]
    public void YearTerms_AllInCalendarYear_AndEvenlySpacedWhenSorted(int year)
    {
        // 语义：ForYear(year) = 该**公历年**内的 24 个节气。
        // 注意顺序：按节气序（立春起）小寒/大寒排在末尾，但它们落在该年 1 月 —— 故按日期排序后才连续。
        var terms = SolarTermCalculator.ForYear(year);
        Assert.Equal(24, terms.Count);
        Assert.All(terms, t => Assert.Equal(year, t.Moment.Year));

        var sorted = terms.OrderBy(t => t.Moment).ToList();
        for (var i = 1; i < sorted.Count; i++)
        {
            var gap = (sorted[i].Moment - sorted[i - 1].Moment).TotalDays;
            Assert.InRange(gap, 14.5, 16.5); // 相邻节气间隔约 15.2 天
        }
    }

    [Fact]
    public void TemperatureTerms_OrderWithinYear()
    {
        // 小寒(1/5 左右) → 大寒(1/20 左右) → 立春(2/4 左右)：跨年边界处顺序仍正确
        var terms = SolarTermCalculator.ForYear(2026).ToDictionary(t => t.Name, t => t.Moment);
        Assert.True(terms["小寒"] < terms["大寒"]);
        Assert.True(terms["大寒"] < terms["立春"]);
        Assert.True(terms["冬至"] > terms["大雪"]);
    }

    [Fact]
    public void OutOfRange_ReturnsNullOrEmpty()
    {
        Assert.Null(SolarTermCalculator.MomentOf(2026, 24));
        Assert.Null(SolarTermCalculator.MomentOf(2026, -1));
        Assert.Null(SolarTermCalculator.MomentOf(1500, 0));
    }

    private static int IndexOf(string name) => Array.IndexOf(SolarTermCalculator.Names, name);
}

/// <summary>农历与节日：只做**自洽性**断言（春节当天必须是农历正月初一），不写死任何公历日期。</summary>
public sealed class LunarAndFestivalTests
{
    [Fact]
    public void LunarInfo_ProducesConsistentChineseText()
    {
        var date = new DateTime(2026, 9, 6);
        var info = LunarInfo.Get(date);

        Assert.NotNull(info);
        Assert.InRange(info!.Month, 1, 12);
        Assert.InRange(info.Day, 1, 30);
        Assert.EndsWith("月", info.MonthText);
        Assert.NotEmpty(info.DayText);
        Assert.Equal(2, info.GanZhiYearText.Length); // 天干 + 地支
        Assert.Single(info.ZodiacText);              // 单个汉字生肖
    }

    [Fact]
    public void SpringFestival_IsFirstDayOfFirstLunarMonth()
    {
        // 在 2026.1 - 2026.3 区间里找"春节"条目，断言它确实是农历正月初一
        var provider = new FestivalProvider();
        var entries = provider.GetEntries(new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31));

        var spring = Assert.Single(entries, e => e.Title == "春节");
        var lunar = LunarInfo.Get(spring.Date.ToDateTime(TimeOnly.MinValue));
        Assert.NotNull(lunar);
        Assert.Equal(1, lunar!.Month);
        Assert.Equal(1, lunar.Day);
        Assert.False(lunar.IsLeapMonth);
    }

    [Fact]
    public void FloatingFestivals_LandOnExpectedWeekday()
    {
        var provider = new FestivalProvider();
        var entries = provider.GetEntries(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));

        var mothers = Assert.Single(entries, e => e.Title == "母亲节");
        Assert.Equal(DayOfWeek.Sunday, mothers.Date.DayOfWeek);
        Assert.Equal(5, mothers.Date.Month);

        var thanks = Assert.Single(entries, e => e.Title == "感恩节");
        Assert.Equal(DayOfWeek.Thursday, thanks.Date.DayOfWeek);
        Assert.Equal(11, thanks.Date.Month);
    }

    [Fact]
    public void GregorianFestivals_UseFixedDates()
    {
        var provider = new FestivalProvider();
        var newYear = provider.GetEntries(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1));
        Assert.Contains(newYear, e => e.Title == "元旦");

        var national = provider.GetEntries(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 1));
        Assert.Contains(national, e => e.Title == "国庆节");
    }
}

/// <summary>节假日年表：缺失/空/损坏都降级为空，绝不用猜测数据；结构正确时正确解析休/班。</summary>
public sealed class HolidayProviderTests
{
    [Fact]
    public void MissingYearTable_ReturnsEmpty()
    {
        var provider = new JsonHolidayProvider(overrideDirectory: System.IO.Path.Combine(System.IO.Path.GetTempPath(), "no-such-calendar-dir"));
        Assert.Empty(provider.GetHolidays(1999)); // 内置无 1999 年表
    }

    [Fact]
    public void CorruptOverrideFile_ReturnsEmpty_NoThrow()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CalendarTests-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "holidays-2026.json"), "{ broken json");
            var provider = new JsonHolidayProvider(overrideDirectory: dir);
            Assert.Empty(provider.GetHolidays(2026));
        }
        finally
        {
            System.IO.Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void OverrideFile_ParsesHolidayAndMakeUpWorkday()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CalendarTests-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            var json = """
            { "year": 2026,
              "days": [
                { "date": "2026-01-01", "name": "元旦", "workday": false },
                { "date": "2026-01-04", "name": "元旦调休", "workday": true }
              ] }
            """;
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "holidays-2026.json"), json);

            var entries = new HolidayProvider(new JsonHolidayProvider(dir))
                .GetEntries(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

            Assert.Equal(2, entries.Count);
            var off = Assert.Single(entries, e => e.Kind == CalendarEntryKind.Holiday);
            Assert.True(off.IsDayOff);
            Assert.Equal(new DateOnly(2026, 1, 1), off.Date);

            var work = Assert.Single(entries, e => e.Kind == CalendarEntryKind.Workday);
            Assert.True(work.IsMakeUpWorkday);
            Assert.Equal("元旦调休", work.Title);
        }
        finally
        {
            System.IO.Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void BuiltInTables_ArePresentButEmpty_UntilOfficialDataFilled()
    {
        // 内置年表随包存在（2026/2027），但 days 待官方安排录入 → 空表，日历不显示休/班
        var provider = new JsonHolidayProvider(overrideDirectory: null);
        Assert.Empty(provider.GetHolidays(2026));
        Assert.Empty(provider.GetHolidays(2027));
    }
}

/// <summary>聚合服务：多提供者合并、排序、按日信息与扩展点注册。</summary>
public sealed class CalendarServiceTests
{
    [Fact]
    public void AggregatesAndSorts_ByKindPriority()
    {
        var service = new CalendarService();
        service.Register(new StubProvider("note", new CalendarEntry(new DateOnly(2026, 6, 1), CalendarEntryKind.Note, "便签")));
        service.Register(new StubProvider("holiday", new CalendarEntry(new DateOnly(2026, 6, 1), CalendarEntryKind.Holiday, "端午")));
        service.Register(new StubProvider("term", new CalendarEntry(new DateOnly(2026, 6, 1), CalendarEntryKind.SolarTerm, "芒种")));

        var entries = service.GetEntries(new DateOnly(2026, 6, 1));

        Assert.Equal(3, entries.Count);
        Assert.Equal(CalendarEntryKind.Holiday, entries[0].Kind);
        Assert.Equal(CalendarEntryKind.SolarTerm, entries[1].Kind);
        Assert.Equal(CalendarEntryKind.Note, entries[2].Kind);
    }

    [Fact]
    public void DayInfo_ReflectsHolidayAndMakeUpWorkday()
    {
        var service = new CalendarService();
        service.Register(new StubProvider("holiday",
            new CalendarEntry(new DateOnly(2026, 1, 1), CalendarEntryKind.Holiday, "元旦"),
            new CalendarEntry(new DateOnly(2026, 1, 4), CalendarEntryKind.Workday, "调休")));

        Assert.True(service.GetDayInfo(new DateOnly(2026, 1, 1)).IsDayOff);
        Assert.False(service.GetDayInfo(new DateOnly(2026, 1, 1)).IsWorkday);

        var makeUp = service.GetDayInfo(new DateOnly(2026, 1, 4)); // 周日
        Assert.True(makeUp.IsWeekend);
        Assert.True(makeUp.IsMakeUpWorkday);
        Assert.True(makeUp.IsWorkday); // 补班日必须算工作日
    }

    [Fact]
    public void DuplicateProviderId_IsIgnored()
    {
        var service = new CalendarService();
        service.Register(new StubProvider("dup", new CalendarEntry(new DateOnly(2026, 6, 1), CalendarEntryKind.Note, "A")));
        service.Register(new StubProvider("dup", new CalendarEntry(new DateOnly(2026, 6, 1), CalendarEntryKind.Note, "B")));

        Assert.Single(service.Providers);
        Assert.Single(service.GetEntries(new DateOnly(2026, 6, 1)));
    }

    private sealed class StubProvider : ICalendarEntryProvider
    {
        private readonly CalendarEntry[] _entries;

        public StubProvider(string id, params CalendarEntry[] entries)
        {
            Id = id;
            _entries = entries;
        }

        public string Id { get; }
        public string DisplayName => Id;
        public bool IsEnabled => true;

#pragma warning disable CS0067
        public event EventHandler? EntriesChanged;
#pragma warning restore CS0067

        public IReadOnlyList<CalendarEntry> GetEntries(DateOnly from, DateOnly to)
            => _entries.Where(e => e.Date >= from && e.Date <= to).ToArray();
    }
}
