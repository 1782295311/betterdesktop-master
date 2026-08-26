// BetterDesktop.Shell.Taskbar — TaskbarServiceBridge 任务栏外观服务桥
// 设置分区（ISettingsSection.Build 仅接 ISettingsService/IThemeTokens）需要访问
// ITaskbarAppearanceService（读当前配置/即时套用）。本桥在 TaskbarAppearancePlugin.LoadAsync
// 时注入，供分区静态取回——避免改动分区接口签名（接口冻结面 ADR-002 D1）。

using BetterDesktop.Shell.Taskbar.Contracts;

namespace BetterDesktop.Shell.Taskbar.Services;

/// <summary>任务栏外观服务桥（进程级，由 TaskbarAppearancePlugin 注入）。</summary>
public static class TaskbarServiceBridge
{
    private static ITaskbarAppearanceService? _service;

    /// <summary>由 TaskbarAppearancePlugin 在 LoadAsync 时调用，注入服务实例。</summary>
    public static void Bind(ITaskbarAppearanceService service) => _service = service;

    /// <summary>尝试取回已注入的服务（未注入返回 null）。</summary>
    public static ITaskbarAppearanceService? TryGet() => _service;
}
