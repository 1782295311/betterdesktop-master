// BetterDesktop.Shell.Status — 状态轮询服务契约
// 统一轮询所有监控项、检测差异、经 IEventBus 广播变化；UI 只订阅，不自行轮询采集。

namespace BetterDesktop.Shell.Status.Contracts;

/// <summary>状态轮询服务：驱动各监控项定时采集并广播变化。</summary>
public interface IStatusPoller : IDisposable
{
    /// <summary>开始周期采集（轮询间隔毫秒）。幂等：重复调用不叠加定时器。</summary>
    void Start(int intervalMilliseconds = 2000);

    /// <summary>停止周期采集。幂等。</summary>
    void Stop();

    /// <summary>立即触发一次采集（供首屏/手动刷新）。</summary>
    void PollNow();
}
