// BetterDesktop.Shell.ContextMenus — 「新建」全量枚举（ShellNew 注册表模型）
// 技术库依据：72-右键菜单/windows-context-menu-registry-model（L2）——HKCR\<ext>\ShellNew
// 五分支：NullFile（空文件）/ Data（写入字节）/ FileName（模板文件复制）/ Command（跑命令）/ Directory（建目录）。
// 取代原先硬编码的"文件夹/文本文档/快捷方式"三项。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace BetterDesktop.Shell.ContextMenus.Services;

/// <summary>新建菜单项（ShellNew 枚举 + 落地）。</summary>
/// <param name="Extension">扩展名（含点，如 ".txt"；目录项为 ""）。</param>
/// <param name="DisplayName">显示名（如 "文本文档"）。</param>
public sealed record ShellNewEntry(string Extension, string DisplayName);

/// <summary>新建项枚举与创建（HKCR ShellNew 模型）。</summary>
public static class ShellNewCatalog
{
    private static List<ShellNewEntry>? _cache;

    /// <summary>枚举本机「新建」可用类型（含"文件夹"；按显示名排序）。缓存进程级。</summary>
    public static IReadOnlyList<ShellNewEntry> Enumerate()
    {
        if (_cache is not null)
        {
            return _cache;
        }

        var list = new List<ShellNewEntry> { new(string.Empty, "文件夹") };
        try
        {
            using var classes = Registry.ClassesRoot;
            foreach (var ext in classes.GetSubKeyNames())
            {
                if (!ext.StartsWith('.'))
                {
                    continue;
                }

                try
                {
                    using var shellNew = classes.OpenSubKey(ext + "\\ShellNew");
                    if (shellNew is null)
                    {
                        continue;
                    }

                    // 五分支至少有其一才可见
                    var hasNullFile = shellNew.GetValue("NullFile") is not null;
                    var hasData = shellNew.GetValue("Data") is not null;
                    var hasFileName = shellNew.GetValue("FileName") is not null;
                    var hasCommand = shellNew.GetValue("Command") is not null;
                    var hasDirectory = shellNew.GetValue("Directory") is not null;
                    if (!(hasNullFile || hasData || hasFileName || hasCommand || hasDirectory))
                    {
                        continue;
                    }

                    list.Add(new ShellNewEntry(ext, FriendlyNameOf(classes, ext)));
                }
                catch
                {
                    // 单项失败跳过（M10）
                }
            }
        }
        catch
        {
            // 枚举失败仅保留"文件夹"（M10）
        }

        _cache = list
            .GroupBy(e => e.Extension, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(e => e.Extension.Length == 0 ? 0 : 1)
            .ThenBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return _cache;
    }

    private static string FriendlyNameOf(RegistryKey classes, string ext)
    {
        try
        {
            using var key = classes.OpenSubKey(ext);
            if (key?.GetValue(null) is string assoc && assoc.Length > 0)
            {
                using var assocKey = classes.OpenSubKey(assoc);
                if (assocKey?.GetValue(null) is string friendly && friendly.Length > 0)
                {
                    return $"{friendly.Trim()} ({ext.TrimStart('.')})";
                }
                return $"{assoc.Trim()} ({ext.TrimStart('.')})";
            }
        }
        catch
        {
            // 失败走默认（M10）
        }
        return ext.TrimStart('.').ToUpperInvariant() + " 文件";
    }

    /// <summary>在目标目录按 ShellNew 规则创建一个新项，返回生成路径；失败返回 null。</summary>
    public static string? Create(ShellNewEntry entry, string directory)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        try
        {
            if (entry.Extension.Length == 0)
            {
                var folder = ZipOps.UniquePath(Path.Combine(directory, "新建文件夹"));
                Directory.CreateDirectory(folder);
                return folder;
            }

            using var key = Registry.ClassesRoot.OpenSubKey(entry.Extension + "\\ShellNew");
            if (key is null)
            {
                return null;
            }

            var baseName = "新建" + entry.Extension.TrimStart('.').ToUpperInvariant();
            var path = ZipOps.UniquePath(Path.Combine(directory, baseName + entry.Extension));

            // 分支 1：Command（最高优先，交给程序自己建）
            if (key.GetValue("Command") is string command && command.Length > 0)
            {
                RunCommand(command);
                return path; // 程序自建，路径为预期名
            }

            // 分支 2：FileName（模板复制）
            if (key.GetValue("FileName") is string template && template.Length > 0)
            {
                var full = Environment.ExpandEnvironmentVariables(template.Trim('"'));
                if (File.Exists(full))
                {
                    File.Copy(full, path, overwrite: false);
                    return path;
                }
                // 模板缺失 → 退化空文件，不中断
                File.WriteAllBytes(path, []);
                return path;
            }

            // 分支 3：Data（写入注册表中的字节）
            if (key.GetValue("Data") is byte[] data)
            {
                File.WriteAllBytes(path, data);
                return path;
            }

            // 分支 4：NullFile（空文件）
            File.WriteAllBytes(path, []);
            return path;
        }
        catch
        {
            return null; // 创建失败静默（M10）
        }
    }

    private static void RunCommand(string command)
    {
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(command);
            var parts = SplitCommand(expanded);
            if (parts.Count == 0)
            {
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(parts[0])
            {
                UseShellExecute = true,
                Arguments = parts.Count > 1 ? string.Join(' ', parts.Skip(1)) : string.Empty,
            });
        }
        catch
        {
            // 启动失败静默（M10）
        }
    }

    private static List<string> SplitCommand(string command)
    {
        var result = new List<string>();
        var current = string.Empty;
        var inQuotes = false;
        foreach (var ch in command)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }
            if (ch == ' ' && !inQuotes)
            {
                if (current.Length > 0)
                {
                    result.Add(current);
                    current = string.Empty;
                }
                continue;
            }
            current += ch;
        }
        if (current.Length > 0)
        {
            result.Add(current);
        }
        return result;
    }
}
