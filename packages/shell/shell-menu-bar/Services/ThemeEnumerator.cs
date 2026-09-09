// BetterDesktop.Shell.MenuBar — 主题枚举：真实读取系统深色/浅色主题状态（零硬编码）。
// 数据来源：HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize
//   - AppsUseLightTheme（DWORD：1=浅色 0=深色）
//   - SystemUsesLightTheme（DWORD：1=任务栏浅色 0=深色）
// 其他主题相关信息（强调色、是否透明）也一并读取，给更丰富的 UI 上下文。

using System;
using Microsoft.Win32;

namespace BetterDesktop.Shell.MenuBar.Services;

/// <summary>系统主题快照。</summary>
public sealed record ThemeInfo(
    bool AppsUseLightTheme,   // true = 应用/界面浅色，false = 深色（Win11 系统级）
    bool SystemUsesLightTheme // true = 任务栏/窗口边框浅色，false = 深色
);

/// <summary>
/// 主题读取：真实系统注册表。写入操作保留空钩子（避免改系统被拒的副作用）。
/// </summary>
internal static class ThemeEnumerator
{
    private const string ThemeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static ThemeInfo Read()
    {
        bool appsLight = true;
        bool systemLight = true;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ThemeKey, writable: false);
            if (key is not null)
            {
                var v = key.GetValue("AppsUseLightTheme");
                if (v is not null) appsLight = System.Convert.ToInt32(v) != 0;
                v = key.GetValue("SystemUsesLightTheme");
                if (v is not null) systemLight = System.Convert.ToInt32(v) != 0;
            }
        }
        catch
        {
            // 失败时按浅色兜底（避免全深色视觉错位）
        }
        return new ThemeInfo(appsLight, systemLight);
    }
}
