namespace BetterDesktop.Shell.AppSource.Models;

/// <summary>
/// 应用来源类型。
/// </summary>
public enum AppSource
{
    /// <summary>
    /// 开始菜单快捷方式 (.lnk / .url / .appref-ms)。
    /// </summary>
    StartMenu,

    /// <summary>
    /// 已安装程序（卸载注册表项）。
    /// </summary>
    Installed,

    /// <summary>
    /// UWP / Microsoft Store 应用。
    /// </summary>
    Store,

    /// <summary>
    /// 用户手动添加的文件或快捷方式。
    /// </summary>
    UserAdded
}
