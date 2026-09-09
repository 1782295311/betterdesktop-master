// BetterDesktop.Shell.ContextMenus — 系统右键菜单管理器引擎（M1 枚举，2026-09-05）
// 对标 ContextMenuManager：场景×双载体（静态 shell / ShellEx COM）枚举。
// 启停/删除在 MenuManagerToggle.cs，新建/注入在 MenuManagerCreate.cs（partial 同类）。
// 【红线】72-右键菜单/windows-context-menu-registry-model：opennewwindow 硬排除；显示名经 @dll,-id 解析。

using System;
using System.Collections.Generic;
using System.Linq;
using BetterDesktop.Kernel.Core;
using Microsoft.Win32;

namespace BetterDesktop.Shell.ContextMenus.Services;

// ── 本文件方法级白话索引（MenuManager 枚举主引擎分片，白话 → 方法）──
//   "枚举某场景的全部菜单项（静态 + ShellEx + 拖放 Handler）" → Enumerate / EnumerateByPath
//   "枚举静态 shell 项（HKLM/HKCU 手动合并，弃用 HKCR 合并视图）" → EnumerateStatic
//   "枚举 ShellEx ContextMenuHandlers / DragDropHandlers" → EnumerateShellex
//   "决定一个项实际写到哪（HKLM 直写 / HKCU 影子）" → ResolveWritePath
//   "读显示名 / 合并值 / 隐藏标记 / command 子键（HKCU 优先、缺失退 HKLM）" → ReadDisplay / MergedValue / MergedFlag / MergedSub
//   启停删除见 MenuManagerToggle.cs，新建注入见 MenuManagerCreate.cs，分组见 MenuManagerGroups.cs，样式见 MenuManagerStyle.cs。
// ────────────────────────────────────

/// <summary>菜单项快照（枚举产物；WritePath 是启停/备份的目标键）。</summary>
public sealed record MenuItemInfo(
    string SceneKey,
    string KeyName,
    string Kind,          // Static | Shellex
    string DisplayName,
    string Origin,        // HKCU | HKLM
    string WritePath,     // 完整注册表路径
    bool Enabled,
    string Clsid,
    string Command,
    // ===== M2 扩展属性（移植 ContextMenuManager ShellItem；全部带默认值保持向后兼容）=====
    string? IconLocation = null,         // Icon 注册表值（"path,index"）
    string? ItemFilePath = null,         // 从 command 反推的 exe/dll 完整路径（ObjectPath.ExtractFilePath）
    string? Position = null,              // top / bottom / null(default)
    bool OnlyWithShift = false,           // Extended 值存在（仅按 Shift 显示）
    bool OnlyInExplorer = false,          // OnlyInBrowserWindow 值存在
    bool NoWorkingDirectory = false,      // NoWorkingDirectory 值存在
    bool NeverDefault = false,            // NeverDefault 值存在
    bool ShowAsDisabledIfHidden = false,  // ShowAsDisabledIfHidden 值存在
    bool HasLUAShield = false,            // HasLUAShield 值存在（管理员小盾牌）
    bool IsMultiItem = false,             // SubCommands 或 ExtendedSubCommandsKey 存在（多级菜单）
    bool IsProtected = false)             // 保护项标记（open/opennewwindow/LnkOpenGuid，启停/删除前需确认）
{
    /// <summary>
    /// 来源标签（新手向 UI 用）：BetterDesktop（自有项，键名 BetterDesk 前缀）/ 自定义（用户新建，键名 UserMenu 前缀）/
    /// 扩展程序（ShellEx COM）/ 系统。
    /// 识别锚点与注入同源（MenuManagerCreate.InjectBetterDeskItems 键名统一 BetterDesk 前缀；
    /// 用户新建项统一 UserMenu 前缀）——禁止另改键名前缀。
    /// </summary>
    public string Source =>
        KeyName.StartsWith("BetterDesk", StringComparison.OrdinalIgnoreCase) ? "BetterDesktop"
        : KeyName.StartsWith("UserMenu", StringComparison.OrdinalIgnoreCase) ? "自定义"
        : Kind == "Shellex" ? "扩展程序"
        : "系统";
}

