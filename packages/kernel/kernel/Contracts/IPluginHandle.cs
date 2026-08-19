// BetterDesktop.Kernel — IPluginHandle 接口定义（ADR-002 D1 冻结面）
// 插件运行时句柄：状态查询与生命周期控制

namespace BetterDesktop.Kernel.Contracts;

/// <summary>
/// 插件运行时句柄（Fiber 对应物）。状态机见 <see cref="PluginState"/>。
/// </summary>
public interface IPluginHandle
{
    /// <summary>当前状态。</summary>
    PluginState State { get; }

    /// <summary>等待当前加载 / 卸载完成。</summary>
    Task AwaitAsync();

    /// <summary>卸载后立即重载（依赖变化触发的自动重载走此路径）。</summary>
    Task RestartAsync();

    /// <summary>显式卸载：执行清理后进入 Disposed，不可重启。</summary>
    Task DisposeAsync();
}
