// BetterDesktop.Shell.Clipboard.Ipc — 剪贴板引擎/面板的启停**请求**（宿主 ClipboardPlugin 与面板 exe 共用）
//
// 【2026-09-20 收敛】本类此前自己 Process.Start 拉起引擎/面板/截图，并自己 GetProcessesByName 判活、
// 自己 Kill 收尸 —— 即"壳侧的第二个生命周期所有者"。现在全部改为**向 core 请求**（见 CoreComponents）：
//   · exe 定位    → core（`core/src/process.rs::resolve_exe` 是"组件 exe 从哪来"的唯一定义）
//   · 判活        → core 的 status（按声明 + 管道判活；本地按进程名判活会把同名的短命菜单读成常驻服务）
//   · gate        → core（用户在设置里关掉后，core 直接拒绝 start，不会"关掉了又被一次检索拉起来"）
//   · 重复拉起防护 → core（`explicit_start_is_noop`）
//   · 停止        → core 的 stop（真杀，`process::stop_by_exe_name`）
//
// 本类保留的只有：**组件名映射 + 面向调用方的语义命名**（Ensure/Open/Restart/Stop）。它不再持有
// 任何路径候选、不再持有任何进程操作原语 —— 那两样都属于 core。

using System;
using BetterDesktop.Kernel.Core;

namespace BetterDesktop.Shell.Clipboard.Ipc;

/// <summary>
/// 剪贴板引擎/面板/截图的启停请求。宿主（EnsureEngine）与面板（启动自愈）共用同一套组件名，
/// 避免两侧各自硬编码"该找哪个 exe"造成「一边找得到、一边找不到」重复拉起抢管道（S7b 真机教训）。
/// </summary>
public static class ClipboardEngineLauncher
{
    /// <summary>引擎组件名（core/components.json 的 <c>clipboard-engine</c>）。</summary>
    public const string EngineComponent = CoreComponents.ClipboardEngine;

    /// <summary>面板**装配入口**组件名（不带 <c>--open</c>）。</summary>
    public const string PanelComponent = CoreComponents.ClipboardPanel;

    /// <summary>面板**打开**组件名（带 <c>--open</c>）。</summary>
    public const string PanelOpenComponent = CoreComponents.ClipboardPanelOpen;

    /// <summary>截图组件名（带 <c>--capture-now</c>）。</summary>
    public const string CaptureComponent = CoreComponents.Capture;

    /// <summary>
    /// 确保引擎在运行：交给 core（已在跑 → core 判活后不会重复拉起）。
    /// </summary>
    public static bool EnsureEngine(Action<string>? log = null)
        => CoreComponents.Start(EngineComponent, log);

    /// <summary>确保**入口面**（面板 exe）在运行——它承载侧边栏「›」手柄等常驻入口。</summary>
    public static bool EnsurePanelEntry(Action<string>? log = null)
        => CoreComponents.Start(PanelComponent, log);

    /// <summary>
    /// 请求打开完整面板（<c>--open</c>）——**IPC 降级路径**：
    /// 面板自身会探活/拉起引擎，所以"打开面板"这件事不该依赖"调用方↔引擎"的长连接是否健康。
    /// </summary>
    public static bool OpenPanel(Action<string>? log = null)
        => CoreComponents.Start(PanelOpenComponent, log);

    /// <summary>
    /// 彻底退场：停面板（连带侧边入口手柄）与引擎，**不留进程**。
    /// <para>
    /// 【与 gate 的顺序纪律】调用方（<c>ClipboardPlugin.ApplyEnabledStateCore(false)</c>）是在
    /// 设置已写入 <c>enabled=false</c> 之后才走到这里 —— 于是 core 的 <c>must_stop</c> 与
    /// <c>auto_start=false</c> 同时成立，停掉就是**持续**停掉。
    /// 若顺序反过来（先停后写设置），core 会在 3 秒内的下一轮 reconcile 把它拉回来，
    /// 用户看到的正是"关了但它还在"。
    /// </para>
    /// </summary>
    public static void StopAll(Action<string>? log = null)
    {
        CoreComponents.Stop(PanelComponent, log);
        CoreComponents.Stop(EngineComponent, log);
    }

    /// <summary>重启引擎（改键/停用后生效）。</summary>
    public static bool RestartEngine(Action<string>? log = null)
    {
        CoreComponents.Stop(EngineComponent, log);
        return CoreComponents.Start(EngineComponent, log);
    }

    /// <summary>
    /// 改键后对**外部进程**的处置结果（供设置中心给出准确状态，不假装生效）。
    /// <para>【2026-09-16】此前改键无条件"杀 + 拉起"，即使该进程本来没运行 ——
    /// 用户改一个截图热键会莫名把截图工具启动起来，且 UI 无法说明"到底谁生效了"。
    /// 正确语义：只有进程在运行时才需要重启；没运行则配置已保存、下次启动自然读到。</para>
    /// </summary>
    public enum ExternalProcessRestart
    {
        /// <summary>进程原本在运行：已重启，新键立即生效。</summary>
        Restarted,

        /// <summary>进程原本未运行：配置已保存，下次启动时生效（不无故拉起程序）。</summary>
        NotRunning,

        /// <summary>进程在运行但重启失败（停不掉 / 拉不起），需用户手动重启该程序。</summary>
        Failed,
    }

    /// <summary>重启截图进程（改键/停用后生效）。</summary>
    public static bool RestartCapture(Action<string>? log = null)
    {
        CoreComponents.Stop(CaptureComponent, log);
        return CoreComponents.Start(CaptureComponent, log);
    }

    /// <summary>截图进程是否存活（问 core）。</summary>
    public static bool IsCaptureRunning() => IsRunning(CaptureComponent);

    /// <summary>引擎是否存活（问 core）。</summary>
    public static bool IsEngineRunning() => IsRunning(EngineComponent);

    /// <summary>面板入口进程是否存活（问 core）。</summary>
    public static bool IsPanelRunning() => IsRunning(PanelComponent);

    /// <summary>
    /// 改键后**按需重启引擎**：只在引擎正在运行时重启（未运行则不无故拉起）。
    /// </summary>
    public static ExternalProcessRestart RestartEngineIfRunning(Action<string>? log = null)
        => RestartIfRunning(EngineComponent, "引擎", log);

    /// <summary>
    /// 改键后**按需重启截图进程**：只在它正在运行时重启（未运行则不无故把截图工具拉起来）。
    /// </summary>
    public static ExternalProcessRestart RestartCaptureIfRunning(Action<string>? log = null)
        => RestartIfRunning(CaptureComponent, "截图进程", log);

    /// <summary>按需重启的共用实现（两个调用点的语义、日志措辞、返回值必须一致）。</summary>
    private static ExternalProcessRestart RestartIfRunning(string component, string what, Action<string>? log)
    {
        if (!IsRunning(component))
        {
            log?.Invoke($"{what}未运行：热键配置已保存，将在{what}下次启动时生效");
            return ExternalProcessRestart.NotRunning;
        }

        CoreComponents.Stop(component, log);
        return CoreComponents.Start(component, log)
            ? ExternalProcessRestart.Restarted
            : ExternalProcessRestart.Failed;
    }

    /// <summary>问 core 判活；问不到（core 不在 / 响应畸形）一律按 false 并让调用方走降级路径。</summary>
    private static bool IsRunning(string component)
        => CoreComponents.TryGetRunning(component, out var running) && running;
}
