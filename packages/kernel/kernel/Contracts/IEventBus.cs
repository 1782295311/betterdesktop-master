// BetterDesktop.Kernel — IEventBus 接口定义
// 事件分发服务（Events 术语定义见 docs/TERMINOLOGY.md）

namespace BetterDesktop.Kernel.Contracts;

/// <summary>
/// 事件分发服务。支持五种分发模式：emit / parallel / serial / bail / waterfall。
/// </summary>
public interface IEventBus
{
    /// <summary>
    /// 广播事件（emit 模式）：所有处理器并行执行，不等待结果。
    /// </summary>
    /// <typeparam name="TEvent">事件类型</typeparam>
    /// <param name="eventData">事件数据</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task EmitAsync<TEvent>(TEvent eventData, CancellationToken cancellationToken = default);

    /// <summary>
    /// 并行分发（parallel 模式）：所有处理器并行执行，等待全部完成。
    /// </summary>
    /// <typeparam name="TEvent">事件类型</typeparam>
    /// <typeparam name="TResult">结果类型</typeparam>
    /// <param name="eventData">事件数据</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>所有处理器的结果</returns>
    Task<IReadOnlyList<TResult>> ParallelAsync<TEvent, TResult>(TEvent eventData, CancellationToken cancellationToken = default);

    /// <summary>
    /// 串行分发（serial 模式）：处理器按注册顺序依次执行，前一个完成后才执行下一个。
    /// </summary>
    /// <typeparam name="TEvent">事件类型</typeparam>
    /// <typeparam name="TResult">结果类型</typeparam>
    /// <param name="eventData">事件数据</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>所有处理器的结果</returns>
    Task<IReadOnlyList<TResult>> SerialAsync<TEvent, TResult>(TEvent eventData, CancellationToken cancellationToken = default);

    /// <summary>
    /// 熔断分发（bail 模式）：处理器按注册顺序依次执行，第一个返回非空结果即停止。
    /// </summary>
    /// <typeparam name="TEvent">事件类型</typeparam>
    /// <typeparam name="TResult">结果类型</typeparam>
    /// <param name="eventData">事件数据</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>第一个非空结果，或默认值</returns>
    Task<TResult?> BailAsync<TEvent, TResult>(TEvent eventData, CancellationToken cancellationToken = default);

    /// <summary>
    /// 瀑布分发（waterfall 模式）：处理器按注册顺序依次执行，前一个的结果作为下一个的输入。
    /// </summary>
    /// <typeparam name="TEvent">事件类型</typeparam>
    /// <typeparam name="TResult">结果类型</typeparam>
    /// <param name="eventData">初始事件数据</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>最终结果</returns>
    Task<TResult> WaterfallAsync<TEvent, TResult>(TEvent eventData, CancellationToken cancellationToken = default);

    /// <summary>
    /// 注册事件处理器。
    /// </summary>
    /// <typeparam name="TEvent">事件类型</typeparam>
    /// <param name="handler">处理器函数</param>
    /// <returns>注销句柄</returns>
    IDisposable On<TEvent>(Func<TEvent, CancellationToken, Task> handler);

    /// <summary>
    /// 注册带返回值的事件处理器。
    /// </summary>
    /// <typeparam name="TEvent">事件类型</typeparam>
    /// <typeparam name="TResult">结果类型</typeparam>
    /// <param name="handler">处理器函数</param>
    /// <returns>注销句柄</returns>
    IDisposable On<TEvent, TResult>(Func<TEvent, CancellationToken, Task<TResult>> handler);
}
