// BetterDesktop.Shell.ContextMenus — CLSID 反查 GuidInfo 引擎（移植自 ContextMenuManager Methods/GuidInfo.cs）
// 用途：由 COM CLSID 反查处理器人读信息（显示名/图标位置/dll路径/注册位置），支撑 ShellEx 菜单管理。
// 【红线】72-右键菜单/clsid-guid-info-resolve：三注册根全查+{GUID:B}格式+字典三级覆盖+六字典缓存+全链未命中返回null不抛。
// 【线程安全】缓存全部 ConcurrentDictionary（参考项目是单线程 WinForms 用普通 Dictionary，我们是多线程服务）。
// 【UWP 暂缺】UwpHelper UWP 包路径解析未实现（覆盖 95% 非 UWP 场景），GetUwpName 恒返回 null。

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Microsoft.Win32;

namespace BetterDesktop.Shell.ContextMenus.Services;

/// <summary>CLSID 反查处理器人读信息（显示名/图标/dll路径/注册位置）。</summary>
internal static class GuidInfo
{
    // ===== 三注册根（32/64 位注册位置不同，只查一个会漏）=====
    private static readonly string[] ClsidPaths =
    [
        @"HKEY_CLASSES_ROOT\CLSID",
        @"HKEY_CLASSES_ROOT\WOW6432Node\CLSID",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Classes\CLSID",
    ];

    // ===== 内置字典（嵌入资源 GuidInfosDic.ini，section=GUID，key=ResText/Text/Icon/UwpName）=====
    private static readonly Dictionary<Guid, Dictionary<string, string>> BuiltInDic = LoadBuiltInDic();

    // ===== per-GUID 缓存（ConcurrentDictionary 保证多线程安全）=====
    private static readonly ConcurrentDictionary<Guid, string?> FilePathCache = new();
    private static readonly ConcurrentDictionary<Guid, string?> ItemTextCache = new();
    private static readonly ConcurrentDictionary<Guid, (string Path, int Index)?> IconLocationCache = new();
    private static readonly ConcurrentDictionary<Guid, string?> ClsidPathCache = new();

    /// <summary>CLSID → dll/exe 完整路径（UWP 暂不支持；全链未命中返回 null）。</summary>
    public static string? GetFilePath(Guid guid)
    {
        if (guid == Guid.Empty)
        {
            return null;
        }
        if (FilePathCache.TryGetValue(guid, out var cached))
        {
            return cached;
        }

        string? filePath = null;

        // 逐根精确打开（参考项目用 RegistryEx.GetRegistryKey，我们用标准 RegistryKey API）
        foreach (string clsidPath in ClsidPaths)
        {
            using var guidKey = OpenRegistryKey($@"{clsidPath}\{guid:B}");
            if (guidKey is null)
            {
                continue;
            }
            foreach (string serverKeyName in new[] { "InprocServer32", "LocalServer32" })
            {
                using var serverKey = guidKey.OpenSubKey(serverKeyName);
                if (serverKey is null)
                {
                    continue;
                }
                // CodeBase 优先（.NET COM 程序集，剥 file:///、/→\）
                string? codeBase = serverKey.GetValue("CodeBase")?.ToString()
                    ?.Replace("file:///", "", StringComparison.OrdinalIgnoreCase)
                    .Replace('/', '\\');
                if (File.Exists(codeBase))
                {
                    filePath = codeBase;
                    break;
                }
                // 默认值经 ObjectPath.ExtractFilePath（可能是命令行字符串）
                string? defaultVal = serverKey.GetValue(null)?.ToString();
                string? extracted = ObjectPath.ExtractFilePath(defaultVal);
                if (File.Exists(extracted))
                {
                    filePath = extracted;
                    break;
                }
            }
            if (File.Exists(filePath))
            {
                ClsidPathCache[guid] = guidKey.Name;
                break;
            }
        }

        FilePathCache[guid] = filePath;
        return filePath;
    }

