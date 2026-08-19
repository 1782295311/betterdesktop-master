// BetterDesktop.Kernel — IEventBus 接口定义（ADR-002 D4 冻结面）
// 内核事件服务：事件名「域/动作」+ 强类型载荷 + 五种分发

namespace BetterDesktop.Kernel.Contracts;

/// <summary>
/// 内核事件服务（ADR-002 D4）：跨插件通信唯一通道，禁止跨程序集裸 event。
/// 载荷类型与事件名不匹配属调用方契约错误（抛 InvalidCastException）。
/// 单监听器异常被隔离：不阻断其余监听器，异常入内核日志。
/// </summary>
public interface IEventBus
{
    /// <summary>注册无返回值监听器，返回注销句柄。</summary>
    IDisposable On<T>(string name, Func<T, CancellationToken, Task> handler) where T : notnull;

    /// <summary>
    /// 注册带返回值监听器（供 parallel / serial / bail / waterfall 收集结果）。
    /// 命名与 On 区分以消除重载歧义：用 On 注册会丢弃返回值。
    /// </summary>
    IDisposable OnResult<T, TResult>(string name, Func<T, CancellationToken, Task<TResult>> handler) where T : notnull;

    /// <summary>emit 分发：并发广播所有监听器，不收集结果。</summary>
    Task EmitAsync<T>(string name, T payload, CancellationToken cancellationToken = default) where T : notnull;

    /// <summary>parallel 分发：并发执行并等待全部完成，收集结果。</summary>
    Task<IReadOnlyList<TResult>> ParallelAsync<T, TResult>(string name, T payload, CancellationToken cancellationToken = default) where T : notnull;

    /// <summary>serial 分发：按注册顺序依次执行，收集结果。</summary>
    Task<IReadOnlyList<TResult>> SerialAsync<T, TResult>(string name, T payload, CancellationToken cancellationToken = default) where T : notnull;

    /// <summary>bail 分发：按注册顺序执行，首个非默认值结果短路返回。</summary>
    Task<TResult?> BailAsync<T, TResult>(string name, T payload, CancellationToken cancellationToken = default) where T : notnull;

    /// <summary>waterfall 分发：按注册顺序同类型折叠，前一个的结果作为下一个的输入。</summary>
    Task<T> WaterfallAsync<T>(string name, T payload, CancellationToken cancellationToken = default) where T : notnull;
}
