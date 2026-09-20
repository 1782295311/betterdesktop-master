using System;
using BetterDesktop.Kernel.Core;

namespace BetterDesktop.Shell.IndexIpc;

/// <summary>
/// 索引引擎的**启动请求**（计划 §6.2）。
/// </summary>
/// <remarks>
/// <para>
/// 【本类曾经是"第二个生命周期所有者"】2026-09-20 体检的真机物证：索引引擎进程的父进程**不是 core**
/// —— 它是由 <c>shell-search</c> / <c>shell-app-source</c> 经本类直接 <c>Process.Start</c> 拉起来的。
/// 这正是 <c>core/src/supervisor.rs</c> 模块头所否定的形态（"根因就是谁都可以拉起谁"）：
/// 拉起点一旦分散，core 的组件表就不再是"能到达什么"的真相源，监护 / gate / 退避 / 熔断
/// **全部只对 core 知道的那部分生效** —— 用户关掉索引开关后，一次检索仍能把它拉起来。
/// </para>
/// <para>
/// 【现在它只做一件事】把"确保引擎在跑"翻译成一句 <c>start index-engine</c> 交给 core。
/// exe 定位、判活、gate、重复拉起防护**全部回到 core**（core 按声明判活，且 gate 关掉时直接拒绝）。
/// 本类不再持有任何 exe 路径候选，也不再 <c>Process.Start</c>。
/// </para>
/// <para>
/// 【降级契约】永不抛异常：core 不可达或拒绝时返回 false 并把原因交给日志回调，
/// 由调用方决定回退本地实现（计划 §5.3 降级必须可见）。索引能力本身可降级为"不检索"，
/// 但**不能**降级为"自己偷偷拉起引擎" —— 那会把刚收敛掉的第二个所有者又请回来。
/// </para>
/// </remarks>
public static class IndexEngineLauncher
{
    /// <summary>组件名（core/components.json 的 <c>index-engine</c>）。</summary>
    public const string ComponentName = CoreComponents.IndexEngine;

    /// <summary>确保引擎在运行：交给 core 判定与拉起，已有实例时 core 不会重复拉起（避免抢管道）。</summary>
    public static bool EnsureEngine(Action<string>? log = null)
        => EnsureEngine(log, CoreComponents.Start);

    /// <summary>可注入缝（单测锚点）：把"发请求"替换掉，避免测试依赖真机 core 与进程状态。</summary>
    internal static bool EnsureEngine(Action<string>? log, Func<string, Action<string>?, bool> start)
        => start(ComponentName, log);

    /// <summary>引擎此刻是否在跑（问 core，不本地枚举同名进程）。</summary>
    public static bool IsEngineRunning() => CoreComponents.TryGetRunning(ComponentName, out var running) && running;
}
