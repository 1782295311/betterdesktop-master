// BetterDesktop.Shell.ContextMenus — 管理器启停/删除（M1 分片 2，partial MenuManagerService）
// 可逆启停：静态=隐藏标记三选一（版本红线）；ShellEx=键移动到 -ContextMenuHandlers（explorer 跳过负前缀）。
// 写侧纪律：备份先行（RegTreeBackup）→ HKLM 夺权（RegTakeover）→ 写 → 诊断。
//
// 【HKCU 影子屏蔽 2026-09-05】宿主非管理员时（SeTakeOwnership 不可用），HKLM 项走影子路径：
// HKCR 合并视图 HKCU 优先——在 HKCU\Software\Classes\<同路径> 建同名键写空 CLSID/LegacyDisable，
// explorer 读到影子键后跳过加载 = 等效禁用；恢复 = 删影子键。全程免管理员。

using System;
using BetterDesktop.Kernel.Core;
using Microsoft.Win32;

namespace BetterDesktop.Shell.ContextMenus.Services;

// ── 本文件方法级白话索引（MenuManager 启停/删除分片，白话 → 方法）──
//   "启用/禁用一个菜单项（总入口，自动区分静态项/ShellEx 项）" → Toggle
//   "彻底删除一个菜单项（写前先 RegTreeBackup）" → Delete
//   "非管理员的影子屏蔽路径（HKCU 镜像屏蔽 HKLM 项）" → ShadowPathOf / CreateShadow / DeleteShadow
//   "静态项的隐藏/恢复（LegacyDisable 等隐藏标记三选一）" → HideStatic / ShowStatic
//   "ShellEx 项禁用/启用（键移入/移出 -ContextMenuHandlers）" → DisableShellex / EnableShellex
// ────────────────────────────────────

public static partial class MenuManagerService
{
    private const string HiddenVelocity = "HideBasedOnVelocityId";
    private const int VelocityValue = 0x639bc8;

    private static bool IsWin10_1703Plus =>
        Environment.OSVersion.Version >= new Version(10, 0, 15063);

    /// <summary>切换启停（可逆；备份先行）。返回操作描述；保护项返回 [PROTECTED] 前缀由 UI 层弹确认；失败抛异常由 UI 捕获显示。</summary>
    public static string Toggle(MenuItemInfo item)
    {
        // 保护项（open/opennewwindow/LnkOpenGuid）：禁用前需 UI 层确认
        if (item.IsProtected && item.Enabled)
        {
            return $"[PROTECTED]「{item.DisplayName}」是系统保护项，确认禁用？";
        }
        return item.Kind == "Shellex"
            ? (item.Enabled ? DisableShellex(item) : EnableShellex(item))
            : (item.Enabled ? HideStatic(item) : ShowStatic(item));
    }

    /// <summary>删除（备份后整树移除；ShellEx 建议用启停而非删除；保护项返回 [PROTECTED] 前缀）。</summary>
    public static string Delete(MenuItemInfo item)
    {
        if (item.IsProtected)
        {
            return $"[PROTECTED]「{item.DisplayName}」是系统保护项，确认删除？";
        }
        _ = RegTreeBackup.Snapshot(item.WritePath)
            ?? throw new InvalidOperationException("备份失败，已中止删除");
        DeleteTree(item.WritePath);
        return $"已删除「{item.DisplayName}」（备份可恢复）";
    }

