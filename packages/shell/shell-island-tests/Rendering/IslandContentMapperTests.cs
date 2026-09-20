using System;
using System.Threading.Tasks;
using BetterDesktop.Activity.Contracts;
using BetterDesktop.Shell.Island.Rendering;
using Xunit;

namespace BetterDesktop.Shell.Island.Tests.Rendering;

/// <summary>
/// 活动 → 岛内容 的映射机检：字形选择（来源/终态）、进度语义（确定/不确定）、
/// 同源合并计数标题、动作适配（回调异常不得冒泡）。
/// </summary>
public class IslandContentMapperTests
{
    private static ActivityItem Item(
        string source,
        ActivityKind kind = ActivityKind.Transient,
        ActivityPriority priority = ActivityPriority.Notice,
        string title = "标题",
        string? body = null,
        double? progress = null,
        int mergeCount = 1,
        bool failed = false,
        ActivityAction[]? actions = null)
        => new(
            Id: "id",
            Source: source,
            Kind: kind,
            Priority: priority,
            Title: title,
            Body: body,
            IconPath: null,
            Progress: progress,
            CreatedAt: DateTimeOffset.Now,
            TimeToLive: TimeSpan.FromSeconds(3),
            Actions: actions ?? Array.Empty<ActivityAction>(),
            MergeCount: mergeCount,
            Failed: failed);

    [Fact]
    public void Clipboard_source_maps_to_clipboard_glyph()
    {
        var content = IslandContentMapper.FromActivity(Item(IslandContentMapper.SourceClipboard, title: "已复制文本", body: "hello"));
        Assert.Equal(IslandGlyph.Clipboard, content.Glyph);
        Assert.Null(content.Progress);
        Assert.False(content.Indeterminate);
        Assert.Equal("hello", content.Subtitle);
        Assert.Contains("已复制文本", content.AutomationName, StringComparison.Ordinal); // 无障碍文本包含标题
    }

    [Fact]
    public void Media_source_maps_to_note_glyph()
    {
        var content = IslandContentMapper.FromActivity(Item(
            IslandContentMapper.SourceMedia, ActivityKind.Sticky, ActivityPriority.Media, "歌名", "歌手 · 应用"));
        Assert.Equal(IslandGlyph.Media, content.Glyph);
        Assert.Equal("歌手 · 应用", content.Subtitle);
    }

    [Fact]
    public void Progress_kind_without_percent_is_indeterminate_and_shows_no_fake_number()
    {
        var content = IslandContentMapper.FromActivity(Item(
            IslandContentMapper.SourceConvert, ActivityKind.Progress, ActivityPriority.Progress, progress: null));
        Assert.True(content.Indeterminate);
        Assert.Null(content.Progress);
    }

    [Fact]
    public void Progress_at_one_hundred_percent_switches_to_check_glyph()
    {
        var content = IslandContentMapper.FromActivity(Item(
            IslandContentMapper.SourceConvert, ActivityKind.Progress, ActivityPriority.Progress, progress: 1.0));
        Assert.Equal(IslandGlyph.Check, content.Glyph);
        Assert.False(content.Indeterminate);
    }

    [Fact]
    public void Failed_terminal_state_uses_warning_glyph_and_flag()
    {
        var content = IslandContentMapper.FromActivity(Item(
            IslandContentMapper.SourceConvert, title: "转换失败", failed: true));
        Assert.True(content.Failed);
        Assert.Equal(IslandGlyph.Warning, content.Glyph);
    }

    [Fact]
    public void Merge_count_is_visible_in_title()
    {
        var content = IslandContentMapper.FromActivity(Item(
            IslandContentMapper.SourceConvert, title: "转换完成", mergeCount: 3));
        Assert.Equal("转换完成 ×3", content.Title);
    }

    [Fact]
    public async Task Actions_are_wrapped_and_null_invokes_are_skipped()
    {
        var invoked = 0;
        var items = new[]
        {
            new ActivityAction("open", "打开", () => { invoked++; return Task.CompletedTask; }),
            new ActivityAction("placeholder", "占位", null),
        };

        var content = IslandContentMapper.FromActivity(Item(IslandContentMapper.SourceConvert, actions: items));
        Assert.Single(content.Actions);
        Assert.Equal("打开", content.Actions[0].Label);

        content.Actions[0].Invoke();
        await Task.Yield();
        Assert.Equal(1, invoked);
    }

    [Fact]
    public void Action_exception_does_not_escape_the_callback()
    {
        var items = new[] { new ActivityAction("boom", "炸", () => throw new InvalidOperationException("boom")) };
        var content = IslandContentMapper.FromActivity(Item(IslandContentMapper.SourceConvert, actions: items));

        // 动作异常必须被吞在映射层：否则会顺着 UI 线程的点击回调带崩渲染循环
        content.Actions[0].Invoke();
    }
}
