using System;
using System.Collections.Generic;
using System.Linq;
using BetterDesktop.Activity.Contracts;

namespace BetterDesktop.Shell.Core.Activity;

/// <summary>
/// 活动仲裁器（纯逻辑，无 UI 无窗口；时钟可注入，单测直接驱动 TTL/排序）。
/// <para>
/// 规则（岛文档 §3 仲裁表）：优先级 + 同优先级后到先显示；抢占回队列不丢失、回落续播；
/// Sticky 常驻、Transient/Progress 按 TTL 自动收起；同源连续事件折叠计数；抑制期进队列、解除后补播。
/// </para>
/// <para>
/// Changed 节流：只在"当前活动身份"或"队列顺序"变化时触发；同 Id 更新（进度/正文/图标）不触发
/// （订阅方帧级轮询 Current）；无订阅者时不做任何变化检测（零开销）。
/// </para>
/// </summary>
public sealed class ActivityService : IActivityService
{
    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _clock;
    private readonly List<ActivityItem> _queue = new();
    private ActivityItem? _current;
    private bool _suppressed;

    /// <param name="clock">时钟（默认系统时间；测试注入固定时钟驱动 TTL/排序）。</param>
    public ActivityService(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    /// <inheritdoc />
    public ActivityItem? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ActivityItem> Queue
    {
        get
        {
            lock (_gate)
            {
                return Ordered(_queue).ToList();
            }
        }
    }

    /// <summary>
    /// 当前活动或队列顺序变化（类级事件，ADR-002 D4：接口不声明跨程序集裸 event；
    /// 岛渲染（P3）跨包消费届时走 IEventBus 桥接；进度更新不触发——订阅方帧级轮询 Current）。
    /// </summary>
    public event Action? Changed;

    /// <inheritdoc />
    public void Post(ActivityItem item)    {
        if (item is null)
        {
            throw new ArgumentNullException(nameof(item));
        }

        lock (_gate)
        {
            var now = _clock();

            // 1) 同 Id → 更新（进度/内容），身份不变 → 不触发 Changed（帧级节流由订阅方做）
            if (TryFind(item.Id, out var existing, out var atCurrent))
            {
                var updated = existing with
                {
                    Source = item.Source,
                    Kind = item.Kind,
                    Priority = item.Priority,
                    Title = item.Title,
                    Body = item.Body,
                    IconPath = item.IconPath,
                    Progress = item.Progress,
                    TimeToLive = item.TimeToLive,
                    Actions = item.Actions,
                };
                if (atCurrent)
                {
                    _current = updated;
                }
                else
                {
                    var i = _queue.FindIndex(x => x.Id == item.Id);
                    _queue[i] = updated;
                    if (HasSubscribers && PriorityOrOrderChanged(updated))
                    {
                        FireChanged(); // 优先级变化 → 可能重排/抢占
                    }
                }

                return;
            }

            // 2) 同源合并：同 Source + 同 Title 的 Transient 条目折叠计数（"5 项转换完成"避免抖动）
            if (TryFindMergeCandidate(item, out var mergeTarget, out var mergeAtCurrent))
            {
                var merged = mergeTarget with { MergeCount = mergeTarget.MergeCount + 1, CreatedAt = now };
                if (mergeAtCurrent)
                {
                    _current = merged; // 当前项续期 + 计数，身份不变 → 不触发
                }
                else
                {
                    var i = _queue.FindIndex(x => x.Id == mergeTarget.Id);
                    _queue[i] = merged;
                    FireChanged(); // CreatedAt 刷新 → 队列顺序可能变化
                }

                return;
            }

            // 3) 新条目：抑制期进队列；无当前 → 直接上；高优先或同优先级后到 → 抢占（被抢占项回队列）
            if (_suppressed)
            {
                _queue.Add(item);
                FireChanged();
                return;
            }

            if (_current is null)
            {
                _current = item;
                FireChanged();
                return;
            }

            if (item.Priority > _current.Priority
                || (item.Priority == _current.Priority && item.CreatedAt >= _current.CreatedAt))
            {
                _queue.Add(_current);
                _current = item;
                FireChanged();
                return;
            }

            _queue.Add(item);
            FireChanged();
        }
    }

    /// <inheritdoc />
    public void Complete(string id, string? resultText = null)
    {
        lock (_gate)
        {
            var wasCurrent = _current is not null && string.Equals(_current.Id, id, StringComparison.Ordinal);
            var removed = _queue.RemoveAll(x => string.Equals(x.Id, id, StringComparison.Ordinal)) > 0;
            if (!wasCurrent && !removed)
            {
                return;
            }

            if (wasCurrent)
            {
                _current = null;
                Resume();
            }

            FireChanged();
        }
    }

    /// <inheritdoc />
    public void Dismiss(string id) => Complete(id);

    /// <inheritdoc />
    public void SetSuppressed(bool suppressed)
    {
        lock (_gate)
        {
            if (_suppressed == suppressed)
            {
                return;
            }

            _suppressed = suppressed;
            if (suppressed)
            {
                if (_current is not null)
                {
                    _queue.Add(_current); // 不弹出：当前项退队列
                    _current = null;
                }
            }
            else
            {
                Resume(); // 解除抑制 → 按优先级补播
            }

            FireChanged();
        }
    }

    /// <summary>
    /// TTL 过期扫描（宿主定时调用；Transient/Progress 到期自动收起，Sticky 永不淘汰）。
    /// </summary>
    public void Tick()
    {
        lock (_gate)
        {
            var now = _clock();
            var removed = _queue.RemoveAll(x => IsExpired(x, now));
            var currentExpired = _current is not null && IsExpired(_current, now);
            if (currentExpired)
            {
                _current = null;
                Resume();
            }

            if ((removed > 0 || currentExpired) && HasSubscribers)
            {
                FireChanged();
            }
        }
    }

    // ---- 内部 ----

    private bool HasSubscribers => Changed is not null;

    private void FireChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch
        {
            // 订阅方异常不得破坏仲裁状态机
        }
    }

