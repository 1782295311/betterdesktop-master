// BetterDesktop.Kernel — PluginHandle 实现（ADR-002 D1）
// 插件运行时（Fiber 对应物）：状态机 + 依赖等待 + effect 托管 + 依赖驱动重载

using BetterDesktop.Kernel.Contracts;

namespace BetterDesktop.Kernel.Core;

/// <summary>插件运行时实现（ADR-002 D1 冻结面）。</summary>
public sealed class PluginHandle : IPluginHandle
{
    private readonly CordisContext _context;
    private readonly IPlugin _plugin;
    private readonly List<EffectRegistration> _effects = new();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _restartGate = new(1, 1);
    private PluginState _state = PluginState.Pending;
    private Task? _current;

    internal PluginHandle(CordisContext context, IPlugin plugin)
    {
        _context = context;
        _plugin = plugin;
    }

    /// <inheritdoc />
    public PluginState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>诊断用：插件名称。</summary>
    public string PluginName => _plugin.Name;

    /// <summary>登记 effect（由 Context.Effect 在加载期调用）。</summary>
    internal void AddEffect(EffectRegistration registration)
    {
        lock (_gate)
        {
            _effects.Add(registration);
        }
    }

    /// <summary>注销 effect（显式注销句柄调用）。</summary>
    internal void RemoveEffect(EffectRegistration registration)
    {
        lock (_gate)
        {
            _effects.Remove(registration);
        }
    }

    /// <summary>服务图变化通知：依赖类型变化时触发重载（epoch 语义 v1 为实例粒度）。</summary>
    internal void OnServiceChanged(Type serviceType)
    {
        if (!_plugin.Inject.Contains(serviceType))
        {
            return;
        }
        if (State is PluginState.Disposed or PluginState.Unloading)
        {
            return;
        }
        _ = RestartSerializedAsync();
    }

    /// <summary>首次启动：依赖已满足则加载，否则保持 PENDING 等待通知。</summary>
    internal Task StartAsync()
    {
        lock (_gate)
        {
            if (_state != PluginState.Pending)
            {
                return _current ?? Task.CompletedTask;
            }
        }
        return RestartSerializedAsync();
    }

    /// <inheritdoc />
    public async Task AwaitAsync()
    {
        Task? current;
        lock (_gate)
        {
            current = _current;
        }
        if (current is not null)
        {
            await current.ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task RestartAsync()
    {
        return RestartSerializedAsync();
    }

    /// <summary>串行化重启，避免并发服务变化导致多个 RestartCoreAsync 互相覆盖 _current（A2）。</summary>
    private async Task RestartSerializedAsync()
    {
        await _restartGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _current = RestartCoreAsync();
            await _current.ConfigureAwait(false);
        }
        finally
        {
            _restartGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        await UnloadInternalAsync().ConfigureAwait(false);
        SetState(PluginState.Disposed);
        _context.RemovePlugin(this);
    }

    private async Task RestartCoreAsync()
    {
        var wasLoaded = State is PluginState.Active or PluginState.Failed;
        if (wasLoaded)
        {
            await UnloadInternalAsync().ConfigureAwait(false);
        }
        await ReloadAsync().ConfigureAwait(false);
    }

    private async Task UnloadInternalAsync()
    {
        var wasLoaded = State is PluginState.Active or PluginState.Failed;
        SetState(PluginState.Unloading);
        if (wasLoaded)
        {
            try
            {
                await _plugin.UnloadAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _context.Logger.Warn($"插件 {_plugin.Name} 卸载异常（已隔离）：{ex}");
            }
        }
        List<EffectRegistration> snapshot;
        lock (_gate)
        {
            snapshot = Enumerable.Reverse(_effects).ToList();
            _effects.Clear();
        }
        foreach (var registration in snapshot)
        {
            if (registration.TryDispose() is { } ex)
            {
                _context.Logger.Warn($"插件 {_plugin.Name} effect 清理异常（已隔离）：{ex}");
            }
        }
    }

    private async Task ReloadAsync()
    {
        SetState(PluginState.Loading);
        if (!AllDependenciesAvailable())
        {
            SetState(PluginState.Pending);
            return;
        }
        try
        {
            _context.ActiveFiber = this;
            try
            {
                await _plugin.LoadAsync(_context).ConfigureAwait(false);
            }
            finally
            {
                _context.ActiveFiber = null;
            }
            SetState(PluginState.Active);
        }
        catch (Exception ex)
        {
            _context.Logger.Error($"插件 {_plugin.Name} 加载失败：{ex}");
            DiagnosticLog.Trace("Plugin", $"FAILED {_plugin.Name}: {ex.GetType().Name}: {ex.Message}");
            SetState(PluginState.Failed);
        }
    }

    private bool AllDependenciesAvailable()
    {
        foreach (var type in _plugin.Inject)
        {
            if (_context.GetService(type) is null)
            {
                return false;
            }
        }
        return true;
    }

    private void SetState(PluginState state)
    {
        lock (_gate)
        {
            _state = state;
        }
    }
}
