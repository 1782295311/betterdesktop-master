// BetterDesktop.Shell.ContextMenus — 右键菜单弹层窗口（统一窗口基类）
// 继承 shell-core 的 ShellWindow（cordis 所有窗口的统一基类：无边框/透明/字号缩放/外观传导），
// 窗口属性对齐 shell-menu-bar 的 MenuBarPopupWindow 范式：
//   Topmost 置顶 + 无边框 + 不进任务栏 + 不透皮肤图（UseSkinBackground=false，菜单令牌外观直绘）。
// 与 MenuBarPopupWindow 的差异：ShowActivated=true——右键菜单需要键盘导航（Esc/方向键），
// 失焦关闭走 Deactivated（激活态窗口才会触发）。
//
// 【黑窗根因（2026-09-02 实测定论，禁止回退）】
//   ShellWindow.ApplyAppearance 在 e.MaterialChanged 时直接调 VibrancyService.Apply（绕过
//   ApplyWindowMaterial 虚方法，空重写拦不住）。DWM 材质（acrylic/blur accent）打在
//   AllowsTransparency 分层窗口上 = 整窗纯黑（layered window 不支持 DWM accent 合成）。
//   解法：构造期把 VibrancyService 置 null——基类的材质路径整体短路，令牌面板直绘。
//
// 【定位生死线（2026-09-02 定稿）】
//   必须在 SourceInitialized（窗口可见前）一次性 SetWindowPos 落位+定尺寸，不得在 Loaded 后
//   补钳制——后者先闪现在默认位置再跳到目标位置 = "位置飘忽"。尺寸来自构造期对菜单面板的
//   预测量（DesiredSize），坐标换算用所在屏 GetDpiForMonitor 反算（物理域落位，跨 DPI 屏正确）。

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.ContextMenus.Contracts;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;

namespace BetterDesktop.Shell.ContextMenus.Windows;

/// <summary>右键菜单弹层窗口（ShellWindow 统一基类；Topmost 短生命周期，失焦/Esc 关闭）。</summary>
internal sealed class ContextMenuPopupWindow : ShellWindow
{
    private readonly Border _menuPanel;

    public ContextMenuPopupWindow(Border menuPanel, Point screenPos,
        IAppearanceService? appearance, IVibrancyService? vibrancy)
        : base(appearance, vibrancy)
    {
        _menuPanel = menuPanel;
        Title = "BetterDesktop.Menu";
        ScreenPos = screenPos;

        // 材质恢复基类路径（与 dock/设置窗口同一 vibrancy 毛玻璃观感——"统一窗口基类该有的样子"）。
        // 注：此前曾因"疑似材质致黑"短路过，实测 dock 同为分层窗口+材质渲染正常，黑感实为
        // 异常尺寸空面板（214x757 黑柱，已修）与深色令牌观感，非材质问题。

        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        // 尺寸交给 SizeToContent（实测正常）；位置只写 Left/Top（见 PositionNow）。
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Content = menuPanel;
        ChromeBorder = menuPanel; // ShellWindow 约定：DEBUG 断言要求非空

        PreviewKeyDown += (_, e) =>
        {
            // Esc 语义（审查 P2-1）：有子菜单打开时放行给 MenuItem（先收子菜单，explorer 同款）；
            // 无子菜单打开才关整窗。
            if (e.Key == Key.Escape && !HasOpenSubmenu(_menuPanel))
            {
                Close();
            }
        };
        Deactivated += (_, _) => Close(); // 点外部/切窗 → 关闭

        // ★ 自愈 1：右键落在菜单窗口自己身上（同位置再次右键时菜单盖住光标）——无处理器=无声吞掉，
        //   且不触发 Deactivated → 菜单永不自关、拦死桌面右键（"同位置多次右键后唤不出"的元凶）。
        //   统一行为：右键落在本窗口 → 立即关闭（下一次右键重新呼出）。
        PreviewMouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            Close();
        };

        SourceInitialized += (_, _) => PositionNow(estimate: true);

        Loaded += (_, _) =>
        {
            PositionNow(estimate: false); // 真实尺寸精调（通常与估算一致，无跳动）

            // ★ 出屏自检（用户实测：屏幕底部呼出会超界）：布局完成后读真实物理矩形，
            //   与所在屏工作区比对，超界按像素差平移——delta 修正完全在物理域，规避单位换算歧义。
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, VerifyOnScreen);

            // ★ 自愈 2：ShowActivated 但系统可能拒绝激活（快速连续操作的前台锁）——
            //   从未激活的窗口不会触发 Deactivated，成为永久隐形拦截器。加载 800ms 仍未激活 → 自关。
            var selfHeal = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            selfHeal.Tick += (_, _) =>
            {
                selfHeal.Stop();
                if (IsVisible && !IsActive)
                {
                    DiagnosticLog.Trace("context-menu", "菜单窗口未获得激活：自愈关闭（防隐形拦截）");
                    Close();
                }
            };
            selfHeal.Start();

