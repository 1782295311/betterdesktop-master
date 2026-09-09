// BetterDesktop.Shell.Clipboard — 系统右键「剪贴板历史…」注册（Phase B 6.6 I10）
// 4 场景：文件（*\shell 带 "%1"）/ 文件夹（Directory\shell 带 "%V"）/ 文件夹背景 / 桌面背景（不带参）。
// 键名 BetterDesktopClipboardHistory + 已存在避让（不覆盖用户同名项，幂等）；命令 = 宿主 --menu-cmd clipboard-history（单实例管道）。

using System;
using Microsoft.Win32;

namespace BetterDesktop.Shell.Clipboard;

/// <summary>系统右键菜单项注册器（HKCU 免夺权，shell-menu-injection 红线）。</summary>
internal static class ClipboardShellMenuRegistrar
{
    private const string KeyName = "BetterDesktopClipboardHistory";
    private const string DisplayText = "剪贴板历史…";

    /// <summary>注入 4 场景右键项（幂等：同名键已存在则跳过；异常不冒泡，注册失败仅记日志由调用方处理）。</summary>
    public static void EnsureRegistered()
    {
        string? hostPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(hostPath))
        {
            return;
        }

        // 历史残留治理：旧版路径 bug 曾在 4 场景生成空壳键 …\shell\shell（explorer 枚举到
        // 无显示名/无 command 的 shell 键 → 右键出现裸「shell」项，点击导致资源管理器卡顿）。
        // 每次注册前清理，杜绝复活。
        CleanLegacyShellStubs();

        string host = $"\"{hostPath}\"";
        (string RegPath, string Args)[] scenes =
        {
            (@"*\shell", "\"%1\""),
            (@"Directory\shell", "\"%V\""),
            (@"Directory\Background\shell", string.Empty),
            (@"DesktopBackground\shell", string.Empty),
        };

        foreach (var (regPath, args) in scenes)
        {
            try
            {
                // regPath 已含 shell（如 "*\shell"），这里只追加键名。
                string path = $@"Software\Classes\{regPath}\{KeyName}";
                string command = args.Length > 0
                    ? $"{host} --menu-cmd clipboard-history {args}"
                    : $"{host} --menu-cmd clipboard-history";
                // 避让：已有同名项不覆盖（保持用户优先）。例外：command 指向 BetterDesktop.Host.exe
                // 的旧路径（构建输出迁移/宿主升级）时更新为新路径，保证右键入口始终可用。
                using (var existing = Registry.CurrentUser.OpenSubKey(path))
                {
                    if (existing is not null && existing.GetValue("MUIVerb") is not null)
                    {
                        using var cmdKey = existing.OpenSubKey("command");
                        var existingCmd = cmdKey?.GetValue(string.Empty) as string;
                        if (existingCmd is not null &&
                            existingCmd.Contains("BetterDesktop.Host.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            using var cmdWrite = Registry.CurrentUser.CreateSubKey(path + @"\command");
                            cmdWrite.SetValue(string.Empty, command);
                            // 旧注册无图标：顺带补齐（图标 = 宿主 exe，标识 BetterDesktop 身份）。
                            if (existing.GetValue("Icon") is null)
                            {
                                using var keyWrite = Registry.CurrentUser.CreateSubKey(path);
                                keyWrite.SetValue("Icon", host);
                            }
                        }
                        continue;
                    }
                }

                using var key = Registry.CurrentUser.CreateSubKey(path);
                key.SetValue("MUIVerb", DisplayText);
                key.SetValue("Icon", host);
                using var commandKey = key.CreateSubKey("command");
                commandKey.SetValue(string.Empty, command);
            }
            catch (Exception)
            {
                // 注册表被占用/权限异常：跳过该场景，不影响其余与整体启动。
            }
        }
    }

    /// <summary>清理旧版路径 bug 留下的空壳 …\shell\shell 键（4 场景）；键不存在则静默跳过。</summary>
    private static void CleanLegacyShellStubs()
    {
        string[] scenes = { @"*\shell", @"Directory\shell", @"Directory\Background\shell", @"DesktopBackground\shell" };
        foreach (var scene in scenes)
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{scene}\shell", throwOnMissingSubKey: false);
            }
            catch (Exception)
            {
                // 注册表被占用/权限异常：跳过该场景，不影响其余与整体启动。
            }
        }
    }
}
