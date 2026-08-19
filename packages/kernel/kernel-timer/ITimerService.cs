// BetterDesktop.Kernel.Timer — ITimerService 契约
// 托管定时器：可取消、异常隔离

namespace BetterDesktop.Kernel.Timer;

/// <summary>托管定时器服务契约。</summary>
public interface ITimerService
{
    /// <summary>一次性定时器：delay 后执行一次。</summary>
    IDisposable SetTimeout(Func<CancellationToken, Task> callback, TimeSpan delay);

    /// <summary>周期定时器：每个 period 执行一次。</summary>
    IDisposable SetInterval(Func<CancellationToken, Task> callback, TimeSpan period);
}
