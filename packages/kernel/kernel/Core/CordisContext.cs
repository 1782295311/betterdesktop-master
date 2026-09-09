// BetterDesktop.Kernel — CordisContext 实现（ADR-002 D1）
// 服务图 + 插件调度 + 托管清理（无透明代理，显式 Get）

using System.Threading;
using BetterDesktop.Kernel.Contracts;

namespace BetterDesktop.Kernel.Core;

// ============================================================
// 【白话导航 · Cordis 插件内核】凭白话需求定位到精确文件（Contracts/ 为对外契约，Core/ 为实现）：
//   "插件怎么写 / 生命周期接口"        → Contracts/IPlugin.cs、IPluginHandle.cs、PluginState.cs
//   "服务注册与获取（Provide/Get/Inject）" → 本文件 CordisContext.cs（契约 Contracts/IContext.cs）
//   "单个插件的加载/卸载/状态句柄"     → Core/PluginHandle.cs
//   "跨插件事件通信"                  → Core/EventBus.cs（契约 Contracts/IEventBus.cs；事件名集中在 ShellEvents/HmrEvents）
//   "热重载（HMR 换插件不重启）"       → kernel-hmr/HmrManager.cs（双 ALC 切换 + 状态迁移 + 回滚）
//   "按清单加载插件 / 程序集隔离"      → kernel-loader/LoaderService.cs + kernel-hmr/PluginLoadContext.cs（AssemblyLoadContext）
//   "插件依赖排序 / 加载顺序"          → kernel-hmr/PluginDependencyResolver.cs
//   "CPU/内存资源配额与治理"          → Core/ResourceGovernor.cs（IResourceGovernor.cs）
//   "电源管理（睡眠/休眠感知）"        → Core/PowerManagement.cs
//   "统一定时器"                      → kernel-timer/TimerService.cs
//   "内核日志 / 诊断"                 → Core/KernelLogger.cs、Core/DiagnosticLog.cs
//   "能力声明与权限"                  → Core/Capability.cs、Core/CapabilityPermission.cs
// ============================================================

/// <summary>内核上下文实现（ADR-002 D1 冻结面）。</summary>
public sealed class CordisContext : IContext, IDisposable
{
    private readonly CordisContext? _parent;
    private readonly List<CordisContext> _children = new();
    private readonly Dictionary<Type, object> _services = new();
    private readonly List<PluginHandle> _plugins = new();
    private readonly Dictionary<Type, List<PluginHandle>> _injectCache = new();
    private readonly List<EffectRegistration> _rootEffects = new();
    private readonly EventBus _eventBus;
    private readonly KernelLogger _logger;
    private readonly AsyncLocal<PluginHandle?> _activeFiber = new();

    /// <summary>构造根/子上下文；logSink 用于测试与诊断（P2 宿主接文件管道）。</summary>
    public CordisContext(CordisContext? parent = null, Action<LogLevel, string>? logSink = null)
    {
        _parent = parent;
        _logger = new KernelLogger(logSink);
        _eventBus = new EventBus(_logger);
    }

    /// <inheritdoc />
    public IEventBus Events => _eventBus;

    /// <inheritdoc />
    public IKernelLogger Logger => _logger;

    /// <summary>当前正在加载的 fiber（Effect 归属），仅供 PluginHandle 设置。用 AsyncLocal 承载，使并发加载的不同插件各自得到正确的归属。</summary>
    internal PluginHandle? ActiveFiber
    {
        get => _activeFiber.Value;
        set => _activeFiber.Value = value;
    }

    /// <inheritdoc />
    public T? Get<T>() where T : class
    {
        return (T?)GetService(typeof(T));
    }

    /// <summary>按类型沿父链解析服务（供依赖检查使用）。</summary>
    internal object? GetService(Type type)
    {
        for (var context = this; context is not null; context = context._parent)
        {
            if (context._services.TryGetValue(type, out var service))
            {
                return service;
            }
        }
        return null;
    }

