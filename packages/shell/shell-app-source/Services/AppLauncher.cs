using System;
using System.Diagnostics;
using BetterDesktop.Shell.AppSource.Models;

namespace BetterDesktop.Shell.AppSource.Services;

/// <summary>
/// 统一应用启动入口：UWP / Store 应用（AUMID）与普通 Win32 应用（路径）走同一 API。
/// - AUMID 启动：ShellExecute `shell:AppsFolder\{aumid}`（虚拟文件夹命名空间项）。
/// - 路径启动：ShellExecute 目标路径。
/// 所有异常静默（M10），返回 false 表示未启动。
/// </summary>
public static class AppLauncher
{
    /// <summary>启动应用。AUMID 优先于路径（Store 应用两者同时给出时按 AUMID）。</summary>
    public static bool Launch(AppItem app)
    {
        if (app is null)
        {
            return false;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(app.AppUserModelId))
            {
                Process.Start(new ProcessStartInfo($"shell:AppsFolder\\{app.AppUserModelId}")
                {
                    UseShellExecute = true
                });
                return true;
            }

            var path = !string.IsNullOrWhiteSpace(app.TargetPath) ? app.TargetPath : app.ShortcutPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch
        {
            // 启动失败静默（M10）。
            return false;
        }
    }
}
