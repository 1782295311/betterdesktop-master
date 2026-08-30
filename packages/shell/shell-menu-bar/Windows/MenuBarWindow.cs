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
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.MenuBar.Contracts;
using BetterDesktop.Shell.MenuBar.Services;
using BetterDesktop.Shell.StartMenu.Contracts;

namespace BetterDesktop.Shell.MenuBar.Windows;

/// <summary>顶部菜单栏主窗口（ShellWindow，全宽置顶，毛玻璃）。</summary>
internal sealed class MenuBarWindow : ShellWindow
{
    /// <summary>显示器配置变化（分辨率/缩放/插拔）后由系统广播。</summary>
    private const int WmDisplayChange = 0x007E;

    /// <summary>系统设置变化（含任务栏位置调整，会影响工作区）。</summary>
    private const int WmSettingChange = 0x001A;

    private readonly IReadOnlyList<IMenuBarExtension> _extensions;
    private readonly Panel _rightHost;
    private readonly Dictionary<IMenuBarExtension, FrameworkElement> _visuals = new();
    private HwndSource? _hwndSource;

    public MenuBarWindow(
        IReadOnlyList<IMenuBarExtension> extensions,
        IVibrancyService vibrancy,
        IAppearanceService? appearance,
        IKernelLogger logger,
        IStartMenuService? startMenu = null)
        : base(appearance, vibrancy)
    {
        _extensions = extensions;
        Title = "BetterDesktop.MenuBar";
        Height = MenuBarMetrics.MenuBarHeight;
        MinHeight = MenuBarMetrics.MenuBarHeight;
        MaxHeight = MenuBarMetrics.MenuBarHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        if (CanSetProperty("ResizeMode")) ResizeMode = ResizeMode.NoResize;
        if (CanSetProperty("ShowActivated")) ShowActivated = true;

        // 定位：一律使用逻辑单位。MenuBarScreen.PrimaryWorkArea 来自 SystemParameters.WorkArea，
        // 与 Window.Left/Top/Width 同域（都是 DIP），不受 DPI 缩放影响。
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

        // 左区：程序菜单 + 位置/下载/文档（服务缺失时该项自动不呈现）
        var leftZone = new MenuBarLeftZone(startMenu)
        {
            Margin = new Thickness(8, 0, 0, 0)
        };
        Grid.SetColumn(leftZone, 0);
        root.Children.Add(leftZone);

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
        foreach (var ext in extensions)
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
                visual.MouseLeftButtonUp += (_, _) => ImeLayoutEnumerator.CycleOnce();
                visual.MouseRightButtonUp += (_, _) => OnExtensionClicked(ext, visual);
            }
            else
            {
                visual.MouseLeftButtonUp += (_, _) => OnExtensionClicked(ext, visual);
            }
            _rightHost.Children.Add(visual);
        }
    }

    /// <summary>按当前主屏工作区重新摆放菜单栏（构造期与显示器变化后共用同一套算法）。</summary>
    private void Reposition()
    {
        var area = MenuBarScreen.PrimaryWorkArea;
        Left = area.Left;
        Top = area.Top;
        Width = area.Width;
    }

    /// <summary>窗口句柄就绪后挂消息钩子，监听显示器/系统设置变化以自动重排。</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwndSource = PresentationSource.FromVisual(this) as HwndSource;
        _hwndSource?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmDisplayChange || msg == WmSettingChange)
        {
            // 分辨率/缩放/任务栏位置变了：工作区随之改变，必须重算，否则菜单栏宽度停留在旧值。
            Reposition();
            // 已打开的弹窗锚点会失效，收起它们（IMenuBarExtension.ClosePopup 是契约的一部分）
            foreach (var ext in _extensions)
            {
                ext.ClosePopup();
            }
        }

        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_hwndSource is not null)
        {
            _hwndSource.RemoveHook(WndProc);
            _hwndSource = null;
        }
        base.OnClosed(e);
    }

    private void OnExtensionClicked(IMenuBarExtension ext, FrameworkElement visual)
    {
        // 传 visual 本身而非预先算好的坐标：物理像素 → 逻辑单位的换算由 PopupAnchor 内部处理，
        // 调用方不接触物理像素，避免"高 DPI 下弹窗整体偏移"这类单位混用问题。
        ext.OpenPopup(visual.PointToScreen(new Point(0, 0)));
    }
}
