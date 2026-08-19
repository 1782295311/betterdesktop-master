// BetterDesktop.Kernel — IPlugin 接口定义（ADR-002 D1 冻结面）
// 插件基础接口（术语定义见 docs/TERMINOLOGY.md）

namespace BetterDesktop.Kernel.Contracts;

/// <summary>
/// 插件基础接口：一切功能的唯一形态，内置能力与第三方扩展同一条通道。
/// </summary>
public interface IPlugin
{
    /// <summary>显示名（日志与状态中心展示）。</summary>
    string Name { get; }

    /// <summary>声明的必需依赖服务类型；全部可用前内核保持 PENDING，任一变化时自动重载。</summary>
    IReadOnlyList<Type> Inject { get; }

    /// <summary>插件加载：注册服务、事件与清理器。抛异常 → Failed 状态。</summary>
    Task LoadAsync(IContext context, CancellationToken cancellationToken = default);

    /// <summary>插件卸载：effect 清理器之外的补充清理。抛异常被记录且不阻断卸载。</summary>
    Task UnloadAsync(CancellationToken cancellationToken = default);
}
