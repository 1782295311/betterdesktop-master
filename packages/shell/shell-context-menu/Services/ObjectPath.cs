// BetterDesktop.Shell.ContextMenus — 命令行→文件路径提取（移植自 ContextMenuManager Methods/ObjectPath.cs）
// 用途：从注册表 command 字符串（如 "\"C:\app\exe.exe\" \"%1\" /param"）中提取可执行文件完整路径，
// 用于静态菜单项图标显示和 GuidInfo DLL 路径解析。
// 【红线】72-右键菜单/command-line-path-extract：非法字符切片+剔除 %1/%v+逆序回退+mshta wrapper 特判+App Paths 兜底+仅认 %SystemRoot% 两目录。

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace BetterDesktop.Shell.ContextMenus.Services;

/// <summary>从命令行字符串中提取可执行文件完整路径（线程安全，带缓存）。</summary>
internal static class ObjectPath
{
    private const string RegAppPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";
    private const string ShellExecuteCommand = "mshta vbscript:createobject(\"shell.application\").shellexecute(\"";

    private static readonly char[] IllegalChars = { '/', '*', '?', '"', '<', '>', '|' };
    private static readonly HashSet<string> IgnoreCommandParts = new(StringComparer.OrdinalIgnoreCase) { "", "%1", "%v" };

    /// <summary>命令→路径缓存（ConcurrentDictionary 保证多线程枚举安全；参考项目用普通 Dictionary 仅限单线程 WinForms）。</summary>
    private static readonly ConcurrentDictionary<string, string?> FilePathCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 从包含文件路径的命令语句中提取文件路径。
    /// </summary>
    /// <param name="command">命令语句（如 "\"C:\Windows\notepad.exe\" \"%1\""）</param>
    /// <returns>成功返回存在的文件完整路径，否则 null。</returns>
    public static string? ExtractFilePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        if (FilePathCache.TryGetValue(command, out var cached))
        {
            return cached;
        }

        string? filePath = null;
        string partCmd = Environment.ExpandEnvironmentVariables(command).Replace(@"\\", @"\");

        // mshta ShellExecute wrapper 特判："mshta vbscript:createobject("shell.application").shellexecute(\"fileName\",\"args\",...)"
        if (partCmd.StartsWith(ShellExecuteCommand, StringComparison.OrdinalIgnoreCase))
        {
            partCmd = partCmd[ShellExecuteCommand.Length..];
            string[] arr = partCmd.Split(new[] { "\",\"" }, StringSplitOptions.None);
            if (arr.Length > 0)
            {
                string fileName = arr[0];
                if (GetFullFilePath(fileName, out filePath))
                {
                    FilePathCache[command] = filePath;
                    return filePath;
                }
                if (arr.Length > 1)
                {
                    string arguments = arr[1];
                    filePath = ExtractFilePath(arguments);
                    if (filePath is not null)
                    {
                        FilePathCache[command] = filePath;
                        return filePath;
                    }
                }
            }
        }

        // 通用路径：按非法字符切片 → 剔除 %1/%v/空串 → 逆序回退（最长路径优先）
        string[] candidates = partCmd.Split(IllegalChars)
            .Where(str => !IgnoreCommandParts.Contains(str.Trim()))
            .Reverse()
            .ToArray();

        foreach (string cand in candidates)
        {
            string working = cand;
            int spaceIdx = -1;
            do
            {
                // 每次从 spaceIdx+1 取后缀作为候选路径（逐步缩短，处理"Program Files"类带空格路径）
                List<string> paths = new() { working[(spaceIdx + 1)..] };
                if (spaceIdx > 0)
                {
                    paths.Add(working[..spaceIdx]);
                }

                // 逗号/连字符进一步拆分（图标索引 "path,0"、参数 "-flag" 等）
                int count = paths.Count;
                for (int i = 0; i < count; i++)
                {
                    foreach (char c in new[] { ',', '-' })
                    {
                        if (paths[i].Contains(c))
                        {
                            paths.AddRange(paths[i].Split(c));
                        }
                    }
                }

                foreach (string path in paths)
                {
                    if (GetFullFilePath(path, out filePath))
                    {
                        FilePathCache[command] = filePath;
                        return filePath;
                    }
                }

                working = paths[0]; // path1 = 后缀
                spaceIdx = working.IndexOf(' ');
            }
            while (spaceIdx != -1);
        }

        FilePathCache[command] = null;
        return null;
    }

    /// <summary>
    /// 根据文件名获取完整文件路径（Win+R / 注册表可直接使用的文件名）。
    /// 【红线】右键菜单仅支持 %SystemRoot%\System32 和 %SystemRoot% 两个环境变量，不考虑其他环境变量（与 Win+R 有区别）。
    /// </summary>
    /// <param name="fileName">文件名（如 "notepad" / "notepad.exe" / "C:\Windows\notepad.exe"）</param>
    /// <param name="fullPath">输出：存在的文件完整路径</param>
    /// <returns>找到返回 true，否则 false。</returns>
    public static bool GetFullFilePath(string? fileName, out string? fullPath)
    {
        fullPath = null;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        foreach (string name in new[] { fileName, $"{fileName}.exe" })
        {
            // 仅认 %SystemRoot% 两目录 + 当前目录（空 dir = 原样路径）
            foreach (string dir in new[] { "", @"%SystemRoot%\System32\", @"%SystemRoot%\" })
            {
                // 带反斜杠或冒号的绝对路径只试当前目录，不拼 SystemRoot
                if (dir != "" && (name.Contains('\\') || name.Contains(':')))
                {
                    return false;
                }
                fullPath = Environment.ExpandEnvironmentVariables($@"{dir}{name}");
                if (File.Exists(fullPath))
                {
                    return true;
                }
            }

            // App Paths 兜底（HKLM\...\App Paths\<name> 默认值）
            fullPath = Registry.GetValue($@"{RegAppPath}\{name}", "", null) as string;
            if (File.Exists(fullPath))
            {
                return true;
            }
        }

        fullPath = null;
        return false;
    }

    /// <summary>清空路径缓存（改键/测试重置时调用）。</summary>
    public static void ClearCache() => FilePathCache.Clear();
}