            // 外观自检埋点（排查"纯黑"：透明是否生效/令牌是否命中/尺寸是否异常）
            var token = Application.Current?.TryFindResource("PopupBackground");
            DiagnosticLog.Trace("context-menu",
                $"菜单窗口 size={ActualWidth:F0}x{ActualHeight:F0} transp={AllowsTransparency} " +
                $"style={WindowStyle} PopupBackground={(token is System.Windows.Media.Brush b ? b.ToString() : token?.ToString() ?? "缺失")}");
        };
    }

    /// <summary>定位锚点（DIP）。</summary>
    public Point ScreenPos { get; }

    /// <summary>视觉树中是否存在已展开的子菜单（Esc 分层收起的判据）。</summary>
    private static bool HasOpenSubmenu(DependencyObject? node)
    {
        if (node is null) return false;
        if (node is MenuItem { IsSubmenuOpen: true }) return true;
        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            if (HasOpenSubmenu(VisualTreeHelper.GetChild(node, i))) return true;
        }
        return false;
    }

    /// <summary>
    /// 按所在屏工作区钳制菜单位置（写 WPF 属性 Left/Top——Show 不会覆盖非 NaN 值；
    /// 禁用 SetWindowPos：会被 Show 的 NaN→CW_USEDEFAULT 级联定位冲掉 = 位置飘忽实测根因）。
    /// estimate=true（SourceInitialized，布局未跑）用保守估算尺寸；false（Loaded）用真实尺寸。
    /// 物理域（MonitorFromPoint + GetMonitorInfo）按所在屏工作区，SystemParameters.WorkArea 只描述主屏（禁用）。
    /// </summary>
    private void PositionNow(bool estimate)
    {
        try
        {
            var width = estimate ? EstWidth : ActualWidth;
            var height = estimate ? EstHeight : ActualHeight;

            // 两步定位：先按窗口自身 DPI 粗算物理点找屏，再用该屏真实 DPI 精确反算（混合 DPI 屏正确）
            var windowDpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var monitorScale = GetScaleForPoint(new Point(ScreenPos.X * windowDpi, ScreenPos.Y * windowDpi));
            var dpi = monitorScale ?? windowDpi;
            var physical = new Point(ScreenPos.X * dpi, ScreenPos.Y * dpi);

            var work = GetWorkAreaPhysical(physical);
            if (work is not { } r)
            {
                if (estimate)
                {
                    // 找不到所在显示器：按物理点 ÷ 窗口 DPI 原样落位（不瞎猜）
                    Left = physical.X / windowDpi;
                    Top = physical.Y / windowDpi;
                }
                return;
            }

            var widthPx = width * windowDpi;
            var heightPx = height * windowDpi;
            var left = Math.Clamp(physical.X, r.Left + 2, Math.Max(r.Left + 2, r.Right - widthPx - 2));
            var top = Math.Clamp(physical.Y, r.Top + 2, Math.Max(r.Top + 2, r.Bottom - heightPx - 2));

            Left = left / windowDpi;
            Top = top / windowDpi;
        }
        catch
        {
            // 落位失败不致命：退回系统默认位置（仍可用）
        }
    }

    /// <summary>
    /// 出屏自检：读窗口真实物理矩形（GetWindowRect）与所在屏工作区比对，
    /// 超出边缘的部分按像素差平移回工作区内。任何前置定位的残余误差都在这里兜底。
    /// </summary>
    private void VerifyOnScreen()
    {
        try
        {
            var hwnd = (PresentationSource.FromVisual(this) as HwndSource)?.Handle ?? IntPtr.Zero;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r))
            {
                return;
            }

            var work = GetWorkAreaPhysical(new Point(r.Left + 2, r.Top + 2))
                ?? GetWorkAreaPhysical(new Point((r.Left + r.Right) / 2.0, (r.Top + r.Bottom) / 2.0));
            if (work is not { } w)
            {
                return;
            }

            var dxRight = Math.Max(0, r.Right - (w.Right - 2));
            var dxLeft = Math.Max(0, (w.Left + 2) - r.Left);
            var dyBottom = Math.Max(0, r.Bottom - (w.Bottom - 2));
            var dyTop = Math.Max(0, (w.Top + 2) - r.Top);
            if (dxRight == 0 && dxLeft == 0 && dyBottom == 0 && dyTop == 0)
            {
                return;
            }

            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            if (dxRight > 0) Left -= dxRight / dpi;
            else if (dxLeft > 0) Left += dxLeft / dpi;
            if (dyBottom > 0) Top -= dyBottom / dpi;
            else if (dyTop > 0) Top += dyTop / dpi;

            DiagnosticLog.Trace("context-menu",
                $"出屏自检平移 dxR={dxRight} dxL={dxLeft} dyB={dyBottom} dyT={dyTop}");
        }
        catch
        {
            // 自检失败不致命（M10）
        }
    }

    /// <summary>取包含指定**物理点**的显示器的工作区（物理像素域）；找不到返回 null。</summary>
    private static Rect? GetWorkAreaPhysical(Point physicalPoint)
    {
        var hMonitor = MonitorFromPoint((int)Math.Round(physicalPoint.X), (int)Math.Round(physicalPoint.Y), MonitorDefaultToNearest);
        if (hMonitor == IntPtr.Zero) return null;
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(hMonitor, ref info)) return null;
        var w = info.rcWork;
        return new Rect(w.Left, w.Top, w.Right - w.Left, w.Bottom - w.Top);
    }

    /// <summary>取指定物理点所在显示器的 DPI 缩放（1.0 = 100%）；取不到返回 null。</summary>
    private static double? GetScaleForPoint(Point physicalPoint)
    {
        var hMonitor = MonitorFromPoint((int)Math.Round(physicalPoint.X), (int)Math.Round(physicalPoint.Y), MonitorDefaultToNearest);
        if (hMonitor == IntPtr.Zero) return null;
        return GetDpiForMonitor(hMonitor, DpiType.Effective, out uint dpiX, out _) == SOk && dpiX > 0
            ? dpiX / 96.0
            : null;
    }

    private const uint MonitorDefaultToNearest = 2;
    private const int SOk = 0;

    /// <summary>SourceInitialized 阶段的保守估算尺寸上限（DIP；Loaded 换真实尺寸精调）。</summary>
    private const double EstWidth = 280;
    private const double EstHeight = 560;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(int x, int y, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("Shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, DpiType dpiType, out uint dpiX, out uint dpiY);

    private enum DpiType
    {
        Effective = 0,
        Angular = 1,
        Raw = 2,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }
}
