// BetterDesktop.Shell.ContextMenus — 注册表静态 verb 枚举（M2 计划 §3.1）
// 领域模型：TECH-KNOWLEDGE/72-右键菜单/windows-context-menu-registry-model.md（L2 源码级）。
// 显示名链：MUIVerb > 项默认值(仅非子菜单) > 内置名称字典(@windows.storage.dll,-id) > KeyName；
// `@dll,-id` 一律经 SHLoadIndirectString 解析（失败回退原文）；文本 ≥80 字符跳过（系统同款隐藏）。
// 可见性：HideBasedOnVelocityId=0x639bc8 / LegacyDisable / ProgrammaticAccessOnly / CommandFlags%16>=8。
//
// 【2026-09-03 诊断修复批】
//  P0-A 缓存键去路径化：静态 verb 集合只由 类型|扩展名|感知类型 决定，与具体路径无关——
//       键含路径导致"右键 a.txt 预热 a.txt，换 b.txt 又是空"，用户对每个新文件只看到一次空菜单。
//       且注册表静态枚举是毫秒级操作 → 取消"首帧返空+后台预热"，未命中走同步快速通道
//       （异步预热仅保留给 COM in-proc 链，见 ShellMenuContributor）。缓存加容量上限。
//  P1-A CommandStore 双根：真实位置 = HKCU/HKLM 的 Explorer\CommandStore\shell（HKCU 优先合并）；
//       此前写死 HKCR\CommandStore\shell——实测该键不存在，SubCommands 多级项（360 压缩等）全灭。
//  P1-B 补 ExtendedSubCommandsKey（7-Zip 式外部键子菜单；值为相对 HKCR 的键路径）。
//  P0-C ExplorerCommandHandler / command\DelegateExecute 形态产出 ModernVerb（IExplorerCommand
//       通道消费，见 ExplorerCommandInterop）——不再 return null 双链丢弃（百度网盘/迅雷等）。
//  P1-C 场景表补齐：Drive\shell、DesktopBackground\shell、Directory\Background\shell、感知类型
//       （HKCR\<ext> 的 PerceivedType 值 → SystemFileAssociations\<type>\shell）。
//  P1-D Invoke 改 explorer 语义：拆 exe/参数 + UseShellExecute，废弃 cmd /c（元字符截断/
//       引号嵌套坑——"看到了点了没反应"的根因）。
//  P2-C AppliesTo 简单谓词（扩展名/文件大小，AND/OR 组合；未知属性保守显示）。
//  小错修正：多级母项 isMultiItem 传参与 SubCommands/Extended 判定同源（默认值不再盖过 MUIVerb）。

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using BetterDesktop.Shell.ContextMenus.Contracts;
using BetterDesktop.Shell.Core.Native;
using Microsoft.Win32;

namespace BetterDesktop.Shell.ContextMenus.Services;

/// <summary>一个注册表静态右键项（解析产物；SubCommands/ExtendedSubCommandsKey 多级项带 Children 树）。
/// 语义对齐 ContextMenuManager-master ShellItem.cs/ShellSubMenuDialog.cs（L2 源码级核对）。</summary>
public sealed record RegistryVerb(
    string KeyName,
    string Text,
    string Command,
    bool Extended,
    string? IconPath,
    bool NoWorkingDirectory,
    string RegPath,
    IReadOnlyList<RegistryVerb>? Children = null,
    string? AppliesTo = null,
    bool HasLUAShield = false,
    bool IsSeparator = false);

/// <summary>
/// 现代动词（ExplorerCommandHandler / command\DelegateExecute 形态）。实现的是 IExplorerCommand
/// 而非 IContextMenu，静态命令分支拿不到、COM 透传 QI 也必然失败——由 ExplorerCommandInterop
/// 通道消费（P0-C：此前两条链都不取，百度网盘/迅雷等 Win10 后形态全部消失）。
/// </summary>
public sealed record ModernVerb(string KeyName, string GuidString, string RegPath, string? Text = null);

