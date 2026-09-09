using System.Collections.Generic;
using BetterDesktop.Shell.Dock.Models;

namespace BetterDesktop.Shell.Dock.Services;

/// <summary>
/// Dock 服务接口，管理 Dock 项目。
/// </summary>
public interface IDockService
{
    /// <summary>
    /// 获取所有 Dock 项目。
    /// </summary>
    IReadOnlyList<DockItemData> Items { get; }

    /// <summary>
    /// 添加 Dock 项目。
    /// </summary>
    void AddItem(DockItemData item);

    /// <summary>
    /// 移除 Dock 项目。
    /// </summary>
    void RemoveItem(DockItemId id);

    /// <summary>
    /// 更新 Dock 项目。
    /// </summary>
    void UpdateItem(DockItemData item);

    /// <summary>
    /// 重新排序 Dock 项目。
    /// </summary>
    void ReorderItems(IReadOnlyList<DockItemId> order);

    /// <summary>
    /// 设置项目活动状态。
    /// </summary>
    void SetItemActive(DockItemId id, bool isActive);
}
