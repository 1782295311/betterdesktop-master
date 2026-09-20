// CLI 侧的**唯一**组件路径解析（S5-4）：core 与其它同目录组件的定位都走这里。
//
// 【为什么必须只有一份】`CoreEnsurer` 与 `ComponentLocator` 各写一遍
// "安装根 → 数据目录 → 调用方同目录"，与本仓已经吃过一次苦头的那个病**完全同族**：
// S4-2 的"路径解析分散在两处、没有共享契约"曾导致 core 期望一套路径、CLI 写另一套，
// 于是"修好了 → 再判定漂移 → 再修"无限循环（见 protocols/native-dll-path-test-vectors.json 的头注）。
// 两处逻辑一旦漂移，症状是"某个组件时而在、时而不在"，且只在特定部署形态下复现。
//
// 【为什么顺序是"安装根 → 数据目录 → 调用方同目录"（别改）】
// 调用方同目录必须**最后**。开发态 CLI 的 bin 目录里可能躺着一份**旧构建**的副本；
// 把它排在最前，`ensure core` / "拉起更新器"就会拉起旧的那一份 ——
// CoreEnsurer 的注释里记着这条真机证据（4 次 ensure 拉起了旧 core）。
//
// 【与 Rust 侧的关系】core 的 `process::exe_search_dirs()` 是同一概念的 Rust 实现
// （core 目录 → `%LOCALAPPDATA%\BetterDesktop`）。两侧**故意**不完全相同：
// CLI 多一层"安装根优先"（它自己可能被从任意目录调用）、core 不需要（它就在自己目录里跑）。
// 因此这里不做跨语言共享向量 —— 差异是设计，不是漂移。

using System;
using System.Collections.Generic;
using System.IO;
using BetterDesktop.Kernel.Deployment;

namespace BetterDesktop.Cli;

/// <summary>按"安装根 → 数据目录 → 调用方同目录"解析同目录组件（裸文件名）。</summary>
internal static class ComponentPathResolver
{
    /// <summary>core 可执行体文件名（与 Rust crate 名一致；跨进程契约）。</summary>
    public const string CoreExeName = "betterdesktop-core.exe";

    /// <summary>应急恢复程序（零依赖的应急进程）。</summary>
    public const string RecoveryExeName = "BetterDesktop.Recovery.exe";

    /// <summary>更新器。</summary>
    public const string UpdaterExeName = "BetterDesktop.Updater.exe";

    /// <summary>解析组件绝对路径；找不到返回 <c>null</c>（调用方负责如实报错，不回退到"随便拉一个"）。</summary>
    public static string? Resolve(string exeName)
    {
        if (!IsBareName(exeName))
        {
            return null;
        }

        foreach (var dir in CandidateDirs())
        {
            try
            {
                var candidate = Path.Combine(dir, exeName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (Exception)
            {
                // 非法路径组合（极少见）→ 继续找下一处，不阻断
            }
        }

        return null;
    }

    /// <summary>
    /// 候选目录，**顺序即优先级**。这是本链的唯一实现 ——
    /// 新增一个"要找组件"的入口时调用它，不要复制这段。
    /// </summary>
    public static IEnumerable<string> CandidateDirs()
    {
        string? installRoot = null;
        try
        {
            installRoot = DeploymentInfo.ResolveInstallRoot();
        }
        catch (Exception)
        {
            // 未用安装器安装时没有 deployment.json：正常情况，继续
        }

        var baseDir = AppContext.BaseDirectory;

        // ① 安装根优先（生产里它就是 CLI 同目录）
        if (!string.IsNullOrEmpty(installRoot))
        {
            yield return installRoot;
        }

        // ② 数据目录
        yield return DeploymentInfo.DirectoryPath;

        // ③ 调用方同目录：**仅开发态兜底**（与 ① 相同则跳过，去重保序）
        if (string.IsNullOrEmpty(installRoot)
            || !string.Equals(installRoot, baseDir, StringComparison.OrdinalIgnoreCase))
        {
            yield return baseDir;
        }
    }

    /// <summary>
    /// 是否**裸文件名**。含分隔符或冒号的输入会让 <c>Path.Combine</c> 被绝对路径整段替换掉，
    /// 从而绕过候选目录直接命中任意位置 —— 与 Rust 侧 <c>process::is_bare_name</c> 是同一道守卫（C19）。
    /// </summary>
    public static bool IsBareName(string name)
        => !string.IsNullOrWhiteSpace(name)
           && !name.Contains('\\')
           && !name.Contains('/')
           && !name.Contains(':');
}
