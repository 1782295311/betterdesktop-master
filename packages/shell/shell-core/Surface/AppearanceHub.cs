// BetterDesktop.Shell.Core — 进程内"外观已变更"广播（ShellWindow 的兜底订阅源）
//
// 【为什么必须有它】ShellWindow 原本只经 IEventBus（ShellEvents.AppearanceChanged）订阅外观变更，
// 而 `Events` 必须由**每个子类在构造期显式赋值**。实际代码里 MenuBarWindow 与 PopupWindowBase 都没赋值，
// 后果是这两类窗口只在首次 Load 时应用一次外观，之后切主题（透白 / 透暗 / 无色 / 字号 / 材质 / 圆角）
// **永不跟随**。真机表现："切到透白后菜单栏还是一块黑的"（其余窗口都订阅了，所以只有菜单栏不变）。
//
// 这里提供一个**不依赖构造期注入**的进程内广播：shell-settings 的 AppearanceService 在每次外观变更时
// 顺带 Raise 一次，ShellWindow 在"没有事件总线"时回退订阅它。这样任何 ShellWindow 子类
//（含插件自己 new 的窗口）都不会再因为漏传 event bus 而卡在旧主题。
//
// 【生命周期红线】静态事件会强引用订阅者 → 订阅方**必须**在窗口关闭时退订
//（ShellWindow 在 DetachWindow 里做，且 OnClosed 会调用 DetachWindow）。

using System;

namespace BetterDesktop.Shell.Core.Surface;

/// <summary>外观变更的进程内广播（参数语义与 IEventBus 的 AppearanceChanged 完全一致）。</summary>
public static class AppearanceHub
{
    /// <summary>外观任意维度变更时触发。</summary>
    public static event Action<AppearanceChangedArgs>? Changed;

    /// <summary>广播一次。由 <c>AppearanceService</c> 的 Raise 调用（UI 线程）。</summary>
    /// <param name="args">本次变更涉及的维度。</param>
    public static void Raise(AppearanceChangedArgs args) => Changed?.Invoke(args);
}
