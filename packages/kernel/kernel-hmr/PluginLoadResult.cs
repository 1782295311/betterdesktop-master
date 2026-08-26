// BetterDesktop.Kernel.Hmr — PluginLoadResult 插件加载结果

using BetterDesktop.Kernel.Contracts;

namespace BetterDesktop.Kernel.Hmr;

/// <summary>插件加载结果。</summary>
public sealed class PluginLoadResult
{
    internal PluginLoadResult(string pluginId, bool success, PluginReloadStatus status, string? error = null, IPluginHandle? handle = null)
    {
        PluginId = pluginId;
        Success = success;
        Status = status;
        Error = error;
        Handle = handle;
    }

    /// <summary>插件 id。</summary>
    public string PluginId { get; }

    /// <summary>是否成功。</summary>
    public bool Success { get; }

    /// <summary>结果状态。</summary>
    public PluginReloadStatus Status { get; }

    /// <summary>错误信息（成功时 null）。</summary>
    public string? Error { get; }

    /// <summary>内核插件句柄（成功时非空）。</summary>
    public IPluginHandle? Handle { get; }
}