    /// <summary>HKCU 影子键路径（遮蔽 HKLM 同名键；相对路径剥离根段后落 HKCU\Software\Classes）。</summary>
    internal static string ShadowPathOf(string writePath)
    {
        var (root, sub) = RegTakeover.SplitPath(writePath);
        _ = root; // 任意根都映射到 HKCU\Software\Classes 同一子路径
        // 【2026-09-05 修复】必须剥离 HKLM\SOFTWARE\Classes 或 HKCU\Software\Classes 前缀，
        // 否则 sub 自带前缀 → UserClassesRoot + sub 产生双重 SOFTWARE\Classes 路径，
        // 影子键落到 Explorer 读不到的位置 → 非管理员模式启停全废（真机实证）。
        if (sub.StartsWith(@"SOFTWARE\Classes\", StringComparison.OrdinalIgnoreCase))
        {
            sub = sub[@"SOFTWARE\Classes\".Length..];
        }
        else if (sub.StartsWith(@"Software\Classes\", StringComparison.OrdinalIgnoreCase))
        {
            sub = sub[@"Software\Classes\".Length..];
        }
        return UserClassesRoot + @"\" + sub;
    }

    private static bool IsShadowPresent(string writePath) => KeyExists(ShadowPathOf(writePath));

    /// <summary>在 HKCU 建影子键（默认值写空；支持指定 RegistryValueKind，HideBasedOnVelocityId 需 DWord）。</summary>
    private static void CreateShadow(string writePath, string valueName, object value, RegistryValueKind kind = RegistryValueKind.String)
    {
        var shadow = ShadowPathOf(writePath);
        var (root, sub) = RegTakeover.SplitPath(shadow);
        using var baseKey = RegistryKey.OpenBaseKey(root, RegistryView.Default);
        using var key = baseKey.CreateSubKey(sub, writable: true)
            ?? throw new InvalidOperationException($"影子键创建失败: {shadow}");
        key.SetValue(valueName, value, kind);
    }

    private static void DeleteShadow(string writePath)
    {
        var shadow = ShadowPathOf(writePath);
        if (KeyExists(shadow))
        {
            DeleteTree(shadow);
        }
    }

    private static string HideStatic(MenuItemInfo item)
    {
        // 非管理员 + HKLM 项 → 影子屏蔽（HKCU 同名 verb 键遮蔽 + LegacyDisable，explorer 读取即隐藏）
        if (item.WritePath.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase) && !Environment.IsPrivilegedProcess)
        {
            // 【红线】opennewwindow 不能用 LegacyDisable（会导致 Win+E 和任务栏 Explorer 图标错误访问），用 HideBasedOnVelocityId
            if (item.KeyName.Equals(ProtectedKey, StringComparison.OrdinalIgnoreCase))
            {
                CreateShadow(item.WritePath, HiddenVelocity, VelocityValue, RegistryValueKind.DWord);
            }
            else
            {
                CreateShadow(item.WritePath, "LegacyDisable", string.Empty);
            }
            return $"已隐藏「{item.DisplayName}」（系统区项目以用户区屏蔽，无需管理员，可随时恢复）";
        }

        _ = RegTreeBackup.Snapshot(item.WritePath)
            ?? throw new InvalidOperationException("备份失败，已中止写操作");
        EnsureWritable(item.WritePath);
        using var key = OpenWritable(item.WritePath)
            ?? throw new InvalidOperationException($"无法打开: {item.WritePath}");
        if (IsWin10_1703Plus)
        {
            key.SetValue(HiddenVelocity, VelocityValue, RegistryValueKind.DWord);
        }
        else
        {
            key.SetValue("LegacyDisable", string.Empty, RegistryValueKind.String);
            key.SetValue("ProgrammaticAccessOnly", string.Empty, RegistryValueKind.String);
        }
        return $"已隐藏「{item.DisplayName}」（备份已存，可逆）";
    }

    private static string ShowStatic(MenuItemInfo item)
    {
        // 影子屏蔽的恢复：仅 HKLM 项可能有 HKCU 影子键；HKCU 项自映射会误判，直接走正常删标记路径
        if (item.WritePath.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase) && IsShadowPresent(item.WritePath))
        {
            DeleteShadow(item.WritePath);
            return $"已显示「{item.DisplayName}」（已移除用户区屏蔽）";
        }

        RegTreeBackup.Snapshot(item.WritePath);
        using var key = OpenWritable(item.WritePath)
            ?? throw new InvalidOperationException($"无法打开: {item.WritePath}");
        foreach (var marker in new[] { HiddenVelocity, "LegacyDisable", "ProgrammaticAccessOnly" })
        {
            try { key.DeleteValue(marker, throwOnMissingValue: false); }
            catch (Exception ex) { DiagnosticLog.Trace("menu-manager", $"删标记失败 {marker}: {ex.Message}"); }
        }
        return $"已显示「{item.DisplayName}」";
    }

    private static string DisableShellex(MenuItemInfo item)
    {
        // 非管理员 + HKLM 项 → 影子屏蔽（HKCU 同名 handler 键 + 空 CLSID，explorer 枚举时跳过加载）
        if (item.WritePath.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase) && !Environment.IsPrivilegedProcess)
        {
            CreateShadow(item.WritePath, string.Empty, string.Empty);
            return $"已停用「{item.DisplayName}」（用户区屏蔽，重启桌面后生效，可随时恢复）";
        }

        // 从 WritePath 自动检测 handlersName（ContextMenuHandlers / DragDropHandlers），支持两类 ShellEx 移键启停
        string handlersName = item.WritePath.Contains("DragDropHandlers", StringComparison.OrdinalIgnoreCase)
            ? "DragDropHandlers" : "ContextMenuHandlers";
        var target = item.WritePath.Replace($@"\{handlersName}\", $@"\-{handlersName}\");
        _ = RegTreeBackup.Snapshot(item.WritePath)
            ?? throw new InvalidOperationException("备份失败，已中止写操作");
        EnsureWritable(item.WritePath);
        EnsureWritable(target);
        CopyTree(item.WritePath, target);
        DeleteTree(item.WritePath);
        return $"已停用「{item.DisplayName}」（键移入 {DisabledPrefix}，可逆）";
    }

    private static string EnableShellex(MenuItemInfo item)
    {
        // 影子屏蔽的恢复优先：仅 HKLM 项可能有 HKCU 影子键；HKCU 项自映射会误判
        if (item.WritePath.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase) && IsShadowPresent(item.WritePath))
        {
            DeleteShadow(item.WritePath);
            return $"已启用「{item.DisplayName}」（已移除用户区屏蔽）";
        }

        // 停用项 WritePath 含 "\-ContextMenuHandlers\" 或 "\-DragDropHandlers\"——Replace 必须匹配带连字符的形态，
        // 否则 backup==自身 → 自拷后删键 → 正键永远无法恢复（真机单测实证）。
        string handlersName = item.WritePath.Contains("-DragDropHandlers", StringComparison.OrdinalIgnoreCase)
            ? "DragDropHandlers" : "ContextMenuHandlers";
        var positive = item.WritePath.Replace($@"\-{handlersName}\", $@"\{handlersName}\");
        if (!KeyExists(item.WritePath))
        {
            throw new InvalidOperationException($"停用键不存在: {item.WritePath}");
        }
        RegTreeBackup.Snapshot(item.WritePath);
        EnsureWritable(item.WritePath);
        EnsureWritable(positive);
        CopyTree(item.WritePath, positive);
        DeleteTree(item.WritePath);
        return $"已启用「{item.DisplayName}」";
    }
}
