// BetterDesktop.Kernel — IContext 接口定义（ADR-002 D1 冻结面）
// 服务图与插件生命周期的容器（术语定义见 docs/TERMINOLOGY.md）

namespace BetterDesktop.Kernel.Contracts;

/// <summary>
/// 服务图与插件生命周期的容器。
/// 服务读取为显式 Get（无透明代理）；Provide 的变更会通知依赖者并触发自动重载。
/// </summary>
public interface IContext
{
    /// <summary>
    /// 沿父链解析服务；未注册返回 null（可选依赖）。
    /// 声明了必需依赖的插件由内核在依赖可用前保持 PENDING。
    /// </summary>
    T? Get<T>() where T : class;

    /// <summary>
    /// 注册服务实例并返回撤销句柄。
    /// 同一实例重复提供不触发通知；实例变化会触发依赖此服务的插件自动重载。
    /// </summary>
    IDisposable Provide<T>(T service) where T : class;

    /// <summary>
    /// 派生一个子上下文（父链继承语义：子可见父的服务，父不可见子的）。
    /// </summary>
    IContext Extend();

    /// <summary>
    /// 注册插件并按其依赖状态调度（「一切皆插件」的唯一通道）。
    /// </summary>
    IPluginHandle Plugin(IPlugin plugin);

    /// <summary>
    /// 注册托管清理器：execute 立即执行并返回 disposer，fiber 卸载时逆序并行执行、单条异常隔离。
    /// </summary>
    IDisposable Effect(Func<IDisposable> execute, string? label = null);

    /// <summary>内核事件服务（五种分发 + 强类型载荷，ADR-002 D4）。</summary>
    IEventBus Events { get; }

    /// <summary>内核日志服务（M10 单一管道；P2 宿主接文件 sink）。</summary>
    IKernelLogger Logger { get; }
}
