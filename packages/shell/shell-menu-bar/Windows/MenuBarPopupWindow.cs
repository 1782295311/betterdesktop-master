// BetterDesktop.Shell.MenuBar — 菜单栏弹出面板统一基类
// 所有独立面板（IME菜单/日历/控制中心/WLAN/音量/…）都继承此类：
//   - 继承 ShellWindow → 窗口属性（无边框/透明/置顶/毛玻璃/字号/描边）由统一基类驱动
//   - 面板外观（背景/描边/圆角/前景）全部走主题令牌（ThemePanelBackground/CardBorderBrush/
//     ThemeForeground 等），随设置里的主题系统（亮/暗/无色模式 + 窗体不透明度滑块）全局切换
//   - 面板不透皮肤图（UseSkinBackground=false）：面板半透明直接透出 DWM 毛玻璃/桌面，
//     而不是被皮肤图铺底（避免"固定深色底、调透明度无反应"）
//   - 失焦自动收起：全局低级鼠标钩子检测"点击窗口外"即隐藏（仿 macOS 面板"点别处即消失"）；
//     Deactivated 保留作双保险
//   - 使用 Show() 显示且不抢夺焦点（ShowActivated=false，避免"打开面板就改了用户输入法"）
//   - 禁止二次 Show：重复调用只做位置调整与置前
// 不做任何 UI 内容硬编码；子类在 BuildContent() 里建内容并绑定真实数据源（内容根容器必须透明）。

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;

namespace BetterDesktop.Shell.MenuBar.Windows;

/// <summary>
/// 菜单栏独立弹出面板的基类。子类实现 BuildContent() 负责真实 UI + Binding。
/// 面板外观由本基类统一挂载（对齐 ShellWindow 窗口属性，走主题令牌），子类内容根容器须透明。
/// </summary>
internal abstract class MenuBarPopupWindow : ShellWindow
{
    private bool _shown;

    /// <summary>屏幕顶部菜单栏条带高度：菜单栏固定在主屏顶部，
    /// 点击该条带视为"操作菜单栏"（打开/切换面板），不触发"点击窗口外收起"。
    /// 引用 MenuBarMetrics 单一真相源（此前硬编码 20 与 MenuBarHeight 16 不同源，存在漂移隐患）。</summary>
    private static readonly double MenuBarStripHeight = Contracts.MenuBarMetrics.MenuBarHeight;

    // ---- 全局低级鼠标钩子（点击窗口外 → 自动收起） ----
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x201;
    private const int WM_RBUTTONDOWN = 0x204;
    private const int WM_MBUTTONDOWN = 0x207;
    private IntPtr _mouseHook;
    private MouseHookCallback? _mouseHookProc; // 持有委托引用，防被 GC 回收导致回调失效

    private delegate IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, MouseHookCallback lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    protected MenuBarPopupWindow(IVibrancyService vibrancy, IAppearanceService? appearance = null)
        : base(appearance, vibrancy)
    {
        // 弹出面板：无边框、透明背景（毛玻璃）、无任务栏条目、默认不激活
        if (CanSetProperty("WindowStyle")) WindowStyle = WindowStyle.None;
        if (CanSetProperty("AllowsTransparency")) AllowsTransparency = true;
        if (CanSetProperty("ShowInTaskbar")) ShowInTaskbar = false;
        if (CanSetProperty("ShowActivated")) ShowActivated = false;
        if (CanSetProperty("ResizeMode")) ResizeMode = ResizeMode.NoResize;
        if (CanSetProperty("Topmost")) Topmost = true;

        // 失焦自动收起（双保险）：窗口若曾被激活，失焦即隐藏。
        Deactivated += (_, _) =>
        {
            if (IsVisible)
            {
                HidePopup();
            }
        };

        Visibility = Visibility.Collapsed;
    }

    /// <summary>面板为浮层：不透皮肤图，半透明面板色直接透出毛玻璃/桌面（消除"固定深色底"）。</summary>
    protected override bool UseSkinBackground => false;

    /// <summary>子类在此构建根内容（布局 + 真实数据 Binding），返回根 Visual。</summary>
    protected abstract FrameworkElement BuildContent();

    /// <summary>
    /// 在指定屏幕坐标显示（或置前）弹窗。
    /// 若已显示则仅更新位置 + 置前；否则首次构建内容并 Show。
    /// </summary>
    public void ShowAt(Point screenTopLeft)
    {
        if (!_shown)
        {
            ApplyContent(BuildContent());
            _shown = true;
        }

        Left = screenTopLeft.X;
        Top = screenTopLeft.Y;

        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        // 注意：不调用 Activate() 抢键盘焦点，否则会把当前输入法切换到本弹窗线程，
        // 造成"打开面板就改了用户输入法"。Topmost 已保证弹窗置前，点击面板时自动获得焦点。
        ShowActivated = false;

        // 显示后挂全局鼠标钩子：鼠标点击面板窗口外任意处 → 自动收起。
        EnsureMouseHook();
    }

