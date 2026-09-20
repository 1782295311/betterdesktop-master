using System;
using System.Collections.Generic;
using System.IO;
using BetterDesktop.Shell.Dock.Models;

namespace BetterDesktop.Shell.Dock.Services;

/// <summary>
/// 重绑所需的外部事实。由调用方采集后传入——本类因此是**纯函数**，可脱离注册表/磁盘单测。
/// </summary>
internal sealed record PinnedRebindSources
{
    /// <summary>注册表已安装程序（<c>ScanInstalledApps</c> 的结果）。</summary>
    public required IReadOnlyList<DockItemData> Installed { get; init; }

    /// <summary>App Paths 索引：**小写 exe 文件名** → 完整路径（HKLM/HKCU 归并）。</summary>
    public required IReadOnlyDictionary<string, string> AppPaths { get; init; }

    /// <summary>
    /// 已安装的 Store 应用 AUMID 集合。
    /// <b>空集合 = 未知</b>（AppsFolderSource 不可用时不据此判孤——与 <c>IsStillInstalled</c> 的保守纪律一致）。
    /// </summary>
    public required IReadOnlySet<string> StoreAppIds { get; init; }

    /// <summary>文件存在性判定（生产 = <c>File.Exists</c>；测试注入假实现）。</summary>
    public required Func<string, bool> FileExists { get; init; }
}

/// <summary>
/// 固定项路径重绑：原快照路径失效时，从强到弱逐级下探，找一个「同一应用的新路径」。
/// <para>
/// <b>为什么需要多级</b>：旧实现只按**名称精确匹配**反查注册表，因此
/// 「更新后改了版本目录」还能救回，「改名 + 换目录」就断链（被判为已卸载而让位）。
/// 分级后覆盖面显著变宽，且每级都可单独测试。
/// </para>
/// <para>
/// <b>保证不了的三类</b>（不承诺，见计划 §6 P5）：便携应用路径变更（无注册表、无 AUMID）、
/// 改名 + 换目录 + 换品牌、Store 应用重打包改变 AUMID —— 这些都会落到 <c>Orphaned</c>，
/// 由用户手动重绑。
/// </para>
/// </summary>
internal static class PinnedRebindResolver
{
    /// <summary>
    /// 逐级重绑，命中即返回可用路径；全失配返回 <c>null</c>（调用方判 <c>Orphaned</c>）。
    /// </summary>
    public static string? Resolve(DockItemData probe, PinnedRebindSources sources)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(sources);

        // ① Store：AUMID 即主键，精确命中即有效
        if (probe.AppType == DockAppType.Uwp)
        {
            var aumid = string.IsNullOrWhiteSpace(probe.AppUserModelId) ? probe.TargetPath : probe.AppUserModelId;
            if (string.IsNullOrWhiteSpace(aumid))
            {
                return null;
            }

            if (sources.StoreAppIds.Contains(aumid))
            {
                return aumid;
            }

            // Store 列表为空 = 未知（不可据此判孤）；有列表但没命中 = 真被卸载
            return sources.StoreAppIds.Count == 0 ? aumid : null;
        }

        // ② App Paths：按原 exe 文件名（安装器注册的规范入口，改名但同 exe 名可救）
        var fileName = ProbeFileName(probe);
        if (fileName.Length > 0
            && sources.AppPaths.TryGetValue(fileName, out var appPath)
            && sources.FileExists(appPath))
        {
            return appPath;
        }

        var nameKey = NormalizeKey(probe.Name);
        var rootKey = InstallRootKey(probe);

        // ③ 同名 + 同安装根（最强组合，无歧义）
        if (nameKey.Length > 0 && rootKey.Length > 0)
        {
            var hit = FindInstalled(sources, app =>
                NormalizeKey(app.Name) == nameKey && InstallRootKey(app) == rootKey);
            if (hit is not null)
            {
                return hit;
            }
        }

        // ④ 同安装根（**显示名变了但位置没变**——版本化目录应用更新后改了显示名的情形）
        if (rootKey.Length > 0)
        {
            var hit = FindInstalled(sources, app => InstallRootKey(app) == rootKey);
            if (hit is not null)
            {
                return hit;
            }
        }

