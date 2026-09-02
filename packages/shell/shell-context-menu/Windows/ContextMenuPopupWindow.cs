// BetterDesktop.Shell.ContextMenus — 右键菜单弹层窗口（统一窗口基类）
// 继承 shell-core 的 ShellWindow（cordis 所有窗口的统一基类：无边框/透明/字号缩放/外观传导），
// 窗口属性对齐 shell-menu-bar 的 MenuBarPopupWindow 范式：
//   Topmost 置顶 + 无边框 + 不进任务栏 + 不透皮肤图（UseSkinBackground=false，菜单令牌外观直绘）。
// 与 MenuBarPopupWindow 的差异：ShowActivated=true——右键菜单需要键盘导航（Esc/方向键），
// 失焦关闭走 Deactivated（激活态窗口才会触发）。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.ContextMenus.Contracts;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;

namespace BetterDesktop.Shell.ContextMenus.Windows;

/// <summary>右键菜单弹层窗口（ShellWindow 统一基类；Topmost 短生命周期，失焦/Esc 关闭）。</summary>
internal sealed class ContextMenuPopupWindow : ShellWindow
{
    public ContextMenuPopupWindow(Border menuPanel, Point screenPos,
        IAppearanceService? appearance, IVibrancyService? vibrancy)
        : base(appearance, vibrancy)
    {
        Title = "BetterDesktop.Menu";
        ScreenPos = screenPos;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent; // 菜单面板自身带令牌底色
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Content = menuPanel;
        ChromeBorder = menuPanel; // ShellWindow 约定：DEBUG 断言要求非空

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        };
        Deactivated += (_, _) => Close(); // 点外部/切窗 → 关闭
        Loaded += (_, _) =>
        {
            // 工作区边缘钳制（防菜单溢出屏幕）
            var work = SystemParameters.WorkArea;
            Left = Math.Clamp(screenPos.X, work.Left + 2, Math.Max(work.Left + 2, work.Right - ActualWidth - 2));
            Top = Math.Clamp(screenPos.Y, work.Top + 2, Math.Max(work.Top + 2, work.Bottom - ActualHeight - 2));

            // 外观自检埋点（排查"纯黑"：透明是否生效/令牌是否命中/尺寸是否异常）
            var token = Application.Current?.TryFindResource("PopupBackground");
            DiagnosticLog.Trace("context-menu",
                $"菜单窗口 size={ActualWidth:F0}x{ActualHeight:F0} transp={AllowsTransparency} " +
                $"style={WindowStyle} PopupBackground={(token is System.Windows.Media.Brush b ? b.ToString() : token?.ToString() ?? "缺失")}");
        };
    }

    /// <summary>定位锚点（DIP）。</summary>
    public Point ScreenPos { get; }

    // ======== 窗口属性（对齐 MenuBarPopupWindow 范式） ========

    /// <inheritdoc />
    protected override bool DefaultTopmost => true;

    /// <summary>右键菜单需要键盘导航（Esc/方向键），激活显示；失焦即关。</summary>
    protected override bool DefaultShowActivated => true;

    /// <inheritdoc />
    protected override ResizeMode DefaultResizeMode => ResizeMode.NoResize;

    /// <summary>不透皮肤图：菜单背景/描边由主题令牌（PopupBackground/PopupBorder）直绘。</summary>
    protected override bool UseSkinBackground => false;

    /// <summary>短生命周期菜单窗口无需 DWM blur（透明底直绘令牌面板）。</summary>
    protected override void ApplyWindowMaterial()
    {
    }
}
