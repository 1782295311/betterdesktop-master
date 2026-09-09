// BetterDesktop.Shell.MenuBar — 顶部菜单栏主窗口
// 分两区：左区（程序菜单/位置/下载/文档，见 MenuBarLeftZone）、右区（按 IMenuBarExtension 顺序横向排列的按钮）。
// 所有按钮的点击 → 调 OpenPopup(anchor)，由扩展自己创建/打开独立 ShellWindow（不把 UI 嵌套在本窗口里）。
// 定位：主屏工作区顶部全宽，高度 16（紧凑菜单栏，见 MenuBarMetrics）。
//
// 【本轮修正】
//   1) 定位改用 MenuBarScreen.PrimaryWorkArea（逻辑单位）：原先写死 PrimaryScreenWidth + Left=0/Top=0，
//      DPI 与任务栏占位都不参与计算；现在与 Window.Left/Top 同域，且监听 WM_DISPLAYCHANGE
//      在分辨率变化/插拔显示器后自动重排，不会出现"改完分辨率菜单栏宽度还停在旧值"。
//   2) 左区由 Text="  " 占位换成真实的 MenuBarLeftZone。

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.Core.Contracts;
using BetterDesktop.Shell.Core.Native;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.Desktop.Contracts;
using BetterDesktop.Shell.MenuBar.Contracts;
using BetterDesktop.Shell.MenuBar.Native;
using BetterDesktop.Shell.MenuBar.Services;
using BetterDesktop.Shell.Settings.Contracts;
using BetterDesktop.Shell.WindowTracker.Contracts;

namespace BetterDesktop.Shell.MenuBar.Windows;

/// <summary>顶部菜单栏主窗口（ShellWindow，全宽置顶，毛玻璃）。</summary>
internal sealed class MenuBarWindow : ShellWindow
{
    /// <summary>显示器配置变化（分辨率/缩放/插拔）后由系统广播。</summary>
    private const int WmDisplayChange = 0x007E;

    /// <summary>系统设置变化（含任务栏位置调整，会影响工作区）。</summary>
    private const int WmSettingChange = 0x001A;

    /// <summary>AppBar 回调消息（WM_APP 区间自定义）：系统经它转发 ABN_* 通知。</summary>
    private const int AppBarCallbackMessage = 0x8100;

    private readonly IReadOnlyList<IMenuBarExtension> _extensions;
    private readonly IMenuBarExtensionRegistry _registry;
    private readonly Panel _rightHost;
    private readonly Dictionary<IMenuBarExtension, FrameworkElement> _visuals = new();
    private MenuBarLeftZone? _leftZone;
    private HwndSource? _hwndSource;
    private bool _appBarRegistered;
    private readonly ISettingsService? _settings;
    private readonly System.Windows.Threading.DispatcherTimer _idleTimer = new()
    {
        // 空闲隐藏轮询：1s 粒度足够（阈值以分钟计）
        Interval = TimeSpan.FromSeconds(1)
    };
    private bool _idleHidden;

