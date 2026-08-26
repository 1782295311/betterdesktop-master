using System;
using BetterDesktop.Shell.AppSource.Models;

namespace BetterDesktop.Shell.Search.Contracts;

/// <summary>
/// 统一搜索结果模型。
/// 消费方（开始菜单搜索视图）按 <see cref="Category"/> 分组展示，
/// 程序类用 <see cref="AppItem"/> 启动/固定，设置类与文件类用 <see cref="LaunchPath"/> 启动
/// （设置 = ms-settings: URI；文件 = 文件路径）。
/// </summary>
public sealed record SearchResult
{
    /// <summary>主标题（应用名 / 设置项名 / 文件名）。</summary>
    public required string Title { get; init; }

    /// <summary>副标题（路径 / 说明）。</summary>
    public string Subtitle { get; init; } = string.Empty;

    /// <summary>结果类别（App / Settings / File），供分组展示。</summary>
    public required string Category { get; init; }

    /// <summary>程序类结果的应用项（可直接启动 / 固定到 zone）。</summary>
    public AppItem? AppItem { get; init; }

    /// <summary>设置类（ms-settings:…）与文件类的启动目标。</summary>
    public string? LaunchPath { get; init; }

    /// <summary>图标来源（AppItemId / 文件路径）。</summary>
    public string? IconPath { get; init; }

    /// <summary>匹配得分（Provider 内排序用，聚合后按此降序）。</summary>
    public int Score { get; init; }

    /// <summary>可选执行动作；为空时消费方按 AppItem / LaunchPath 自行启动。</summary>
    public Action? Execute { get; init; }
}
