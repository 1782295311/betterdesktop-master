using System;
using BetterDesktop.Shell.AppSource.Models;

namespace BetterDesktop.Shell.Recent.Contracts;

/// <summary>
/// 最近项模型：最近程序（Kind="Program"）或最近文档（Kind="Document"）。
/// 程序类带 <see cref="AppItem"/>（可启动/固定）；文档类 Path 为 Windows Recent 目录下的 .lnk（可直接 ShellExecute）。
/// </summary>
public sealed record RecentItem
{
    /// <summary>显示名。</summary>
    public required string Name { get; init; }

    /// <summary>程序 = TargetPath；文档 = Recent 目录下 .lnk 路径。</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>类别（Program / Document）。</summary>
    public required string Kind { get; init; }

    /// <summary>最近使用时间。</summary>
    public DateTime LastUsed { get; init; }

    /// <summary>程序类的应用项（仅 Kind="Program" 有值）。</summary>
    public AppItem? AppItem { get; init; }
}