        // ⑤ 同名（位置也变了）：**仅当同名候选唯一时**才绑。
        //    两个「原神」（正式服 / B 服）这类同名不同目录的兄弟应用，猜错等于静默启动错误的程序，
        //    故宁可判为失效让用户手动指认，也不猜。
        if (nameKey.Length > 0)
        {
            var candidates = new List<string>();
            foreach (var app in sources.Installed)
            {
                if (NormalizeKey(app.Name) != nameKey)
                {
                    continue;
                }

                var path = UsablePath(app, sources);
                if (path is not null)
                {
                    candidates.Add(path);
                }
            }

            if (candidates.Count == 1)
            {
                return candidates[0];
            }
        }

        return null;
    }

    /// <summary>在已安装列表里找第一个「谓词命中且路径可用」的候选。</summary>
    private static string? FindInstalled(PinnedRebindSources sources, Func<DockItemData, bool> predicate)
    {
        foreach (var app in sources.Installed)
        {
            if (!predicate(app))
            {
                continue;
            }

            var path = UsablePath(app, sources);
            if (path is not null)
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>可用路径：优先存在的 TargetPath，其次存在的 ShortcutPath；都不可用返回 null。</summary>
    private static string? UsablePath(DockItemData app, PinnedRebindSources sources)
    {
        if (!string.IsNullOrWhiteSpace(app.TargetPath) && sources.FileExists(app.TargetPath))
        {
            return app.TargetPath;
        }

        if (!string.IsNullOrWhiteSpace(app.ShortcutPath) && sources.FileExists(app.ShortcutPath))
        {
            return app.ShortcutPath;
        }

        return null;
    }

    /// <summary>快照里的 exe 文件名（小写，用于 App Paths 查表）。</summary>
    private static string ProbeFileName(DockItemData probe)
    {
        var path = string.IsNullOrWhiteSpace(probe.TargetPath) ? probe.ShortcutPath : probe.TargetPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var name = Path.GetFileName(path);
        return name is null ? string.Empty : name.ToLowerInvariant();
    }

    /// <summary>
    /// 安装根目录键 = exe 所在目录的父目录（沿用 <c>DockAppsService.DedupKey</c> 的粒度）：
    /// Edge 的 <c>...\Edge\Application\151.x\msedge.exe</c> 与 <c>152.x\msedge.exe</c> 同为 <c>Application</c>，
    /// 故版本更新视为同一应用。
    /// <para><b>盘根不算根</b>：<c>D:\Gone\gone.exe</c> 的「父目录的父目录」是 <c>D:\</c>，
    /// 它会把同一盘上所有浅目录应用视为同根（误绑）。这种情况返回空串 → 该级跳过。</para>
    /// </summary>
    private static string InstallRootKey(DockItemData app)
    {
        var path = string.IsNullOrWhiteSpace(app.TargetPath) ? app.ShortcutPath : app.TargetPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir))
        {
            return string.Empty;
        }

        var root = Path.GetDirectoryName(dir);
        if (string.IsNullOrEmpty(root) || string.Equals(root, Path.GetPathRoot(root), StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        // 【2026-09-14 真机修复】共享容器根**不算根** —— 与上面"盘根不算根"是同一条道理，只是低了一层。
        // 实证（真机固定库）：`%LocalAppData%\Programs` 下的 WorkBuddy 被第 ④ 级错指到同根下的
        // CodeBuddy CN.exe（**完全不同的程序**）；`D:\...\Steam\steamapps\common` 下的小黑盒加速器
        // 被错指到 BongoCat.exe。原因：这些根下面并排放着几十个互不相干的应用，单看"同根"必然误绑。
        if (IsSharedContainerRoot(root))
        {
            return string.Empty;
        }

        return NormalizeKey(root);
    }

    /// <summary>
    /// 共享容器根判定：根目录本身就是"一堆互不相干应用的公共父目录"。
    ///
    /// <para>按**叶子目录名**判定（而不是"路径里含 Program Files"）：后者会把
    /// <c>D:\Program Files\Blender Foundation</c> 这种**专属**根也误杀，而它正是第 ④ 级
    /// 想救的合法场景（版本化目录更新）。</para>
    /// </summary>
    private static bool IsSharedContainerRoot(string root)
    {
        var leaf = Path.GetFileName(root);
        if (string.IsNullOrEmpty(leaf))
        {
            return false;
        }

        return leaf.Equals("Programs", StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("Program Files", StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("Program Files (x86)", StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("common", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeKey(string? value) => value is null ? string.Empty : value.Trim().ToLowerInvariant();
}
