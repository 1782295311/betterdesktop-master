using System;
using BetterDesktop.Shell.AppSource.Models;
using BetterDesktop.Shell.IndexIpc;

namespace BetterDesktop.Shell.AppSource.Services;

/// <summary>
/// 【M2 · 2026-09-13】把引擎的 raw 候选（<see cref="AppCandidate"/>）**升格**为 <see cref="AppItem"/>。
/// <para>
/// <b>权威源纪律（计划 §5.1）</b>：引擎只产出「磁盘上存在哪些可执行文件」这一**事实**
///（路径 + 名称候选 + 来源根标记），**不做** lnk 目标解析、显示名解析、排除词过滤、AppItem 语义。
/// 语义层全部留在这里 —— 因此本类**必须复用既有 C# 语义**（<see cref="ShellLinkResolver"/> /
/// <c>AppSourceService.ResolveFromPath</c>），不得另起一套判定，否则会出现"引擎路径与本地路径结果不一致"。
/// </para>
/// <para>
/// ⚠️ <b>两个 "source" 不是一回事</b>：引擎的 <see cref="AppCandidate.Source"/> 是**来源根**
///（<c>start-menu</c> / <c>program-files</c>），而 <see cref="AppItem.Source"/> 是**应用类型**
///（由文件扩展名经 <see cref="ShellLinkResolver"/> 判定）。本类只用前者**分组**，后者一律重新判定。
/// </para>
/// </summary>
internal static class AppCandidateMapper
{
    /// <summary>引擎来源根标记：开始菜单。</summary>
    public const string EngineSourceStartMenu = "start-menu";

    /// <summary>引擎来源根标记：Program Files 盘点。</summary>
    public const string EngineSourceProgramFiles = "program-files";

    /// <summary>
    /// 开始菜单候选 → AppItem：**复用 <c>ResolveFromPath</c> 的完整过滤链**
    ///（IsSupportedFile / 排除名 / 文档目标 / 可执行 / 系统工具），语义与本地 <c>ScanDirectory</c> 完全一致。
    /// </summary>
    /// <param name="candidate">引擎候选。</param>
    /// <param name="resolve">本地升格函数（由 <c>AppSourceService.ResolveFromPath</c> 提供）。</param>
    public static AppItem? FromStartMenuCandidate(AppCandidate candidate, Func<string, AppItem?> resolve)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(resolve);
        return string.IsNullOrWhiteSpace(candidate.Path) ? null : resolve(candidate.Path);
    }

    /// <summary>
    /// Program Files 候选 → AppItem：与本地 <c>EnumerateExecutablesRecursive</c> **逐条对齐** ——
    /// 直接 <see cref="ShellLinkResolver.Resolve"/>（**不跑**排除名/文档目标/可执行/系统工具四道过滤），
    /// Id 走 <c>AppSourceService.CreateStableId</c>（与本地/干净模式/固定库**同一个函数**），
    /// TargetPath 为空时回退自身路径。
    /// </summary>
    public static AppItem? FromProgramFilesCandidate(AppCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (string.IsNullOrWhiteSpace(candidate.Path))
        {
            return null;
        }

        try
        {
            var (displayName, targetPath, source) = ShellLinkResolver.Resolve(candidate.Path);
            // 显示名为空时退回引擎给的名称候选（本地此处理论上是文件名，引擎已按 file_stem 提供）
            var name = string.IsNullOrWhiteSpace(displayName) ? candidate.NameHint : displayName;
            return new AppItem
            {
                // 与本地 EnumerateExecutablesRecursive 共用同一个 Id 函数（这里曾是各自拼 "all:" 前缀）。
                // 引擎路径与本地路径必须产出同一个 Id：否则同一程序在两种路径下固定态分裂，
                // 且 M2 刚建立的「引擎/本地点数平价」会在 Id 维度失效。
                Id = AppSourceService.CreateStableId(source, candidate.Path, targetPath),
                Name = string.IsNullOrWhiteSpace(name) ? candidate.Path : name,
                ShortcutPath = candidate.Path,
                TargetPath = string.IsNullOrWhiteSpace(targetPath) ? candidate.Path : targetPath,
                Source = source,
            };
        }
        catch (Exception)
        {
            // 单个文件解析失败不阻断（与本地 EnumerateExecutablesRecursive 同纪律）
            return null;
        }
    }
}
