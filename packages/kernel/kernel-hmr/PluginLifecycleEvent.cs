// BetterDesktop.Kernel.Hmr — PluginLifecycleEvent 生命周期事件载荷
// 经内核事件总线分发

namespace BetterDesktop.Kernel.Hmr;

/// <summary>插件生命周期事件载荷（经内核事件总线分发）。</summary>
public sealed class PluginLifecycleEvent
{
    /// <summary>构造。</summary>
    public PluginLifecycleEvent(string pluginId, string? version, PluginLifecycleKind kind, string? message = null)
    {
        PluginId = pluginId;
        Version = version;
        Kind = kind;
        Message = message;
        TimestampUtc = DateTimeOffset.UtcNow;
    }

    /// <summary>插件稳定 id。</summary>
    public string PluginId { get; }

    /// <summary>插件版本（未知时为 null）。</summary>
    public string? Version { get; }

    /// <summary>事件种类。</summary>
    public PluginLifecycleKind Kind { get; }

    /// <summary>补充信息（错误原因等）。</summary>
    public string? Message { get; }

    /// <summary>事件时间（UTC）。</summary>
    public DateTimeOffset TimestampUtc { get; }
}
