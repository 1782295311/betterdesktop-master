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
                // 避让：已存在（含用户自定义同名项）不覆盖，保持幂等与用户优先。
                using (var existing = Registry.CurrentUser.OpenSubKey(path))
                {
                    if (existing is not null && existing.GetValue("MUIVerb") is not null)
                    {
                        continue;
                    }
                }

                using var key = Registry.CurrentUser.CreateSubKey(path);
                key.SetValue("MUIVerb", DisplayText);
                string command = args.Length > 0
                    ? $"{host} --menu-cmd clipboard-history {args}"
                    : $"{host} --menu-cmd clipboard-history";
                using var commandKey = key.CreateSubKey("command");
                commandKey.SetValue(string.Empty, command);
            }
            catch (Exception)
            {
                // 注册表被占用/权限异常：跳过该场景，不影响其余与整体启动。
            }
        }
    }
}
