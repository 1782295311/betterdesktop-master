// BetterDesktop.Shell.Core — 弹出面板统一基类（2026-09-07 弹窗体系上提）
//
// 【来源】shell-menu-bar/Windows/MenuBarPopupWindow.cs（P0-2 上提，行为等价优先）。
// 所有独立弹出面板（菜单栏 IME/日历/控制中心/WLAN/音量/搜索、未来 dock/context-menu 弹窗）都继承此类：
//   - 继承 ShellWindow → 窗口属性（无边框/透明/置顶/毛玻璃/字号/描边）由统一基类驱动
//   - 面板外观（背景/描边/圆角/前景）全部走主题令牌（ThemePanelBackground/CardBorderBrush/
//     ThemeForeground 等），随设置里的主题系统（亮/暗/无色模式 + 窗体不透明度滑块）全局切换
//   - 面板不透皮肤图（UseSkinBackground=false）：面板半透明直接透出 DWM 毛玻璃/桌面
//   - 失焦自动收起：全局低级鼠标钩子检测"点击窗口外"即隐藏（仿 macOS 面板"点别处即消失"）；
//     Deactivated 保留作双保险
//   - 使用 Show() 显示且不抢夺焦点（ShowActivated=false，避免"打开面板就改了用户输入法"）
//   - 禁止二次 Show：重复调用只做位置调整与置前
// 【602 纪律】UseNoActivateWindowStyle + WS_EX_NOACTIVATE 双层缺一不可；含键盘输入的弹窗必须重写为 false。
// 【603 纪律】钩子生命周期：ShowAt 时挂、Hide 时卸、OnClosed 兜底卸；delegate 字段强引用（防 GC 回收）。
// 【7438 纪律】MouseHook 卸载幂等；Dispose/OnClosed 成对清理。

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using BetterDesktop.Shell.Core.Native;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.Core.Windowing;

namespace BetterDesktop.Shell.Core.Windows;

/// <summary>
/// 弹出面板统一基类。子类实现 <see cref="BuildContent"/> 负责真实 UI + Binding。
/// 面板外观由本基类统一挂载（对齐 ShellWindow 窗口属性，走主题令牌），子类内容根容器须透明。
/// </summary>
public abstract class PopupWindowBase : ShellWindow
{
    private bool _shown;

    // ---- 全局低级鼠标钩子（点击窗口外 → 自动收起）----
    // 7435/7437 收口：WH_MOUSE_LL 统一走 shell-core/Native（MouseHook 封装 + NativeMethods 声明）。
    private readonly MouseHook _mouseHook;

    protected PopupWindowBase(IVibrancyService vibrancy, IAppearanceService? appearance = null)
        : base(appearance, vibrancy)
    {
        _mouseHook = new MouseHook(OnMouseEvent);

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

    /// <summary>
    /// 是否套用 Win32 层 WS_EX_NOACTIVATE + WS_EX_TOOLWINDOW（与 WPF 层 ShowActivated=false
    /// 「双层缺一不可」，对照 602 文档）。含键盘输入的弹窗（搜索框/密码框/重命名）必须重写为 false——
    /// NOACTIVATE 会让窗口点击后仍不获得焦点，键盘输入落不进 TextBox。
    /// </summary>
    protected virtual bool UseNoActivateWindowStyle => true;

    /// <summary>句柄就绪后补 Win32 层"不抢焦点"样式（全库 MakeFloatingNoActivate 唯一消费点）。</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (UseNoActivateWindowStyle)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                WindowStyleHelper.MakeFloatingNoActivate(hwnd);
            }
        }
    }

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
    protected void HidePopup()
    {
        RemoveMouseHook();
        if (IsVisible)
        {
            Hide();
        }
    }

    private void EnsureMouseHook()
    {
        _mouseHook.Start();
    }

    private void RemoveMouseHook()
    {
        _mouseHook.Stop();
    }

    private bool _suppressOutsideClickHide;

    private void OnMouseEvent(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
        {
            return;
        }

        var msg = (int)wParam;
        if (msg is NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_RBUTTONDOWN or NativeMethods.WM_MBUTTONDOWN)
        {
            // 面板弹出了自己的 WPF ContextMenu（独立 HWND）期间：点菜单项在几何上位于
            // 面板窗口外，但那是菜单交互，绝不能触发收起（否则菜单在 Click 生效前就被关掉）。
            if (_suppressOutsideClickHide)
            {
                return;
            }

            var info = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
            // 面板窗口内 → 不收起（面板自身交互）；宿主条带内 → 不收起（操作宿主图标）；
            // 其余"窗口外"点击 → 自动收起。
            if (!IsPointInWindow(info.pt) && !IsPointInMenuBarStrip(info.pt))
            {
                // 异步收起（不在系统钩子上下文里做复杂 UI 操作）
                Dispatcher.BeginInvoke(new Action(HidePopup));
            }
        }
    }

    /// <summary>
    /// 挂起"点击窗口外自动收起"。面板弹出 WPF ContextMenu（独立 HWND）期间必须挂起：
    /// 点菜单项在几何上位于面板窗口外，不挂起会被外点判定收起面板，菜单在 Click 生效前被关闭。
    /// 菜单 Closed 后务必恢复（传 false）。
    /// </summary>
    protected void SetOutsideClickHideSuppressed(bool suppressed) => _suppressOutsideClickHide = suppressed;

    /// <summary>
    /// 点击点是否落在"宿主条带"内（如屏幕顶部菜单栏条带：点击条带视为操作宿主，不触发收起）。
    /// 默认无条带（false）；菜单栏宿主重写为条带几何判定。
    /// </summary>
    protected virtual bool IsPointInMenuBarStrip(NativeMethods.POINT pt) => false;

    private bool IsPointInWindow(NativeMethods.POINT pt)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var rect))
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
