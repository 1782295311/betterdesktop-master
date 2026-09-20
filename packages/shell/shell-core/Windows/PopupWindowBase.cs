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
        // 【手动收起模式必须一并受控】只关外点钩子会漏这条路径：点面板外 → 面板失焦 →
        // Deactivated 照样把它关掉，表现为"设了手动收起，点别处还是消失"（2026-09-12）。
        Deactivated += (_, _) =>
        {
            DiagTrace("Deactivated; IsVisible=" + IsVisible);
            if (AutoHideOnOutsideClick && IsVisible)
            {
                HidePopup();
            }
        };

        Visibility = Visibility.Collapsed;
    }

    /// <summary>面板为浮层：不透皮肤图，半透明面板色直接透出毛玻璃/桌面（消除"固定深色底"）。</summary>
    protected override bool UseSkinBackground => false;

    /// <summary>
    /// 弹层窗口默认套用 Win32 层 WS_EX_NOACTIVATE + WS_EX_TOOLWINDOW（与 WPF 层 ShowActivated=false
    /// 「双层缺一不可」，对照 602 文档）。含键盘输入的弹窗（搜索框/密码框/重命名）必须重写为 false——
    /// NOACTIVATE 会让窗口点击后仍不获得焦点，键盘输入落不进 TextBox。
    /// 注：机制本体与消费点在 <see cref="ShellWindow.OnSourceInitialized"/>，此处只改默认值。
    /// </summary>
    protected override bool UseNoActivateWindowStyle => true;

    /// <summary>
    /// 是否「点窗口外 / 失焦即自动收起」（默认 true，仿 macOS 面板"点别处即消失"）。
    /// 重写为 false = **手动收起**：只有显式 <see cref="HidePopup"/>（关闭按钮 / Esc）才收起，
    /// 面板可长期停留（适合边看历史边在其他窗口操作）。
    /// 【两处路径都要受控】外点钩子（<see cref="OnMouseEvent"/>）与 <see cref="Deactivated"/> 是双保险 ——
    /// 只关一处会漏（见构造器内注释）。子类通常在构造期从配置读取该值。
    /// </summary>
    protected virtual bool AutoHideOnOutsideClick => true;

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
        DiagTrace($"ShowAt after Show IsVisible={IsVisible} shown={_shown} at={screenTopLeft.X:0},{screenTopLeft.Y:0}");

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        // 注意：不调用 Activate() 抢键盘焦点，否则会把当前输入法切换到本弹窗线程，
        // 造成"打开面板就改了用户输入法"。Topmost 已保证弹窗置前，点击面板时自动获得焦点。
        ShowActivated = false;

        // 显示后挂全局鼠标钩子：鼠标点击面板窗口外任意处 → 自动收起。
        EnsureMouseHook();

        Hotkeys.SurfaceScopeBridge.Report(SurfaceScopeId, active: true);
    }

    /// <summary>隐藏面板并卸下全局鼠标钩子（下次 Show 再挂）。</summary>
    protected void HidePopup()
    {
        DiagTrace("HidePopup ENTER\n" + Environment.StackTrace);
        Hotkeys.SurfaceScopeBridge.Report(SurfaceScopeId, active: false);
        OnBeforeHide(); // 【必须在 Hide 之前】此时本进程仍是前台进程（见 OnBeforeHide 注释）
        RemoveMouseHook();
        if (IsVisible)
        {
            Hide();
        }
    }

    /// <summary>
    /// 收起**之前**的钩子（在窗口仍可见、本进程仍是前台进程时调用）。默认空实现。
    /// <para>
    /// 【为什么需要 · 2026-09-12 真机】WPF `Hide()` 会把焦点移交给**同进程的另一个可见窗口**
    ///（如侧边栏手柄），`WS_EX_NOACTIVATE` 拦不住这条路径。后果：面板收起后前台"蒸发"到自家窗口上，
    /// 于是 `SendPaste` 的 Ctrl+V 打在自己身上 —— 按序粘贴**第一条凭空消失**
    ///（用户实测"还是漏了第一个"，且因时序竞态而**偶发**：固定延时有时够、有时不够）。
    /// </para>
    /// <para>
    /// 子类可在此刻 `SetForegroundWindow` 把前台还给用户的窗口。**必须在此刻做**：
    /// 只有当前台进程调用时 `SetForegroundWindow` 才被系统接受，`Hide()` 之后再调会被静默忽略。
    /// </para>
    /// </summary>
    protected virtual void OnBeforeHide()
    {
    }

    /// <summary>
    /// 弹窗诊断追踪总开关，默认关闭。
    /// <para>
    /// 本方法被 WH_MOUSE_LL 钩子回调调用（每次鼠标按下 1~3 条），属**全局低级钩子上下文**：
    /// 在那里做任何磁盘 IO（即使异步入队也有锁与队列成本）都可能让钩子超过系统的
    /// LowLevelHooksTimeout 而被静默摘除，表现正是"外点自动收起偶发失效"。
    /// 排查时设环境变量 BETTERDESKTOP_POPUP_TRACE=1 打开（此时日志走内核异步管道）。
    /// </para>
    /// </summary>
    private static readonly bool PopupTraceEnabled =
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BETTERDESKTOP_POPUP_TRACE"));

    internal static void DiagTrace(string msg)
    {
        if (!PopupTraceEnabled)
        {
            return;
        }

        // 转投内核单管道（异步 + 有界），不再直写 %LocalAppData% 的 popup-trace.log。
        BetterDesktop.Kernel.Core.DiagnosticLog.Trace("popup-diag", msg);
    }

    private void EnsureMouseHook()
    {
        // 手动收起模式不挂全局鼠标钩子：少一个低级钩子常驻，也少一条意外的收起路径。
        if (!AutoHideOnOutsideClick)
        {
            return;
        }
        _mouseHook.Start();
    }

    private void RemoveMouseHook()
    {
        _mouseHook.Stop();
    }

    private bool _suppressOutsideClickHide;

    private void OnMouseEvent(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0 || !AutoHideOnOutsideClick)
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
            var inWin = IsPointInWindow(info.pt);
            var inStrip = IsPointInMenuBarStrip(info.pt);
            DiagTrace($"click pt={info.pt.X},{info.pt.Y} inWin={inWin} inStrip={inStrip} vis={IsVisible}");
            if (!inWin && !inStrip)
            {
                DiagTrace($"OutsideClick msg=0x{msg:X} pt={info.pt.X},{info.pt.Y} -> hide");
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
            DiagTrace($"IsPointInWindow no-hwnd/rect hwnd={hwnd} pt={pt.X},{pt.Y}");
            return false;
        }
        var inside = pt.X >= rect.Left && pt.X <= rect.Right && pt.Y >= rect.Top && pt.Y <= rect.Bottom;
        DiagTrace($"IsPointInWindow pt={pt.X},{pt.Y} rect=({rect.Left},{rect.Top},{rect.Right},{rect.Bottom}) inside={inside}");
        return inside;
    }

    /// <summary>
    /// 面板背景覆盖（默认 null = 用主题令牌 ThemePanelBackground 半透明面板底）。
    /// 全透明面板（如热键侧板，用户要求"窗口属性全透明"）重写为 Brushes.Transparent，
    /// 同时去掉 CardBorderBrush 描边，文字靠自身阴影衬底可读。
    /// </summary>
    protected virtual Brush? PanelBackgroundOverride => null;

    /// <summary>
    /// 挂载面板内容并应用统一主题外观（ShellWindow 窗口属性对齐入口）。
    /// 面板外观完全由主题令牌驱动：背景 ThemePanelBackground（随窗体不透明度滑块半透明）、
    /// 描边 CardBorderBrush、圆角 SystemCornerRadius、前景 ThemeForeground（附加属性继承传导到未显式设色的子文本）。
    /// 子类重建内容（如 IME 切子视图）也应调用本方法，保证外观始终统一。
    /// </summary>
    protected void ApplyContent(FrameworkElement inner)
    {
        var transparentPanel = PanelBackgroundOverride is not null;
        // 面板根 Border：背景/描边/圆角全部走主题，随设置里的主题系统切换。
        var chrome = new Border
        {
            CornerRadius = new CornerRadius(SystemCornerRadius),
            BorderThickness = transparentPanel
                ? new Thickness(0)
                : new Thickness(AppearanceService?.CardBorderThickness ?? 1),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            Child = inner
        };
        if (transparentPanel)
        {
            chrome.Background = PanelBackgroundOverride;
        }
        else
        {
            SetThemeBinding(chrome, Border.BackgroundProperty, "ThemePanelBackground");
            SetThemeBinding(chrome, Border.BorderBrushProperty, "CardBorderBrush");
        }
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
        Hotkeys.SurfaceScopeBridge.Report(SurfaceScopeId, active: false); // 兜底：非 HidePopup 路径关闭也退作用域
        RemoveMouseHook();
        _shown = false;
        base.OnClosed(e);
    }
}
