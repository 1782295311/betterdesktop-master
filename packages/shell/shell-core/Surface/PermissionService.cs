using System;
using System.Collections.Generic;

namespace BetterDesktop.Shell.Core.Surface;

/// <summary>
/// 插件权限服务：白名单裁决窗口高级能力（当前：置顶）。
/// 内置窗口不受限（返回 true）；第三方插件仅当插件 ID 在白名单内才放行。
/// 宿主可按需注册白名单（如官方扩展 ID）。
/// </summary>
public static class PermissionService
{
    private static readonly HashSet<string> _topmostAllowed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>注册允许置顶的插件 ID（仅官方/受信插件调用）。</summary>
    public static void AllowTopmostFor(string pluginId)
    {
        if (!string.IsNullOrWhiteSpace(pluginId)) _topmostAllowed.Add(pluginId);
    }

    /// <summary>裁决：该插件是否允许置顶。内置窗口传 null/空返回 true（不受限）。</summary>
    public static bool AllowTopmost(string? pluginId)
        => string.IsNullOrWhiteSpace(pluginId) || _topmostAllowed.Contains(pluginId);
}
