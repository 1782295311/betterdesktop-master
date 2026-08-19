// BetterDesktop.Kernel — PluginState 枚举定义
// 插件运行时状态机（Fiber 术语定义见 docs/TERMINOLOGY.md）

namespace BetterDesktop.Kernel.Contracts;

/// <summary>
/// 插件运行时状态机：PENDING→LOADING→ACTIVE→FAILED→UNLOADING→DISPOSED。
/// </summary>
public enum PluginState
{
    /// <summary>
    /// 等待中：依赖尚未全部可用。
    /// </summary>
    Pending,

    /// <summary>
    /// 加载中：正在执行 LoadAsync。
    /// </summary>
    Loading,

    /// <summary>
    /// 活跃：加载成功，服务已注册。
    /// </summary>
    Active,

    /// <summary>
    /// 失败：加载或运行时发生异常。
    /// </summary>
    Failed,

    /// <summary>
    /// 卸载中：正在执行清理器。
    /// </summary>
    Unloading,

    /// <summary>
    /// 已释放：清理完成，资源已回收。
    /// </summary>
    Disposed
}
