// BetterDesktop.Api — 天气数据源契约（shell-calendar 迁入，2026-09-08 信息源 API 独立化）
// 本仓当前没有真实天气服务，故默认注入 NullWeatherDayProvider：
// 没有数据源就不显示天气行，绝不显示编造的天气（M10）。
// 未来接入天气服务（或第三方实现）后，实现本接口并在插件装配处注入即可，日历自动显示。

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BetterDesktop.Shell.Calendar.Contracts;

namespace BetterDesktop.Shell.Calendar.Services;

/// <summary>
/// 天气数据源（按日）。实现后经插件装配注入，日历自动显示天气行。
/// </summary>
public interface IWeatherDayProvider
{
    /// <summary>是否有可用数据源（false → 日历隐藏天气分组）。</summary>
    bool IsAvailable { get; }

    /// <summary>取 [from, to] 区间的天气条目；失败返回空列表。</summary>
    Task<IReadOnlyList<CalendarEntry>> GetAsync(DateOnly from, DateOnly to, CancellationToken ct = default);
}

/// <summary>无天气源时的空实现（默认注入）。</summary>
public sealed class NullWeatherDayProvider : IWeatherDayProvider
{
    public bool IsAvailable => false;

    public Task<IReadOnlyList<CalendarEntry>> GetAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<CalendarEntry>>(Array.Empty<CalendarEntry>());
}