    /// <summary>CLSID → 显示名（六链优先级；全链未命中返回 null）。</summary>
    public static string? GetText(Guid guid)
    {
        if (guid == Guid.Empty)
        {
            return null;
        }
        if (ItemTextCache.TryGetValue(guid, out var cached))
        {
            return cached;
        }

        string? text = null;

        // 1. 字典 ResText（@dll,-id 资源串，经 ResourceRef 解析）
        if (TryGetDicValue(guid, "ResText", out var resText) && resText is not null)
        {
            text = ResourceRef.Resolve(resText);
        }

        // 2. 字典 {culture}-Text（如 zh-CN-Text）
        if (string.IsNullOrWhiteSpace(text)
            && TryGetDicValue(guid, CultureInfo.CurrentUICulture.Name + "-Text", out var cultureText))
        {
            text = cultureText;
        }

        // 3. 字典 Text（默认简体中文，可能也是 @dll,-id 格式）
        if (string.IsNullOrWhiteSpace(text) && TryGetDicValue(guid, "Text", out var dicText) && dicText is not null)
        {
            text = ResourceRef.Resolve(dicText) ?? dicText;
        }

        // 4. 注册表 LocalizedString / InfoTip / 默认值（逐根逐值）
        if (string.IsNullOrWhiteSpace(text))
        {
            foreach (string clsidPath in ClsidPaths)
            {
                foreach (string valueName in new[] { "LocalizedString", "InfoTip", "" })
                {
                    string? raw = Registry.GetValue($@"{clsidPath}\{guid:B}", valueName, null)?.ToString();
                    if (raw is not null)
                    {
                        text = ResourceRef.Resolve(raw);
                    }
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        break;
                    }
                }
                if (!string.IsNullOrWhiteSpace(text))
                {
                    break;
                }
            }
        }

        // 5. 反查 dll/exe 的 FileVersionInfo.FileDescription → 文件名
        if (string.IsNullOrWhiteSpace(text))
        {
            string? filePath = GetFilePath(guid);
            if (File.Exists(filePath))
            {
                try
                {
                    text = FileVersionInfo.GetVersionInfo(filePath!).FileDescription;
                }
                catch
                {
                    text = null;
                }
                if (string.IsNullOrWhiteSpace(text))
                {
                    text = Path.GetFileName(filePath);
                }
            }
        }

