// BetterDesktop.Shell.ContextMenus — 管理器注册表写侧助手（M1 分片 3，partial）

using System;
using Microsoft.Win32;

namespace BetterDesktop.Shell.ContextMenus.Services;

public static partial class MenuManagerService
{
    /// <summary>HKLM 先夺权（单向红线，仅对具体目标键）；HKCU 免夺权。</summary>
    internal static void EnsureWritable(string regPath)
    {
        if (regPath.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase))
        {
            // 【2026-09-05 修复】EnablePrivileges 原本全仓无人调用 = 提权死代码——
            // SeTakeOwnership/SeRestore 在 token 里默认禁用，不显式启用即使管理员也夺权失败。
            _ = RegTakeover.EnablePrivileges();
            if (RegTakeover.TakeTreeOwnership(regPath) < 0)
            {
                throw new InvalidOperationException($"夺权失败（需要管理员权限运行）: {regPath}");
            }
        }
    }

    private static RegistryKey? OpenWritable(string regPath)
    {
        var (root, sub) = RegTakeover.SplitPath(regPath);
        using var baseKey = RegistryKey.OpenBaseKey(root, RegistryView.Default);
        return baseKey.OpenSubKey(sub, writable: true);
    }

    internal static void CopyTree(string source, string target)
    {
        var (srcRoot, srcSub) = RegTakeover.SplitPath(source);
        var (dstRoot, dstSub) = RegTakeover.SplitPath(target);
        using var srcBase = RegistryKey.OpenBaseKey(srcRoot, RegistryView.Default);
        using var dstBase = RegistryKey.OpenBaseKey(dstRoot, RegistryView.Default);
        using var srcKey = srcBase.OpenSubKey(srcSub)
            ?? throw new InvalidOperationException($"源键不存在: {source}");
        using var dstKey = dstBase.CreateSubKey(dstSub, writable: true)
            ?? throw new InvalidOperationException($"目标键创建失败: {target}");
        CopyKeyContents(srcKey, dstKey);
    }

    private static void CopyKeyContents(RegistryKey source, RegistryKey target)
    {
        foreach (var name in source.GetValueNames())
        {
            target.SetValue(name.Length == 0 ? string.Empty : name,
                source.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)!,
                source.GetValueKind(name));
        }
        foreach (var sub in source.GetSubKeyNames())
        {
            using var srcSub = source.OpenSubKey(sub)
                ?? throw new InvalidOperationException($"子键打不开: {sub}");
            using var dstSub = target.CreateSubKey(sub, writable: true)
                ?? throw new InvalidOperationException($"子键建不了: {sub}");
            CopyKeyContents(srcSub, dstSub);
        }
    }

    internal static void DeleteTree(string regPath)
    {
        var (root, sub) = RegTakeover.SplitPath(regPath);
        using var baseKey = RegistryKey.OpenBaseKey(root, RegistryView.Default);
        var lastSlash = sub.LastIndexOf('\\');
        var parent = lastSlash < 0 ? string.Empty : sub[..lastSlash];
        var name = lastSlash < 0 ? sub : sub[(lastSlash + 1)..];
        using var parentKey = baseKey.OpenSubKey(parent, writable: true)
            ?? throw new InvalidOperationException($"父键打不开: {root}\\{parent}");
        parentKey.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
    }
}
