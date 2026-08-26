// BetterDesktop.Kernel — IResourceSubject 内存治理契约
// 治理器通过此抽象隔离/重启任意受控对象（in-process 插件或外部进程），
// 不直接依赖 HmrManager，避免跨工程耦合。

using BetterDesktop.Kernel.Contracts;

namespace BetterDesktop.Kernel.Core;

/// <summary>
/// 内存治理的受控对象抽象。ResourceGovernor 周期采样其内存，
/// 并按策略调用 IsolateAsync / RestartAsync / Quarantine。
/// HmrManager.ManagedPlugin 与外部进程适配器均实现此接口。
/// </summary>
public interface IResourceSubject
{
    /// <summary>稳定 id（插件 id 或外部进程 id）。</summary>
    string Id { get; }

    /// <summary>人类可读名称（日志用）。</summary>
    string? DisplayName { get; }

    /// <summary>
    /// 是否允许治理器在超阈值时主动隔离/重启（自愈）。默认 true。
    /// in-process 插件共享单一进程，单插件超阈值多源于进程整体压力（采样值为进程工作集），治理器优先报警；
    /// 但若该插件持续持有未释放资源，仍允许治理器尝试重启以回收（自愈）。
    /// 核心基础设施若需免于自愈可返回 false（当前默认全部允许，遵循"所有插件均可自愈"决策）。
    /// </summary>
    bool IsSelfHealable { get; }

    /// <summary>采样当前内存占用（字节）。返回 0 表示无法采样（治理器跳过）。</summary>
    long SampleMemoryBytes();

    /// <summary>隔离：优雅卸载并停止自重启（等价于 DisposeAsync）。</summary>
    Task IsolateAsync(CancellationToken cancellationToken = default);

    /// <summary>杀掉并自重启：对外部进程 = Kill 后按退避拉起；对 in-process = RestartAsync。</summary>
    Task KillAndRestartAsync(CancellationToken cancellationToken = default);

    /// <summary>进入熔断（连续内存崩溃达上限）：停止治理器的任何自动动作，等待人工干预。</summary>
    void Quarantine();

    /// <summary>是否已熔断（治理器跳过已熔断对象）。</summary>
    bool IsQuarantined { get; }

    /// <summary>记录一次内存压力事件（超任意阈值），供状态中心展示累计次数。</summary>
    void RecordPressure();
}
