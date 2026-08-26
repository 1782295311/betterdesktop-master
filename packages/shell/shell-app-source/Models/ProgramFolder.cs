using System;
using System.Collections.Generic;

namespace BetterDesktop.Shell.AppSource.Models;

/// <summary>
/// 程序文件夹层级模型：开始菜单 "Programs" 树的一个节点。
/// 供经典开始菜单（"所有程序"树）与全应用浏览视图使用（IAppSourceService.GetProgramTree）。
/// 用户与公共两个 Programs 目录同名子文件夹在树中合并（<c>SubFolders</c> 按名称，<c>Items</c> 按 Id 去重）。
/// </summary>
public sealed record ProgramFolder
{
    /// <summary>文件夹显示名（合并根节点为 "Programs"）。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>物理完整路径（合并根节点为空字符串）。</summary>
    public string FullPath { get; init; } = string.Empty;

    /// <summary>子文件夹（按名称排序）。</summary>
    public IReadOnlyList<ProgramFolder> SubFolders { get; init; } = Array.Empty<ProgramFolder>();

    /// <summary>本层直接包含的应用（按名称排序）。</summary>
    public IReadOnlyList<AppItem> Items { get; init; } = Array.Empty<AppItem>();
}
