// BetterDesktop.Shell.MenuBar — 扩展中心独立弹出面板（管理外部扩展功能插件）
// 「+」按钮打开：列出 ExtensionCatalog 中的条目（菜单栏内建模块 + 外部扩展功能插件），
// 每个条目带开关——启用态持久化于 ISettingsService（extensions.<id>.enabled）；
// 映射到菜单栏按钮的项，开关可直接控制该按钮实时显隐；外部扩展仅持久化意图（需内核支持后接入）。

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.MenuBar.Contracts;
using BetterDesktop.Shell.MenuBar.Status;
using BetterDesktop.Shell.Settings.Contracts;

namespace BetterDesktop.Shell.MenuBar.Windows;

/// <summary>
/// 扩展中心面板：管理外部扩展功能插件与菜单栏模块。
/// </summary>
internal sealed class ExtensionsCenterWindow : MenuBarPopupWindow
{
    private const double PanelWidth = 320;
    private const double ListMaxHeight = 380;

    /// <summary>已落地的外部扩展（开关可真实启停）；其余为规划条目，开关先持久化意图。</summary>
    private static readonly HashSet<string> Implemented = new() { "quick-note", "programs-menu" };

    private readonly ISettingsService? _settings;
    private readonly Action<MenuBarStatusButtonId, bool>? _applyVisibility;

    public ExtensionsCenterWindow(
        ISettingsService? settings,
        Action<MenuBarStatusButtonId, bool>? applyMenuBarVisibility,
        IVibrancyService vibrancy,
        IAppearanceService? appearance = null)
        : base(vibrancy, appearance)
    {
        _settings = settings;
        _applyVisibility = applyMenuBarVisibility;
        Width = PanelWidth;
        MinWidth = PanelWidth;
        SizeToContent = SizeToContent.Height;
    }

    /// <summary>Playground/大容器 预览入口：直接取内容 UI（不走 ShellWindow 生命周期）。</summary>
    public FrameworkElement BuildPreviewContent() => BuildContent();

    protected override FrameworkElement BuildContent()
    {
        var root = new Border
        {
            Padding = new Thickness(14),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };

        var column = new StackPanel { Orientation = Orientation.Vertical };

        var title = new TextBlock
        {
            Text = "扩展中心",
            Foreground = MenuBarTheme.Foreground,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold
        };
        column.Children.Add(title);
        column.Children.Add(new Border { Height = 10 });

        // 【职责边界】扩展中心**只管外部扩展功能插件**。
        // 菜单栏系统功能（CPU/内存/音量/电池…）的显隐已移至「设置 → 菜单栏」，不再出现在这里。
        var list = new StackPanel { Orientation = Orientation.Vertical };
        foreach (var ext in ExtensionCatalog.External)
        {
            list.Children.Add(BuildRow(ext));
        }

        var scroller = new ScrollViewer
        {
            Content = list,
            MaxHeight = ListMaxHeight,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        column.Children.Add(scroller);

        root.Child = column;
        return root;
    }

    private FrameworkElement BuildRow(ExtensionDescriptor ext)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 图标字符块
        var tile = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        tile.Child = new TextBlock
        {
            Text = ext.Glyph,
            Foreground = MenuBarTheme.Foreground,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        Grid.SetColumn(tile, 0);

        // 名称 + 描述（+ 外部标记）
        var text = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };
        text.Children.Add(new TextBlock
        {
            Text = ext.Name,
            Foreground = MenuBarTheme.Foreground,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold
        });
        text.Children.Add(new TextBlock
        {
            Text = ext.Description,
            Foreground = new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)),
            FontSize = 10,
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });
        // 已落地的扩展可真实启停；其余为规划条目，开关先持久化意图（内核接入后自动生效）。
        text.Children.Add(new TextBlock
        {
            Text = Implemented.Contains(ext.Id) ? "已接入 · 开关立即生效" : "规划中 · 开关将保存你的选择",
            Foreground = new SolidColorBrush(Color.FromArgb(130, 255, 255, 255)),
            FontSize = 9,
            Margin = new Thickness(0, 2, 0, 0)
        });
        Grid.SetColumn(text, 1);

        // 开关：启用态持久化；映射到菜单栏按钮的项实时显隐。
        // 外部扩展**默认关闭**（它们是可选能力，不该一上来就往桌面加东西——
        // 此前 quick-note 默认 true，导致启动后桌面上凭空多出一个笔记浮窗）。
        var toggle = new ToggleSwitch { IsOn = _settings?.Get(ext.SettingsKey, false) ?? false };
        toggle.Toggled += (_, _) =>
        {
            bool on = toggle.IsOn;
            _settings?.Set(ext.SettingsKey, on);
            if (ext.MenuBarButton is { } btnId)
            {
                _applyVisibility?.Invoke(btnId, on);
            }
        };
        Grid.SetColumn(toggle, 2);

        grid.Children.Add(tile);
        grid.Children.Add(text);
        grid.Children.Add(toggle);
        return grid;
    }
}
