// BetterDesktop.Shell.StartMenu — StartMenuServiceBridge
// 设置分区（ISettingsSection.Build 仅接 ISettingsService/IThemeTokens）经此桥取回
// IStartMenuService（参照 Taskbar 的 TaskbarServiceBridge 模式）。

using BetterDesktop.Shell.StartMenu.Contracts;

namespace BetterDesktop.Shell.StartMenu.Services;

/// <summary>开始菜单服务桥（由 StartMenuPlugin 注入）。</summary>
public static class StartMenuServiceBridge
{
    private static IStartMenuService? _service;

    /// <summary>由 StartMenuPlugin.LoadAsync 调用。</summary>
    public static void Bind(IStartMenuService service) => _service = service;

    /// <summary>取回服务（未注入返回 null）。</summary>
    public static IStartMenuService? TryGet() => _service;
}
