// BetterDesktop.Shell.Clipboard — 系统右键「剪贴板历史…」注册（Phase B 6.6 I10 / S8 改向）
// 4 场景：文件（*\shell）/ 文件夹（Directory\shell）/ 文件夹背景 / 桌面背景。
// 键名 BetterDesktopClipboardHistory + 已存在避让（不覆盖用户同名项，幂等）。
//
// 命令目标（2026-09-12 S8 起，用户口径 O1）：
//   · 面板 exe 存在 → "<panelExe>" --open —— 宿主未运行时右键仍可用（面板自行探活引擎）；
//   · 面板 exe 缺失 → 回退宿主 --menu-cmd clipboard-history（旧链路，避免右键彻底失效）。

using System;
using System.IO;
using Microsoft.Win32;

namespace BetterDesktop.Shell.Clipboard;

/// <summary>系统右键菜单项注册器（HKCU 免夺权，shell-menu-injection 红线）。</summary>
internal static class ClipboardShellMenuRegistrar
{
    private const string KeyName = "BetterDesktopClipboardHistory";
    private const string DisplayText = "剪贴板历史…";

    /// <summary>
    /// 注入 4 场景右键项（幂等；异常不冒泡，注册失败仅跳过该场景）。
    /// <paramref name="panelExePath"/> 非空且存在 → 命令指向面板 exe（宿主未运行仍可用）；
    /// 否则回退宿主自身 <c>--menu-cmd clipboard-history</c>。
    /// </summary>
    public static void EnsureRegistered(string? panelExePath)
    {
        // 历史残留治理：旧版路径 bug 曾在 4 场景生成空壳键 …\shell\shell（explorer 枚举到
        // 无显示名/无 command 的 shell 键 → 右键出现裸「shell」项，点击导致资源管理器卡顿）。
        // 每次注册前清理，杜绝复活。
        CleanLegacyShellStubs();

        bool usePanel = !string.IsNullOrWhiteSpace(panelExePath) && File.Exists(panelExePath);
        string targetExe = usePanel ? panelExePath! : Environment.ProcessPath ?? string.Empty;
        if (string.IsNullOrEmpty(targetExe))
        {
            return;
        }

        string quoted = $"\"{targetExe}\"";
        string targetName = Path.GetFileName(targetExe);

        // 面板 exe 只开面板，不消费文件/目录参数；宿主旧链路需按场景追加 %1/%V。
        (string RegPath, string Args)[] scenes = usePanel
            ? new[]
            {
                (@"*\shell", string.Empty),
                (@"Directory\shell", string.Empty),
                (@"Directory\Background\shell", string.Empty),
                (@"DesktopBackground\shell", string.Empty),
            }
            : new[]
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
                string path = $@"Software\Classes\{regPath}\{KeyName}";
                string command = usePanel
                    ? $"{quoted} --open"
                    : (args.Length > 0
                        ? $"{quoted} --menu-cmd clipboard-history {args}"
                        : $"{quoted} --menu-cmd clipboard-history");

                using (var existing = Registry.CurrentUser.OpenSubKey(path))
                {
                    if (existing is not null && existing.GetValue("MUIVerb") is not null)
                    {
                        using var cmdKey = existing.OpenSubKey("command");
                        var existingCmd = cmdKey?.GetValue(string.Empty) as string;
                        // 迁移：命令未指向当前目标 exe（旧宿主路径残留 / 后端切换 / 构建输出迁移）→ 更新。
                        // 含目标 exe 文件名即视为已是最新，保持不动。
                        bool stale = existingCmd is null
                            || !existingCmd.Contains(targetName, StringComparison.OrdinalIgnoreCase);
                        if (stale)
                        {
                            using var cmdWrite = Registry.CurrentUser.CreateSubKey(path + @"\command");
                            cmdWrite.SetValue(string.Empty, command);
                            using var keyWrite = Registry.CurrentUser.CreateSubKey(path);
                            keyWrite.SetValue("Icon", quoted);
                        }
                        continue;
                    }
                }

                using var key = Registry.CurrentUser.CreateSubKey(path);
                key.SetValue("MUIVerb", DisplayText);
                key.SetValue("Icon", quoted);
                using var commandKey = key.CreateSubKey("command");
                commandKey.SetValue(string.Empty, command);
            }
            catch (Exception)
            {
                // 注册表被占用/权限异常：跳过该场景，不影响其余与整体启动。
            }
        }
    }

    /// <summary>
    /// 注销 4 场景右键项（幂等；异常不冒泡）。
    /// <para>
    /// 【2026-09-18 形态统一】「剪贴板历史」已改为快照「桌面控制」子菜单里的**功能开关**（带图标 + 勾选框），
    /// 不再使用静态注册项（静态项语义是"打开面板"，与其它开关形态不一致，且只出现在经典菜单）。
    /// 本方法由 ClipboardPlugin 在装配时调用以清理历史静态项——否则经典菜单与新版菜单会各出现一个
    /// 同名但语义不同的入口。打开面板的入口仍保留：托盘「打开剪贴板历史」/ 侧边手柄 / 全局热键。
    /// </para>
    /// </summary>
    public static void Unregister()
    {
        string[] scenes = { @"*\shell", @"Directory\shell", @"Directory\Background\shell", @"DesktopBackground\shell" };
        foreach (var scene in scenes)
        {
            try
            {
                using var parent = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{scene}", writable: true);
                parent?.DeleteSubKeyTree(KeyName, throwOnMissingSubKey: false);
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
