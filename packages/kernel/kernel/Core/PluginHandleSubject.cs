// BetterDesktop.Kernel — PluginHandleSubject 内存治理适配器
// 把 CordisContext 直接注册的插件（内置服务 / 未来磁盘插件）包装成 IResourceSubject，
// 注册给 ResourceGovernor，消除治理器空转：所有进 Context 的插件都被真实监控。
// in-process 插件共享 GC 堆，SampleMemoryBytes 用进程级近似（与 ManagedPlugin 同款）。

using System.Threading;
using System.Threading.Tasks;
using BetterDesktop.Kernel.Contracts;

namespace BetterDesktop.Kernel.Core;

/// <summary>PluginHandle 的 IResourceSubject 适配器：使任意进 Context 的插件受内存治理器监控。</summary>
public sealed class PluginHandleSubject : IResourceSubject
{
    private readonly PluginHandle _handle;
    private bool _quarantined;

    public PluginHandleSubject(PluginHandle handle)
    {
        _handle = handle ?? throw new System.ArgumentNullException(nameof(handle));
    }

    /// <inheritdoc />
    /// B3 修复：改用插件实例唯一键（PluginHandle.InstanceId）。
    /// 旧实现返回 GetType().FullName，全部插件共享同一字符串，导致
    /// ResourceGovernor 按 Id 去重时只监控第一个插件、其余注册全被跳过；
    /// CordisContext.RemovePlugin 又用同一键注销，任一插件卸载即清空唯一监控对象。
    public string Id => _handle.InstanceId;

    /// <inheritdoc />
    /// 诊断显示用插件名（旧实现返回 GetType().Name 恒为 "PluginHandle"，无辨识度）。
    public string? DisplayName => _handle.PluginName;

    /// <inheritdoc />
    /// 所有进 Context 的插件默认可自愈（遵循"所有插件均可自愈"决策）。
    public bool IsSelfHealable => true;

    /// <inheritdoc />
    /// in-process 共享 GC 堆，单插件无法精准归因；故返回当前进程的实时工作集（WorkingSet64），
    /// 与任务管理器口径一致（而非 GC.GetTotalMemory 的托管堆），使治理阈值（按物理内存比例）
    /// 能真正生效，也避免与用户看到的进程内存对不上（11MB 托管堆 vs ~148MB 工作集）。
    public long SampleMemoryBytes()
    {
        try
        {
            using var proc = System.Diagnostics.Process.GetCurrentProcess();
            proc.Refresh();
            return proc.WorkingSet64;
        }
        catch
        {
            return 0;
        }
    }

    /// <inheritdoc />
    public Task IsolateAsync(CancellationToken cancellationToken = default)
        => _handle.DisposeAsync();

    /// <inheritdoc />
    public Task KillAndRestartAsync(CancellationToken cancellationToken = default)
        => _handle.RestartAsync();

    /// <inheritdoc />
    public void Quarantine()
    {
        _quarantined = true;
    }

    /// <inheritdoc />
    public bool IsQuarantined => _quarantined;

    /// <inheritdoc />
    public void RecordPressure()
    {
        // 压力计数可由 PluginHandle 自身状态承载；此处无额外状态需求。
    }
}
