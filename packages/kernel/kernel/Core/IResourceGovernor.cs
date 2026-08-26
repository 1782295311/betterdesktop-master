// BetterDesktop.Kernel — IResourceGovernor 内存治理注册契约（Core 内，避免 Core→Hmr 反向依赖）
// CordisContext（Core）仅依赖此接口把插件注册给治理器；HmrManager 实现它并同时实现 IHmrManager。

using BetterDesktop.Kernel.Contracts;

namespace BetterDesktop.Kernel.Core;

/// <summary>内存治理器向内核暴露的最小注册契约。CordisContext 仅依赖此（Core 内）接口。</summary>
public interface IResourceGovernor
{
    /// <summary>注册受控对象（插件 / 外部进程适配器）到内存治理器，消除治理器空转。</summary>
    void RegisterSubject(IResourceSubject subject);

    /// <summary>从内存治理器注销受控对象（插件卸载时调用）。</summary>
    void UnregisterSubject(string id);

    /// <summary>进程级持续超致命阈值的兜底回调（宿主挂 C1 自重启）。</summary>
    System.Action<string>? OnProcessCritical { get; set; }
}
