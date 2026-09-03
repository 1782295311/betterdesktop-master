// BetterDesktop.Shell.ContextMenus — 注册表静态 verb 枚举（M2 计划 §3.1）
// 领域模型：TECH-KNOWLEDGE/72-右键菜单/windows-context-menu-registry-model.md（L2 源码级）。
// 显示名链：MUIVerb > 项默认值(仅非子菜单) > 内置名称字典(@windows.storage.dll,-id) > KeyName；
// `@dll,-id` 一律经 SHLoadIndirectString 解析（失败回退原文）；文本 ≥80 字符跳过（系统同款隐藏）。
// 可见性：HideBasedOnVelocityId=0x639bc8 / LegacyDisable / ProgrammaticAccessOnly / CommandFlags%16>=8。
// 秒开纪律：注册表扫描永不进 Build 同步段——GetFor 缓存未命中时返回空 + 后台预热（第二次右键出现）。
// SubCommands 多级项 v1 跳过（CommandStore 模型后续批次）；DelegateExecute 动态命令归 COM 透传。

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using BetterDesktop.Shell.ContextMenus.Contracts;
using Microsoft.Win32;

namespace BetterDesktop.Shell.ContextMenus.Services;

/// <summary>一个注册表静态右键项（解析产物）。</summary>
public sealed record RegistryVerb(
    string KeyName,
    string Text,
    string Command,
    bool Extended,
    string? IconPath,
    bool NoWorkingDirectory,
    string RegPath);

/// <summary>注册表静态右键项枚举 + 执行（全静态；缓存进程级）。</summary>
public static class RegistryVerbs
{
    /// <summary>缓存 TTL（注册表变更重启进程或 5min 后生效——变更频率极低，无需监听）。</summary>
    internal static TimeSpan Ttl = TimeSpan.FromMinutes(5);

    /// <summary>间接字符串解析缝（测试注入；生产 = SHLoadIndirectString）。</summary>
    internal static Func<string, string?>? IndirectStringResolver { get; set; }

    private static readonly ConcurrentDictionary<string, (List<RegistryVerb> Items, DateTime Stamp)> Cache = new();

    /// <summary>内置动词名 → windows.storage.dll 资源 id（72-文档 DefaultNameIndexs）。</summary>
    private static readonly Dictionary<string, int> DefaultNameIndexs = new(StringComparer.OrdinalIgnoreCase)
    {
        { "open", 8496 }, { "edit", 8516 }, { "print", 8497 }, { "find", 8503 },
        { "play", 8498 }, { "runas", 8505 }, { "explore", 8502 }, { "preview", 8499 },
    };

    /// <summary>模板已提供的高频项（避免重复出现）。</summary>
    private static readonly HashSet<string> SuppressedKeys = new(StringComparer.OrdinalIgnoreCase)
    { "open", "openas", "runas", "edit", "print", "preview", "delete", "rename", "cut", "copy", "paste" };

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

    /// <summary>可见性（72-文档红线 4；子菜单规则差异由调用方按需处理，v1 顶层项）。</summary>
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

