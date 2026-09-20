using System;
using System.Linq;
using BetterDesktop.Activity.Contracts;
using BetterDesktop.Shell.Core.Activity;
using Xunit;

namespace BetterDesktop.Shell.Core.Tests.Activity;

/// <summary>
/// 活动仲裁纯逻辑测试（M3：优先级抢占、同源合并、TTL 过期、粘性不淘汰、抑制补播、Changed 节流、零订阅者）。
/// 时钟注入固定时间驱动排序与过期。
/// </summary>
public class ActivityServiceTests
{
    private sealed class Clock
    {
        public DateTimeOffset Now = new(2026, 9, 15, 8, 0, 0, TimeSpan.FromHours(8));
        public DateTimeOffset Get() => Now;
        public void Advance(TimeSpan t) => Now += t;
    }

    private static ActivityItem Item(
        string id,
        string source,
        ActivityPriority priority,
        string? title = null,
        ActivityKind kind = ActivityKind.Transient,
        DateTimeOffset? createdAt = null,
        TimeSpan? ttl = null)
        => new(
            id, source, kind, priority,
            title ?? "标题-" + id, // 默认标题按 id 区分，避免同源合并误触发；合并测试显式传同标题
            Body: null, IconPath: null, Progress: null,
            createdAt ?? new DateTimeOffset(2026, 9, 15, 8, 0, 0, TimeSpan.FromHours(8)),
            ttl ?? TimeSpan.FromSeconds(10),
            Array.Empty<ActivityAction>());

    [Fact]
    public void Higher_priority_preempts_lower_and_lower_resumes_after_complete()
    {
        var clock = new Clock();
        var svc = new ActivityService(clock.Get);
        svc.Post(Item("notice", "src", ActivityPriority.Notice, createdAt: clock.Now));
        Assert.Equal("notice", svc.Current!.Id);

        svc.Post(Item("attention", "src2", ActivityPriority.Attention, createdAt: clock.Now));
        Assert.Equal("attention", svc.Current!.Id);          // 抢占
        Assert.Contains(svc.Queue, x => x.Id == "notice");   // 被抢占项回队列不丢失

        svc.Complete("attention");
        Assert.Equal("notice", svc.Current!.Id);             // 优先级回落续播
    }

    [Fact]
    public void Lower_priority_queues_without_preempting()
    {
        var clock = new Clock();
        var svc = new ActivityService(clock.Get);
        svc.Post(Item("attention", "src", ActivityPriority.Attention, createdAt: clock.Now));
        svc.Post(Item("notice", "src", ActivityPriority.Notice, createdAt: clock.Now));
        Assert.Equal("attention", svc.Current!.Id);
        Assert.Single(svc.Queue);
    }

    [Fact]
    public void Same_priority_newer_created_at_shows_first()
    {
        var clock = new Clock();
        var svc = new ActivityService(clock.Get);
        svc.Post(Item("old", "src", ActivityPriority.Clipboard, title: "旧", createdAt: clock.Now));
        clock.Advance(TimeSpan.FromSeconds(1));
        svc.Post(Item("new", "src", ActivityPriority.Clipboard, title: "新", createdAt: clock.Now));
        Assert.Equal("new", svc.Current!.Id); // 后到先显示
    }

    [Fact]
    public void Same_id_post_updates_in_place_and_does_not_fire_changed()
    {
        var clock = new Clock();
        var svc = new ActivityService(clock.Get);
        var fires = 0;
        svc.Changed += () => fires++;

        svc.Post(Item("conv", "convert", ActivityPriority.Progress, title: "批量转换", createdAt: clock.Now));
        Assert.Equal(1, fires);
        var before = fires;

        // 同 Id 进度更新：身份不变 → 不触发 Changed（帧级节流）
        svc.Post(Item("conv", "convert", ActivityPriority.Progress, title: "批量转换",
            createdAt: clock.Now) with { Progress = 0.5 });
        Assert.Equal(before, fires);
        Assert.Equal(0.5, svc.Current!.Progress);
    }

    [Fact]
    public void Same_source_same_title_transient_folds_with_merge_count()
    {
        var clock = new Clock();
        var svc = new ActivityService(clock.Get);
        svc.Post(Item("done1", "convert", ActivityPriority.Notice, title: "转换完成", createdAt: clock.Now));
        clock.Advance(TimeSpan.FromSeconds(1));
        svc.Post(Item("done2", "convert", ActivityPriority.Notice, title: "转换完成", createdAt: clock.Now));

        Assert.Empty(svc.Queue);                        // 折叠为一条（当前项），不堆叠
        Assert.Equal("convert", svc.Current!.Source);
        Assert.Equal(2, svc.Current!.MergeCount);       // 计数累计（"2 项转换完成"）
    }

    [Fact]
    public void Same_source_different_title_does_not_merge()
    {
        var clock = new Clock();
        var svc = new ActivityService(clock.Get);
        svc.Post(Item("a", "src", ActivityPriority.Notice, title: "甲", createdAt: clock.Now));
        svc.Post(Item("b", "src", ActivityPriority.Notice, title: "乙", createdAt: clock.Now));
        Assert.Equal(2, svc.Queue.Count + 1);
    }

