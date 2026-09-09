// BetterDesktop.Shell.Status — 状态监控项基础契约（语义层输出面）
// 每个系统状态采集项都实现该接口，产出语义快照；UI 层只订阅事件与读取快照，不感知采集细节。

namespace BetterDesktop.Shell.Status.Contracts;

/// <summary>系统状态监控项的基础契约。</summary>
public interface IStatusMonitor
{
    /// <summary>监控项标识（"memory" / "battery" / "volume" / "microphone" / "network" / "ime"）。</summary>
    string MonitorId { get; }

    /// <summary>本监控项的采样周期（毫秒）。各监控项独立调度，互不拖累。
    /// 返回 0 表示"不应按固定周期轮询"（由事件/按需驱动）。默认 2000ms。</summary>
    int PollIntervalMilliseconds => 2000;

    /// <summary>立即采集一次并返回语义快照；任何异常均降级为正常空态，不抛异常。</summary>
    StatusSnapshot GetSnapshot();

    /// <summary>状态发生变化时触发（由统一轮询器在检测到差异时抛出；订阅方按需接收，不必自行轮询）。</summary>
    event EventHandler<StatusSnapshot>? Changed;
}