    /// <summary>间接字符串解析：@dll,-id → SHLoadIndirectString；其他原样返回（失败回退原文）。</summary>
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
            return resolver(raw) ?? raw;
        }
        var buffer = new StringBuilder(1024);
        return SHLoadIndirectString(raw, buffer, (uint)buffer.Capacity, IntPtr.Zero) == 0
            ? buffer.ToString()
            : raw;
    }

    // ===== 枚举（缓存 + 后台预热） =====

    /// <summary>目标文件/目录适用的注册表 verb（缓存命中即返；未命中返回空 + 后台预热）。</summary>
    public static IReadOnlyList<RegistryVerb> GetFor(FileKind kind, string path)
    {
        var cacheKey = CacheKey(kind, path);
        if (Cache.TryGetValue(cacheKey, out var hit) && DateTime.UtcNow - hit.Stamp < Ttl)
        {
            return hit.Items;
        }

        // 后台预热（秒开纪律）：本次返回空，下一次右键出现
        Cache[cacheKey] = ([], DateTime.UtcNow);
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            List<RegistryVerb> loaded;
            try
            {
                loaded = LoadScene(kind, path);
            }
            catch
            {
                loaded = []; // 注册表读失败 = 无第三方项（M10）
            }
            Cache[cacheKey] = (loaded, DateTime.UtcNow);
        });
        return [];
    }

    /// <summary>执行一个 verb（多选时逐个启动）。</summary>
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
                // 命令串 = 可执行文件 + 参数；用explorer 语义整体交给 ShellExecute（按首段拆 exe）
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c " + command,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                if (!verb.NoWorkingDirectory)
                {
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    {
                        psi.WorkingDirectory = dir;
                    }
                }
                System.Diagnostics.Process.Start(psi);
            }
            catch
            {
                // 启动失败 = explorer 同款"点了没反应"（命令指向不存在 exe，不归菜单管）
            }
        }
    }

    /// <summary>场景路径集（72-文档场景表）。缓存键 = 场景串 + 目标路径。</summary>
    internal static string CacheKey(FileKind kind, string path)
        => $"{kind}|{path}|{Path.GetExtension(path)}";

    internal static List<RegistryVerb> LoadScene(FileKind kind, string path)
    {
        var ext = Path.GetExtension(path);
        var sceneRoots = new List<string>();
        if (kind is FileKind.Folder or FileKind.Drive)
        {
            sceneRoots.AddRange(["*\\shell", "AllFilesystemObjects\\shell", "Folder\\shell", "Directory\\shell"]);
        }
        else
        {
            sceneRoots.AddRange(["*\\shell", "AllFilesystemObjects\\shell", $"SystemFileAssociations\\{ext}\\shell"]);
            if (!string.IsNullOrEmpty(ext))
            {
                sceneRoots.Add($"{ext}\\shell");
                var progId = Registry.GetValue($"HKEY_CLASSES_ROOT\\{ext}", null, null) as string;
                if (!string.IsNullOrWhiteSpace(progId))
                {
                    sceneRoots.Add($"{progId}\\shell");
                }
            }
        }

        var items = new List<RegistryVerb>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in sceneRoots)
        {
            var shellKey = Registry.ClassesRoot.OpenSubKey(root);
            if (shellKey is null)
            {
                continue;
            }
            using (shellKey)
            {
                foreach (var name in shellKey.GetSubKeyNames())
                {
                    if (name.StartsWith("SHELLEX", StringComparison.OrdinalIgnoreCase)
                        || SuppressedKeys.Contains(name)
                        || !seen.Add($"{root}|{name}"))
                    {
                        continue;
                    }

                    var item = LoadVerb(shellKey, name, path);
                    if (item is not null)
                    {
                        items.Add(item);
                    }
                }
            }
        }
        return items;
    }

    private static RegistryVerb? LoadVerb(RegistryKey shellKey, string name, string targetPath)
    {
        try
        {
            using var key = shellKey.OpenSubKey(name);
            if (key is null)
            {
                return null;
            }

            var isMulti = key.GetValue("SubCommands") is not null; // 多级项 v1 跳过
            if (isMulti)
            {
                return null;
            }
            // 动态命令（DelegateExecute/ExplorerCommandHandler）归 COM 透传，静态分支跳过
            using (var commandKey = key.OpenSubKey("command"))
            {
                if (commandKey?.GetValue("DelegateExecute") is not null
                    || key.GetValue("ExplorerCommandHandler") is not null)
                {
                    return null;
                }
                var rawCommand = commandKey?.GetValue(null) as string;
                if (string.IsNullOrWhiteSpace(rawCommand))
                {
                    return null;
                }

                var visible = IsVerbVisible(
                    hideBasedOnVelocity: key.GetValue("HideBasedOnVelocityId") is { } v && Convert.ToInt64(v) == 0x639bc8,
                    legacyDisable: key.GetValue("LegacyDisable") is not null,
                    programmaticAccessOnly: key.GetValue("ProgrammaticAccessOnly") is not null,
                    commandFlags: key.GetValue("CommandFlags") is { } f && int.TryParse(f.ToString(), out var flags) ? flags : 0);
                if (!visible)
                {
                    return null;
                }

                var text = ResolveDisplayName(name, key.GetValue("MUIVerb") as string, key.GetValue(null) as string, isMultiItem: false);
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
                    RegPath: $@"{shellKey.Name}\{name}");
            }
        }
        catch
        {
            return null; // 单项解析失败不拖累整场景（M10）
        }
    }

    /// <summary>Icon 值 = "路径,-索引" → 仅当路径为现存 exe/dll 时返回（供 file:/tool: 图标协议；否则留空不猜图标）。</summary>
    internal static string? ExtractIconExecutable(string? iconValue)
    {
        if (string.IsNullOrWhiteSpace(iconValue))
        {
            return null;
        }
        var path = iconValue.Split(',')[0].Trim('"').Trim();
        return path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path) ? path : null;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string pszSource, StringBuilder pszOutBuffer, uint cchOutBuffer, IntPtr reserved);
}
