// BetterDesktop.Kernel — IContext 接口定义
// 服务图与插件生命周期的容器（Context 术语定义见 docs/TERMINOLOGY.md）

namespace BetterDesktop.Kernel.Contracts;

/// <summary>
/// 服务图与插件生命周期的容器。
/// 提供/读取服务、extend/isolate/intercept、托管 effect 与事件。
/// </summary>
public interface IContext
{
    /// <summary>
    /// 从服务图中获取指定类型的服务实例。
    /// </summary>
    /// <typeparam name="T">服务类型</typeparam>
    /// <returns>服务实例，未注册时返回 null</returns>
    T? Get<T>() where T : class;

    /// <summary>
    /// 向服务图注册服务实例。
    /// </summary>
    /// <typeparam name="T">服务类型</typeparam>
    /// <param name="service">服务实例</param>
    void Provide<T>(T service) where T : class;

    /// <summary>
    /// 派生一个新的子上下文（extend 语义）。
    /// 子上下文继承父上下文的服务，并可覆盖或新增服务。
    /// </summary>
    /// <returns>新的子上下文</returns>
    IContext Extend();

    /// <summary>
    /// 创建一个隔离的上下文（isolate 语义）。
    /// 隔离上下文不继承父上下文的服务。
    /// </summary>
    /// <returns>隔离的上下文</returns>
    IContext Isolate();

    /// <summary>
    /// 拦截服务解析（intercept 语义）。
    /// 在服务解析链中插入拦截器，可修改或替换解析结果。
    /// </summary>
    /// <typeparam name="T">服务类型</typeparam>
    /// <param name="interceptor">拦截器函数</param>
    void Intercept<T>(Func<T?, T?> interceptor) where T : class;
}
