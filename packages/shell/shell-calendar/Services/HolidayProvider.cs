using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using BetterDesktop.Shell.Calendar.Contracts;

namespace BetterDesktop.Shell.Calendar.Services;

/// <summary>
/// 从 JSON 年表读取法定节假日与调休补班。
/// 查找顺序（先命中先用，全部失败 → 空表，日历不显示休/班标记，绝不猜数据）：
///   1. 用户覆盖：<c>%LocalAppData%\BetterDesktop\Calendar\holidays-&lt;year&gt;.json</c>
///   2. 内置：<c>Data/holidays-&lt;year&gt;.json</c>（EmbeddedResource，随包发布）
/// 年表结构见 Data 目录的示例文件；<c>days</c> 为空属正常状态（官方安排尚未录入）。
/// </summary>
public sealed class JsonHolidayProvider : IHolidayProvider
{
    private readonly Dictionary<int, IReadOnlyList<HolidayDay>> _cache = new();
    private readonly string? _overrideDirectory;

    public JsonHolidayProvider(string? overrideDirectory = null)
    {
        _overrideDirectory = overrideDirectory
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BetterDesktop", "Calendar");
    }

    public IReadOnlyList<HolidayDay> GetHolidays(int year)
    {
        if (_cache.TryGetValue(year, out var cached))
        {
            return cached;
        }

        var days = Load(year);
        _cache[year] = days;
        return days;
    }

    private IReadOnlyList<HolidayDay> Load(int year)
    {
        var json = ReadOverride(year) ?? ReadEmbedded(year);
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<HolidayDay>();
        }

        try
        {
            var doc = JsonSerializer.Deserialize<HolidayTable>(json, JsonOptions);
            var result = new List<HolidayDay>();
            foreach (var day in doc?.Days ?? new List<HolidayRow>())
            {
                if (day is null || string.IsNullOrWhiteSpace(day.Date))
                {
                    continue;
                }
                if (!DateOnly.TryParse(day.Date, out var date))
                {
                    continue;
                }
                result.Add(new HolidayDay(date, string.IsNullOrWhiteSpace(day.Name) ? "节假日" : day.Name, day.Workday));
            }
            return result;
        }
        catch (JsonException)
        {
            return Array.Empty<HolidayDay>(); // 年表写坏：降级为空，不崩日历
        }
    }

    private string? ReadOverride(int year)
    {
        if (string.IsNullOrEmpty(_overrideDirectory))
        {
            return null;
        }
        try
        {
            var path = Path.Combine(_overrideDirectory, $"holidays-{year}.json");
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ReadEmbedded(int year)
    {
        try
        {
            using var stream = typeof(JsonHolidayProvider).Assembly
                .GetManifestResourceStream($"BetterDesktop.Shell.Calendar.Data.holidays-{year}.json");
            if (stream is null)
            {
                return null;
            }
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private sealed class HolidayTable
    {
        public int Year { get; set; }
        public List<HolidayRow>? Days { get; set; }
    }

    private sealed class HolidayRow
    {
        public string? Date { get; set; }
        public string? Name { get; set; }

        [JsonPropertyName("workday")]
        public bool Workday { get; set; }
    }
}

/// <summary>
/// 把 <see cref="IHolidayProvider"/> 的数据适配成日历条目。
/// 换成在线年表时只要替换构造参数里的 provider，本类与 UI 都不用动。
/// </summary>
public sealed class HolidayProvider : ICalendarEntryProvider
{
    private readonly IHolidayProvider _source;

    public HolidayProvider(IHolidayProvider? source = null)
    {
        _source = source ?? new JsonHolidayProvider();
    }

    public string Id => "holiday";

    public string DisplayName => "法定节假日";

    public bool IsEnabled => true;

    // 年表是静态文件；若将来换成在线年表，在此事件里广播更新即可（接口已就位）。
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
                foreach (var day in _source.GetHolidays(year))
                {
                    if (day.Date < from || day.Date > to)
                    {
                        continue;
                    }
                    result.Add(new CalendarEntry(
                        day.Date,
                        day.IsWorkday ? CalendarEntryKind.Workday : CalendarEntryKind.Holiday,
                        day.Name,
                        day.IsWorkday ? "调休补班" : "放假")
                    {
                        Source = $"holiday:{year}"
                    });
                }
            }
        }
        catch
        {
            return Array.Empty<CalendarEntry>();
        }

        return result;
    }
}
