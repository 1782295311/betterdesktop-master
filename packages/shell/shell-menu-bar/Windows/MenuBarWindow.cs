// BetterDesktop.Shell.MenuBar — 顶部菜单栏主窗口
// 分两区：左区（程序菜单/位置/下载/文档占位）、右区（按 IMenuBarExtension 顺序横向排列的按钮）。
// 所有按钮的点击 → 调 OpenPopup(anchor)，由扩展自己创建/打开独立 ShellWindow（不把 UI 嵌套在本窗口里）。
// 定位：主屏工作区顶部全宽，高度 16（紧凑菜单栏）。

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.MenuBar.Contracts;
using BetterDesktop.Shell.MenuBar.Services;

namespace BetterDesktop.Shell.MenuBar.Windows;

/// <summary>顶部菜单栏主窗口（ShellWindow，全宽置顶，毛玻璃）。</summary>
internal sealed class MenuBarWindow : ShellWindow
{
    private readonly IReadOnlyList<IMenuBarExtension> _extensions;
    private readonly Panel _rightHost;
    private readonly Dictionary<IMenuBarExtension, FrameworkElement> _visuals = new();

    public MenuBarWindow(
        IReadOnlyList<IMenuBarExtension> extensions,
        IVibrancyService vibrancy,
        IAppearanceService? appearance,
        IKernelLogger logger)
        : base(appearance, vibrancy)
    {
        _extensions = extensions;
        Title = "BetterDesktop.MenuBar";
        Height = MenuBarMetrics.MenuBarHeight;
        MinHeight = MenuBarMetrics.MenuBarHeight;
        MaxHeight = MenuBarMetrics.MenuBarHeight;
        Width = SystemParameters.PrimaryScreenWidth;
        Left = 0;
        Top = 0;
        WindowStartupLocation = WindowStartupLocation.Manual;
        if (CanSetProperty("ResizeMode")) ResizeMode = ResizeMode.NoResize;
        if (CanSetProperty("ShowActivated")) ShowActivated = true;

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

        var leftZone = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 0, 0)
        };
        leftZone.Children.Add(new TextBlock
        {
            Text = "  ", // 左区占位：后续 CairoMenu/程序菜单在此实现；本轮不放硬编码菜单名
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 9,
            Foreground = ForegroundForBackground()
        });
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
            // IME 扩展：左键=切换一次输入法（Win+Space），右键=打开独立面板
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

    private Brush ForegroundForBackground()
    {
        // 菜单栏背景透明，文字颜色按工作区桌面平均色自适应；
        // 这里先给中性灰白，后续 AppearanceService 会统一覆盖主题色（Readme 规则：透明背景下深→白字/浅→黑字）。
        return Brushes.White;
    }

    private void OnExtensionClicked(IMenuBarExtension ext, FrameworkElement visual)
    {
        // 从按钮左上角取屏幕坐标：视觉相对点(0,Height)=按钮下沿，用于锚定
        var topLeftScreen = visual.PointToScreen(new Point(0, 0));
        ext.OpenPopup(topLeftScreen);
    }
}