    /// <summary>隐藏面板并卸下全局鼠标钩子（下次 Show 再挂）。</summary>
    private void HidePopup()
    {
        RemoveMouseHook();
        if (IsVisible)
        {
            Hide();
        }
    }

    private void EnsureMouseHook()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            return;
        }
        _mouseHookProc = MouseHookProc;
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseHookProc, GetModuleHandle(null), 0);
    }

    private void RemoveMouseHook()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
    }

    private IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var msg = (int)wParam;
            if (msg is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN)
            {
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                // 面板窗口内 → 不收起（面板自身交互）；菜单栏条带内 → 不收起（操作菜单栏图标）；
                // 其余"窗口外"点击 → 自动收起。
                if (!IsPointInWindow(info.pt) && !IsInMenuBarStrip(info.pt))
                {
                    // 异步收起（不在系统钩子上下文里做复杂 UI 操作）
                    Dispatcher.BeginInvoke(new Action(HidePopup));
                }
            }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    /// <summary>点击点是否落在屏幕顶部菜单栏条带内（菜单栏固定在主屏顶部，全宽）。</summary>
    private bool IsInMenuBarStrip(POINT pt)
    {
        return pt.Y >= 0 && pt.Y < MenuBarStripHeight;
    }

    private bool IsPointInWindow(POINT pt)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect))
        {
            return false;
        }
        return pt.X >= rect.Left && pt.X <= rect.Right && pt.Y >= rect.Top && pt.Y <= rect.Bottom;
    }

    /// <summary>
    /// 挂载面板内容并应用统一主题外观（ShellWindow 窗口属性对齐入口）。
    /// 面板外观完全由主题令牌驱动：背景 ThemePanelBackground（随窗体不透明度滑块半透明）、
    /// 描边 CardBorderBrush、圆角 SystemCornerRadius、前景 ThemeForeground（附加属性继承传导到未显式设色的子文本）。
    /// 子类重建内容（如 IME 切子视图）也应调用本方法，保证外观始终统一。
    /// </summary>
    protected void ApplyContent(FrameworkElement inner)
    {
        // 面板根 Border：背景/描边/圆角全部走主题，随设置里的主题系统切换。
        var chrome = new Border
        {
            CornerRadius = new CornerRadius(SystemCornerRadius),
            BorderThickness = new Thickness(AppearanceService?.CardBorderThickness ?? 1),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            Child = inner
        };
        SetThemeBinding(chrome, Border.BackgroundProperty, "ThemePanelBackground");
        SetThemeBinding(chrome, Border.BorderBrushProperty, "CardBorderBrush");
        // 前景统一绑定主题主色：未显式设 Foreground 的子文本自动继承，随亮/暗/无色模式切换。
        SetThemeBinding(chrome, TextElement.ForegroundProperty, "ThemeForeground");
        // 递归兜底：对未显式设前景的 TextBlock 逐一绑定 ThemeForeground（按钮默认样式等会中断继承）。
        BindThemeForeground(inner);
        Content = chrome;
        ChromeBorder = chrome;
    }

    /// <summary>
    /// 递归把未显式设前景的文本绑定到主题主色令牌（不覆盖已用 SetThemeBinding/调色板设置的层次）。
    /// Control（Button/自定义控件等）：只在其自身前景未显式设置时绑 Control.Foreground 令牌，
    /// 不深入模板内部——让模板内文本继承 Control.Foreground，避免破坏强调按钮（显式白字）等设计。
    /// </summary>
    private static void BindThemeForeground(DependencyObject root)
    {
        if (root is Control control)
        {
            if (control.ReadLocalValue(Control.ForegroundProperty) == DependencyProperty.UnsetValue)
            {
                SetThemeBinding(control, Control.ForegroundProperty, "ThemeForeground");
            }
            return;
        }

        if (root is TextBlock textBlock
            && textBlock.ReadLocalValue(TextBlock.ForegroundProperty) == DependencyProperty.UnsetValue)
        {
            SetThemeBinding(textBlock, TextBlock.ForegroundProperty, "ThemeForeground");
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            BindThemeForeground(VisualTreeHelper.GetChild(root, i));
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        RemoveMouseHook();
        _shown = false;
        base.OnClosed(e);
    }
}
