// BetterDesktop.Shell.Calendar — 天气行实现
// 把 IWeatherDayProvider（契约在 BetterDesktop.Api）适配成日历条目提供者：
// 异步预取 + 缓存，同步出数；无数据源时默认注入 NullWeatherDayProvider。

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BetterDesktop.Shell.Calendar.Contracts;

namespace BetterDesktop.Shell.Calendar.Services;

/// <summary>把 <see cref="IWeatherDayProvider"/> 适配成日历条目提供者（异步预取 + 缓存，同步出数）。</summary>
public sealed class WeatherProvider : ICalendarEntryProvider
{
    private readonly IWeatherDayProvider _source;
    private readonly Dictionary<DateOnly, List<CalendarEntry>> _cache = new();
    private DateOnly _cacheFrom;
    private DateOnly _cacheTo;
    private bool _hasCache;
    private bool _loading;

    public WeatherProvider(IWeatherDayProvider? source = null)
    {
        _source = source ?? new NullWeatherDayProvider();
    }

    public string Id => "weather";

    public string DisplayName => "天气";

    public bool IsEnabled => _source.IsAvailable;

    public event EventHandler? EntriesChanged;

    public IReadOnlyList<CalendarEntry> GetEntries(DateOnly from, DateOnly to)
    {
        var result = new List<CalendarEntry>();
        if (!IsEnabled || to < from)
        {
            return result;
        }

        if (!_hasCache || from < _cacheFrom || to > _cacheTo)
        {
            _ = EnsureLoadedAsync(from, to);
        }

        for (var date = from; date <= to; date = date.AddDays(1))
        {
            if (_cache.TryGetValue(date, out var list))
            {
                result.AddRange(list);
            }
        }
        return result;
    }

    private async Task EnsureLoadedAsync(DateOnly from, DateOnly to)
    {
        if (_loading)
        {
            return;
        }
        _loading = true;
        try
        {
            var entries = await _source.GetAsync(from, to).ConfigureAwait(false);
            _cache.Clear();
            foreach (var entry in entries)
            {
                if (!_cache.TryGetValue(entry.Date, out var list))
                {
                    list = new List<CalendarEntry>();
                    _cache[entry.Date] = list;
                }
                list.Add(entry);
            }
            _cacheFrom = from;
            _cacheTo = to;
            _hasCache = true;
        }
        catch
        {
            // 天气取不到：静默降级为空，不冒泡
        }
        finally
        {
            _loading = false;
        }

        EntriesChanged?.Invoke(this, EventArgs.Empty);
    }
}
