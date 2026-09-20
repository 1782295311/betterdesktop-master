using System;
using BetterDesktop.Shell.Hotkeys.Contracts;

namespace BetterDesktop.Shell.Core.Hotkeys;

/// <summary>
/// 作用域静态桥：让不便于注入服务的窗口（PopupWindowBase 基类、ShellWindow 子类）在 show/hide 时
/// 经全局通道上报表面作用域，避免为每个窗口改构造注入。
/// <para>宿主装配：hotkeys-panel 插件创建 <see cref="HotkeyScopeTracker"/> 后 <see cref="Attach"/>；
/// 窗口只调 <see cref="Report"/>（scopeId 为空/桥未挂载时静默忽略）。测试可 <c>Attach(null)</c> 重置。</para>
/// </summary>
public static class SurfaceScopeBridge
{
    private static IHotkeyScopeTracker? _sink;

    /// <summary>挂载当前作用域聚合器（宿主插件 Load 时；null = 卸载/测试重置）。</summary>
    public static void Attach(IHotkeyScopeTracker? tracker) => _sink = tracker;

    /// <summary>上报表面作用域起落（active=true 显示 / false 收起；未挂载或 scopeId 空则忽略）。</summary>
    public static void Report(string? scopeId, bool active)
    {
        if (string.IsNullOrWhiteSpace(scopeId) || _sink is null)
        {
            return;
        }

        if (active)
        {
            _sink.EnterScope(scopeId);
        }
        else
        {
            _sink.ExitScope(scopeId);
        }
    }
}