    public MenuBarWindow(
        IMenuBarExtensionRegistry registry,
        IVibrancyService vibrancy,
        IAppearanceService? appearance,
        IKernelLogger logger,
        ISettingsWindowService? settingsWindow = null,
        IWindowTrackerService? windowTracker = null,
        IDesktopBrowser? desktopBrowser = null,
        ISettingsService? settings = null,
        IEventBus? events = null)
        : base(appearance, vibrancy)
    {
        _extensions = registry.GetAll(); // assembly snapshot
        _registry = registry;
        _settings = settings;
        Title = "BetterDesktop.MenuBar";
        Height = MenuBarMetrics.MenuBarHeight;
        MinHeight = MenuBarMetrics.MenuBarHeight;
        MaxHeight = MenuBarMetrics.MenuBarHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        if (CanSetProperty("ResizeMode")) ResizeMode = ResizeMode.NoResize;
        if (CanSetProperty("ShowActivated")) ShowActivated = true;

        // 定位：一律使用逻辑单位。贴主屏顶边（AppBar edge=Top 语义），
        // 不读 WorkArea——AppBar 是工作区的定义者，读它定位自己是循环依赖。
        Reposition();

        // 根布局：ChromeBorder（供 ShellWindow 统一驱动外观） → 内部 Grid 分左区/弹簧/右区
        var chrome = new Border
        {
            CornerRadius = new CornerRadius(SystemCornerRadius),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            Background = Brushes.Transparent
        };
        ChromeBorder = chrome;

        var root = new Grid
        {
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            Background = Brushes.Transparent
        };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 左区：Logo 快捷功能菜单（三态动画图标）+ 前台窗口标题 + 位置/下载/文档 + 文件夹工具条
        _leftZone = new MenuBarLeftZone(vibrancy, appearance, settingsWindow, registry, windowTracker, desktopBrowser, settings, events)
        {
            Margin = new Thickness(8, 0, 0, 0)
        };
        Grid.SetColumn(_leftZone, 0);
        root.Children.Add(_leftZone);

        _rightHost = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 16, 0)
        };
        Grid.SetColumn(_rightHost, 1);
        root.Children.Add(_rightHost);

        chrome.Child = root;
        Content = chrome;

        // 把所有扩展点的 Visual 挂到右区，并绑定点击 → OpenPopup
        foreach (var ext in _extensions)
        {
            var visual = ext.GetVisual();
            if (visual is null)
            {
                continue;
            }
            _visuals[ext] = visual;
            visual.Margin = new Thickness(8, 0, 0, 0);
            visual.VerticalAlignment = VerticalAlignment.Center;
            // IME 扩展：左键=切换一次输入法（直接切下一个，不弹系统选择器 UI），右键=打开独立面板
            // 其他扩展：左键=打开弹窗
            if (ext.Id == "ime")
            {
                visual.MouseLeftButtonUp += (_, _) =>
                {
                    ImeLayoutEnumerator.CycleOnce();
                    // 切换后立即刷新按钮图标：模拟热键切换不改变前台窗口，事件泵不触发，
                    // 500ms 兜底轮询也可能判定快照无变化——主动刷新保证图标跟随切换。
                    if (ext is ImeMenuBarExtension imeExt) imeExt.RefreshVisual();
                };
                visual.MouseRightButtonUp += (_, _) => OnExtensionClicked(ext, visual);
            }
            else
            {
                visual.MouseLeftButtonUp += (_, _) => OnExtensionClicked(ext, visual);
            }
            _rightHost.Children.Add(visual);
        }
    }

    /// <summary>
    /// 重新摆放菜单栏（构造期与显示器变化后共用同一套算法）。
    /// 顶部 AppBar 的语义就是"贴主屏顶边"——位置由屏幕边界决定，
    /// **绝不读 WorkArea**：AppBar 自己是工作区的定义者，读它定位自己是循环依赖
    /// （坏工作区会把菜单栏推到底部且无法自愈，即"整体被压到屏幕底下"的根因）。
    /// </summary>
    private void Reposition()
    {
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
    }

    /// <summary>
    /// AppBar 定位统一入口：先摆到贴顶目标位，再向系统申请空间，并把**系统协商后**
    /// 的矩形回写窗口（物理→逻辑换算）。矩形无变化时跳过赋值，断开
    /// ABN_POSCHANGED → SETPOS → POSCHANGED 的震荡环（此前程序挂死的根因）。
    /// </summary>
    private void SyncAppBarPosition()
    {
        if (!_appBarRegistered || _hwndSource is null)
        {
            return;
        }

        Reposition(); // 先站到目标位（申请的 rc = 窗口当前矩形）
        if (AppBarReservation.TryApplyPos(_hwndSource.Handle, out var agreed))
        {
            ApplyAgreedRect(agreed);
        }
    }

    /// <summary>把系统协商后的 AppBar 矩形（物理像素）回写到窗口（逻辑单位）；有实际变化才赋值。</summary>
    private void ApplyAgreedRect(AppBarReservation.NativeRect r)
    {
        var transform = _hwndSource?.CompositionTarget?.TransformFromDevice ?? default;
        var scale = transform.M11 > 0 ? transform.M11 : 1.0;
        var left = r.Left / scale;
        var top = r.Top / scale;
        var width = (r.Right - r.Left) / scale;

        if (Math.Abs(Left - left) > 0.5 || Math.Abs(Top - top) > 0.5 || Math.Abs(Width - width) > 0.5)
        {
            Left = left;
            Top = top;
            Width = width;
        }
    }

    /// <summary>窗口句柄就绪后挂消息钩子 + 注册顶部 AppBar（桌面图标让出菜单栏空间）。</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwndSource = PresentationSource.FromVisual(this) as HwndSource;
        _hwndSource?.AddHook(WndProc);

        // 注册顶部 AppBar：explorer 自动把工作区下移，桌面图标/最大化窗口让出菜单栏条带。
        // 失败静默降级（如已被其他 AppBar 占用），菜单栏仍显示但桌面不避让（M10）。
        if (_hwndSource is not null)
        {
            _appBarRegistered = AppBarReservation.Register(_hwndSource.Handle, AppBarCallbackMessage);
            SyncAppBarPosition();
        }

        // 空闲自动隐藏（与 dock 同阈值 shell.idleHideMinutes，默认 20 分钟）：
        //   - 用户有任何输入 → 绝不隐藏（淡入恢复）；
        //   - 无输入 ≥ 阈值 → 淡出（视觉隐藏；**不 Hide 窗口**，AppBar 条带登记保留，
        //     避免反复注册/注销 AppBar 引发工作区震荡）；
        //   - 空闲期间鼠标移到屏幕顶部热区（<6px）→ 临时唤出。
        _idleTimer.Tick += (_, _) =>
        {
            try
            {
                var threshold = _settings?.Get("shell.idleHideMinutes", 20d) ?? 20d;
                var idleMinutes = GetSystemIdleMs() / 60000.0;

                // 空闲中贴顶热区 → 临时唤出
                if (_idleHidden && NativeMethods.GetCursorPos(out var pt) && pt.Y < 6)
                {
                    SetIdleHidden(false);
                    return;
                }

                if (idleMinutes >= threshold)
                {
                    SetIdleHidden(true);
                }
                else if (_idleHidden)
                {
                    // 用户恢复操作：立即淡入
                    SetIdleHidden(false);
                }
            }
            catch
            {
                // 轮询失败不阻断（M10）
            }
        };
        _idleTimer.Start();
    }

    /// <summary>空闲隐藏切换：淡出保留窗口（AppBar 登记），淡入恢复交互。</summary>
    private void SetIdleHidden(bool hidden)
    {
        if (hidden == _idleHidden)
        {
            return;
        }

        _idleHidden = hidden;
        IsHitTestVisible = !hidden;

        // 隐藏时挂起 DWM 材质（毛玻璃/亚克力）：窗口仍存在（AppBar 登记保留），
        // 但 DWM 背景不随 Opacity 淡出 → 不挂起就会在顶部原地残留一条玻璃带。
        // 显现时自动按当前主题材质恢复（见 ShellWindow.SetMaterialSuspended）。
        SetMaterialSuspended(hidden);

        // ⚠️ 必须先写基值再用 FillBehavior.Stop 过渡：
        //    若只播 1→0 动画而不改基值，Stop 会在动画结束露出基值 1，
        //    Opacity 弹回 → "菜单栏根本没隐藏"（实测回归）。
        //    正确姿势：基值=目标值，动画从旧值过渡到基值，结束无跳变、无锁定。
        var from = Opacity;
        Opacity = hidden ? 0 : 1;
        var anim = new System.Windows.Media.Animation.DoubleAnimation(
            from, hidden ? 0 : 1, TimeSpan.FromMilliseconds(300))
        {
            FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop
        };
        BeginAnimation(OpacityProperty, anim);
    }

    /// <summary>系统级用户空闲毫秒数（最后一次鼠标/键盘输入至今；GetLastInputInfo）。</summary>
    private static double GetSystemIdleMs()
    {
        var info = new NativeMethods.LASTINPUTINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.LASTINPUTINFO>() };
        return NativeMethods.GetLastInputInfo(ref info)
            ? unchecked(Environment.TickCount - (int)info.dwTime)
            : 0; // 检测失败按"刚有输入"处理 → 不隐藏
    }


    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmDisplayChange || msg == WmSettingChange)
        {
            // 分辨率/缩放/任务栏位置变了：重新贴顶 + 重新申请 AppBar 空间（协商制，不会震荡）。
            SyncAppBarPosition();
            // 已打开的弹窗锚点会失效，收起它们（IMenuBarExtension.ClosePopup 是契约的一部分）
            foreach (var ext in _extensions)
            {
                ext.ClosePopup();
            }
        }
        else if (msg == AppBarCallbackMessage && unchecked((uint)wParam.ToInt64()) == AppBarReservation.AbnPosChanged)
        {
            // 系统通知工作区变化（如其他 AppBar 增删）：重新贴顶 + 重新申请。
            // SyncAppBarPosition 内"协商 rc 无变化则跳过赋值"保证这里最多执行一轮，不会震荡。
            SyncAppBarPosition();
        }

        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_hwndSource is not null)
        {
            if (_appBarRegistered)
            {
                // 必须注销：否则顶部预留空间在退出后仍被占用（桌面图标回不来）。
                AppBarReservation.Unregister(_hwndSource.Handle);
                _appBarRegistered = false;
            }
            _hwndSource.RemoveHook(WndProc);
            _hwndSource = null;
        }
        // B2：先停空闲轮询——否则窗口关闭后 Timer 仍每秒 Tick，闭包持续引用已关窗口。
        _idleTimer.Stop();
        _leftZone?.Dispose(); // 退订前台窗口事件
        _leftZone = null;
        base.OnClosed(e);
    }

    private void OnExtensionClicked(IMenuBarExtension ext, FrameworkElement visual)
    {
        // 传 visual 本身而非预先算好的坐标：物理像素 → 逻辑单位的换算由 PopupAnchor 内部处理，
        // 调用方不接触物理像素，避免"高 DPI 下弹窗整体偏移"这类单位混用问题。
        ext.OpenPopup(visual.PointToScreen(new Point(0, 0)));
    }
}
