// BetterDesktop.Kernel.Timer.Tests — 定时器契约测试
// 一次性 / 周期 / 注销 / 异常隔离

using BetterDesktop.Kernel.Core;
using BetterDesktop.Kernel.Timer;
using Xunit;

namespace BetterDesktop.Kernel.Timer.Tests;

public sealed class TimerServiceTests
{
    private static (CordisContext Context, ITimerService Timer) CreateService()
    {
        var context = new CordisContext();
        var service = new TimerService();
        var handle = context.Plugin(service);
        handle.AwaitAsync().GetAwaiter().GetResult();
        return (context, context.Get<ITimerService>()!);
    }

    [Fact(DisplayName = "setTimeout 延时后执行一次")]
    public async Task SetTimeout_FiresOnceAfterDelay()
    {
        var (context, timer) = CreateService();
        using (context)
        {
            var tcs = new TaskCompletionSource();
            var calls = 0;
            timer.SetTimeout(_ =>
            {
                calls++;
                tcs.TrySetResult();
                return Task.CompletedTask;
            }, TimeSpan.FromMilliseconds(80));

            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(150);
            Assert.Equal(1, calls);
        }
    }

    [Fact(DisplayName = "setInterval 周期执行，注销后停止")]
    public async Task SetInterval_FiresPeriodically_StopsOnDispose()
    {
        var (context, timer) = CreateService();
        using (context)
        {
            var count = 0;
            var handle = timer.SetInterval(_ =>
            {
                Interlocked.Increment(ref count);
                return Task.CompletedTask;
            }, TimeSpan.FromMilliseconds(60));

            await Task.Delay(400);
            var fired = Volatile.Read(ref count);
            Assert.True(fired >= 3, $"期望 >= 3 次，实际 {fired} 次");

            handle.Dispose();
            await Task.Delay(200);
            var after = Volatile.Read(ref count);
            Assert.Equal(fired, after);
        }
    }

    [Fact(DisplayName = "回调异常被隔离且终止该定时器")]
    public async Task Callback_Throws_IsolatedAndStopped()
    {
        var (context, timer) = CreateService();
        using (context)
        {
            var calls = 0;
            timer.SetInterval(_ =>
            {
                Interlocked.Increment(ref calls);
                throw new InvalidOperationException("boom");
            }, TimeSpan.FromMilliseconds(60));

            await Task.Delay(300);
            var fired = Volatile.Read(ref calls);
            Assert.Equal(1, fired);

            await Task.Delay(200);
            Assert.Equal(1, Volatile.Read(ref calls));
        }
    }

    [Fact(DisplayName = "服务卸载取消全部定时器")]
    public async Task Unload_CancelsAllTimers()
    {
        var context = new CordisContext();
        var service = new TimerService();
        var handle = context.Plugin(service);
        await handle.AwaitAsync();
        var timer = context.Get<ITimerService>()!;

        var calls = 0;
        timer.SetInterval(_ =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        }, TimeSpan.FromMilliseconds(60));

        await Task.Delay(200);
        await handle.DisposeAsync();

        var fired = Volatile.Read(ref calls);
        await Task.Delay(200);
        Assert.Equal(fired, Volatile.Read(ref calls));
        context.Dispose();
    }
}
