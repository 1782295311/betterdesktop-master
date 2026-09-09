// BetterDesktop.Shell.Status.Tests — 轮询器：变化检测 + IEventBus 广播
// 核心验收：值变化时才广播；值未变 (轮询重复) 不重复广播。

using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.Status.Contracts;
using BetterDesktop.Shell.Status.Services;
using Xunit;

namespace BetterDesktop.Shell.Status.Tests;

/// <summary>轮询器广播测试。</summary>
public class StatusPollerTests
{
    private sealed class StubEventBus : IEventBus
    {
        public List<(string Name, StatusSnapshot Snapshot)> Emitted { get; } = new();

        public IDisposable On<T>(string name, Func<T, CancellationToken, Task> handler) where T : notnull => Empty.Disposable;
        public IDisposable OnResult<T, TResult>(string name, Func<T, CancellationToken, Task<TResult>> handler) where T : notnull => Empty.Disposable;
        public Task EmitAsync<T>(string name, T payload, CancellationToken cancellationToken = default) where T : notnull
        {
            if (payload is StatusSnapshot s)
            {
                Emitted.Add((name, s));
            }
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<TResult>> ParallelAsync<T, TResult>(string name, T payload, CancellationToken cancellationToken = default) where T : notnull
            => Task.FromResult<IReadOnlyList<TResult>>(Array.Empty<TResult>());
        public Task<IReadOnlyList<TResult>> SerialAsync<T, TResult>(string name, T payload, CancellationToken cancellationToken = default) where T : notnull
            => Task.FromResult<IReadOnlyList<TResult>>(Array.Empty<TResult>());
        public Task<TResult?> BailAsync<T, TResult>(string name, T payload, CancellationToken cancellationToken = default) where T : notnull
            => Task.FromResult<TResult?>(default);
        public Task<T> WaterfallAsync<T>(string name, T payload, CancellationToken cancellationToken = default) where T : notnull
            => Task.FromResult(payload);

        private static class Empty { public static readonly IDisposable Disposable = new Noop(); }
        private sealed class Noop : IDisposable { public void Dispose() { } }
    }

    [Fact]
    public void PollNow_AfterValueChange_EmittedOnEventBus()
    {
        var source = new FakeSystemSource { Memory = FakeSystemSource.MakeMemory(50) };
        var monitor = new MemoryMonitor(source);
        var bus = new StubEventBus();
        using var poller = new StatusPoller(new IStatusMonitor[] { monitor }, bus);

        // 首次采集：记录并广播。
        poller.PollNow();
        Assert.Single(bus.Emitted);
        Assert.Equal("status.changed", bus.Emitted[0].Name);

        // 值变化后再采集：再次广播。
        source.Memory = FakeSystemSource.MakeMemory(80);
        poller.PollNow();
        Assert.Equal(2, bus.Emitted.Count);
        Assert.Equal(80, bus.Emitted[1].Snapshot.Progress);

        // 值未变（重复轮询）：不重复广播。
        poller.PollNow();
        Assert.Equal(2, bus.Emitted.Count);
    }

    [Fact]
    public void Changed_EventFiresOnlyWhenValueDiffers()
    {
        var source = new FakeSystemSource { Memory = FakeSystemSource.MakeMemory(50) };
        var monitor = new MemoryMonitor(source);
        var bus = new StubEventBus();
        using var poller = new StatusPoller(new IStatusMonitor[] { monitor }, bus);

        var changedCount = 0;
        monitor.Changed += (_, _) => changedCount++;

        poller.PollNow();
        poller.PollNow(); // 未变 → 不再触发
        Assert.Equal(1, changedCount);

        source.Memory = FakeSystemSource.MakeMemory(30);
        poller.PollNow();
        Assert.Equal(2, changedCount);
    }
}