    private bool TryFind(string id, out ActivityItem item, out bool atCurrent)
    {
        if (_current is not null && string.Equals(_current.Id, id, StringComparison.Ordinal))
        {
            item = _current;
            atCurrent = true;
            return true;
        }

        var i = _queue.FindIndex(x => string.Equals(x.Id, id, StringComparison.Ordinal));
        if (i >= 0)
        {
            item = _queue[i];
            atCurrent = false;
            return true;
        }

        item = null!;
        atCurrent = false;
        return false;
    }

    private bool TryFindMergeCandidate(ActivityItem incoming, out ActivityItem item, out bool atCurrent)
    {
        if (_current is not null && IsMergeable(_current, incoming))
        {
            item = _current;
            atCurrent = true;
            return true;
        }

        var i = _queue.FindIndex(x => IsMergeable(x, incoming));
        if (i >= 0)
        {
            item = _queue[i];
            atCurrent = false;
            return true;
        }

        item = null!;
        atCurrent = false;
        return false;
    }

    private static bool IsMergeable(ActivityItem existing, ActivityItem incoming)
        => existing.Kind == ActivityKind.Transient
           && string.Equals(existing.Source, incoming.Source, StringComparison.Ordinal)
           && string.Equals(existing.Title, incoming.Title, StringComparison.Ordinal);

    private bool PriorityOrOrderChanged(ActivityItem updated)
    {
        // 同 Id 更新只在优先级变化可能影响仲裁时才需要重排检查
        var current = _current;
        return current is not null
               && string.Equals(updated.Id, current.Id, StringComparison.Ordinal)
               && updated.Priority != current.Priority;
    }

    private void Resume()
    {
        if (_suppressed)
        {
            return;
        }

        var now = _clock();
        _queue.RemoveAll(x => IsExpired(x, now)); // 续播前清掉过期项
        var best = Ordered(_queue).FirstOrDefault();
        if (best is not null)
        {
            _queue.Remove(best);
            _current = best;
        }
    }

    private static bool IsExpired(ActivityItem item, DateTimeOffset now)
        => item.Kind != ActivityKind.Sticky && now - item.CreatedAt > item.TimeToLive;

    /// <summary>仲裁排序：优先级降序，同优先级 CreatedAt 降序（后到先显示）。</summary>
    private static IEnumerable<ActivityItem> Ordered(IEnumerable<ActivityItem> items)
        => items.OrderByDescending(x => x.Priority).ThenByDescending(x => x.CreatedAt);
}
