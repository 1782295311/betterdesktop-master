// BetterDesktop 启动器 —— 组件定位（同目录 → 安装根 → %LOCALAPPDATA%\BetterDesktop）。
//
// 【为什么不各写一份】仓库对"查找链"有明确纪律：桌面服务的三级定位已有单一实现
//（Shell.Core 的 DesktopControlLocator），启动器直接复用它，不再复刻。
// 其余组件名与目录约定同 tray/AppPaths 与 watchdog/Program.cs 的 BuildTargets（跨进程契约）。

using System;
using System.Collections.Generic;
using System.IO;
using BetterDesktop.Kernel.Deployment;
using BetterDesktop.Shell.Core.DesktopControl;

namespace BetterDesktop.Launcher.Services;

/// <summary>组件路径解析。</summary>
internal static class ComponentPaths
{
    /// <summary>启动器自身所在目录（publish 后 = 安装根）。</summary>
    public static string BaseDir => AppContext.BaseDirectory;

    /// <summary>数据目录 %LOCALAPPDATA%\BetterDesktop（留痕文件与日志都在这儿）。</summary>
    public static string DataDir => DeploymentInfo.DirectoryPath;

    /// <summary>日志目录。</summary>
    public static string LogDir => Path.Combine(DataDir, "logs");

    /// <summary>设置文件 %APPDATA%\BetterDesktop\settings.json（跨进程唯一真相）。</summary>
    public static string SettingsFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "BetterDesktop",
        "settings.json");

    /// <summary>当前安装根（deployment.json 记录；未用安装器安装时为 null）。</summary>
    public static string? InstallRoot => DeploymentInfo.ResolveInstallRoot();

    /// <summary>
    /// 查找顺序：调用方同目录 → 安装根 → %LOCALAPPDATA%\BetterDesktop。
    /// 与 DesktopControlLocator 同一约定（自己的那份优先，开发态 bin 直接跑也不受影响）。
    /// </summary>
    public static IReadOnlyList<string> SearchDirs
    {
        get
        {
            var dirs = new List<string> { BaseDir };
            if (InstallRoot is { } root && !string.Equals(root, BaseDir, StringComparison.OrdinalIgnoreCase))
            {
                dirs.Add(root);
            }

            dirs.Add(DataDir);
            return dirs;
        }
    }

    /// <summary>按文件名（可含相对子路径，如 <c>native\X.dll</c>）定位；找不到返回 null。</summary>
    public static string? Find(string fileName)
    {
        foreach (var dir in SearchDirs)
        {
            try
            {
                var candidate = Path.Combine(dir, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (Exception)
            {
                // 非法路径组合（极少见）：继续找下一处，不阻断启动
            }
        }

        return null;
    }

    /// <summary>目录是否存在（如 <c>engines</c> 第三方引擎树）。</summary>
    public static bool DirectoryExists(string relativePath)
    {
        foreach (var dir in SearchDirs)
        {
            try
            {
                if (Directory.Exists(Path.Combine(dir, relativePath)))
                {
                    return true;
                }
            }
            catch (Exception)
            {
                // 同上
            }
        }

        return false;
    }

    /// <summary>桌面服务 exe（复用仓库既有三级定位链）。</summary>
    public static string? DesktopControlExe => DesktopControlLocator.Find();

    /// <summary>留痕文件绝对路径（*-stopped.flag / watchdog-pause.flag 等）。</summary>
    public static string FlagPath(string flagName) => Path.Combine(DataDir, flagName);

    /// <summary>某个留痕文件当前是否存在。</summary>
    public static bool FlagExists(string flagName)
    {
        try
        {
            return File.Exists(FlagPath(flagName));
        }
        catch (Exception)
        {
            return false;
        }
    }
}
