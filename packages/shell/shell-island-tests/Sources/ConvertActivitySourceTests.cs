using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BetterDesktop.Activity.Contracts;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.Convert.Contracts;
using BetterDesktop.Shell.Core.Activity;
using BetterDesktop.Shell.Island.Sources;
using Xunit;

namespace BetterDesktop.Shell.Island.Tests.Sources;

/// <summary>
/// 转换来源 × 活动仲裁 的接线机检（计划 T4）：进度上屏、失败终态抢占、退订配对（7438）。
/// 用假的 IEventBus 驱动真实事件载荷，不依赖转换引擎。
/// </summary>
public class ConvertActivitySourceTests
{
    private sealed class Clock
    {
        public DateTimeOffset Now = new(2026, 9, 16, 9, 0, 0, TimeSpan.FromHours(8));
        public DateTimeOffset Get() => Now;
    }

    [Fact]
    public async Task Progress_event_lands_on_island_as_progress_activity()
    {
        var clock = new Clock();
        var activity = new ActivityService(clock.Get);
        var bus = new RecordingEventBus();
        using var source = new ConvertActivitySource(bus, activity, null);
        source.Start();

        Assert.Equal(1, bus.HandlerCount("convert/progress"));

        await bus.EmitAsync("convert/progress", new ConvertProgressEventPayload(
            Source: @"C:\tmp\big.docx", Target: "pdf", Engine: "managed", Percent: 42, Phase: "running", ElapsedMs: 1200));

        var current = activity.Current;
        Assert.NotNull(current);
        Assert.Equal("island.convert.progress", current!.Id);
        Assert.Equal(IslandSourceConvert, current.Source);
        Assert.Equal(ActivityKind.Progress, current.Kind);
        Assert.Equal(ActivityPriority.Progress, current.Priority);
        Assert.Equal(0.42, current.Progress!.Value, 3);
        Assert.False(current.Failed);
        Assert.Contains("big.docx", current.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Indeterminate_progress_publishes_null_percent_without_faking_precision()
    {
        var activity = new ActivityService(new Clock().Get);
        var bus = new RecordingEventBus();
        using var source = new ConvertActivitySource(bus, activity, null);
        source.Start();

        await bus.EmitAsync("convert/progress", new ConvertProgressEventPayload(
            @"C:\tmp\a.pdf", null, "poppler", Percent: null, Phase: "verifying", ElapsedMs: 300));

        Assert.Null(activity.Current!.Progress);
    }

    [Fact]
    public async Task Failure_preempts_and_marks_failed()
    {
        var activity = new ActivityService(new Clock().Get);
        var bus = new RecordingEventBus();
        using var source = new ConvertActivitySource(bus, activity, null);
        source.Start();

        await bus.EmitAsync("convert/progress", new ConvertProgressEventPayload(
            @"C:\tmp\a.pdf", "docx", "managed", 10, "running", 100));
        await bus.EmitAsync("convert/failed", new ConvertEventPayload(
            @"C:\tmp\a.pdf", "docx", "managed", "引擎未找到", 900));

        var current = activity.Current;
        Assert.Equal("转换失败", current!.Title);
        Assert.True(current.Failed);
        Assert.Equal(ActivityPriority.Attention, current.Priority); // 失败要压过普通提示
    }

    [Fact]
    public async Task Batch_finished_reports_counts_and_stops_progress_polling_of_activity()
    {
        var activity = new ActivityService(new Clock().Get);
        var bus = new RecordingEventBus();
        using var source = new ConvertActivitySource(bus, activity, null);
        source.Start();

        await bus.EmitAsync("convert/progress", new ConvertProgressEventPayload(
            @"C:\tmp\a.pdf", "docx", "managed", 50, "running", 100));
        await bus.EmitAsync("convert/batch-finished", new ConvertBatchEventPayload(Total: 3, Succeeded: 2, Failed: 1, Target: "pdf", ElapsedMs: 4300));

        var current = activity.Current;
        Assert.Equal("转换完成（有失败）", current!.Title);
        Assert.True(current.Failed);
        Assert.Equal(ActivityKind.Transient, current.Kind);
        Assert.Contains("失败 1 项", current.Body!, StringComparison.Ordinal);
        Assert.Empty(activity.Queue); // 进度活动已被终态 Complete 清掉
    }

    [Fact]
    public void Stop_unsubscribes_every_handler()
    {
        var activity = new ActivityService(new Clock().Get);
        var bus = new RecordingEventBus();
        var source = new ConvertActivitySource(bus, activity, null);
        source.Start();
        Assert.Equal(4, bus.TotalHandlerCount);

        source.Stop();
        Assert.Equal(0, bus.TotalHandlerCount);

        source.Dispose(); // 幂等：二次停止不得抛
        Assert.Equal(0, bus.TotalHandlerCount);
    }

    private static string IslandSourceConvert => "convert";

    /// <summary>最小 IEventBus 假件：只实现 On/Emit，其余分发模式测试不需要（抛 NotSupported）。</summary>
    private sealed class RecordingEventBus : IEventBus
    {
        private readonly Dictionary<string, List<object>> _handlers = new();

        public int TotalHandlerCount
        {
            get
            {
                var total = 0;
                foreach (var list in _handlers.Values)
                {
                    total += list.Count;
                }

                return total;
            }
        }

        public int HandlerCount(string name) => _handlers.TryGetValue(name, out var list) ? list.Count : 0;

        public IDisposable On<T>(string name, Func<T, CancellationToken, Task> handler) where T : notnull
        {
            if (!_handlers.TryGetValue(name, out var list))
            {
                list = new List<object>();
                _handlers[name] = list;
            }

            list.Add(handler);
            return new Unsubscriber(() => list.Remove(handler));
        }

        public Task EmitAsync<T>(string name, T payload, CancellationToken cancellationToken = default) where T : notnull
        {
            if (!_handlers.TryGetValue(name, out var list))
            {
                return Task.CompletedTask;
            }

            foreach (var handler in list.ToArray())
            {
                if (handler is Func<T, CancellationToken, Task> typed)
                {
                    typed(payload, cancellationToken);
                }
            }

            return Task.CompletedTask;
        }

        public IDisposable OnResult<T, TResult>(string name, Func<T, CancellationToken, Task<TResult>> handler) where T : notnull
            => throw new NotSupportedException();

        public Task<IReadOnlyList<TResult>> ParallelAsync<T, TResult>(string name, T payload, CancellationToken cancellationToken = default) where T : notnull
            => throw new NotSupportedException();

        public Task<IReadOnlyList<TResult>> SerialAsync<T, TResult>(string name, T payload, CancellationToken cancellationToken = default) where T : notnull
            => throw new NotSupportedException();

        public Task<TResult?> BailAsync<T, TResult>(string name, T payload, CancellationToken cancellationToken = default) where T : notnull
            => throw new NotSupportedException();

        public Task<T> WaterfallAsync<T>(string name, T payload, CancellationToken cancellationToken = default) where T : notnull
            => throw new NotSupportedException();

        private sealed class Unsubscriber : IDisposable
        {
            private Action? _dispose;

            public Unsubscriber(Action dispose) => _dispose = dispose;

            public void Dispose()
            {
                _dispose?.Invoke();
                _dispose = null;
            }
        }
    }
}