/// <summary>系统右键菜单管理器引擎（UI 无关，可单测）。</summary>
public static partial class MenuManagerService
{
    internal const string ProtectedKey = "opennewwindow"; // 红线：可枚举但受保护（启停用 HideBasedOnVelocityId，不用 LegacyDisable）
    internal const string DisabledPrefix = "-ContextMenuHandlers";
    internal static readonly string UserClassesRoot = @"HKEY_CURRENT_USER\Software\Classes";

    /// <summary>特殊键名→windows.storage.dll 资源 ID（open/edit/print 等系统项的本地化显示名）。</summary>
    private static readonly Dictionary<string, int> DefaultNameIndexs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["open"] = 8496,
        ["edit"] = 8516,
        ["print"] = 8497,
        ["find"] = 8503,
        ["play"] = 8498,
        ["runas"] = 8505,
        ["explore"] = 8502,
        ["preview"] = 8499,
    };

    /// <summary>场景表（key, 显示名, HKCR 相对路径）。</summary>
    public static readonly (string Key, string Display, string RegPath)[] Scenes =
    [
        ("DesktopBackground", "桌面背景", "DesktopBackground"),
        ("DirectoryBackground", "文件夹内空白", "Directory\\Background"),
        ("Directory", "文件夹", "Directory"),
        ("Folder", "所有文件夹", "Folder"),
        ("Drive", "驱动器", "Drive"),
        ("AllObjects", "所有对象", "AllFilesystemObjects"),
        ("AllFiles", "所有文件", "*"),
        ("LnkFile", "快捷方式", "lnkfile"),
        ("ExeFile", "可执行文件", "exefile"),
        ("Unknown", "未知类型", "Unknown"),
    ];

    /// <summary>枚举一个场景的全部菜单项（静态 + ShellEx 双载体）。</summary>
    public static List<MenuItemInfo> Enumerate(string sceneKey)
    {
        var scene = Scenes.FirstOrDefault(s => s.Key == sceneKey);
        if (scene.Key is null)
        {
            return [];
        }
        return EnumerateByPath(sceneKey, scene.RegPath);
    }

    /// <summary>按场景注册路径枚举（测试缝：任意 HKCR 相对路径）。</summary>
    internal static List<MenuItemInfo> EnumerateByPath(string sceneKey, string regPath)
    {
        var result = EnumerateStatic(sceneKey, regPath);
        result.AddRange(EnumerateShellex(sceneKey, regPath, "ContextMenuHandlers"));
        result.AddRange(EnumerateShellex(sceneKey, regPath, "DragDropHandlers"));
        return result;
    }

    private static List<MenuItemInfo> EnumerateStatic(string sceneKey, string regPath)
    {
        var list = new List<MenuItemInfo>();
        var shellPath = $@"{regPath}\shell";

        // 【2026-09-05 手动合并】弃用 HKCR 合并视图——它有进程级缓存，HKCU 影子键的新建/删除
        // 不会即时反映（实证：影子删除后宿主仍读到幽灵键 → 名字丢失/状态卡死）。
        // 语义对齐递归合并：子键并集，值级 HKCU 优先、缺失回退 HKLM。
        using var lmBase = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default);
        using var cuBase = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        using var lm = lmBase.OpenSubKey(@"SOFTWARE\Classes\" + shellPath);
        using var cu = cuBase.OpenSubKey(@"Software\Classes\" + shellPath);
        if (lm is null && cu is null)
        {
            return list;
        }

        var names = new List<string>();
        if (lm is not null)
        {
            names.AddRange(lm.GetSubKeyNames());
        }
        if (cu is not null)
        {
            foreach (var n in cu.GetSubKeyNames())
            {
                if (!names.Contains(n, StringComparer.OrdinalIgnoreCase))
                {
                    names.Add(n);
                }
            }
        }

        foreach (var name in names)
        {
            // M2：opennewwindow 不再硬排除——可枚举+IsProtected 标记，启停时用 HideBasedOnVelocityId（不用 LegacyDisable）
            try
            {
                using var cuItem = cu?.OpenSubKey(name);
                using var lmItem = lm?.OpenSubKey(name);
                if (cuItem is null && lmItem is null)
                {
                    continue;
                }
                var displayName = ReadDisplay(cuItem, string.Empty);
                if (string.IsNullOrWhiteSpace(displayName))
                {
                    displayName = ReadDisplay(lmItem, string.Empty);
                }
                // 特殊键名资源映射：open/edit/print 等系统项从 windows.storage.dll 取本地化字符串
                if (DefaultNameIndexs.TryGetValue(name, out var resId) && string.IsNullOrWhiteSpace(displayName))
                {
                    displayName = ResourceRef.Resolve($"@windows.storage.dll,-{resId}") ?? name;
                }
                if (string.IsNullOrWhiteSpace(displayName))
                {
                    displayName = name;
                }
                var hidden = MergedFlag(cuItem, lmItem, "HideBasedOnVelocityId")
                    || MergedFlag(cuItem, lmItem, "LegacyDisable")
                    || MergedFlag(cuItem, lmItem, "ProgrammaticAccessOnly");
                var command = MergedSub(cuItem, lmItem, "command")?.GetValue(null) as string ?? string.Empty;
                var (origin, writePath) = ResolveWritePath($@"{shellPath}\{name}");
                // M2 扩展属性（移植 ContextMenuManager ShellItem）
                var iconLocation = MergedValue(cuItem, lmItem, "Icon") as string;
                var itemFilePath = ObjectPath.ExtractFilePath(command);
                var posRaw = (MergedValue(cuItem, lmItem, "Position") as string)?.ToLowerInvariant();
                var position = posRaw is "top" or "bottom" ? posRaw : null;
                var onlyWithShift = MergedFlag(cuItem, lmItem, "Extended");
                var onlyInExplorer = MergedFlag(cuItem, lmItem, "OnlyInBrowserWindow");
                var noWorkingDirectory = MergedFlag(cuItem, lmItem, "NoWorkingDirectory");
                var neverDefault = MergedFlag(cuItem, lmItem, "NeverDefault");
                var showAsDisabledIfHidden = MergedFlag(cuItem, lmItem, "ShowAsDisabledIfHidden");
                var hasLuaShield = MergedFlag(cuItem, lmItem, "HasLUAShield");
                var isMultiItem = MergedFlag(cuItem, lmItem, "SubCommands")
                    || !string.IsNullOrWhiteSpace(MergedValue(cuItem, lmItem, "ExtendedSubCommandsKey") as string);
                var isProtected = name.Equals("open", StringComparison.OrdinalIgnoreCase)
                    || name.Equals(ProtectedKey, StringComparison.OrdinalIgnoreCase);
                list.Add(new MenuItemInfo(sceneKey, name, "Static", displayName, origin, writePath,
                    Enabled: !hidden, Clsid: string.Empty, Command: command,
                    IconLocation: iconLocation, ItemFilePath: itemFilePath, Position: position,
                    OnlyWithShift: onlyWithShift, OnlyInExplorer: onlyInExplorer,
                    NoWorkingDirectory: noWorkingDirectory, NeverDefault: neverDefault,
                    ShowAsDisabledIfHidden: showAsDisabledIfHidden, HasLUAShield: hasLuaShield,
                    IsMultiItem: isMultiItem, IsProtected: isProtected));
            }
            catch (Exception ex)
            {
                DiagnosticLog.Trace("menu-manager", $"静态项枚举跳过 {name}: {ex.Message}");
            }
        }
        return list;
    }

    private static List<MenuItemInfo> EnumerateShellex(string sceneKey, string regPath, string handlersName = "ContextMenuHandlers")
    {
        var list = new List<MenuItemInfo>();
        var handlersPath = $@"{regPath}\shellex\{handlersName}";
        var negativePath = $@"{regPath}\shellex\-{handlersName}";

        // 【手动合并】同 EnumerateStatic——HKCU/HKLM 直读，不经过有进程缓存的 HKCR 合并视图
        using var lmBase = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default);
        using var cuBase = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        using var lm = lmBase.OpenSubKey(@"SOFTWARE\Classes\" + handlersPath);
        using var cu = cuBase.OpenSubKey(@"Software\Classes\" + handlersPath);
        using var lmNeg = lmBase.OpenSubKey(@"SOFTWARE\Classes\" + negativePath);
        using var cuNeg = cuBase.OpenSubKey(@"Software\Classes\" + negativePath);
        if (lm is null && cu is null && lmNeg is null && cuNeg is null)
        {
            return list;
        }

        var positiveNames = new List<string>();
        if (lm is not null)
        {
            positiveNames.AddRange(lm.GetSubKeyNames());
        }
        if (cu is not null)
        {
            foreach (var n in cu.GetSubKeyNames())
            {
                if (!positiveNames.Contains(n, StringComparer.OrdinalIgnoreCase))
                {
                    positiveNames.Add(n);
                }
            }
        }

        foreach (var name in positiveNames)
        {
            try
            {
                using var cuSub = cu?.OpenSubKey(name);
                using var lmSub = lm?.OpenSubKey(name);
                var cuClsid = cuSub?.GetValue(null) as string;
                var lmClsid = lmSub?.GetValue(null) as string;
                // 影子屏蔽：HKCU 键存在 + CLSID 空 + HKLM 原键有 CLSID（用户区屏蔽 HKLM 项）
                var shadowed = cuSub is not null && lmSub is not null
                    && string.IsNullOrWhiteSpace(cuClsid) && !string.IsNullOrWhiteSpace(lmClsid);
                var clsid = !string.IsNullOrWhiteSpace(cuClsid) ? cuClsid
                    : !string.IsNullOrWhiteSpace(lmClsid) ? lmClsid
                    : name;
                var displayName = ResolveClsidName(clsid) ?? name;
                // 影子项 WritePath 指向 HKLM 原始键（恢复 = 删影子键，语义见 MenuManagerToggle）
                var (origin, writePath) = shadowed
                    ? ("HKLM", @"HKLM\SOFTWARE\Classes\" + handlersPath + @"\" + name)
                    : !string.IsNullOrWhiteSpace(cuClsid)
                        ? ("HKCU", UserClassesRoot + @"\" + handlersPath + @"\" + name)
                        : ("HKLM", @"HKLM\SOFTWARE\Classes\" + handlersPath + @"\" + name);
                var (iconLoc, itemPath, isProt) = ResolveShellexExtras(clsid);
                list.Add(new MenuItemInfo(sceneKey, name, "Shellex", displayName, origin, writePath,
                    Enabled: !shadowed, Clsid: clsid, Command: string.Empty,
                    IconLocation: iconLoc, ItemFilePath: itemPath, IsProtected: isProt));
            }
            catch (Exception ex)
            {
                DiagnosticLog.Trace("menu-manager", $"ShellEx 枚举跳过 {name}: {ex.Message}");
            }
        }

        // 已停用：负前缀兄弟键（shellex\-ContextMenuHandlers）中不在启用集里的项
        var negativeNames = new List<string>();
        if (lmNeg is not null)
        {
            negativeNames.AddRange(lmNeg.GetSubKeyNames());
        }
        if (cuNeg is not null)
        {
            foreach (var n in cuNeg.GetSubKeyNames())
            {
                if (!negativeNames.Contains(n, StringComparer.OrdinalIgnoreCase))
                {
                    negativeNames.Add(n);
                }
            }
        }
        foreach (var name in negativeNames)
        {
            if (positiveNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }
            try
            {
                using var cuSub = cuNeg?.OpenSubKey(name);
                using var lmSub = lmNeg?.OpenSubKey(name);
                var clsid = (cuSub?.GetValue(null) as string) is { Length: > 0 } v1 ? v1
                    : (lmSub?.GetValue(null) as string) is { Length: > 0 } v2 ? v2
                    : name;
                var displayName = ResolveClsidName(clsid) ?? name;
                var (origin, writePath) = ResolveWritePath($@"{negativePath}\{name}");
                var (iconLoc, itemPath, isProt) = ResolveShellexExtras(clsid);
                list.Add(new MenuItemInfo(sceneKey, name, "Shellex", displayName, origin, writePath,
                    Enabled: false, Clsid: clsid, Command: string.Empty,
                    IconLocation: iconLoc, ItemFilePath: itemPath, IsProtected: isProt));
            }
            catch (Exception ex)
            {
                DiagnosticLog.Trace("menu-manager", $"停用项枚举跳过 {name}: {ex.Message}");
            }
        }
        return list;
    }

    /// <summary>合并值读取（HKCU 优先，缺失回退 HKLM；对齐 HKCR 递归合并的值级语义）。</summary>
    private static object? MergedValue(RegistryKey? cu, RegistryKey? lm, string name)
        => cu?.GetValue(name) is { } v ? v : lm?.GetValue(name);

    /// <summary>合并标记读取（值存在于任一蜂巢即为真——递归合并是并集语义）。</summary>
    private static bool MergedFlag(RegistryKey? cu, RegistryKey? lm, string name)
        => MergedValue(cu, lm, name) is not null;

    /// <summary>合并子键读取（HKCU 优先，缺失回退 HKLM）。</summary>
    private static RegistryKey? MergedSub(RegistryKey? cu, RegistryKey? lm, string name)
        => cu?.OpenSubKey(name) ?? lm?.OpenSubKey(name);

    // ===== 内部助手（partial 共享） =====

    internal static bool KeyExists(string regPath)
    {
        var (root, sub) = RegTakeover.SplitPath(regPath);
        using var baseKey = RegistryKey.OpenBaseKey(root, RegistryView.Default);
        using var key = baseKey.OpenSubKey(sub);
        return key is not null;
    }

    internal static (string Origin, string WritePath) ResolveWritePath(string relativePath)
    {
        var user = UserClassesRoot + @"\" + relativePath;
        if (KeyExists(user))
        {
            return ("HKCU", user);
        }
        return ("HKLM", @"HKLM\SOFTWARE\Classes\" + relativePath);
    }

    private static string ReadDisplay(RegistryKey? item, string fallback)
    {
        if (item is null)
        {
            return fallback;
        }
        var mui = item.GetValue("MUIVerb") as string;
        if (!string.IsNullOrWhiteSpace(mui))
        {
            return ResourceRef.Resolve(mui) ?? fallback;
        }
        var def = item.GetValue(null) as string;
        return !string.IsNullOrWhiteSpace(def) ? ResourceRef.Resolve(def) ?? fallback : fallback;
    }

    /// <summary>CLSID → 显示名（M2 改用 GuidInfo 引擎：字典+注册表+FileVersionInfo 六链反查；非 GUID 键名直接回显）。</summary>
    private static string? ResolveClsidName(string clsid)
    {
        if (!Guid.TryParse(clsid.Trim('{', ' ', '}'), out var guid))
        {
            return clsid;
        }
        return GuidInfo.GetText(guid);
    }

    /// <summary>快捷方式打开项 CLSID（受保护，禁用/删除前需确认）。</summary>
    private static readonly Guid LnkOpenGuid = new("00021401-0000-0000-c000-000000000046");

    /// <summary>ShellEx 项扩展属性（图标位置/dll路径/保护项标记）。</summary>
    private static (string? IconLocation, string? ItemFilePath, bool IsProtected) ResolveShellexExtras(string clsid)
    {
        if (!Guid.TryParse(clsid.Trim('{', ' ', '}'), out var guid))
        {
            return (null, null, false);
        }
        string? iconLoc = GuidInfo.GetIconLocation(guid) is { } loc ? $"{loc.Path},{loc.Index}" : null;
        string? itemPath = GuidInfo.GetFilePath(guid);
        bool isProt = guid == LnkOpenGuid;
        return (iconLoc, itemPath, isProt);
    }
}
