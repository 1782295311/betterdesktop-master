namespace BetterDesktop.Shell.Dock.Models;

/// <summary>
/// 固定项健康态（**由磁盘事实判定，不由 UI 判**）。
/// <para>固定项 = 不可变快照（<c>PinnedItem.AppItem</c>）+ 可变健康态；三态语义见
/// <c>docs/plans/2026-09-13-appgrabber-scope-and-entry-menu.md</c> §6 P5。</para>
/// </summary>
public enum PinnedHealthState
{
    /// <summary>快照路径有效（Store 应用则为 AUMID 仍在已安装列表）——正常显示。</summary>
    Healthy,

    /// <summary>原路径失效，但多级重绑命中同一应用的新路径——用新路径显示并回写快照。</summary>
    Healable,

    /// <summary>各级匹配全失配（视为已卸载）——让位不显示，**但保留快照**，重装同路径即自动回来。</summary>
    Orphaned,
}

/// <summary>单个固定项的健康报告（供设置中心「Dock 固定项」展示与排障）。</summary>
public sealed record PinnedHealth
{
    /// <summary>固定项业务主键（与 <c>DockItemData.Id</c> 同源）。</summary>
    public required DockItemId Id { get; init; }

    /// <summary>显示名（快照里的名称，不回写）。</summary>
    public required string Name { get; init; }

    /// <summary>健康态。</summary>
    public required PinnedHealthState State { get; init; }

    /// <summary>当前可用路径：Healthy = 快照路径；Healable = 重绑后的新路径；Orphaned = null。</summary>
    public string? CurrentPath { get; init; }

    /// <summary>判定依据（可读文案，用于设置中心展示与排障——不静默）。</summary>
    public required string Detail { get; init; }
}
