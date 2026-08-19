// BetterDesktop.Kernel — EventBus 实现（ADR-002 D4）
// 事件名「域/动作」+ 强类型载荷 + 五种分发 + 单监听器异常隔离
// 实现说明：监听器统一装箱为 Func<object, CT, Task<object?>> 存储；
// 无返回值监听器装箱后返回 null；值监听器装箱为 object。

using BetterDesktop.Kernel.Contracts;

namespace BetterDesktop.Kernel.Core;

/// <summary>内核事件服务实现（ADR-002 D4）。</summary>
public sealed class EventBus : IEventBus
{
    private readonly Dictionary<string, List<HandlerEntry>> _handlers = new();
    private readonly object _gate = new();
    private readonly KernelLogger _logger;

    /// <summary>构造。</summary>
    public EventBus(KernelLogger logger)
    {
        _logger = logger;
    }

    private sealed class HandlerEntry
    {
        public HandlerEntry(Func<object, CancellationToken, Task<object?>> handler)
        {
            Handler = handler;
        }

        public Func<object, CancellationToken, Task<object?>> Handler { get; }
    }

    /// <inheritdoc />
    public IDisposable On<T>(string name, Func<T, CancellationToken, Task> handler) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(handler);
        Func<object, CancellationToken, Task<object?>> wrapped = async (payload, ct) =>
        {
            await handler((T)payload, ct).ConfigureAwait(false);
            return null;
        };
        return Register(name, wrapped);
    }

    /// <inheritdoc />
    public IDisposable OnResult<T, TResult>(string name, Func<T, CancellationToken, Task<TResult>> handler) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(handler);
        Func<object, CancellationToken, Task<object?>> wrapped = async (payload, ct) =>
            await handler((T)payload, ct).ConfigureAwait(false);
        return Register(name, wrapped);
    }

    /// <inheritdoc />
    public async Task EmitAsync<T>(string name, T payload, CancellationToken cancellationToken = default) where T : notnull
    {
        var handlers = Snapshot(name);
        await Task.WhenAll(handlers.Select(h => RunQuiet(h, payload, cancellationToken))).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TResult>> ParallelAsync<T, TResult>(string name, T payload, CancellationToken cancellationToken = default) where T : notnull
    {
        var handlers = Snapshot(name);
        var results = await Task.WhenAll(handlers.Select(h => RunTypedQuiet<T, TResult>(h, payload, cancellationToken))).ConfigureAwait(false);
        return results.Where(r => r is not null).Select(r => r!).ToArray();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TResult>> SerialAsync<T, TResult>(string name, T payload, CancellationToken cancellationToken = default) where T : notnull
    {
        var results = new List<TResult>();
        foreach (var handler in Snapshot(name))
        {
            var result = await RunTypedQuiet<T, TResult>(handler, payload, cancellationToken).ConfigureAwait(false);
            if (result is not null)
            {
                results.Add(result);
            }
        }
        return results;
    }

    /// <inheritdoc />
    public async Task<TResult?> BailAsync<T, TResult>(string name, T payload, CancellationToken cancellationToken = default) where T : notnull
    {
        foreach (var handler in Snapshot(name))
        {
            var result = await RunTypedQuiet<T, TResult>(handler, payload, cancellationToken).ConfigureAwait(false);
            if (result is not null && !EqualityComparer<TResult>.Default.Equals(result, default))
            {
                return result;
            }
        }
        return default;
    }

    /// <inheritdoc />
    public async Task<T> WaterfallAsync<T>(string name, T payload, CancellationToken cancellationToken = default) where T : notnull
    {
        var current = payload;
        foreach (var handler in Snapshot(name))
        {
            var result = await RunTypedQuiet<T, T>(handler, current, cancellationToken).ConfigureAwait(false);
            if (result is not null)
            {
                current = result;
            }
        }
        return current;
    }

    private IDisposable Register(string name, Func<object, CancellationToken, Task<object?>> handler)
    {
        var entry = new HandlerEntry(handler);
        lock (_gate)
        {
            if (!_handlers.TryGetValue(name, out var list))
            {
                list = new List<HandlerEntry>();
                _handlers[name] = list;
            }
            list.Add(entry);
        }
        return new ActionDisposable(() =>
        {
            lock (_gate)
            {
                if (_handlers.TryGetValue(name, out var list))
                {
                    list.Remove(entry);
                }
            }
        });
    }

    private List<HandlerEntry> Snapshot(string name)
    {
        lock (_gate)
        {
            return _handlers.TryGetValue(name, out var list) ? list.ToList() : new List<HandlerEntry>();
        }
    }

    private async Task RunQuiet<T>(HandlerEntry handler, T payload, CancellationToken cancellationToken) where T : notnull
    {
        try
        {
            await handler.Handler(payload, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warn($"事件监听器异常（已隔离）：{ex}");
        }
    }

    private async Task<TResult?> RunTypedQuiet<T, TResult>(HandlerEntry handler, T payload, CancellationToken cancellationToken) where T : notnull
    {
        try
        {
            var result = await handler.Handler(payload, cancellationToken).ConfigureAwait(false);
            return result is null ? default : (TResult)result;
        }
        catch (Exception ex)
        {
            _logger.Warn($"事件监听器异常（已隔离）：{ex}");
            return default;
        }
    }
}
