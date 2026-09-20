// BetterDesktop.Shell.Dock — DockWindow 的底部定位接线（partial）
// 【2026-09-12 去 AppBar 决策】dock 不再注册系统 AppBar：
//   AppBar 会让最大化窗口的工作区上移（把窗口往上抬）；dock 沉底/置顶切换时，
//   注册/释放条带导致窗口被反复推挤，与 dock "上下竞争打架"（用户实测"dock 太抢戏"）。
//   新模型：dock 平时沉底（被窗口自然盖住，不在主窗口上显示）、使用中置顶浮在窗口底部
//   （macOS Dock 风格），**完全不占用工作区**。定位 = 纯底部居中（PositionToBottomCenter），
//   无协商、无条带、无 ABN_POSCHANGED。

using System;
using System.Windows;
using System.Windows.Interop;
using BetterDesktop.Shell.Core.Native;

namespace BetterDesktop.Shell.Dock;

public partial class DockWindow
{
    /// <summary>
    /// 句柄就绪：设置 Aero Peek / Alt-Tab 豁免（dock 在系统 peek 切换时保持可见）。
    /// 注：EXCLUDED_FROM_PEEK 对 DwmActivateLivePreview（悬停缩略图透明化）实测无效，
    /// 但 Alt-Tab/系统级 peek 场景保留设置无害。
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                var excludedFromPeek = 1;
                _ = NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DwmWindowAttributeExcludedFromPeek, ref excludedFromPeek, sizeof(int));
            }
        }
        catch
        {
            // 属性设置失败不阻断 dock。
        }

        // 【2026-09-14 真机验证】桌面层实例（计划里的「正本」）：BETTERDESKTOP_DOCK_DESKTOP_LAYER=1 时
        // 把本窗口挂到 SHELLDLL_DefView —— peek 只透明化顶层窗口，子窗口免疫（桌面图标能存活即此理）。
        // 默认关 → 本行不产生任何行为，零风险回退。取舍/交接协议/验收见
        // docs/plans/2026-09-14-dock-desktop-layer-peek-v2.md §3 / §6。
        DockDesktopLayer.ApplyIfEnabled(this);
    }

    /// <summary>纯底部居中定位（无 AppBar 协商；AppBar 已整体移除）。</summary>
    private void SyncAppBarPosition() => PositionToBottomCenter();
}
