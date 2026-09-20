// BetterDesktop.DesktopControl — 菜单承载窗（1px 不可见）
//
// 【为什么必须有它】
//   ① **贴边收敛的换算源**：DesktopMenuPopup 用 Application.Current.MainWindow 的 PresentationSource 把
//      鼠标物理坐标 / 显示器工作区换算到 DIP。没有窗口 → 换算源为 null → 收敛整段退化为"光标即左上角"，
//      菜单底部项在屏幕下沿会被裁掉（2026-09-17 用户实测过这个 bug，在多项菜单上表现为"点了没反应"）。
//   ② **前台/焦点**：本进程由 explorer（→ CLI）间接拉起，不是用户直接点开的窗口进程；
//      没有可激活窗口时，弹出层拿不到鼠标输入。Show + Activate 让本进程成为前台进程（窗口本身在屏幕外、
//      完全透明，用户看不到任何东西，只会看到光标处的菜单）。
//
// 【取舍】多显示器混合 DPI 下，换算源窗口在主屏 → 副屏高 DPI 时收敛位置可能有几像素误差。
// 菜单只有 5~7 项、极少触边，暂不为它引入"按显示器建窗"的复杂度（记在方案文档的 Risks 里）。

using System;
using System.Windows;

namespace BetterDesktop.Shell.DesktopControl;

/// <summary>屏幕外 1px 透明窗口：只做 DPI 换算源与前台锚点。</summary>
internal sealed class MenuAnchorWindow : Window
{
    /// <summary>远离可见区域的坐标（多显示器负数坐标场景下也不会撞上真实屏幕）。</summary>
    private const double OffscreenCoordinate = -32000;

    public MenuAnchorWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = true; // 需要激活：弹出层要有前台进程归属才能收鼠标
        AllowsTransparency = true; // 透明前提（配合 Opacity=0）
        Opacity = 0;
        Width = 1;
        Height = 1;
        Left = OffscreenCoordinate;
        Top = OffscreenCoordinate;
        Title = "BetterDesktop.DesktopControl.Anchor";
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 再抢一次前台：Show 时的激活可能被 Windows 前台锁拒绝（本进程由别的进程拉起）。
        // 失败也不致命（多数情况下 Show 的激活已成功），故只留痕不报警。
        try
        {
            Activate();
            DesktopControlLog.Trace($"承载窗就绪（DPI 换算源 + 前台锚点）hwnd={new System.Windows.Interop.WindowInteropHelper(this).Handle}");
        }
        catch (Exception ex)
        {
            DesktopControlLog.Trace($"承载窗激活失败（继续弹菜单）: {ex.Message}");
        }
    }
}
