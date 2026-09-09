// BetterDesktop.Shell.ContextMenus — 管理器扩展分组 / 整体开关（partial MenuManagerService）
// 新手向 UI 的数据模型：场景内的菜单项按「来源」归组——
//   BetterDesktop（自有，键名前缀）→ 一组；ShellEx（扩展程序，按 CLSID 合并同名应用）→ 每组一扩展；
//   其余静态项 → 「系统」组（UI 只读提示）。
// 整体开关 = 组内逐项设置目标状态（复用单项 Toggle，单一真源；幂等）。

using System;
using System.Collections.Generic;
using System.Linq;
using BetterDesktop.Kernel.Core;

namespace BetterDesktop.Shell.ContextMenus.Services;

/// <summary>场景内的扩展分组（UI 渲染单元）。</summary>
public sealed record MenuExtensionGroup(
    string Key,                              // 分组键：BetterDesktop / CLSID / System
    string Name,                             // 显示名（应用名 / BetterDesktop / 命令项）
    string Source,                           // BetterDesktop | 扩展程序 | 系统
    IReadOnlyList<MenuItemInfo> Items);

public static partial class MenuManagerService
{
    private const string BetterDeskPrefix = "BetterDesk";

    /// <summary>组整体启用状态：组内全部启用 = 开；任一停用 = 关（混合按关处理，点击整体开关即全部启用）。</summary>
    public static bool IsGroupEnabled(MenuExtensionGroup group) => group.Items.All(i => i.Enabled);

    /// <summary>
    /// 按来源分组（纯函数，可单测）。分组规则：
    /// 1. BetterDesktop 自有项（键名 BetterDesk 前缀）→ 单组，排最前；
    /// 2. 自定义项（用户新建，键名 UserMenu 前缀）→ 单组；
    /// 3. ShellEx 项按 CLSID 合并（同名应用一个扩展）→ 组名取首个解析名；
    /// 4. 其余静态项 → 「系统」组，排最后（UI 只读）。
    /// </summary>
    public static IReadOnlyList<MenuExtensionGroup> GroupExtensions(IEnumerable<MenuItemInfo> items)
    {
        var list = items.ToList();

        var better = list.Where(i => i.Source == "BetterDesktop").ToList();
        var custom = list.Where(i => i.Source == "自定义").ToList();
        var shellex = list.Where(i => i.Source == "扩展程序").ToList();
        var system = list.Where(i => i.Source == "系统").ToList();

        var groups = new List<MenuExtensionGroup>();
        if (better.Count > 0)
        {
            groups.Add(new MenuExtensionGroup("BetterDesktop", "BetterDesktop", "BetterDesktop", better));
        }
        if (custom.Count > 0)
        {
            groups.Add(new MenuExtensionGroup("Custom", "我创建的菜单项", "自定义", custom));
        }
        foreach (var g in shellex.GroupBy(i => i.Clsid, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.First().DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var first = g.First();
            groups.Add(new MenuExtensionGroup(first.Clsid, first.DisplayName, "扩展程序", g.ToList()));
        }
        if (system.Count > 0)
        {
            groups.Add(new MenuExtensionGroup("System", "命令项", "系统", system));
        }
        return groups;
    }

    /// <summary>
    /// 整体开关：把组内全部项设置到目标状态（enable）。幂等——已处于目标状态的项跳过。
    /// 【真实状态判定】每次以注册表当前状态为准（不用组快照）：同一 group 重复调用不会反向翻转。
    /// 注意：单项 Toggle 按「调用方传入的 Enabled」决定隐藏/显示方向，必须用真实状态修正快照（with 表达式）
    /// 再调用，否则陈旧快照会让整体开关方向错乱（2026-09-05 单测实证）。
    /// </summary>
    public static string ToggleGroup(MenuExtensionGroup group, bool enable)
    {
        var changed = 0;
        var errors = new List<string>();
        foreach (var item in group.Items)
        {
            var real = IsCurrentlyEnabled(item);
            if (real == enable)
            {
                continue;
            }
            try
            {
                _ = Toggle(item with { Enabled = real });
                changed++;
            }
            catch (Exception ex)
            {
                errors.Add($"{item.DisplayName}: {ex.Message}");
            }
        }
        var verb = enable ? "开启" : "关闭";
        var summary = changed == 0
            ? $"「{group.Name}」已处于{verb}状态"
            : $"已{verb}「{group.Name}」（{changed} 项）";
        return errors.Count == 0 ? summary : summary + "；部分失败：" + string.Join("；", errors);
    }

    /// <summary>读取某项的注册表真实启停状态（单一真源；影子屏蔽优先，ShellEx 看 WritePath 正键，静态项看隐藏标记）。</summary>
    internal static bool IsCurrentlyEnabled(MenuItemInfo item)
    {
        try
        {
            // 仅 HKLM 项可能有 HKCU 影子屏蔽；HKCU 项自映射会误判（影子路径=项本身）
            if (item.WritePath.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase) && IsShadowPresent(item.WritePath))
            {
                return false; // HKCU 影子屏蔽 = 用户区禁用态
            }
            if (item.Kind == "Shellex")
            {
                return item.WritePath.Contains(@"\ContextMenuHandlers\", StringComparison.OrdinalIgnoreCase);
            }
            var (root, sub) = RegTakeover.SplitPath(item.WritePath);
            using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(root, Microsoft.Win32.RegistryView.Default);
            using var key = baseKey.OpenSubKey(sub);
            if (key is null)
            {
                return false;
            }
            return key.GetValue("HideBasedOnVelocityId") is null
                && key.GetValue("LegacyDisable") is null
                && key.GetValue("ProgrammaticAccessOnly") is null;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("menu-manager", $"状态读取失败 {item.WritePath}: {ex.Message}");
            return item.Enabled;
        }
    }
}
