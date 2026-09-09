using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BetterDesktop.Shell.Calendar.Contracts;

namespace BetterDesktop.Shell.Calendar.Services;

/// <summary>
/// 系统日程（Windows 日历 / Outlook 约会）：走 WinRT <c>Windows.ApplicationModel.Appointments</c>。
///
/// 【权限纪律】桌面应用读取约会可能触发隐私授权或被系统拒绝：
/// - 任何失败（未授权 / 无日历 / 服务不可用）→ <see cref="IsEnabled"/> 置 false，日历不再显示"日程"分组；
/// - 异常绝不冒泡到 UI（M10），也绝不重试打搅用户。
///
/// 【取数纪律】WinRT 调用是异步的，而 <see cref="ICalendarEntryProvider"/> 是同步接口：
/// 本类在后台预取并缓存，取到后发 <see cref="EntriesChanged"/>；未就绪时 GetEntries 返回已缓存部分。
/// </summary>
public sealed class SystemEventsProvider : ICalendarEntryProvider
{
    private readonly Dictionary<DateOnly, List<CalendarEntry>> _cache = new();
    private DateOnly _cacheFrom;
    private DateOnly _cacheTo;
    private bool _hasCache;
    private bool _loading;
    private bool _enabled = true;

    public string Id => "system-event";

    public string DisplayName => "日程";

    public bool IsEnabled => _enabled;

    public event EventHandler? EntriesChanged;

    /// <summary>首屏预取：构造后立刻后台拉当前月，不阻塞 UI。</summary>
    public SystemEventsProvider()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var from = new DateOnly(today.Year, today.Month, 1);
        _ = EnsureLoadedAsync(from, from.AddMonths(1).AddDays(-1));
    }

    public IReadOnlyList<CalendarEntry> GetEntries(DateOnly from, DateOnly to)
    {
        var result = new List<CalendarEntry>();
        if (!_enabled || to < from)
        {
            return result;
        }

        // 缓存未覆盖请求区间 → 后台补拉，本次先返回已有部分
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

    /// <summary>后台预取 [from, to] 区间日程；失败即禁用本提供者。</summary>
    public async Task EnsureLoadedAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        if (_loading)
        {
            return;
        }
        _loading = true;
        try
        {
            var loaded = await TryLoadAsync(from, to).ConfigureAwait(false);
            if (!loaded)
            {
                _enabled = false;
                return;
            }
            _cacheFrom = from;
            _cacheTo = to;
            _hasCache = true;
        }
        catch
        {
            _enabled = false; // 任何未预期失败都降级为"不显示日程"
        }
        finally
        {
            _loading = false;
        }

        EntriesChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task<bool> TryLoadAsync(DateOnly from, DateOnly to)
    {
        // WinRT：请求只读约会存储；未授权会抛（或返回 null）
        var store = await Windows.ApplicationModel.Appointments.AppointmentManager
            .RequestStoreAsync(Windows.ApplicationModel.Appointments.AppointmentStoreAccessType.AllCalendarsReadOnly)
            .AsTask().ConfigureAwait(false);
        if (store is null)
        {
            return false;
        }

        var calendars = await store
            .FindAppointmentCalendarsAsync(Windows.ApplicationModel.Appointments.FindAppointmentCalendarsOptions.IncludeHidden)
            .AsTask().ConfigureAwait(false);

        var start = from.ToDateTime(TimeOnly.MinValue);
        var span = to.ToDateTime(TimeOnly.MaxValue) - start;

        var next = new Dictionary<DateOnly, List<CalendarEntry>>();
        foreach (var calendar in calendars)
        {
            var options = new Windows.ApplicationModel.Appointments.FindAppointmentsOptions
            {
                IncludeHidden = false
            };
            options.FetchProperties.Add(Windows.ApplicationModel.Appointments.AppointmentProperties.Subject);
            options.FetchProperties.Add(Windows.ApplicationModel.Appointments.AppointmentProperties.StartTime);
            options.FetchProperties.Add(Windows.ApplicationModel.Appointments.AppointmentProperties.Duration);
            options.FetchProperties.Add(Windows.ApplicationModel.Appointments.AppointmentProperties.Location);
            options.FetchProperties.Add(Windows.ApplicationModel.Appointments.AppointmentProperties.AllDay);

            var appointments = await calendar.FindAppointmentsAsync(start, span, options).AsTask().ConfigureAwait(false);
            foreach (var appointment in appointments)
            {
                var local = appointment.StartTime.LocalDateTime;
                var date = DateOnly.FromDateTime(local.Date);
                if (date < from || date > to)
                {
                    continue;
                }
                if (!next.TryGetValue(date, out var list))
                {
                    list = new List<CalendarEntry>();
                    next[date] = list;
                }

                var title = string.IsNullOrWhiteSpace(appointment.Subject) ? "(无标题日程)" : appointment.Subject;
                list.Add(new CalendarEntry(date, CalendarEntryKind.Event, title,
                    string.IsNullOrWhiteSpace(appointment.Location) ? null : appointment.Location)
                {
                    Time = appointment.AllDay ? null : TimeOnly.FromDateTime(local),
                    Source = "winrt"
                });
            }
        }

        _cache.Clear();
        foreach (var (date, list) in next)
        {
            _cache[date] = list;
        }
        return true;
    }
}
