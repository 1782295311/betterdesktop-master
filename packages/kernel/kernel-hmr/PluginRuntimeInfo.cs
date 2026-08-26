// BetterDesktop.Kernel.Hmr — PluginRuntimeInfo 插件运行时信息
// 状态中心数据源（监控快照）

namespace BetterDesktop.Kernel.Hmr;

/// <summary>插件运行时信息（状态中心数据源）。</summary>
public sealed class PluginRuntimeInfo
{
    internal PluginRuntimeInfo(
        string pluginId,
        string? name,
        string? version,
        PluginReloadStatus status,
        int loadCount,
        int failureCount,
        int rollbackCount,
        DateTimeOffset? lastTransitionUtc,
        string? lastError,
        long lastMemoryBytes = 0,
        int memoryPressureCount = 0,
        bool quarantined = false)
    {
        PluginId = pluginId;
        Name = name;
        Version = version;
        Status = status;
        LoadCount = loadCount;
        FailureCount = failureCount;
        RollbackCount = rollbackCount;
        LastTransitionUtc = lastTransitionUtc;
        LastError = lastError;
        LastMemoryBytes = lastMemoryBytes;
        MemoryPressureCount = memoryPressureCount;
        Quarantined = quarantined;
    }

    /// <summary>插件稳定 id。</summary>
    public string PluginId { get; }

    /// <summary>显示名。</summary>
    public string? Name { get; }

    /// <summary>当前版本。</summary>
    public string? Version { get; }

    /// <summary>当前状态。</summary>
    public PluginReloadStatus Status { get; }

    /// <summary>累计加载成功次数。</summary>
    public int LoadCount { get; }

    /// <summary>累计失败次数。</summary>
    public int FailureCount { get; }

    /// <summary>累计回滚次数。</summary>
    public int RollbackCount { get; }

    /// <summary>最近一次状态迁移时间（UTC）。</summary>
    public DateTimeOffset? LastTransitionUtc { get; }

    /// <summary>最近一次错误信息。</summary>
    public string? LastError { get; }

    /// <summary>最近一次采样的内存占用（字节，0 表示未采样）。</summary>
    public long LastMemoryBytes { get; }

    /// <summary>累计内存压力次数（超软/硬/致命上限计次）。</summary>
    public int MemoryPressureCount { get; }

    /// <summary>是否已被内存治理器熔断隔离（连续内存崩溃达上限）。</summary>
    public bool Quarantined { get; }
}
