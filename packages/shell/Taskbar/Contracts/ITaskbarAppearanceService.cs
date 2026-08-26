namespace BetterDesktop.Shell.Taskbar.Contracts;

/// <summary>
/// 任务栏外观服务契约：对外暴露给设置分区与其他插件。
/// 引擎实现此接口；设置分区经内核 Get&lt;ITaskbarAppearanceService&gt; 写入配置并实时生效。
/// </summary>
public interface ITaskbarAppearanceService
{
    /// <summary>当前生效的配置（引擎内部持有的副本）。</summary>
    TaskbarAppearanceConfig Current { get; }

    /// <summary>写入新配置并立即对所有任务栏套用（RefreshAll）。</summary>
    void SetConfig(TaskbarAppearanceConfig config);

    /// <summary>还原任务栏到系统默认外观（进程退出/插件卸载时调用）。</summary>
    void ReturnToStock();

    /// <summary>Win11 注入桥是否可用（ExplorerTAP.dll 注入成功）。不可用则 Win11 高级外观降级。</summary>
    bool Win11BridgeAvailable { get; }

    /// <summary>引擎是否为 Win11 XAML 任务栏路径（true=走 ITaskbarService 注入；false=Win10 直改）。</summary>
    bool IsWindows11 { get; }
}
