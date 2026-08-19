// BetterDesktop.Kernel — IPlugin 接口定义
// 插件基础接口（Plugin 术语定义见 docs/TERMINOLOGY.md）

namespace BetterDesktop.Kernel.Contracts;

/// <summary>
/// 插件基础接口。一切功能的唯一形态：内置功能与第三方扩展同一条通道。
/// </summary>
public interface IPlugin
{
    /// <summary>
    /// 插件名称（唯一标识）。
    /// </summary>
    string Name { get; }

    /// <summary>
    /// 插件版本。
    /// </summary>
    string Version { get; }

    /// <summary>
    /// 插件声明的依赖服务类型列表。
    /// 依赖可用前插件保持 PENDING 状态，服务变化时自动重载。
    /// </summary>
    IReadOnlyList<Type> Inject { get; }

    /// <summary>
    /// 插件加载时调用。
    /// 在此方法中注册服务、效果和事件处理器。
    /// </summary>
    /// <param name="context">插件运行时上下文</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task LoadAsync(IContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// 插件卸载时调用。
    /// 在此方法中清理资源（或通过 Effect 注册清理器）。
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    Task UnloadAsync(CancellationToken cancellationToken = default);
}