/// <summary>一次场景枚举产物（静态 verb + 现代动词）。</summary>
internal sealed record SceneResult(IReadOnlyList<RegistryVerb> Verbs, IReadOnlyList<ModernVerb> Modern);

/// <summary>注册表静态右键项枚举 + 执行（缓存进程级，键去路径化）。</summary>
public static class RegistryVerbs
{
    /// <summary>缓存 TTL（注册表变更重启进程或 5min 后生效——变更频率极低，无需监听）。</summary>
    internal static TimeSpan Ttl = TimeSpan.FromMinutes(5);

    /// <summary>间接字符串解析缝（测试注入；生产 = SHLoadIndirectString）。</summary>
    internal static Func<string, string?>? IndirectStringResolver { get; set; }

    /// <summary>CommandStore 根测试缝（返回打开的 RegistryKey 列表；null = 生产双根）。</summary>
    internal static Func<IReadOnlyList<RegistryKey>?>? CommandStoreRootsOverride { get; set; }

    /// <summary>CommandStore 真实子路径（P1-A 实证：HKCR\CommandStore\shell 不存在）。</summary>
    internal const string CommandStoreSubPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\CommandStore\shell";

    private const int Capacity = 256;
    private static readonly ConcurrentDictionary<string, (SceneResult Result, DateTime Stamp)> Cache = new();

    /// <summary>内置动词名 → windows.storage.dll 资源 id（72-文档 DefaultNameIndexs）。</summary>
    private static readonly Dictionary<string, int> DefaultNameIndexs = new(StringComparer.OrdinalIgnoreCase)
    {
        { "open", 8496 }, { "edit", 8516 }, { "print", 8497 }, { "find", 8503 },
        { "play", 8498 }, { "runas", 8505 }, { "explore", 8502 }, { "preview", 8499 },
    };

    /// <summary>模板已提供的高频项（避免重复出现）。</summary>
    private static readonly HashSet<string> SuppressedKeys = new(StringComparer.OrdinalIgnoreCase)
    { "open", "openas", "runas", "edit", "print", "preview", "delete", "rename", "cut", "copy", "paste" };

    // ===== 对外取数 =====

    /// <summary>目标文件/目录适用的注册表 verb（P0-A：未命中同步加载，首帧即出；不含路径条件按文件评估）。</summary>
    public static IReadOnlyList<RegistryVerb> GetFor(FileKind kind, string path)
        => FilterAppliesTo(GetScene(CacheKey(kind, path), () => LoadScene(kind, path)).Verbs, path);

    /// <summary>目标适用的现代动词（ExplorerCommand 形态；P0-C）。</summary>
    public static IReadOnlyList<ModernVerb> GetModernFor(FileKind kind, string path)
        => GetScene(CacheKey(kind, path), () => LoadScene(kind, path)).Modern;

    /// <summary>桌面空白（Background 场景；P1-C：Git Bash here/WSL/终端的注册位置）。</summary>
    public static IReadOnlyList<RegistryVerb> GetForBackground()
        => GetScene("bg", LoadBackgroundScene).Verbs;

    private static SceneResult GetScene(string cacheKey, Func<SceneResult> load)
    {
        if (Cache.TryGetValue(cacheKey, out var hit) && DateTime.UtcNow - hit.Stamp < Ttl)
        {
            return hit.Result;
        }

        // P0-A：注册表静态枚举毫秒级 → 同步快速通道（取消"本次返空，第二次右键才出现"）。
        SceneResult loaded;
        try
        {
            loaded = load();
        }
        catch
        {
            loaded = new SceneResult([], []); // 注册表读失败 = 无第三方项（M10）
        }
        Cache[cacheKey] = (loaded, DateTime.UtcNow);
        Prune();
        return loaded;
    }