        ItemTextCache[guid] = text;
        return text;
    }

    /// <summary>CLSID → 图标位置（路径+索引；字典 Icon 优先，否则 dll 路径索引0；未命中返回 null）。</summary>
    public static (string Path, int Index)? GetIconLocation(Guid guid)
    {
        if (guid == Guid.Empty)
        {
            return null;
        }
        if (IconLocationCache.TryGetValue(guid, out var cached))
        {
            return cached;
        }

        (string Path, int Index)? result = null;

        // 字典 Icon（格式 "path,index"，index 负数=资源索引，非负=顺序序号）
        if (TryGetDicValue(guid, "Icon", out var iconStr))
        {
            result = ParseIconLocation(iconStr);
        }

        // 回退：反查到的 dll/exe 路径，索引 0
        if (result is null)
        {
            string? filePath = GetFilePath(guid);
            if (File.Exists(filePath))
            {
                result = (filePath!, 0);
            }
        }

        IconLocationCache[guid] = result;
        return result;
    }

    /// <summary>CLSID → 命中的注册键全名（如 HKEY_CLASSES_ROOT\CLSID\{...}）；未命中返回 null。</summary>
    public static string? GetClsidPath(Guid guid)
    {
        if (guid == Guid.Empty)
        {
            return null;
        }
        // GetFilePath 过程中会填充 ClsidPathCache；如果还没填充，主动调一次
        if (!ClsidPathCache.ContainsKey(guid))
        {
            _ = GetFilePath(guid);
        }
        return ClsidPathCache.TryGetValue(guid, out var path) ? path : null;
    }

    /// <summary>失效指定 CLSID 的缓存（改键/删除后调用）。</summary>
    public static void Invalidate(Guid guid)
    {
        FilePathCache.TryRemove(guid, out _);
        ItemTextCache.TryRemove(guid, out _);
        IconLocationCache.TryRemove(guid, out _);
        ClsidPathCache.TryRemove(guid, out _);
    }

    /// <summary>清空全部缓存（测试重置/字典热更新时调用）。</summary>
    public static void ClearCache()
    {
        FilePathCache.Clear();
        ItemTextCache.Clear();
        IconLocationCache.Clear();
        ClsidPathCache.Clear();
    }

    // ===== 内部助手 =====

    /// <summary>字典查找（空值语义：string.Empty ≠ 未命中，以 != string.Empty 判命中）。</summary>
    private static bool TryGetDicValue(Guid guid, string key, out string? value)
    {
        value = null;
        if (!BuiltInDic.TryGetValue(guid, out var dic))
        {
            return false;
        }
        if (!dic.TryGetValue(key, out var raw))
        {
            return false;
        }
        value = raw;
        return !string.IsNullOrEmpty(raw); // 空值视为未命中（用户用空值表示清除覆盖）
    }

    /// <summary>解析 "path,index" 格式图标位置（LastIndexOf(',') 分割，处理路径中含逗号的极端情况）。</summary>
    private static (string Path, int Index)? ParseIconLocation(string? iconStr)
    {
        if (string.IsNullOrWhiteSpace(iconStr))
        {
            return null;
        }
        int commaIdx = iconStr.LastIndexOf(',');
        if (commaIdx < 0)
        {
            return (iconStr.Trim(), 0);
        }
        string path = iconStr[..commaIdx].Trim();
        string indexStr = iconStr[(commaIdx + 1)..].Trim();
        if (int.TryParse(indexStr, out int index))
        {
            return (path, index);
        }
        return (path, 0);
    }

    /// <summary>按字符串注册表路径打开键（支持 HKCR/HKLM/HKCU 全称，只读）。</summary>
    private static RegistryKey? OpenRegistryKey(string fullPath)
    {
        int firstSlash = fullPath.IndexOf('\\');
        if (firstSlash < 0)
        {
            return null;
        }
        string rootName = fullPath[..firstSlash];
        string subPath = fullPath[(firstSlash + 1)..];

        RegistryHive hive = rootName.ToUpperInvariant() switch
        {
            "HKEY_CLASSES_ROOT" => RegistryHive.ClassesRoot,
            "HKEY_LOCAL_MACHINE" => RegistryHive.LocalMachine,
            "HKEY_CURRENT_USER" => RegistryHive.CurrentUser,
            "HKEY_USERS" => RegistryHive.Users,
            _ => RegistryHive.ClassesRoot,
        };

        // HKCR 包含 32 位重定向（WOW6432Node），路径含 WOW6432Node 时用 Registry32 视图
        RegistryView view = rootName.Contains("WOW6432Node", StringComparison.OrdinalIgnoreCase)
            || subPath.StartsWith("WOW6432Node", StringComparison.OrdinalIgnoreCase)
            ? RegistryView.Registry32
            : RegistryView.Default;

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            return baseKey.OpenSubKey(subPath, writable: false);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>从嵌入资源加载内置 GUID 字典（INI 格式：[GUID] section + key=value）。</summary>
    private static Dictionary<Guid, Dictionary<string, string>> LoadBuiltInDic()
    {
        var dic = new Dictionary<Guid, Dictionary<string, string>>();
        try
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            // 嵌入资源名：{RootNamespace}.{文件夹路径}.{文件名}
            string resourceName = "BetterDesktop.Shell.ContextMenus.Assets.GuidInfosDic.ini";
            using Stream? stream = asm.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                return dic;
            }
            using var reader = new StreamReader(stream, Encoding.UTF8);
            Guid currentGuid = Guid.Empty;
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                line = line.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith(';'))
                {
                    continue;
                }
                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    string guidStr = line[1..^1];
                    if (Guid.TryParse(guidStr, out var g))
                    {
                        currentGuid = g;
                        if (!dic.ContainsKey(currentGuid))
                        {
                            dic[currentGuid] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        }
                    }
                    else
                    {
                        currentGuid = Guid.Empty;
                    }
                    continue;
                }
                if (currentGuid == Guid.Empty)
                {
                    continue;
                }
                int eqIdx = line.IndexOf('=');
                if (eqIdx <= 0)
                {
                    continue;
                }
                string key = line[..eqIdx].Trim();
                string value = line[(eqIdx + 1)..].Trim();
                dic[currentGuid][key] = value;
            }
        }
        catch
        {
            // 字典加载失败不影响运行（退化为纯注册表反查）
        }
        return dic;
    }
}
