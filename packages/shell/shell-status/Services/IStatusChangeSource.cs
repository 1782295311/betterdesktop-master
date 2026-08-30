// BetterDesktop.Shell.Status — 监控项内部变更触发点（同程序集内部接口）
// poller 检测到变化后统一经此触发各 monitor 的 Changed 事件；对外业务不直接调用。

namespace BetterDesktop.Shell.Status.Services;

using Contracts;

/// <summary>监控项变更触发的内部约定，供 StatusPoller 泛化调用。</summary>
internal interface IStatusChangeSource
{
    /// <summary>触发本监控项的 Changed 事件。</summary>
    void RaiseChanged(StatusSnapshot snapshot);
}

/// <summary>
/// 标记"事件驱动"的监控项：除周期兜底轮询外，还希望在前台窗口切换时（如输入法切换 / 中英切换）
/// 由 StatusPoller 挂的前台 WinEvent 泵即时拉取一次，消除"至多等一个周期"的延迟。
/// </summary>
internal interface IEventDrivenMonitor
{
}