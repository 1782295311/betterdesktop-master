// BetterDesktop.Kernel.Hmr — HmrManager 热重载管理器实现
// 双 ALC 切换 + 旧状态迁移 + 失败回滚（术语定义见 docs/TERMINOLOGY.md）

using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Kernel.Core;

namespace BetterDesktop.Kernel.Hmr;

// ── 本文件方法级白话索引（内核热重载管理器 HMR，白话 → 方法）──
//   "HMR 总开关"                     → Enable / Disable
//   "加载/卸载/重载单个插件、重载全部" → LoadPluginAsync / UnloadPluginAsync / ReloadPluginAsync / ReloadAllPluginsAsync
//   "查插件状态/运行时信息"          → GetPluginStatus / GetPluginRuntimeInfos
//   "资源治理（注册/注销被治理对象、更新配额）" → RegisterSubject / UnregisterSubject / GetResourceSubjects / UpdateGovernorOptions
//   "装载内核 / 回滚 / 生命周期事件"  → LoadCoreAsync / UnloadCoreAsync / RollbackAsync / EmitAsync
//   "托管表存取 / 版本快照（回滚比对）" → GetOrCreateManaged / TryGetManaged / SnapshotLoadedVersions
//   并发模型：_gate 保护字典、_operationGate 串行化装载；插件句柄见 PluginHandle，上下文见 CordisContext。
// ────────────────────────────────────

/// <summary>HMR 热重载管理器：双 ALC 切换 + 旧状态迁移 + 失败回滚 + 内存治理接入。</summary>
public sealed class HmrManager : IHmrManager, IDisposable
{
    private readonly IContext _context;
    private readonly SemanticVersion _kernelAbi;
    private readonly Func<PluginManifest, IPluginSource> _sourceFactory;
    private readonly Dictionary<string, ManagedPlugin> _plugins = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly ResourceGovernor _governor;
    private bool _isEnabled = true;

    /// <summary>默认内核 ABI 版本。</summary>
    public static SemanticVersion DefaultKernelAbi { get; } = new(1, 0, 0);

    /// <summary>构造：默认启用；sourceFactory 缺省为 AssemblyPluginSource；kernelAbi 缺省为 DefaultKernelAbi。</summary>
    public HmrManager(
        IContext context,
        SemanticVersion? kernelAbi = null,
        Func<PluginManifest, IPluginSource>? sourceFactory = null,
        ResourceGovernorOptions? governorOptions = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _kernelAbi = kernelAbi ?? DefaultKernelAbi;
        _sourceFactory = sourceFactory ?? (_ => new AssemblyPluginSource());
        _governor = new ResourceGovernor(_context.Logger, governorOptions);
    }

    /// <inheritdoc />
    public bool IsEnabled
    {
        get
        {
            lock (_gate)
            {
                return _isEnabled;
            }
        }
    }

    /// <inheritdoc />
    public SemanticVersion KernelAbi => _kernelAbi;

    /// <inheritdoc />
    public void Enable()
    {
        lock (_gate)
        {
            _isEnabled = true;
        }
    }

    /// <inheritdoc />
    public void Disable()
    {
        lock (_gate)
        {
            _isEnabled = false;
        }
    }

