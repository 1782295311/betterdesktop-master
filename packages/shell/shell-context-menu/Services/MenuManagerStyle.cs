// BetterDesktop.Shell.ContextMenus — 管理器菜单样式开关（partial MenuManagerService）
// Win11 经典右键菜单键（网络核验 + 实现后真机验证）：
//   HKCU\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32
//   默认值空串 = Win10 经典样式；键不存在 = Win11 新样式。改后需重启资源管理器。
// 写侧纪律：键已存在则备份先行（RegTreeBackup）→ HKCU 免夺权 → 写；删除同样先备份。

using System;
using BetterDesktop.Kernel.Core;
using Microsoft.Win32;

namespace BetterDesktop.Shell.ContextMenus.Services;

public static partial class MenuManagerService
{
    /// <summary>Win11 经典菜单 CLSID 键（网络核验 [verified]，实现后真机验证）。</summary>
    internal const string ClassicMenuClsid = "{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";

    internal static string ClassicMenuKeyPath =>
        $@"{UserClassesRoot}\CLSID\{ClassicMenuClsid}\InprocServer32";

    /// <summary>Win11 是否启用经典（Win10 样式）右键菜单；非 Win11 恒 false。</summary>
    public static bool IsWin11ClassicMenuEnabled()
        => Environment.OSVersion.Version.Build >= 22000 && IsClassicMenuEnabledAt(ClassicMenuKeyPath);

    /// <summary>切换 Win11 经典菜单（true=经典样式；false=新样式）。非 Win11 抛异常。写前备份。</summary>
    public static void SetWin11ClassicMenu(bool enabled)
    {
        if (Environment.OSVersion.Version.Build < 22000)
        {
            throw new InvalidOperationException("仅 Windows 11 支持切换经典菜单样式");
        }
        SetClassicMenuAt(ClassicMenuKeyPath, enabled);
    }

    // ===== 参数化测试缝（写侧不碰真机键） =====

    internal static bool IsClassicMenuEnabledAt(string keyPath) => KeyExists(keyPath);

    internal static void SetClassicMenuAt(string keyPath, bool enabled)
    {
        if (enabled)
        {
            // 键已存在则先备份（写前纪律）；不存在无需备份直接创建
            if (KeyExists(keyPath))
            {
                _ = RegTreeBackup.Snapshot(keyPath)
                    ?? throw new InvalidOperationException("备份失败，已中止写操作");
            }
            EnsureWritable(keyPath);
            var (root, sub) = RegTakeover.SplitPath(keyPath);
            using var baseKey = RegistryKey.OpenBaseKey(root, RegistryView.Default);
            using var key = baseKey.CreateSubKey(sub, writable: true)
                ?? throw new InvalidOperationException($"无法创建: {keyPath}");
            // 默认值空串 = 经典菜单（红线：禁止用 Registry.SetValue 对不存在的键直写，会静默失败）
            key.SetValue(string.Empty, string.Empty, RegistryValueKind.String);
            DiagnosticLog.Trace("menu-manager", $"启用经典菜单: {keyPath}");
        }
        else
        {
            if (!KeyExists(keyPath))
            {
                return; // 已是新样式，幂等
            }
            _ = RegTreeBackup.Snapshot(keyPath)
                ?? throw new InvalidOperationException("备份失败，已中止写操作");
            DeleteTree(keyPath);
            DiagnosticLog.Trace("menu-manager", $"恢复新样式菜单: {keyPath}");
        }
    }
}
