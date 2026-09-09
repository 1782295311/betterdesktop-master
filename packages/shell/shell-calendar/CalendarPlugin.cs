using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.Calendar.Contracts;
using BetterDesktop.Shell.Calendar.Services;

namespace BetterDesktop.Shell.Calendar;

// ============================================================
// 【白话导航 · 日历域】凭白话需求定位到精确文件：
//   "日历聚合数据 / 某天条目"   → Services/CalendarService.cs + CalendarServiceFactory.cs
//   "农历换算"                  → Services/LunarInfo.cs
//   "二十四节气"                → Services/SolarTermCalculator.cs + Services/SolarTermProvider.cs
//   "法定节假日 / 调休"          → Services/HolidayProvider.cs（IHolidayProvider）
//   "传统节日"                  → Services/FestivalProvider.cs
//   "天气"                      → Services/WeatherProvider.cs
//   "系统日程事件"              → Services/SystemEventsProvider.cs
//   "新增一个日历数据源"        → 实现 Contracts/ICalendarEntryProvider.cs（条目模型 Contracts/CalendarEntry.cs）
// ============================================================

/// <summary>
/// 日历插件（shell.calendar）：装配各类条目提供者并 Provide <see cref="ICalendarService"/>。
/// 必须早于 menu-bar 注册（日历面板消费该服务）。
///
/// 【便签预留】未来的 quick-note 只需实现 <see cref="ICalendarEntryProvider"/>，
/// 在它自己的插件里 <c>context.Get&lt;ICalendarService&gt;()?.Register(new NoteEntryProvider(...))</c>
/// 即可让便签出现在月视图与日详情里，本插件与日历 UI 都不用改。
/// </summary>
public sealed class CalendarPlugin : IPlugin
{
    public string Name => "shell.calendar";

    public IReadOnlyList<Type> Inject => Array.Empty<Type>();

    private CalendarService? _service;

    public Task LoadAsync(IContext context, CancellationToken cancellationToken = default)
    {
        // 提供者装配统一走工厂（与 Playground 预览同源）。
        // 天气：本仓当前无天气数据源 → Null 实现（IsAvailable=false，日历隐藏该分组）；
        // 有实现时 context.Get<IWeatherDayProvider>() 返回真实现，工厂直接装配。
        _service = CalendarServiceFactory.CreateDefault(context.Get<IWeatherDayProvider>());

        context.Provide<ICalendarService>(_service);
        context.Logger.Info("[Calendar] 插件已加载：节假日/节气/节日/日程/天气（无源则隐藏）提供者就绪");
        return Task.CompletedTask;
    }

    public Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        _service?.Dispose();
        _service = null;
        return Task.CompletedTask;
    }
}
