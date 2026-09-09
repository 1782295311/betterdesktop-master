// BetterDesktop.Shell.MenuBar — 左区 Logo 快捷功能菜单（参照 CairoShell CairoMenu 裁剪为本程序快捷功能）
// 菜单分三组：
//   [本程序] 关于 / 设置（ISettingsWindowService）/ 应用提取器（IEventBus shell.appgrabber.show → shell.dock）
//   [系统入口] Windows 控制面板 / Windows 设置 / 运行 / 任务管理器
//   [电源与 session] 锁定 / 注销 / 重启 / 关机 / 退出 BetterDesktop
// 面板继承 MenuBarPopupWindow（失焦自动收起）；收起时经 Hidden 回调驱动左区图标反向动画回常态。

using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.MenuBar.Contracts;
using BetterDesktop.Shell.Settings.Contracts;

namespace BetterDesktop.Shell.MenuBar.Windows;

/// <summary>左区 Logo 快捷功能菜单。</summary>
internal sealed class LogoMenuWindow : MenuBarPopupWindow
{
    private const double DefaultWidth = 220;

    private readonly ISettingsWindowService? _settingsWindow;
    private readonly IEventBus? _events;

    /// <summary>true = 显示"关于"子视图（窗口内切换，替代系统 MessageBox——窗口属性统一走基类外观）。</summary>
    private bool _aboutMode;

    /// <summary>面板隐藏（失焦/点击外部/Esc）后触发，供左区图标回常态动画。</summary>
    public event Action? Hidden;

    public LogoMenuWindow(ISettingsWindowService? settingsWindow, IVibrancyService vibrancy, IAppearanceService? appearance = null, IEventBus? events = null)
        : base(vibrancy, appearance)
    {
        _settingsWindow = settingsWindow;
        _events = events;
        Width = DefaultWidth;
        MinWidth = DefaultWidth;
        SizeToContent = SizeToContent.Height;
        IsVisibleChanged += (_, e) =>
        {
            if (!(bool)e.NewValue)
            {
                _aboutMode = false; // 复位子视图：下次打开显示主菜单
                Hidden?.Invoke();
            }
        };
    }

    public FrameworkElement BuildPreviewContent() => BuildContent();

    protected override FrameworkElement BuildContent()
    {
        if (_aboutMode)
        {
            return BuildAboutContent();
        }

        var root = new Border
        {
            Padding = new Thickness(4, 4, 4, 6),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };

        var column = new StackPanel { Orientation = Orientation.Vertical };

        // —— 本程序 ——
        // "关于"是窗口内子视图切换：hideOnClick=false，点击后就地切到关于页（不收起面板）。
        column.Children.Add(CreateItem("关于 BetterDesktop", () =>
        {
            _aboutMode = true;
            RebuildContent();
        }, hideOnClick: false));
        column.Children.Add(CreateItem("设置", () => _settingsWindow?.Show()));
        // 应用提取器在 shell.dock 包内（跨包不经类型引用）：经 IEventBus 契约由 DockPlugin 打开。
        column.Children.Add(CreateItem("应用提取器", () =>
        {
            try { _ = _events?.EmitAsync<string>("shell.appgrabber.show", string.Empty); }
            catch { /* 事件发送失败静默（M10） */ }
        }));

        column.Children.Add(CreateSeparator());

        // —— 系统入口 ——
        column.Children.Add(CreateItem("Windows 控制面板", () => Start("control.exe")));
        column.Children.Add(CreateItem("Windows 设置", () => Start("ms-settings:")));
        column.Children.Add(CreateItem("运行……", () => Start("shell:::{2559a1f3-21d7-11d4-bdaf-00c04f60b9f0}")));
        column.Children.Add(CreateItem("任务管理器", () => Start("taskmgr.exe")));

        column.Children.Add(CreateSeparator());

        // —— 电源与会话 ——
        column.Children.Add(CreateItem("锁定", () => Start("rundll32.exe", "user32.dll,LockWorkStation")));
        column.Children.Add(CreateItem("注销……", () => Start("shutdown.exe", "/l")));
        column.Children.Add(CreateItem("重启……", () => Start("shutdown.exe", "/r /t 0")));
        column.Children.Add(CreateItem("关机……", () => Start("shutdown.exe", "/s /t 0")));

        column.Children.Add(CreateSeparator());

        column.Children.Add(CreateItem("退出 BetterDesktop……", () => Application.Current.Shutdown()));

        root.Child = column;
        return root;
    }