    [Fact]
    public void Ttl_expires_transient_current_and_resumes_next()
    {
        var clock = new Clock();
        var svc = new ActivityService(clock.Get);
        svc.Post(Item("notice", "src", ActivityPriority.Notice, ttl: TimeSpan.FromSeconds(5), createdAt: clock.Now));
        svc.Post(Item("backup", "src2", ActivityPriority.Background, createdAt: clock.Now));

        clock.Advance(TimeSpan.FromSeconds(6));
        svc.Tick();
        Assert.Equal("backup", svc.Current!.Id);  // 当前过期 → 续播队列
        Assert.DoesNotContain(svc.Queue, x => x.Id == "notice");
    }

    [Fact]
    public void Sticky_never_expires_until_explicit_complete()
    {
        var clock = new Clock();
        var svc = new ActivityService(clock.Get);
        svc.Post(Item("music", "media", ActivityPriority.Media, title: "正在播放",
            kind: ActivityKind.Sticky, createdAt: clock.Now));

        clock.Advance(TimeSpan.FromHours(2)); // 远超 TTL
        svc.Tick();
        Assert.Equal("music", svc.Current!.Id); // 粘性不淘汰

        svc.Complete("music");
        Assert.Null(svc.Current);
    }

    [Fact]
    public void Suppression_queues_current_and_resumes_by_priority_on_release()
    {
        var clock = new Clock();
        var svc = new ActivityService(clock.Get);
        svc.Post(Item("notice", "src", ActivityPriority.Notice, createdAt: clock.Now));
        svc.Post(Item("attention", "src2", ActivityPriority.Attention, createdAt: clock.Now));
        Assert.Equal("attention", svc.Current!.Id);

        svc.SetSuppressed(true);
        Assert.Null(svc.Current);              // 全屏/DND：不弹出
        Assert.Equal(2, svc.Queue.Count);

        svc.SetSuppressed(false);
        Assert.Equal("attention", svc.Current!.Id); // 解除后按优先级补播
    }

    [Fact]
    public void Queue_ordered_by_priority_then_created_at_desc()
    {
        var clock = new Clock();
        var svc = new ActivityService(clock.Get);
        svc.Post(Item("low", "src", ActivityPriority.Background, createdAt: clock.Now));
        svc.Post(Item("high", "src", ActivityPriority.Notice, createdAt: clock.Now));
        svc.Post(Item("mid", "src", ActivityPriority.Progress, createdAt: clock.Now));
        svc.Post(Item("top", "src", ActivityPriority.Attention, createdAt: clock.Now)); // 占据当前

        var queue = svc.Queue.Select(x => x.Id).ToList();
        Assert.Equal(new[] { "high", "mid", "low" }, queue); // 优先级降序：Notice > Progress > Background
    }

    [Fact]
    public void Changed_fires_only_on_identity_or_order_changes()
    {
        var clock = new Clock();
        var svc = new ActivityService(clock.Get);
        var fires = 0;
        svc.Changed += () => fires++;

        svc.Post(Item("a", "src", ActivityPriority.Notice, createdAt: clock.Now)); // 新当前 → 触发
        var afterPost = fires;
        Assert.Equal(1, afterPost);

        svc.Post(Item("b", "src2", ActivityPriority.Notice, createdAt: clock.Now)); // 同优先级后到 → 抢占 → 触发
        Assert.Equal(2, fires);

        svc.Complete("b"); // 当前完成 → 触发
        Assert.Equal(3, fires);

        svc.SetSuppressed(true); // 抑制切换 → 触发
        Assert.Equal(4, fires);
    }

    [Fact]
    public void Zero_subscribers_operations_are_safe_and_consistent()
    {
        var clock = new Clock();
        var svc = new ActivityService(clock.Get); // 无订阅者
        svc.Post(Item("a", "src", ActivityPriority.Notice, createdAt: clock.Now));
        svc.Post(Item("b", "src2", ActivityPriority.Attention, createdAt: clock.Now, kind: ActivityKind.Sticky)); // 粘性：Tick 不淘汰
        svc.SetSuppressed(true);
        svc.SetSuppressed(false);
        svc.Complete("a");
        clock.Advance(TimeSpan.FromSeconds(30));
        svc.Tick();
        Assert.Equal("b", svc.Current!.Id); // 状态机在无订阅者时保持一致
    }

    [Fact]
    public void Complete_removes_from_queue_when_not_current()
    {
        var clock = new Clock();
        var svc = new ActivityService(clock.Get);
        svc.Post(Item("current", "src", ActivityPriority.Notice, createdAt: clock.Now));
        svc.Post(Item("queued", "src2", ActivityPriority.Background, createdAt: clock.Now));
        Assert.Single(svc.Queue);

        svc.Complete("queued");
        Assert.Empty(svc.Queue);
        Assert.Equal("current", svc.Current!.Id);
    }
}
