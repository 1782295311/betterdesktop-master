using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using BetterDesktop.Shell.AppSource.Models;

namespace BetterDesktop.Shell.AppSource.Contracts;

/// <summary>
/// 应用图标服务（统一入口）。
/// 自 shell-dock 上移：真实图标提取（Win32/ManagedShell）与缓存/预取逻辑集中在本插件，
/// dock 等 UI 插件只通过 Inject 消费本契约，不再持有提取实现。
/// </summary>
public interface IAppIconService
{
    /// <summary>
    /// 根据应用项提取图标（优先高清源）。
    /// </summary>
    Task<ImageSource?> GetIconAsync(AppItem item, CancellationToken ct = default);

    /// <summary>
    /// 使指定应用项的图标缓存失效。
    /// </summary>
    void Invalidate(AppItemId id);

    /// <summary>
    /// 批量预取图标（用于应用提取器等大列表场景）。
    /// </summary>
    Task PrefetchAsync(IEnumerable<AppItem> items, CancellationToken ct = default);
}