    /// <summary>容量上限治理：超限先清过期条目，仍超则按最旧逐出（键空间小，正常到不了）。</summary>
    private static void Prune()
    {
        if (Cache.Count < Capacity)
        {
            return;
        }
        var cutoff = DateTime.UtcNow - Ttl;
        foreach (var stale in Cache.Where(kv => kv.Value.Stamp < cutoff).Select(kv => kv.Key).ToList())
        {
            Cache.TryRemove(stale, out _);
        }
        while (Cache.Count >= Capacity)
        {
            var oldest = Cache.OrderBy(kv => kv.Value.Stamp).First().Key;
            Cache.TryRemove(oldest, out _);
        }
    }

    // ===== 解析纯函数（internal = 测试缝） =====

    /// <summary>显示名回退链（72-文档红线 2）。isMultiItem=true 时项默认值不参与。</summary>
    internal static string ResolveDisplayName(string keyName, string? muiVerb, string? defaultValue, bool isMultiItem)
    {
        foreach (var raw in new[] { muiVerb, isMultiItem ? null : defaultValue })
        {
            var text = ResolveIndirect(raw);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        if (DefaultNameIndexs.TryGetValue(keyName, out var idx))
        {
            var name = ResolveIndirect($"@windows.storage.dll,-{idx}");
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }
        return keyName; // 兜底：永不空串（72-文档不变量 2）
    }

    /// <summary>可见性（72-文档红线 4）。</summary>
    internal static bool IsVerbVisible(bool hideBasedOnVelocity, bool legacyDisable, bool programmaticAccessOnly, int commandFlags)
    {
        if (hideBasedOnVelocity)
        {
            return false;
        }
        if (legacyDisable || programmaticAccessOnly)
        {
            return false;
        }
        if (commandFlags % 16 >= 8)
        {
            return false;
        }
        return true;
    }

    /// <summary>命令占位符替换：%1/%L/%V → "目标路径"（引号包裹）；其余占位符原样保留由目标程序处理。</summary>
    internal static string BuildCommand(string rawCommand, string targetPath)
    {
        if (string.IsNullOrWhiteSpace(rawCommand))
        {
            return string.Empty;
        }
        var quoted = $"\"{targetPath}\"";
        var result = rawCommand;
        foreach (var ph in new[] { "%1", "%L", "%V", "%v", "%l" })
        {
            result = result.Replace(ph, quoted, StringComparison.Ordinal);
        }
        return result.Trim();
    }

    internal static bool IsTextValid(string? text) => !string.IsNullOrWhiteSpace(text) && text.Length < 80;

    /// <summary>
    /// 间接字符串解析：@dll,-id → SHLoadIndirectString；其他原样返回。
    /// 对齐前辈 ResourceString.GetDirectString：@ 形态解析失败返回 null（调用链兜底 KeyName），
    /// 不回退原文——显示 "@xxx.dll,-123" 这类垃圾文本比没文本更糟。
    /// </summary>
    internal static string? ResolveIndirect(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        if (!raw.StartsWith('@'))
        {
            return raw;
        }
        if (IndirectStringResolver is { } resolver)
        {
            return resolver(raw);
        }
        var buffer = new StringBuilder(1024);
        return NativeMethods.SHLoadIndirectString(raw, buffer, buffer.Capacity, IntPtr.Zero) == 0
            ? buffer.ToString()
            : null;
    }

    // ===== 缓存键 / 场景表 =====

    /// <summary>缓存键（P0-A）：绝不含目标路径——静态 verb 集合只由 类型|扩展名|感知类型 决定。</summary>
    internal static string CacheKey(FileKind kind, string path)
    {
        var ext = Path.GetExtension(path);
        return $"{kind}|{ext.ToLowerInvariant()}|{PerceivedTypeOf(ext).ToLowerInvariant()}";
    }

    /// <summary>PerceivedType 读取（值在 HKCR\&lt;ext&gt; 键下，非子键；P1-C 感知类型场景依据）。</summary>
    internal static string PerceivedTypeOf(string ext)
    {
        if (string.IsNullOrEmpty(ext))
        {
            return string.Empty;
        }
        try
        {
            return Registry.GetValue($@"HKEY_CLASSES_ROOT\{ext}", "PerceivedType", null) as string ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 打开方式 ProgID 解析（语义对齐前辈 FileExtension.GetOpenMode）：
    /// UserChoice 优先 → HKCR\&lt;ext&gt; 默认值；校验：键存在、长度 ≤255、排除 Applications\ 前缀
    /// （Applications\X 只用于"打开方式"清单，不是可展开 shell 场景的 ProgID）。
    /// </summary>
    internal static string? ResolveProgId(string ext)
    {
        if (string.IsNullOrEmpty(ext))
        {
            return null;
        }
        try
        {
            var userChoice = Registry.GetValue(
                $@"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{ext}\UserChoice",
                "ProgId", null) as string;
            var mergedDefault = Registry.GetValue($@"HKEY_CLASSES_ROOT\{ext}", null, null) as string;
            foreach (var candidate in new[] { userChoice, mergedDefault })
            {
                if (ProgIdValid(candidate))
                {
                    return candidate;
                }
            }
        }
        catch
        {
            // 解析失败按无 ProgID 场景（M10）
        }
        return null;
    }

    internal static bool ProgIdValid(string? progId)
    {
        if (string.IsNullOrWhiteSpace(progId) || progId.Length > 255)
        {
            return false;
        }
        if (progId.StartsWith(@"Applications\", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        // G1（2026-09-04 审查）：RegistryKey 持有原生 HKEY——必须 using，禁止丢弃靠终结器兜底
        using var key = Registry.ClassesRoot.OpenSubKey(progId);
        return key is not null;
    }

    /// <summary>静态 shell 场景根（72-文档场景表 + P1-C 补齐；ProgID 解析经 ResolveProgId）。background=true 为桌面空白/目录背景。</summary>
    internal static List<string> SceneShellRoots(FileKind kind, string ext, string perceivedType, string? progId, bool background)
    {
        var roots = new List<string>();
        if (background)
        {
            roots.Add(@"DesktopBackground\shell");
            roots.Add(@"Directory\Background\shell");
            return roots;
        }

        roots.Add(@"*\shell");
        roots.Add(@"AllFilesystemObjects\shell");
        if (kind is FileKind.Folder or FileKind.Drive)
        {
            roots.Add(@"Folder\shell");
            roots.Add(@"Directory\shell");
            if (kind == FileKind.Drive)
            {
                roots.Add(@"Drive\shell"); // P1-C：BitLocker/WSL 等驱动器项
            }
        }
        else
        {
            if (!string.IsNullOrEmpty(ext))
            {
                roots.Add($@"SystemFileAssociations\{ext}\shell");
                roots.Add($@"{ext}\shell");
                if (!string.IsNullOrWhiteSpace(progId))
                {
                    roots.Add($@"{progId}\shell");
                }
            }
            if (!string.IsNullOrEmpty(perceivedType))
            {
                roots.Add($@"SystemFileAssociations\{perceivedType}\shell");
            }
        }
        return roots;
    }

    // ===== 枚举（P0-A：同步快速通道） =====

    internal static SceneResult LoadScene(FileKind kind, string path)
    {
        var ext = Path.GetExtension(path);
        var roots = SceneShellRoots(kind, ext, PerceivedTypeOf(ext), ResolveProgId(ext), background: false);
        return LoadRoots(roots, path);
    }

    internal static SceneResult LoadBackgroundScene()
        => LoadRoots(SceneShellRoots(FileKind.None, string.Empty, string.Empty, null, background: true), null);

    private static SceneResult LoadRoots(IReadOnlyList<string> roots, string? targetPath)
    {
        var items = new List<RegistryVerb>();
        var modern = new List<ModernVerb>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            using var shellKey = Registry.ClassesRoot.OpenSubKey(root);
            if (shellKey is null)
            {
                continue;
            }
            foreach (var name in shellKey.GetSubKeyNames())
            {
                if (name.StartsWith("SHELLEX", StringComparison.OrdinalIgnoreCase)
                    || SuppressedKeys.Contains(name)
                    || !seen.Add($"{root}|{name}"))
                {
                    continue;
                }

                switch (LoadVerb(shellKey, name, targetPath))
                {
                    case RegistryVerb verb:
                        items.Add(verb);
                        break;
                    case ModernVerb mv:
                        modern.Add(mv);
                        break;
                }
            }
        }
        return new SceneResult(items, modern);
    }

    private static object? LoadVerb(RegistryKey shellKey, string name, string? targetPath, int depth = 0, bool isSubItem = false)
    {
        try
        {
            if (depth > 3)
            {
                return null; // 层级护栏
            }

            using var key = shellKey.OpenSubKey(name);
            if (key is null)
            {
                return null;
            }

            var hideBasedOnVelocity = key.GetValue("HideBasedOnVelocityId") is { } v && Convert.ToInt64(v) == 0x639bc8;
            var legacyDisable = key.GetValue("LegacyDisable") is not null;
            var programmaticAccessOnly = key.GetValue("ProgrammaticAccessOnly") is not null;
            var commandFlags = key.GetValue("CommandFlags") is { } f && int.TryParse(f.ToString(), out var flags) ? flags : 0;

            // 语义对齐前辈 ShellItem.ItemVisible + ShellSubMenuDialog（L2 核对）：
            // ① HideBasedOnVelocityId 全局生效（Win10 1703+）；
            // ② LegacyDisable/ProgrammaticAccessOnly/CommandFlags%16>=8 【不作用于子菜单项】——
            //    此前我们对 CommandStore 子命令也套用，误杀系统自带子命令；
            // ③ 子项 CommandFlags%16>=8 在子菜单中渲染为【分隔线】而非隐藏。
            if (isSubItem && commandFlags % 16 >= 8)
            {
                return new RegistryVerb(
                    KeyName: name, Text: string.Empty, Command: string.Empty,
                    Extended: false, IconPath: null, NoWorkingDirectory: false,
                    RegPath: $@"{shellKey.Name}\{name}",
                    IsSeparator: true);
            }
            if (hideBasedOnVelocity)
            {
                return null;
            }
            if (!isSubItem && (legacyDisable || programmaticAccessOnly || commandFlags % 16 >= 8))
            {
                return null;
            }

            var muiVerb = key.GetValue("MUIVerb") as string;
            var defaultValue = key.GetValue(null) as string;
            var extendedRef = (key.GetValue("ExtendedSubCommandsKey") as string)?.Trim();
            var isMulti = key.GetValue("SubCommands") is not null || !string.IsNullOrEmpty(extendedRef);

            string? rawCommand;
            using (var commandKey = key.OpenSubKey("command"))
            {
                rawCommand = commandKey?.GetValue(null) as string ?? string.Empty;

                // P0-C：现代动词判定（ExplorerCommandHandler 优先于 command\DelegateExecute）
                var explorerCommand = (key.GetValue("ExplorerCommandHandler") as string)
                    ?? commandKey?.GetValue("DelegateExecute") as string;
                if (!string.IsNullOrWhiteSpace(explorerCommand)
                    && Guid.TryParse(explorerCommand.Trim('{', ' ', '}'), out _))
                {
                    var modernText = MenuText.FromWin32(ResolveDisplayName(name, muiVerb, defaultValue, isMultiItem: false));
                    return new ModernVerb(name, explorerCommand, $@"{shellKey.Name}\{name}",
                        IsTextValid(modernText) ? modernText : null);
                }
            }

            IReadOnlyList<RegistryVerb> children;
            if (isMulti)
            {
                var list = new List<RegistryVerb>();
                var subCommands = key.GetValue("SubCommands") as string;

                if (!string.IsNullOrWhiteSpace(subCommands))
                {
                    // P1-A：CommandStore 双根（HKCU 优先、HKLM 兜底，跨根按键名去重）。
                    // 前辈 ShellSubMenuDialog 语义：列表项 "|" = 分隔线；列表项顺序即子菜单顺序。
                    var sepIndex = 0;
                    foreach (var storeRoot in OpenCommandStoreRoots())
                    {
                        using (storeRoot)
                        {
                            foreach (var raw in subCommands.Split([';', ' '], StringSplitOptions.RemoveEmptyEntries))
                            {
                                var trimmed = raw.Trim();
                                if (trimmed == "|")
                                {
                                    list.Add(new RegistryVerb(
                                        KeyName: "|", Text: string.Empty, Command: string.Empty,
                                        Extended: false, IconPath: null, NoWorkingDirectory: false,
                                        RegPath: $@"{shellKey.Name}\|{sepIndex++}",
                                        IsSeparator: true));
                                    continue;
                                }
                                if (LoadVerb(storeRoot, trimmed, targetPath, depth + 1, isSubItem: true) is not RegistryVerb child
                                    || (child is { IsSeparator: false }
                                        && list.Any(c => string.Equals(c.KeyName, child.KeyName, StringComparison.OrdinalIgnoreCase))))
                                {
                                    continue;
                                }
                                list.Add(child);
                            }
                        }
                    }
                }
                else
                {
                    // P1-B：ExtendedSubCommandsKey（外部键）——值指向一个场景键，
                    // 子命令在 <值>\shell 子键下（对齐前辈 ShellSubMenuDialog 与 MSDN 形态）；
                    // 回退：<值> 直接子键（兼容个别软件的非标准写法）→ verb\Shell 子项。
                    RegistryKey? extRoot = null;
                    if (!string.IsNullOrEmpty(extendedRef))
                    {
                        extRoot = Registry.ClassesRoot.OpenSubKey($@"{extendedRef}\shell")
                            ?? Registry.ClassesRoot.OpenSubKey(extendedRef);
                    }
                    if (extRoot is not null)
                    {
                        using (extRoot)
                        {
                            foreach (var childName in extRoot.GetSubKeyNames())
                            {
                                if (childName.StartsWith("SHELLEX", StringComparison.OrdinalIgnoreCase))
                                {
                                    continue;
                                }
                                if (LoadVerb(extRoot, childName, targetPath, depth + 1, isSubItem: true) is RegistryVerb child)
                                {
                                    list.Add(child);
                                }
                            }
                        }
                    }

                    if (list.Count == 0)
                    {
                        using var subShell = key.OpenSubKey("Shell");
                        if (subShell is not null)
                        {
                            foreach (var childName in subShell.GetSubKeyNames())
                            {
                                if (childName.StartsWith("SHELLEX", StringComparison.OrdinalIgnoreCase))
                                {
                                    continue;
                                }
                                if (LoadVerb(subShell, childName, targetPath, depth + 1, isSubItem: true) is RegistryVerb child)
                                {
                                    list.Add(child);
                                }
                            }
                        }
                    }
                }

                if (list.Count == 0)
                {
                    return null; // 任一解析为空 → 整体放弃（避免空壳子菜单）
                }
                children = list;
                rawCommand = defaultValue ?? string.Empty; // 父项自身命令极少见；空 = 点父项无操作
            }
            else
            {
                if (string.IsNullOrWhiteSpace(rawCommand))
                {
                    return null;
                }
                children = [];
            }

            // 修正：多级母项默认值不参与显示名链（isMultiItem 与 SubCommands/Extended 判定同源）
            var text = MenuText.FromWin32(ResolveDisplayName(name, muiVerb, defaultValue, isMultiItem: isMulti));
            if (!IsTextValid(text))
            {
                return null; // ≥80 字符系统同款隐藏（72-文档红线 3）
            }

            var iconRaw = key.GetValue("Icon") as string;
            var iconExe = ExtractIconExecutable(iconRaw);

            return new RegistryVerb(
                KeyName: name,
                Text: text,
                Command: rawCommand,
                Extended: key.GetValue("Extended") is not null,
                IconPath: iconExe,
                NoWorkingDirectory: key.GetValue("NoWorkingDirectory") is not null,
                RegPath: $@"{shellKey.Name}\{name}",
                Children: children,
                AppliesTo: key.GetValue("AppliesTo") as string,
                HasLUAShield: key.GetValue("HasLUAShield") is not null);
        }
        catch
        {
            return null; // 单项解析失败不拖累整场景（M10）
        }
    }

    /// <summary>CommandStore 双根打开（P1-A）：HKCU 优先（用户级覆盖机器级）、HKLM 兜底；调用方负责 Dispose。</summary>
    internal static List<RegistryKey> OpenCommandStoreRoots()
    {
        if (CommandStoreRootsOverride is { } provider)
        {
            return [.. provider() ?? []];
        }

        var list = new List<RegistryKey>();
        var hkcu = Registry.CurrentUser.OpenSubKey(CommandStoreSubPath);
        if (hkcu is not null)
        {
            list.Add(hkcu);
        }
        var hklm = Registry.LocalMachine.OpenSubKey(CommandStoreSubPath);
        if (hklm is not null)
        {
            list.Add(hklm);
        }
        return list;
    }

    // ===== AppliesTo 简单谓词（P2-C） =====

    /// <summary>
    /// AppliesTo 逐文件过滤：缓存按 类型|扩展名 共享，谓词含文件大小等逐文件条件，
    /// 必须在取用时评估（不进缓存判定）。空/背景场景/评估失败 → 显示（保守默认，不因无法评估丢项）。
    /// </summary>
    private static IReadOnlyList<RegistryVerb> FilterAppliesTo(IReadOnlyList<RegistryVerb> verbs, string path)
    {
        if (!verbs.Any(v => !string.IsNullOrWhiteSpace(v.AppliesTo)))
        {
            return verbs;
        }

        var result = new List<RegistryVerb>(verbs.Count);
        foreach (var verb in verbs)
        {
            if (MatchesAppliesTo(verb.AppliesTo, path))
            {
                result.Add(verb);
            }
        }
        return result;
    }

    /// <summary>
    /// AppliesTo 简单谓词：支持 System.FileExtension:=.ext、System.FileSize(&lt;|&lt;=|&gt;|&gt;=|=|!=)N，
    /// AND/OR 组合（如 MobaTextEditor 的 6MB 限制）。未知属性 → true 保守显示。
    /// </summary>
    internal static bool MatchesAppliesTo(string? appliesTo, string? targetPath)
    {
        if (string.IsNullOrWhiteSpace(appliesTo) || targetPath is null)
        {
            return true;
        }

        try
        {
            foreach (var orClause in Regex.Split(appliesTo, @"\bOR\b", RegexOptions.IgnoreCase))
            {
                var atoms = Regex.Split(orClause, @"\bAND\b", RegexOptions.IgnoreCase)
                    .Where(a => !string.IsNullOrWhiteSpace(a))
                    .ToList();
                if (atoms.Count > 0 && atoms.All(a => EvalAppliesToAtom(a, targetPath)))
                {
                    return true;
                }
            }
        }
        catch
        {
            return true; // 评估失败保守显示（M10）
        }
        return false;
    }

    private static bool EvalAppliesToAtom(string atom, string path)
    {
        atom = atom.Trim();
        var colon = atom.IndexOf(':');
        if (colon <= 0)
        {
            return true; // 不成形 → 保守显示
        }

        var prop = atom[..colon].Trim();
        var rest = atom[(colon + 1)..].Trim().TrimStart('=').Trim();

        try
        {
            if (prop.Equals("System.FileExtension", StringComparison.OrdinalIgnoreCase))
            {
                var want = rest.Trim('"', '~');
                if (!want.StartsWith('.'))
                {
                    want = "." + want;
                }
                return string.Equals(Path.GetExtension(path), want, StringComparison.OrdinalIgnoreCase);
            }

            if (prop.Equals("System.FileSize", StringComparison.OrdinalIgnoreCase))
            {
                var m = Regex.Match(rest, @"^(<=|>=|!=|=|<|>)\s*(\d+)$");
                if (!m.Success)
                {
                    return true;
                }
                var file = new FileInfo(path);
                if (!file.Exists)
                {
                    return m.Groups[1].Value == "!=";
                }
                var size = file.Length;
                var n = long.Parse(m.Groups[2].Value);
                return m.Groups[1].Value switch
                {
                    "<" => size < n,
                    "<=" => size <= n,
                    ">" => size > n,
                    ">=" => size >= n,
                    "=" => size == n,
                    "!=" => size != n,
                    _ => true,
                };
            }
        }
        catch
        {
            return true;
        }
        return true; // 未知属性：保守显示
    }

    // ===== 执行（explorer 语义） =====

    /// <summary>
    /// 执行一个 verb（多选逐个启动）。explorer 语义（P1-D）：ExpandEnvironmentVariables →
    /// 「首段 exe / 其余参数」拆命令行（参数段原样保留，不重排引号）→ UseShellExecute 启动。
    /// 废弃 cmd /c：路径含 &amp; 等元字符被 shell 截断、引号嵌套解析歧义——"看到了点了没反应"的根因。
    /// </summary>
    public static void Invoke(RegistryVerb verb, IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                var command = BuildCommand(verb.Command, path);
                if (string.IsNullOrWhiteSpace(command))
                {
                    continue;
                }

                var (exe, args) = SplitCommand(Environment.ExpandEnvironmentVariables(command));
                if (string.IsNullOrWhiteSpace(exe))
                {
                    continue;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = true,
                };
                if (!string.IsNullOrEmpty(args))
                {
                    psi.Arguments = args;
                }
                if (!verb.NoWorkingDirectory)
                {
                    // 目标是目录 → 工作目录=该目录；文件 → 所在目录（explorer 同款；背景场景=桌面自身）
                    var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    {
                        psi.WorkingDirectory = dir;
                    }
                }
                Process.Start(psi);
            }
            catch
            {
                // 启动失败 = explorer 同款"点了没反应"（命令指向不存在 exe，不归菜单管）
            }
        }
    }

    /// <summary>拆 exe/参数：引号形态取配对右引号；否则取首个空格。参数段原样保留。</summary>
    internal static (string Exe, string Args) SplitCommand(string command)
    {
        var cmd = command.TrimStart();
        if (cmd.Length == 0)
        {
            return (string.Empty, string.Empty);
        }
        if (cmd[0] == '"')
        {
            var end = cmd.IndexOf('"', 1);
            if (end > 0)
            {
                return (cmd[1..end], cmd[(end + 1)..].TrimStart());
            }
        }
        var sp = cmd.IndexOf(' ');
        return sp < 0 ? (cmd, string.Empty) : (cmd[..sp], cmd[sp..].TrimStart());
    }

    /// <summary>
    /// Icon 值 = "路径,-索引" → 提取可提取图标的文件路径（供 file:/tool: 图标协议；否则留空不猜图标）。
    /// 对齐前辈 ShellItem.ItemIcon 链：支持 exe/dll/ico、环境变量展开（%SystemRoot%\system32\...）、
    /// 系统相对名（shell32.dll/imageres.dll 落 System32）；索引忽略（SHGetFileInfo 取第一图标，近似）。
    /// </summary>
    internal static string? ExtractIconExecutable(string? iconValue)
    {
        if (string.IsNullOrWhiteSpace(iconValue))
        {
            return null;
        }
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(iconValue);
            var path = expanded.Split(',')[0].Trim('"').Trim();
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }
            if (!Path.IsPathRooted(path))
            {
                var sys = Path.Combine(Environment.SystemDirectory, path);
                if (File.Exists(sys))
                {
                    path = sys;
                }
            }
            var ext = Path.GetExtension(path);
            var extractable = ext is ".exe" or ".dll" or ".ico";
            return extractable && File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

}
