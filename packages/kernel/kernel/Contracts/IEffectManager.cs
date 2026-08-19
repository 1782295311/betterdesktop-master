// BetterDesktop.Kernel — IEffectManager 接口定义
// 托管清理器注册与执行（Effect 术语定义见 docs/TERMINOLOGY.md）

namespace BetterDesktop.Kernel.Contracts;

/// <summary>
/// 托管清理器管理器。插件注册的清理器在卸载时逆序并行执行，单条异常隔离。
/// </summary>
public interface IEffectManager
{
    /// <summary>
    /// 注册一个清理器。卸载时按注册顺序逆序执行。
    /// </summary>
    /// <param name="cleanup">清理函数</param>
    /// <returns>效果句柄，可用于提前注销</returns>
    IDisposable RegisterEffect(Func<CancellationToken, Task> cleanup);

    /// <summary>
    /// 执行所有已注册的清理器（逆序并行）。
    /// 单条异常隔离：某个清理器失败不影响其他清理器执行。
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>执行过程中发生的异常列表</returns>
    Task<IReadOnlyList<Exception>> ExecuteAllAsync(CancellationToken cancellationToken = default);
}
