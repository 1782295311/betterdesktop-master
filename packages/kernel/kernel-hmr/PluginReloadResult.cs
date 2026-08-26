// BetterDesktop.Kernel.Hmr — PluginReloadResult 插件热重载结果

namespace BetterDesktop.Kernel.Hmr;

/// <summary>插件热重载结果。</summary>
public sealed class PluginReloadResult
{
    internal PluginReloadResult(string pluginId, bool success, PluginReloadStatus status, bool rolledBack, string? error = null)
    {
        PluginId = pluginId;
        Success = success;
        Status = status;
        RolledBack = rolledBack;
        Error = error;
    }

    /// <summary>插件 id。</summary>
    public string PluginId { get; }

    /// <summary>是否成功。</summary>
    public bool Success { get; }

    /// <summary>结果状态。</summary>
    public PluginReloadStatus Status { get; }

    /// <summary>是否触发回滚（新版本失败、旧版本继续服务）。</summary>
    public bool RolledBack { get; }

    /// <summary>错误信息（成功时 null）。</summary>
    public string? Error { get; }
}
