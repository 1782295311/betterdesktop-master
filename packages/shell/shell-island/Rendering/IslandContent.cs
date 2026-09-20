// BetterDesktop.Shell.Island — 活动 → 岛内容模型（渲染层只认这个模型，不认消息源）

using System;
using System.Collections.Generic;
using BetterDesktop.Activity.Contracts;

namespace BetterDesktop.Shell.Island.Rendering;

/// <summary>岛上的一个动作（展开态按钮）。</summary>
public sealed record IslandAction(string Label, Action Invoke);

/// <summary>
/// 岛的呈现内容（与消息源解耦：三个来源都先转成它再交给表面）。
/// 尺寸不在这里算——文本实测宽由窗口按当前 DPI/字号测量（渲染层职责）。
/// </summary>
public sealed record IslandContent(
    string Title,
    string? Subtitle,
    IslandGlyph Glyph,
    double? Progress,
    bool Indeterminate,
    bool Failed,
    IReadOnlyList<IslandAction> Actions,
    string AutomationName);

/// <summary>活动条目 → 岛内容 的映射（唯一映射点，来源差异全部在此收口）。</summary>
public static class IslandContentMapper
{
    /// <summary>来源标识（与各 ActivitySource 使用的 Source 常量一致）。</summary>
    public const string SourceClipboard = "clipboard";

    /// <summary>剪贴板「按序粘贴 / 按格粘」会话进度来源标识。</summary>
    public const string SourcePasteSession = "paste-session";

    /// <summary>媒体播放来源标识。</summary>
    public const string SourceMedia = "media";

    /// <summary>格式转换来源标识。</summary>
    public const string SourceConvert = "convert";

    /// <summary>把活动条目转成岛内容（动作回调在本层适配为无返回值的 Action，异常不影响仲裁）。</summary>
    public static IslandContent FromActivity(ActivityItem item)
    {
        if (item is null)
        {
            throw new ArgumentNullException(nameof(item));
        }

        var actions = new List<IslandAction>();
        foreach (var action in item.Actions)
        {
            if (action.Invoke is null)
            {
                continue;
            }

            var invoke = action.Invoke;
            actions.Add(new IslandAction(action.Label, () =>
            {
                try
                {
                    _ = invoke();
                }
                catch
                {
                    // 动作失败只在来源侧记日志；不冒泡到 UI 线程（否则会带崩渲染循环）
                }
            }));
        }

        var title = item.MergeCount > 1 ? $"{item.Title} ×{item.MergeCount}" : item.Title;
        var subtitle = string.IsNullOrWhiteSpace(item.Body) ? null : item.Body;
        var automation = string.IsNullOrWhiteSpace(subtitle) ? title : $"{title}，{subtitle}";

        return new IslandContent(
            Title: title,
            Subtitle: subtitle,
            Glyph: ResolveGlyph(item),
            Progress: item.Kind == ActivityKind.Progress ? item.Progress : null,
            Indeterminate: item.Kind == ActivityKind.Progress && item.Progress is null,
            Failed: item.Failed,
            Actions: actions,
            AutomationName: automation);
    }

    private static IslandGlyph ResolveGlyph(ActivityItem item)
    {
        if (item.Failed)
        {
            return IslandGlyph.Warning;
        }

        // 进度到顶（调用方显式发 1.0）视为完成：勾号比"转圈到顶"更有终态感。
        if (item.Kind == ActivityKind.Progress && item.Progress >= 0.999)
        {
            return IslandGlyph.Check;
        }

        return item.Source switch
        {
            SourceClipboard => IslandGlyph.Clipboard,
            SourcePasteSession => IslandGlyph.Clipboard,
            SourceMedia => IslandGlyph.Media,
            SourceConvert => IslandGlyph.Convert,
            _ => IslandGlyph.Dot,
        };
    }
}