    /// <inheritdoc />
    public async Task<PluginLoadResult> LoadPluginAsync(PluginManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!IsEnabled)
        {
            return new PluginLoadResult(manifest.Id, false, PluginReloadStatus.Unknown, "HMR 已禁用");
        }
        if (!_kernelAbi.IsAbiCompatibleWith(manifest.KernelAbi))
        {
            var message = $"内核 ABI 不兼容：插件要求 >= {manifest.KernelAbi}，当前 {_kernelAbi}";
            await EmitAsync(new PluginLifecycleEvent(manifest.Id, manifest.Version.ToString(), PluginLifecycleKind.Rejected, message)).ConfigureAwait(false);
            _context.Logger.Error($"插件 {manifest.Id} 被拒绝：{message}");
            return new PluginLoadResult(manifest.Id, false, PluginReloadStatus.Failed, message);
        }

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetManaged(manifest.Id, out var existing) && existing.Status == PluginReloadStatus.Loaded)
            {
                return new PluginLoadResult(manifest.Id, false, PluginReloadStatus.Unknown, "插件已加载，请先卸载或使用重载");
            }

            var available = SnapshotLoadedVersions(excludeId: manifest.Id);
            var missing = PluginDependencyResolver.FindMissingRequiredDependencies(manifest, available);
            if (missing.Count > 0)
            {
                var message = $"缺少必需依赖：{string.Join("、", missing)}";
                await EmitAsync(new PluginLifecycleEvent(manifest.Id, manifest.Version.ToString(), PluginLifecycleKind.Rejected, message)).ConfigureAwait(false);
                _context.Logger.Error($"插件 {manifest.Id} 被拒绝：{message}");
                return new PluginLoadResult(manifest.Id, false, PluginReloadStatus.Failed, message);
            }

            var managed = GetOrCreateManaged(manifest);
            lock (_gate)
            {
                managed.Status = PluginReloadStatus.Loading;
                managed.LastTransitionUtc = DateTimeOffset.UtcNow;
            }
            try
            {
                await LoadCoreAsync(managed, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    managed.Status = PluginReloadStatus.Failed;
                    managed.MarkFailure();
                    managed.LastError = ex.Message;
                    managed.LastTransitionUtc = DateTimeOffset.UtcNow;
                }
                _context.Logger.Error($"插件 {manifest.Id} 加载失败：{ex}");
                await EmitAsync(new PluginLifecycleEvent(manifest.Id, manifest.Version.ToString(), PluginLifecycleKind.Rejected, ex.Message)).ConfigureAwait(false);
                return new PluginLoadResult(manifest.Id, false, PluginReloadStatus.Failed, ex.Message);
            }

            lock (_gate)
            {
                managed.Status = PluginReloadStatus.Loaded;
                managed.LoadCount++;
                managed.LastTransitionUtc = DateTimeOffset.UtcNow;
            }
            _context.Logger.Info($"插件 {manifest.Id} v{manifest.Version} 已加载");
            await EmitAsync(new PluginLifecycleEvent(manifest.Id, manifest.Version.ToString(), PluginLifecycleKind.Loaded)).ConfigureAwait(false);
            return new PluginLoadResult(manifest.Id, true, PluginReloadStatus.Loaded, handle: managed.Handle);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> UnloadPluginAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pluginId);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!TryGetManaged(pluginId, out var managed) || managed.Status != PluginReloadStatus.Loaded)
            {
                return false;
            }
            await UnloadCoreAsync(managed).ConfigureAwait(false);
            managed.Status = PluginReloadStatus.Unloaded;
            managed.LastTransitionUtc = DateTimeOffset.UtcNow;
            _context.Logger.Info($"插件 {pluginId} 已卸载");
            await EmitAsync(new PluginLifecycleEvent(pluginId, managed.Manifest.Version.ToString(), PluginLifecycleKind.Unloaded)).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<PluginReloadResult> ReloadPluginAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pluginId);
        if (!IsEnabled)
        {
            return new PluginReloadResult(pluginId, false, PluginReloadStatus.Unknown, false, "HMR 已禁用");
        }
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!TryGetManaged(pluginId, out var managed) || managed.Status != PluginReloadStatus.Loaded)
            {
                return new PluginReloadResult(pluginId, false, PluginReloadStatus.Unknown, false, "插件未加载");
            }

            lock (_gate)
            {
                managed.Status = PluginReloadStatus.Reloading;
                managed.LastTransitionUtc = DateTimeOffset.UtcNow;
            }

            PluginModule newModule;
            try
            {
                newModule = await _sourceFactory(managed.Manifest).LoadAsync(managed.Manifest, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return await RollbackAsync(managed, ex, "新版本构造失败").ConfigureAwait(false);
            }

            PluginStateSnapshot? snapshot = null;
            if (managed.Module?.StateProvider is { } oldProvider)
            {
                try
                {
                    snapshot = await oldProvider.CaptureStateAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _context.Logger.Warn($"插件 {pluginId} 状态捕获失败（已隔离）：{ex}");
                }
            }

            IPluginHandle? newHandle = null;
            try
            {
                newHandle = _context.Plugin(newModule.Plugin);
                await newHandle.AwaitAsync().ConfigureAwait(false);
                if (newHandle.State == PluginState.Failed)
                {
                    throw new InvalidOperationException("新版本加载失败（Failed）");
                }
            }
            catch (Exception ex)
            {
                if (newHandle is not null)
                {
                    await newHandle.DisposeAsync().ConfigureAwait(false);
                }
                await newModule.DisposeAsync().ConfigureAwait(false);
                return await RollbackAsync(managed, ex, "新版本激活失败").ConfigureAwait(false);
            }

            if (snapshot is not null && newModule.StateProvider is { } newProvider)
            {
                try
                {
                    await newProvider.RestoreStateAsync(snapshot, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _context.Logger.Warn($"插件 {pluginId} 状态恢复失败（已隔离）：{ex}");
                }
            }

            var oldHandle = managed.Handle;
            var oldModule = managed.Module;
            lock (_gate)
            {
                managed.Handle = newHandle;
                managed.Module = newModule;
                managed.Status = PluginReloadStatus.Loaded;
                managed.LoadCount++;
                managed.LastTransitionUtc = DateTimeOffset.UtcNow;
            }
            if (oldHandle is not null)
            {
                await oldHandle.DisposeAsync().ConfigureAwait(false);
            }
            if (oldModule is not null)
            {
                await oldModule.DisposeAsync().ConfigureAwait(false);
            }

            _context.Logger.Info($"插件 {pluginId} v{managed.Manifest.Version} 重载成功");
            await EmitAsync(new PluginLifecycleEvent(pluginId, managed.Manifest.Version.ToString(), PluginLifecycleKind.ReloadSucceeded)).ConfigureAwait(false);
            return new PluginReloadResult(pluginId, true, PluginReloadStatus.Loaded, false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<int> ReloadAllPluginsAsync(CancellationToken cancellationToken = default)
    {
        var ids = SnapshotLoadedIds();
        var success = 0;
        foreach (var id in ids)
        {
            var result = await ReloadPluginAsync(id, cancellationToken).ConfigureAwait(false);
            if (result.Success)
            {
                success++;
            }
        }
        return success;
    }

    /// <inheritdoc />
    public PluginReloadStatus GetPluginStatus(string pluginId)
    {
        if (pluginId is null)
        {
            return PluginReloadStatus.Unknown;
        }
        lock (_gate)
        {
            return _plugins.TryGetValue(pluginId, out var managed) ? managed.Status : PluginReloadStatus.Unknown;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<PluginRuntimeInfo> GetPluginRuntimeInfos()
    {
        lock (_gate)
        {
            return _plugins.Values
                .OrderBy(p => p.Manifest.Id, StringComparer.Ordinal)
                .Select(p => new PluginRuntimeInfo(
                    p.Manifest.Id,
                    p.Manifest.Name,
                    p.Manifest.Version.ToString(),
                    p.Status,
                    p.LoadCount,
                    p.FailureCount,
                    p.RollbackCount,
                    p.LastTransitionUtc,
                    p.LastError,
                    p.LastMemoryBytes,
                    p.MemoryPressureCount,
                    p.IsQuarantined))
                .ToList();
        }
    }

    /// <summary>热更新内存治理阈值（设置界面改值后即时生效，不重建治理器生命周期）。</summary>
    public void UpdateGovernorOptions(ResourceGovernorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _governor.UpdateOptions(options);
    }

    /// <inheritdoc />
    public void RegisterSubject(IResourceSubject subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        _governor.Register(subject);
    }

    /// <inheritdoc />
    public void UnregisterSubject(string id)
    {
        if (!string.IsNullOrEmpty(id))
        {
            _governor.Unregister(id);
        }
    }

    /// <inheritdoc />
    public Action<string>? OnProcessCritical
    {
        get => _governor.OnProcessCritical;
        set => _governor.OnProcessCritical = value;
    }

    /// <summary>导出受内存治理器监控的受控对象（供 CordisContext 注册到 ResourceGovernor）。</summary>
    public IReadOnlyList<IResourceSubject> GetResourceSubjects()
    {
        lock (_gate)
        {
            return _plugins.Values
                .Where(p => p.Status == PluginReloadStatus.Loaded)
                .Cast<IResourceSubject>()
                .ToList();
        }
    }

    private async Task LoadCoreAsync(ManagedPlugin managed, CancellationToken cancellationToken)
    {
        var module = await _sourceFactory(managed.Manifest).LoadAsync(managed.Manifest, cancellationToken).ConfigureAwait(false);
        IPluginHandle? handle = null;
        try
        {
            handle = _context.Plugin(module.Plugin);
            await handle.AwaitAsync().ConfigureAwait(false);
            if (handle.State == PluginState.Failed)
            {
                throw new InvalidOperationException("插件加载失败（Failed）");
            }
        }
        catch
        {
            if (handle is not null)
            {
                await handle.DisposeAsync().ConfigureAwait(false);
            }
            await module.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        lock (_gate)
        {
            managed.Module = module;
            managed.Handle = handle;
        }
        _governor.Register(managed);
    }

    private async Task UnloadCoreAsync(ManagedPlugin managed)
    {
        var handle = managed.Handle;
        var module = managed.Module;
        lock (_gate)
        {
            managed.Handle = null;
            managed.Module = null;
        }
        if (handle is not null)
        {
            await handle.DisposeAsync().ConfigureAwait(false);
        }
        if (module is not null)
        {
            await module.DisposeAsync().ConfigureAwait(false);
        }
        _governor.Unregister(managed.Manifest.Id);
    }

    private async Task<PluginReloadResult> RollbackAsync(ManagedPlugin managed, Exception error, string stage)
    {
        lock (_gate)
        {
            managed.Status = PluginReloadStatus.Loaded;
            managed.MarkFailure();
            managed.RollbackCount++;
            managed.LastError = error.Message;
            managed.LastTransitionUtc = DateTimeOffset.UtcNow;
        }
        var message = $"{stage}，已回滚：{error.Message}";
        _context.Logger.Error($"插件 {managed.Manifest.Id} {message}");
        await EmitAsync(new PluginLifecycleEvent(managed.Manifest.Id, managed.Manifest.Version.ToString(), PluginLifecycleKind.RolledBack, message)).ConfigureAwait(false);
        return new PluginReloadResult(managed.Manifest.Id, false, PluginReloadStatus.Loaded, true, message);
    }

    private Task EmitAsync(PluginLifecycleEvent payload)
    {
        var name = payload.Kind switch
        {
            PluginLifecycleKind.Loaded => HmrEvents.Loaded,
            PluginLifecycleKind.Unloaded => HmrEvents.Unloaded,
            PluginLifecycleKind.ReloadSucceeded => HmrEvents.ReloadSucceeded,
            PluginLifecycleKind.RolledBack => HmrEvents.RolledBack,
            PluginLifecycleKind.Rejected => HmrEvents.Rejected,
            _ => throw new ArgumentOutOfRangeException(nameof(payload))
        };
        return _context.Events.EmitAsync(name, payload);
    }

    private ManagedPlugin GetOrCreateManaged(PluginManifest manifest)
    {
        lock (_gate)
        {
            if (!_plugins.TryGetValue(manifest.Id, out var managed))
            {
                managed = new ManagedPlugin(this, manifest);
                _plugins[manifest.Id] = managed;
            }
            else
            {
                managed.Manifest = manifest;
            }
            return managed;
        }
    }

    private bool TryGetManaged(string pluginId, out ManagedPlugin managed)
    {
        lock (_gate)
        {
            return _plugins.TryGetValue(pluginId, out managed!);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _governor.Dispose();
    }

    private Dictionary<string, SemanticVersion> SnapshotLoadedVersions(string? excludeId)
    {
        lock (_gate)
        {
            var result = new Dictionary<string, SemanticVersion>(StringComparer.Ordinal);
            foreach (var pair in _plugins)
            {
                if (pair.Key == excludeId)
                {
                    continue;
                }
                if (pair.Value.Status == PluginReloadStatus.Loaded)
                {
                    result[pair.Key] = pair.Value.Manifest.Version;
                }
            }
            return result;
        }
    }

    private List<string> SnapshotLoadedIds()
    {
        lock (_gate)
        {
            return _plugins.Where(pair => pair.Value.Status == PluginReloadStatus.Loaded).Select(pair => pair.Key).ToList();
        }
    }

    private sealed class ManagedPlugin : IResourceSubject
    {
        private readonly HmrManager _owner;
        public ManagedPlugin(HmrManager owner, PluginManifest manifest)
        {
            _owner = owner;
            Manifest = manifest;
        }

        public PluginManifest Manifest { get; set; }
        public PluginModule? Module { get; set; }
        public IPluginHandle? Handle { get; set; }
        public PluginReloadStatus Status { get; set; } = PluginReloadStatus.Unknown;
        public int LoadCount { get; set; }
        public int FailureCount { get; set; }
        public int RollbackCount { get; set; }
        public DateTimeOffset? LastTransitionUtc { get; set; }
        public string? LastError { get; set; }

        // 内存治理维度
        public long LastMemoryBytes { get; set; }
        public int MemoryPressureCount { get; set; }
        public bool IsQuarantined { get; private set; }

        // IResourceSubject —— in-process 插件无独立 GC 堆，返回当前进程实时工作集（WorkingSet64），
        // 与任务管理器口径一致（而非 GC.GetTotalMemory 的托管堆），使按比例治理阈值真正生效。
        // 跨语言外部进程由 ExternalPluginAdapter 覆盖为各自 Process.WorkingSet64（各自独立进程）。
        string IResourceSubject.Id => Manifest.Id;
        string? IResourceSubject.DisplayName => Manifest.Name;
        bool IResourceSubject.IsSelfHealable => true;

        long IResourceSubject.SampleMemoryBytes()
        {
            try
            {
                using var proc = System.Diagnostics.Process.GetCurrentProcess();
                proc.Refresh();
                LastMemoryBytes = proc.WorkingSet64;
                return LastMemoryBytes;
            }
            catch
            {
                return 0;
            }
        }

        Task IResourceSubject.IsolateAsync(CancellationToken cancellationToken)
            => _owner.UnloadPluginAsync(Manifest.Id, cancellationToken);

        Task IResourceSubject.KillAndRestartAsync(CancellationToken cancellationToken)
            => Task.FromResult(_owner.ReloadPluginAsync(Manifest.Id, cancellationToken));

        void IResourceSubject.Quarantine()
        {
            IsQuarantined = true;
            _owner._context.Logger.Error($"[Governor] 插件 {Manifest.Id} 已进入熔断隔离，停止自动内存自愈");
        }

        void IResourceSubject.RecordPressure() => MemoryPressureCount++;

        /// <summary>异常压力计数：连续加载/重载失败达上限则熔断（与内存崩溃熔断对称）。</summary>
        internal const int MaxFailures = 3;
        internal void MarkFailure()
        {
            FailureCount++;
            LastTransitionUtc = DateTimeOffset.UtcNow;
            if (FailureCount >= MaxFailures && !IsQuarantined)
            {
                QuarantineInternal();
            }
        }

        private void QuarantineInternal()
        {
            IsQuarantined = true;
            _owner._context.Logger.Error($"[Governor] 插件 {Manifest.Id} 连续 {FailureCount} 次失败，熔断隔离（停止自动重启，需人工干预）");
        }
    }
}
