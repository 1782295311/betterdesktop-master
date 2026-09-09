using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using BetterDesktop.Shell.Dock.Models;

namespace BetterDesktop.Shell.Dock.Services;

/// <summary>
/// Dock 图标服务：负责真实图标加载、缓存与失效刷新。
/// </summary>
public interface IDockIconService
{
    /// <summary>
    /// 根据 Dock 项目信息获取图标（优先使用 IconCacheKey，回退到 ShortcutPath/TargetPath）。
    /// </summary>
    Task<ImageSource?> GetIconAsync(DockItemData item, CancellationToken ct = default);

    /// <summary>
    /// 使指定 Dock 项目的图标缓存失效（主题切换 / 文件变化后刷新）。
    /// </summary>
    void Invalidate(DockItemId id);

    /// <summary>
    /// 预取一批 Dock 项目图标（Dock 首次渲染前可调用）。
    /// </summary>
    Task PrefetchAsync(IEnumerable<DockItemData> items, CancellationToken ct = default);
}
