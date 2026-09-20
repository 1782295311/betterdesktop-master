namespace BetterDesktop.Shell.Dock.Services;

/// <summary>
/// 把 <see cref="IDockAppsService"/> 暴露给**只接 (ISettingsService, IThemeTokens)** 的设置分区。
/// <para>为什么需要：<c>ISettingsSection.Build</c> 的签名只给设置与主题令牌，拿不到内核上下文；
/// 仓库既有做法同此（<c>TaskbarServiceBridge</c> / <c>SettingsKernelBridge</c> /
/// <c>StartMenuServiceBridge</c>）——由插件在 LoadAsync 里 Bind。</para>
/// <para>未 Bind（dock 组件被关闭）时返回 null，分区显示占位说明而不是崩。</para>
/// </summary>
internal static class DockAppsServiceBridge
{
    private static IDockAppsService? _service;

    /// <summary>当前 dock 固定项服务（未装配时为 null）。</summary>
    public static IDockAppsService? Current => _service;

    /// <summary>由 DockPlugin 在装配完成后调用。</summary>
    public static void Bind(IDockAppsService service) => _service = service;

    /// <summary>由 DockPlugin 卸载时调用（避免持有已释放实例）。</summary>
    public static void Unbind() => _service = null;
}
