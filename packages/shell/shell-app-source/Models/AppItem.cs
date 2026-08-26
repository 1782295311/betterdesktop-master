namespace BetterDesktop.Shell.AppSource.Models;

/// <summary>
/// 通用应用数据模型。
/// </summary>
/// <remarks>
/// 主键规则见 <see cref="AppItemId"/>。
/// </remarks>
public sealed record AppItem
{
    /// <summary>
    /// 业务主键（强类型）。
    /// </summary>
    public required AppItemId Id { get; init; }

    /// <summary>
    /// 显示名称。
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// 快捷方式路径（LNK / URL / AppRef 等），可为空。
    /// </summary>
    public string ShortcutPath { get; init; } = string.Empty;

    /// <summary>
    /// 真实目标路径（EXE / AUMID / URL 等）。
    /// </summary>
    public string TargetPath { get; init; } = string.Empty;

    /// <summary>
    /// 应用来源。
    /// </summary>
    public required AppSource Source { get; init; }

    /// <summary>
    /// UWP / Store 应用的 AppUserModelId（普通 Win32 应用为空）。
    /// </summary>
    public string? AppUserModelId { get; init; }

    /// <summary>
    /// UWP / Store 应用的包族名（PackageFamilyName，如
    /// <c>Microsoft.WindowsCalculator_8wekyb3d8bbwe</c>）。普通 Win32 应用为空。
    /// 用于 Store 应用管理界面展示与按包卸载。
    /// </summary>
    public string? PackageFamilyName { get; init; }

    /// <summary>
    /// 图标缓存键（可为空，回退到 ShortcutPath / TargetPath）。
    /// </summary>
    public string? IconCacheKey { get; init; }

    /// <summary>
    /// 卸载命令（来自卸载注册表的 UninstallString，如 <c>MsiExec.exe /X{guid}</c>
    /// 或 <c>"C:\...\uninstall.exe" /S</c>）。仅"已安装"来源（卸载注册表项）有值；
    /// 开始菜单/全程序扫描项为空。用于干净模式右键"卸载"入口。
    /// </summary>
    public string? UninstallCommand { get; init; }
}
