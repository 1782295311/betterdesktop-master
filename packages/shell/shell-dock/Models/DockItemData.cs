namespace BetterDesktop.Shell.Dock.Models;

/// <summary>
/// Dock 项目数据模型（真实应用版）。
/// </summary>
/// <remarks>
/// 主键规则：
/// - Win32 / Url：以 <see cref="ShortcutPath"/> 作为稳定主键来源；
/// - UWP：以 <see cref="AppUserModelId"/> 作为稳定主键来源。
/// </remarks>
public sealed record DockItemData
{
    /// <summary>
    /// 业务主键（强类型）。
    /// </summary>
    public required DockItemId Id { get; init; }

    /// <summary>
    /// 显示名称。
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// 快捷方式路径（LNK / URL / AppRef 等）。
    /// </summary>
    public required string ShortcutPath { get; init; }

    /// <summary>
    /// 真实目标路径（EXE / AUMID / URL 等）。
    /// </summary>
    public required string TargetPath { get; init; }

    /// <summary>
    /// 应用类型。
    /// </summary>
    public required DockAppType AppType { get; init; }

    /// <summary>
    /// UWP / Store 应用的 AppUserModelId（普通 Win32 应用为空）。
    /// </summary>
    public string? AppUserModelId { get; init; }

    /// <summary>
    /// 图标缓存键（可为空，回退到 ShortcutPath / TargetPath）。
    /// </summary>
    public string? IconCacheKey { get; init; }

    /// <summary>
    /// 是否正在运行。
    /// </summary>
    public bool IsRunning { get; init; }

    /// <summary>
    /// 是否固定在 Dock。
    /// </summary>
    public bool IsPinned { get; init; } = true;

    /// <summary>
    /// 通知角标数量。
    /// </summary>
    public int BadgeCount { get; init; }

    /// <summary>
    /// 卸载命令（来自卸载注册表 UninstallString）。仅"已安装"来源有值，其余为空。
    /// 用于干净模式右键"卸载"入口；全程序模式不暴露（直接删 exe 不清理残留）。
    /// </summary>
    public string? UninstallCommand { get; init; }
}
