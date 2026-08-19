// BetterDesktop.Kernel — PluginState 枚举定义（ADR-002 D1 冻结面）
// 插件运行时状态机（Fiber 术语定义见 docs/TERMINOLOGY.md）

namespace BetterDesktop.Kernel.Contracts;

/// <summary>
/// 插件运行时状态机：Pending → Loading → Active → Failed → Unloading → Disposed。
/// </summary>
public enum PluginState
{
    /// <summary>等待中：必需依赖尚未全部可用。</summary>
    Pending,

    /// <summary>加载中：正在执行 LoadAsync。</summary>
    Loading,

    /// <summary>活跃：加载成功，服务已注册。</summary>
    Active,

    /// <summary>失败：加载或运行期发生异常（被隔离，不拖垮外壳）。</summary>
    Failed,

    /// <summary>卸载中：正在执行清理器。</summary>
    Unloading,

    /// <summary>已释放：清理完成，不可重启。</summary>
    Disposed
}
