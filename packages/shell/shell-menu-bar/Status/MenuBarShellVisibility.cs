// BetterDesktop.Shell.MenuBar — 菜单栏"整体是否可见"的进程内状态（2026-09-18 电源管理）
//
// 【为什么必须有这个状态】
//   帧率组件（FpsIcon）是全库**唯一**订阅 CompositionTarget.Rendering 的地方，而 WPF 语义是：
//   **只要存在渲染订阅者，合成器就认定"有人需要每帧回调"，并按刷新率（60/120/144Hz）持续出帧**，
//   与控件是否可见/Collapsed 无关。
//   菜单栏空闲隐藏（MenuBarWindow.SetIdleHidden，默认 20 分钟无输入后淡出）走的是 Opacity + 挂起材质，
//   窗口与可视树都还在 → 之前"隐藏即退订"只覆盖了扩展开关那条路，**空闲淡出这条路上订阅仍然活着**，
//   于是用户已经离开电脑、系统准备进现代待机（S0ix）时，菜单栏仍在满帧出图（笔记本风扇长转的原因之一）。
//
// 【为什么用静态状态而不是事件总线/接口】
//   两侧都是本装配内的单例（MenuBarWindow ↔ MenuBarStatusStrip），语义只有一个布尔、零跨进程含义；
//   走 IEventBus 反而引入序列化与订阅生命周期负担。Changed 事件只做"重新评估一次采样"这点小事。

using System;

namespace BetterDesktop.Shell.MenuBar.Status;

/// <summary>菜单栏整体可见性（空闲隐藏期间为不可见）。由窗口写入、帧率组件消费。</summary>
internal static class MenuBarShellVisibility
{
    private static volatile bool _visible = true;

    /// <summary>菜单栏当前是否对用户可见（空闲淡出期间为 false）。</summary>
    public static bool IsVisible => _visible;

    /// <summary>可见性变化（空闲隐藏 / 唤出）——订阅方据此重新评估"要不要继续每帧采样"。</summary>
    public static event Action? Changed;

    /// <summary>更新可见性（幂等：值未变不发事件）。由 <c>MenuBarWindow.SetIdleHidden</c> 调用（UI 线程）。</summary>
    public static void Set(bool visible)
    {
        if (_visible == visible)
        {
            return;
        }

        _visible = visible;
        Changed?.Invoke();
    }
}
