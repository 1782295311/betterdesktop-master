// BetterDesktop.Kernel.Timer — TimerService 实现
// 一切皆插件：timer 自身是插件，LoadAsync 提供 ITimerService；卸载取消全部定时器

using BetterDesktop.Kernel.Contracts;

namespace BetterDesktop.Kernel.Timer;

/// <summary>托管定时器服务实现。</summary>
public sealed class TimerService : IPlugin, ITimerService
{
    private readonly List<CancellationTokenSource> _timers = new();
    private readonly object _gate = new();
    private IContext? _context;

    /// <inheritdoc />
    public string Name => "kernel.timer";

    /// <inheritdoc />
    public IReadOnlyList<Type> Inject => Array.Empty<Type>();

    /// <inheritdoc />
    public Task LoadAsync(IContext context, CancellationToken cancellationToken = default)
    {
        _context = context;
        context.Provide<ITimerService>(this);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        List<CancellationTokenSource> snapshot;
        lock (_gate)
        {
            snapshot = _timers.ToList();
            _timers.Clear();
        }
        foreach (var timer in snapshot)
        {
            timer.Cancel();
            timer.Dispose();
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IDisposable SetTimeout(Func<CancellationToken, Task> callback, TimeSpan delay)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var cts = Register();
        _ = RunAsync(callback, delay, cts, periodic: false);
        return new TimerDisposable(cts);
    }

    /// <inheritdoc />
    public IDisposable SetInterval(Func<CancellationToken, Task> callback, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var cts = Register();
        _ = RunAsync(callback, period, cts, periodic: true);
        return new TimerDisposable(cts);
    }

    private CancellationTokenSource Register()
    {
        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            _timers.Add(cts);
        }
        return cts;
    }

    private void Unregister(CancellationTokenSource cts)
    {
        lock (_gate)
        {
            _timers.Remove(cts);
        }
    }

    private async Task RunAsync(Func<CancellationToken, Task> callback, TimeSpan span, CancellationTokenSource cts, bool periodic)
    {
        try
        {
            if (periodic)
            {
                using var timer = new PeriodicTimer(span);
                while (await timer.WaitForNextTickAsync(cts.Token).ConfigureAwait(false))
                {
                    await callback(cts.Token).ConfigureAwait(false);
                }
            }
            else
            {
                await Task.Delay(span, cts.Token).ConfigureAwait(false);
                await callback(cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 取消即正常结束
        }
        catch (Exception ex)
        {
            _context?.Logger.Warn($"定时器回调异常（已隔离，定时器终止）：{ex}");
        }
        finally
        {
            Unregister(cts);
            cts.Dispose();
        }
    }

    private sealed class TimerDisposable : IDisposable
    {
        private CancellationTokenSource? _cts;

        public TimerDisposable(CancellationTokenSource cts)
        {
            _cts = cts;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _cts, null)?.Cancel();
        }
    }
}
