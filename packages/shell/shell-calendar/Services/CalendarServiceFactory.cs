using BetterDesktop.Shell.Calendar.Contracts;

namespace BetterDesktop.Shell.Calendar.Services;

/// <summary>
/// 默认提供者装配（插件与 Playground 预览共用同一套，避免两处漂移）。
/// 新增数据源在此登记一处即可全端生效；未来的便签由它自己的插件调用
/// <c>service.Register(new NoteEntryProvider(...))</c> 挂载，不经过本工厂。
/// </summary>
public static class CalendarServiceFactory
{
    public static CalendarService CreateDefault(IWeatherDayProvider? weather = null)
    {
        var service = new CalendarService();
        service.Register(new HolidayProvider());            // 法定节假日 + 调休（年表 JSON）
        service.Register(new SolarTermProvider());          // 24 节气（天文算法）
        service.Register(new FestivalProvider());           // 传统节日 / 公历节日
        service.Register(new SystemEventsProvider());       // 系统日程（WinRT，未授权自动隐藏）
        service.Register(new WeatherProvider(weather));     // 天气（无数据源自动隐藏）
        return service;
    }
}
