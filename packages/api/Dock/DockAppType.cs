namespace BetterDesktop.Shell.Dock.Models;

/// <summary>
/// Dock 应用类型。
/// </summary>
public enum DockAppType
{
    /// <summary>
    /// Win32 桌面应用（默认）。
    /// </summary>
    Win32 = 0,

    /// <summary>
    /// UWP / Store 应用（AppUserModelId 为主键）。
    /// </summary>
    Uwp = 1,

    /// <summary>
    /// URL 快捷方式。
    /// </summary>
    Url = 2
}