    /// <inheritdoc />
    public IDisposable Provide<T>(T service) where T : class
    {
        ArgumentNullException.ThrowIfNull(service);
        var type = typeof(T);
        var added = !_services.ContainsKey(type);
        var previous = added ? null : _services[type];
        _services[type] = service;
        if (added || !ReferenceEquals(previous, service))
        {
            NotifyDependents(type);
        }
        return new ActionDisposable(() =>
        {
            if (_services.TryGetValue(type, out var current) && ReferenceEquals(current, service))
            {
                _services.Remove(type);
                NotifyDependents(type);
            }
        });
    }

    /// <inheritdoc />
    public IContext Extend()
    {
        var child = new CordisContext(this);
        _children.Add(child);
        return child;
    }

    /// <inheritdoc />
    public IPluginHandle Plugin(IPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        var handle = new PluginHandle(this, plugin);
        _plugins.Add(handle);
        // 构建依赖缓存：按 Inject 类型索引插件句柄，Provide 时 O(1) 查找替代 O(N) 全量遍历
        foreach (var injectType in plugin.Inject)
        {
            if (!_injectCache.TryGetValue(injectType, out var list))
            {
                list = new List<PluginHandle>();
                _injectCache[injectType] = list;
            }
            list.Add(handle);
        }
        _ = handle.StartAsync();
        // 注册到内存治理器：所有进 Context 的插件都被真实监控，消除治理器空转。
        // 治理器在进程级持续超致命阈值时触发 C1 宿主自重启兜底（不擅自逐个杀 in-process 插件）。
        if (GetService(typeof(IResourceGovernor)) is IResourceGovernor gov)
        {
            gov.RegisterSubject(new PluginHandleSubject(handle));
        }
        return handle;
    }

    /// <summary>从上下文注销已卸载的插件句柄，避免 _plugins 累积僵尸条目（B4）。</summary>
    internal void RemovePlugin(PluginHandle handle)
    {
        _plugins.Remove(handle);
        // 同步从依赖缓存移除：遍历该插件的 Inject 类型，从对应缓存列表中删除句柄
        foreach (var injectType in handle.InjectTypes)
        {
            if (_injectCache.TryGetValue(injectType, out var list))
            {
                list.Remove(handle);
                if (list.Count == 0)
                {
                    _injectCache.Remove(injectType);
                }
            }
        }
        // 同步从内存治理器注销（按插件实例唯一键，与 PluginHandleSubject.Id 对齐；B3 修复）。
        if (GetService(typeof(IResourceGovernor)) is IResourceGovernor gov)
        {
            gov.UnregisterSubject(handle.InstanceId);
        }
    }

    /// <summary>列出当前已注册的插件句柄（诊断用）。</summary>
    public IEnumerable<PluginHandle> GetHandles() => _plugins.ToList();

    /// <inheritdoc />
    public IDisposable Effect(Func<IDisposable> execute, string? label = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        var disposer = execute();
        var registration = new EffectRegistration(label, disposer);
        var fiber = _activeFiber.Value;
        if (fiber is not null)
        {
            fiber.AddEffect(registration);
            return new ActionDisposable(() =>
            {
                fiber.RemoveEffect(registration);
                registration.Dispose();
            });
        }
        _rootEffects.Add(registration);
        return new ActionDisposable(() =>
        {
            _rootEffects.Remove(registration);
            registration.Dispose();
        });
    }

    /// <summary>通知依赖指定类型的插件（递归到子上下文）。通过依赖缓存 O(1) 查找替代 O(N) 全量遍历。</summary>
    internal void NotifyDependents(Type serviceType)
    {
        if (_injectCache.TryGetValue(serviceType, out var dependents))
        {
            foreach (var plugin in dependents)
            {
                plugin.OnServiceChanged(serviceType);
            }
        }
        foreach (var child in _children)
        {
            child.NotifyDependents(serviceType);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _parent?._children.Remove(this);
        foreach (var plugin in _plugins.ToList())
        {
            // Dispose 路径允许同步等待（非 UI 线程；coding-standards 并发纪律 2 的例外已登记）
            plugin.DisposeAsync().GetAwaiter().GetResult();
        }
        foreach (var registration in Enumerable.Reverse(_rootEffects).ToList())
        {
            if (registration.TryDispose() is { } ex)
            {
                _logger.Warn($"根效应清理异常（已隔离）：{ex}");
            }
        }
        _rootEffects.Clear();
    }
}
