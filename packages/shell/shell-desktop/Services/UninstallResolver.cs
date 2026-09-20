// BetterDesktop.Shell.Desktop — 应用卸载入口解析
// 用途：桌面图标右键"卸载"（用户 2026-09-07 拍板：永久删除对应用改为卸载，唤起应用本体卸载器）。
// 解析链路：快捷方式(.lnk/.url/.exe) → ShellLinkResolver.Resolve 拿目标 exe →
//           按 目标 exe 目录 / 目标 exe 名 / 快捷方式名 匹配卸载注册表（HKCU+HKLM+WOW6432Node 三视图）
//           → 命中返回 UninstallString；未命中返回 false（菜单回退"永久删除"）。
// 进程级缓存 60s：右键高频触发，注册表枚举不能每次跑（对齐 ToolCatalog 同款策略）。

using System;
using System.Collections.Generic;
using System.IO;
using BetterDesktop.Shell.AppSource.Services;

namespace BetterDesktop.Shell.Desktop.Services;

/// <summary>应用卸载入口解析（快捷方式 → 卸载注册表 UninstallString）。</summary>
public static class UninstallResolver
{
    private const int TtlMs = 60_000;

    // 索引：规范化目录 / 规范化名称 → UninstallString
    private static Dictionary<string, string>? _byDir;
    private static Dictionary<string, string>? _byName;
    private static DateTime _cachedAt;

    /// <summary>
    /// 尝试解析快捷方式的卸载入口。
    /// <paramref name="shortcutPath"/> 为 .lnk/.url/.exe 等可执行目标（普通文件/文件夹返回 false）。
    /// 命中时 <paramref name="uninstallCommand"/> = 注册表 UninstallString（如 "MsiExec.exe /X{guid}"）。
    /// </summary>
    public static bool TryResolve(string shortcutPath, out string? uninstallCommand)
    {
        uninstallCommand = null;
        if (string.IsNullOrWhiteSpace(shortcutPath) || !ShellLinkResolver.IsSupportedFile(shortcutPath))
        {
            return false;
        }

        string? targetExe = null;
        try
        {
            var (_, target, _) = ShellLinkResolver.Resolve(shortcutPath);
            targetExe = target;
        }
        catch
        {
            // 解析失败按无目标处理（仍可尝试按快捷方式名匹配）
        }

        var (dirs, names) = GetIndex();

        // ① 目标 exe 所在目录精确匹配（最可靠：uninstall 项的 InstallLocation/DisplayIcon 指向该目录）
        if (!string.IsNullOrWhiteSpace(targetExe))
        {
            var dir = NormalizeDir(Path.GetDirectoryName(targetExe));
            if (dir.Length > 0 && dirs.TryGetValue(dir, out var cmd))
            {
                uninstallCommand = cmd;
                return true;
            }

            // ② 目标 exe 文件名（去扩展）匹配显示名
            var exeName = NormalizeName(Path.GetFileNameWithoutExtension(targetExe));
            if (exeName.Length > 0 && names.TryGetValue(exeName, out var cmd2))
            {
                uninstallCommand = cmd2;
                return true;
            }
        }

        // ③ 快捷方式名匹配显示名（.lnk 未解析出目标时的兜底）
        var lnkName = NormalizeName(Path.GetFileNameWithoutExtension(shortcutPath));
        if (lnkName.Length > 0 && names.TryGetValue(lnkName, out var cmd3))
        {
            uninstallCommand = cmd3;
            return true;
        }

        return false;
    }

    /// <summary>
    /// 唤起应用本体卸载器（cmd /c UninstallString，复刻 shell-dock AppGrabber 同款：
    /// UninstallString 常带参数且路径可能带引号，按 FileName/Arguments 拆分会破坏，cmd 最稳）。
    /// </summary>
    public static void RunUninstaller(string uninstallCommand)
    {
        if (string.IsNullOrWhiteSpace(uninstallCommand))
        {
            return;
        }

        try
        {
            // C1：`/c` 与卸载命令**分成两个参数**传（见 AppEntryActions.RunUninstaller 的同类说明）。
            // uninstallCommand 来自注册表，含引号时字符串拼接会破坏参数边界。
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(uninstallCommand);
            _ = System.Diagnostics.Process.Start(psi);
        }
        catch
        {
            // 卸载器启动失败静默（M10）
        }
    }

    /// <summary>构建（或取缓存）的卸载注册表双索引：规范化目录 / 规范化名称 → UninstallString。</summary>
    private static (Dictionary<string, string> Dirs, Dictionary<string, string> Names) GetIndex()
    {
        if (_byDir is not null && _byName is not null &&
            (DateTime.UtcNow - _cachedAt).TotalMilliseconds < TtlMs)
        {
            return (_byDir, _byName);
        }

        var dirs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var views = new (Microsoft.Win32.RegistryKey Root, string Path)[]
        {
            (Microsoft.Win32.Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Microsoft.Win32.Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Microsoft.Win32.Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
        };

        foreach (var (root, regPath) in views)
        {
            try
            {
                using var parent = root.OpenSubKey(regPath);
                if (parent?.GetSubKeyNames() is not { Length: > 0 } subNames)
                {
                    continue;
                }

                foreach (var subName in subNames)
                {
                    try
                    {
                        using var sub = parent.OpenSubKey(subName);
                        if (sub is null)
                        {
                            continue;
                        }

                        var cmd = sub.GetValue("UninstallString") as string;
                        var displayName = sub.GetValue("DisplayName") as string;
                        if (string.IsNullOrWhiteSpace(cmd) || string.IsNullOrWhiteSpace(displayName))
                        {
                            continue;
                        }

                        // 名称索引（Explorer 卸载面板同名匹配）
                        var nameKey = NormalizeName(displayName);
                        if (nameKey.Length > 0 && !names.ContainsKey(nameKey))
                        {
                            names[nameKey] = cmd;
                        }

                        // 目录索引：InstallLocation + DisplayIcon 所在目录
                        AddDir(dirs, sub.GetValue("InstallLocation") as string, cmd);
                        if (sub.GetValue("DisplayIcon") is string iconRaw)
                        {
                            var iconPath = iconRaw.Split(',')[0].Trim().Trim('"');
                            AddDir(dirs, Path.GetDirectoryName(iconPath), cmd);
                        }
                    }
                    catch
                    {
                        // 单项失败跳过（M10）
                    }
                }
            }
            catch
            {
                // 视图访问失败按无该根处理（M10）
            }
        }

        _byDir = dirs;
        _byName = names;
        _cachedAt = DateTime.UtcNow;
        return (dirs, names);
    }

    private static void AddDir(Dictionary<string, string> dirs, string? dir, string cmd)
    {
        if (string.IsNullOrWhiteSpace(dir))
        {
            return;
        }

        var key = NormalizeDir(dir);
        if (key.Length > 0 && !dirs.ContainsKey(key))
        {
            dirs[key] = cmd;
        }
    }

    private static string NormalizeDir(string? dir)
        => (dir ?? string.Empty).Replace("/", "\\").Trim().TrimEnd('\\').ToLowerInvariant();

    private static string NormalizeName(string name)
        => (name ?? string.Empty).Trim().ToLowerInvariant();
}
