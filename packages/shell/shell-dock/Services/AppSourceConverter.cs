using System;
using BetterDesktop.Shell.AppSource.Models;
using BetterDesktop.Shell.Dock.Models;

namespace BetterDesktop.Shell.Dock.Services;

/// <summary>
/// app-source 模型与 Dock 模型之间的类型映射工具。
/// 用于 dock 侧在消费 app-source 的 <see cref="AppSource"/> 时转换为 Dock 专用的 <see cref="DockAppType"/>。
/// </summary>
public static class AppSourceConverter
{
    /// <summary>
    /// 将 app-source 的来源类型映射为 Dock 应用类型。
    /// <list type="bullet">
    ///   <item>Store → Uwp；</item>
    ///   <item>其余默认 Win32，但若目标路径为 .url 快捷方式则识别为 Url。</item>
    /// </list>
    /// </summary>
    public static DockAppType ToDockAppType(BetterDesktop.Shell.AppSource.Models.AppSource source, string? path)
    {
        if (source == BetterDesktop.Shell.AppSource.Models.AppSource.Store)
        {
            return DockAppType.Uwp;
        }

        if (!string.IsNullOrWhiteSpace(path)
            && path.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
        {
            return DockAppType.Url;
        }

        return DockAppType.Win32;
    }
}