    /// <summary>按当前模式重建内容并应用统一主题外观（背景/描边/前景走基类）。</summary>
    private void RebuildContent()
    {
        ApplyContent(BuildContent());
    }

    /// <summary>
    /// "关于"子视图（窗口内切换，走统一基类外观——不用系统 MessageBox）：
    /// 返回栏 + 程序名 + 版本 + 一句话描述。
    /// </summary>
    private FrameworkElement BuildAboutContent()
    {
        var root = new Border
        {
            Padding = new Thickness(6, 6, 6, 10),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };

        var column = new StackPanel { Orientation = Orientation.Vertical };

        // 顶栏：返回 + 标题
        var header = new Grid { Height = 32, Margin = new Thickness(2, 0, 2, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var back = CreateItem("‹ 返回", () =>
        {
            _aboutMode = false;
            RebuildContent();
        }, hideOnClick: false);
        back.Height = 28;
        back.Margin = new Thickness(0);
        Grid.SetColumn(back, 0);
        header.Children.Add(back);
        var title = new TextBlock
        {
            Text = "关于",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0)
        };
        Grid.SetColumn(title, 1);
        header.Children.Add(title);
        column.Children.Add(header);

        column.Children.Add(CreateSeparator());

        // 程序名 + 版本 + 描述
        var name = new TextBlock
        {
            Text = "BetterDesktop",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(10, 10, 0, 2)
        };
        column.Children.Add(name);

        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        var ver = new TextBlock
        {
            Text = $"版本 {version?.ToString(3) ?? "1.0"}",
            FontSize = 11.5,
            Margin = new Thickness(10, 0, 0, 8)
        };
        SetThemeBinding(ver, TextBlock.ForegroundProperty, "ThemeMutedForeground");
        column.Children.Add(ver);

        var desc = new TextBlock
        {
            Text = "Cordis 风格插件内核的 Windows 桌面外壳。",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(10, 0, 10, 6)
        };
        column.Children.Add(desc);

        root.Child = column;
        return root;
    }

    /// <summary>一条菜单行：紧凑行 + hover 高亮。默认点击后收起面板（子视图内导航项传 hideOnClick=false）。</summary>
    private FrameworkElement CreateItem(string label, Action onClick, bool hideOnClick = true)
    {
        var row = new Border
        {
            Height = 30,
            Margin = new Thickness(2, 1, 2, 1),
            Padding = new Thickness(10, 0, 8, 0),
            CornerRadius = new CornerRadius(6),
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent
        };
        var text = new TextBlock
        {
            Text = label,
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        row.Child = text;

        row.MouseEnter += (_, _) => row.Background = MenuBarTheme.Hover;
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonDown += (_, _) => row.Background = MenuBarTheme.Pressed;
        row.MouseLeftButtonUp += (_, _) =>
        {
            if (hideOnClick)
            {
                Hide();
            }
            try { onClick(); }
            catch { /* 命令执行失败静默（M10） */ }
        };
        return row;
    }

    private static Border CreateSeparator()
    {
        var sep = new Border { Height = 1, Margin = new Thickness(8, 5, 8, 5) };
        SetThemeBinding(sep, Border.BackgroundProperty, "ThemeSeparator");
        return sep;
    }

    /// <summary>ShellExecute 启动（程序/URI/桌面协议）。失败静默（M10）。</summary>
    private static void Start(string fileName, string? arguments = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo(fileName, arguments ?? string.Empty) { UseShellExecute = true });
        }
        catch
        {
            // 启动失败静默，不打断菜单
        }
    }
}
