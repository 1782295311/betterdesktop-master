using System;
using System.Diagnostics;
using System.IO;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Kernel.Deployment;

namespace BetterDesktop.Shell.Settings.Services;

/// <summary>
/// 系统管理真实逻辑：自启注册表、配置重置/导出、缓存清理、日志目录打开。
/// 全部为静态工具，调用方（SystemSection 按钮）负责 UI 线程与确认交互。
/// </summary>
public static class SystemManagement
{

    private static string SettingsFilePath
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BetterDesktop", "settings.json");

    private static string LogDirectory
        => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    /// <summary>
    /// 设置开机自启（HKCU）。
    /// 【2026-09-17】改走 <see cref="AutostartRegistrar"/>：Run 键 + StartupApproved **双写**——
    /// 只写 Run 键会被任务管理器的"禁用"状态静默压制（技术力 7430 红线 1，本仓此前两处都踩了）。
    /// 指向目标也一并修正：以前写的是"当前进程 exe"（从独立设置进程点开关会把**设置中心**登记成自启），
    /// 现在优先登记组件目录/安装根里的 BetterDesktop.Host.exe（这开关的语义就是"开机启动桌面环境"）。
    /// </summary>
    public static void SetAutoStart(bool enabled)
    {
        var exe = ResolveComponent("BetterDesktop.Host.exe") ?? Environment.ProcessPath;
        if (enabled && string.IsNullOrWhiteSpace(exe))
        {
            return;
        }

        if (!AutostartRegistrar.Set(AutostartRegistrar.ShellValueName, enabled ? exe : null, out var error))
        {
            DiagnosticLog.Trace("shell.settings", $"开机自启设置失败（enabled={enabled}）：{error}");
        }
    }

    /// <summary>查询当前是否已配置开机自启（含"任务管理器是否禁用"这一层，见 AutostartRegistrar.IsEnabled）。</summary>
    public static bool IsAutoStartEnabled()
        => AutostartRegistrar.IsEnabled(AutostartRegistrar.ShellValueName);

    /// <summary>
    /// 解析组件路径：同目录优先 → 安装根（deployment.json）→ 同目录父级兜底。
    /// 独立设置进程与宿主不在同一目录时，"同目录"会落空，故必须带安装根这一层。
    /// </summary>
    private static string? ResolveComponent(string exeName)
    {
        try
        {
            var sameDir = Path.Combine(AppContext.BaseDirectory, exeName);
            if (File.Exists(sameDir))
            {
                return sameDir;
            }

            var root = BetterDesktop.Kernel.Deployment.DeploymentInfo.ResolveInstallRoot();
            if (root is not null)
            {
                var installed = Path.Combine(root, exeName);
                if (File.Exists(installed))
                {
                    return installed;
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.settings", $"解析组件路径失败（{exeName}）：{ex.Message}");
        }

        return null;
    }

    /// <summary>看门狗注册表键名（HKCU\Run）—— 现在只用于**清理**旧值，见 <see cref="SetWatchdogAutoStart"/>。</summary>
    private const string WatchdogAppName = "BetterDesktop.Watchdog";

    /// <summary>
    /// 看门狗开机自启：**已随 S4-4（2026-09-20）退役**（那个进程不存在了，监护职责迁入 core）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 方法签名保留（设置中心仍在调用），但语义只剩一件事：**清掉历史遗留的 Run 值**。
    /// 这才是当前真实且**用户可见**的故障 —— 旧版本注册的自启指向 `BetterDesktop.Watchdog.exe`，
    /// 文件已不存在，于是每次开机都**静默失败**：Windows 找不到目标，而用户看不到任何提示。
    /// </para>
    /// <para>
    /// 注意原先的 <c>ResolveComponent</c> 保护只挡住了"指向不存在的**新**路径"，
    /// 挡不住**已经写进注册表的旧值** —— 所以这里改成无条件清理。
    /// </para>
    /// <para>
    /// `enabled = true` 不再"注册"任何东西：已经没有可注册的进程了；
    /// 继续写一个指向不存在文件的路径，等于把那个静默失败再生产一次。
    /// </para>
    /// </remarks>
    public static void SetWatchdogAutoStart(bool enabled)
    {
        _ = enabled; // 参数保留只为不改调用方签名；两种取值都走同一条"清理"路径。
        if (!AutostartRegistrar.Set(AutostartRegistrar.WatchdogValueName, null, out var error))
        {
            DiagnosticLog.Trace("shell.settings", $"清理看门狗自启失败：{error}");
        }
    }

    /// <summary>看门狗是否已配置开机自启：**恒 false**（它已退役；且上面那一路会把旧值清掉）。</summary>
    /// <remarks>
    /// 刻意不返回"注册表里是否还有值"：那是一个**正在被清理的残留**，
    /// 把它显示成"已启用"只会让用户以为这个开关还有意义。
    /// </remarks>
    public static bool IsWatchdogAutoStartEnabled() => false;

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
