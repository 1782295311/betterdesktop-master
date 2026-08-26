using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace BetterDesktop.Shell.Settings.Services;

/// <summary>
/// 系统管理真实逻辑：自启注册表、配置重置/导出、缓存清理、日志目录打开。
/// 全部为静态工具，调用方（SystemSection 按钮）负责 UI 线程与确认交互。
/// </summary>
public static class SystemManagement
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "BetterDesktop";

    private static string SettingsFilePath
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BetterDesktop", "settings.json");

    private static string LogDirectory
        => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    /// <summary>设置开机自启（写入 HKCU\Run）。</summary>
    public static void SetAutoStart(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key is null)
        {
            return;
        }

        if (enabled)
        {
            // 当前可执行文件完整路径；host 入口可能为 BetterDesktop.Host.exe
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe))
            {
                return;
            }

            key.SetValue(AppName, $"\"{exe}\"");
        }
        else
        {
            if (key.GetValue(AppName) is not null)
            {
                key.DeleteValue(AppName, throwOnMissingValue: false);
            }
        }
    }

    /// <summary>查询当前是否已配置开机自启。</summary>
    public static bool IsAutoStartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key is not null && key.GetValue(AppName) is not null;
    }

    /// <summary>
    /// 看门狗常驻 exe 名称（与 Host 同目录，由 host/Bootstrap 或 watchdog 自身安装时放置）。
    /// </summary>
    private const string WatchdogExeName = "BetterDesktop.Watchdog.exe";

    /// <summary>看门狗注册表键名（HKCU\Run）。</summary>
    private const string WatchdogAppName = "BetterDesktop.Watchdog";

    /// <summary>
    /// 设置看门狗开机自启（写入 HKCU\Run，指向同目录的 BetterDesktop.Watchdog.exe）。
    /// 看门狗为外部常驻进程（C3），负责 Host 崩溃时拉起，shell 替代必须常驻。
    /// </summary>
    public static void SetWatchdogAutoStart(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key is null)
        {
            return;
        }

        if (enabled)
        {
            var hostDir = Path.GetDirectoryName(Environment.ProcessPath);
            if (string.IsNullOrWhiteSpace(hostDir))
            {
                return;
            }

            var watchdogExe = Path.Combine(hostDir, WatchdogExeName);
            // 仅当看门狗 exe 确实存在时才写入（避免指向不存在的路径导致启动失败）
            if (!File.Exists(watchdogExe))
            {
                return;
            }

            key.SetValue(WatchdogAppName, $"\"{watchdogExe}\"");
        }
        else
        {
            if (key.GetValue(WatchdogAppName) is not null)
            {
                key.DeleteValue(WatchdogAppName, throwOnMissingValue: false);
            }
        }
    }

    /// <summary>查询看门狗是否已配置开机自启。</summary>
    public static bool IsWatchdogAutoStartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key is not null && key.GetValue(WatchdogAppName) is not null;
    }

    /// <summary>查询看门狗进程当前是否正在运行（C3 常驻）。</summary>
    public static bool IsWatchdogRunning()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("BetterDesktop.Watchdog"))
            {
                p.Dispose();
                return true;
            }
        }
        catch
        {
            // 枚举失败当作不在
        }
        return false;
    }

    /// <summary>重置所有设置：删除 settings.json（下次启动重建默认）。</summary>
    public static void ResetSettings()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                File.Delete(SettingsFilePath);
            }
        }
        catch
        {
            // 删除失败不抛：可能被占用，下次启动仍可覆盖
        }
    }

    /// <summary>导出配置到桌面备份（settings.json → 桌面\settings-备份-时间戳.json）。</summary>
    public static string? ExportConfig()
    {
        if (!File.Exists(SettingsFilePath))
        {
            return null;
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var dest = Path.Combine(LogDirectory, $"settings-backup-{stamp}.json");
        try
        {
            File.Copy(SettingsFilePath, dest, overwrite: true);
            return dest;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>清除缓存目录（%LOCALAPPDATA%\BetterDesktop\cache，如不存在则无操作）。</summary>
    public static void ClearCache()
    {
        try
        {
            var cacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BetterDesktop", "cache");
            if (Directory.Exists(cacheDir))
            {
                Directory.Delete(cacheDir, recursive: true);
            }
        }
        catch
        {
            // 缓存清理失败不阻断（可能文件被占用）
        }
    }

    /// <summary>打开日志所在目录（桌面，含 BetterDesktop_debug.log / BetterDesktop_crash.log）。</summary>
    public static void OpenLogDirectory()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = LogDirectory,
                UseShellExecute = true
            });
        }
        catch
        {
            // ShellExecute 失败（如无文件管理器）静默忽略
        }
    }
}
