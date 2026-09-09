using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BetterDesktop.Shell.AppSource.Models;
using BetterDesktop.Shell.ContextMenus.Services;
using BetterDesktop.Shell.Recent.Contracts;
using BetterDesktop.Shell.Search.Contracts;
using BetterDesktop.Shell.StartMenu.Contracts;
using BetterDesktop.Shell.StartMenu.Services;

namespace BetterDesktop.Shell.StartMenu.Windows.Layouts;

// ── 本文件方法级白话索引（Win11 风格开始菜单布局，白话 → 方法）──
//   "构建整个 Win11 布局"                → BuildLayout
//   "刷新固定/所有应用条目"              → RefreshItems
//   "把搜索结果渲染到布局"              → RenderResults
//   "最近使用栏（芯片条）"              → BuildRecentBar / PopulateRecentBar / CreateRecentChip
//   "异步加载应用图标"                  → LoadIconAsync
//   B7 缺陷点：行/悬停宿主的 MouseLeftButtonUp 应走 ActivateOrLaunch（L186/L199 已改 AttachNative，L179/L192 待核）。
//   其他布局对照：Win10Layout/Win7Layout/ClassicLayout/AllAppsLayout（同目录，同一 IStartMenuLayoutProvider 契约）。
// ────────────────────────────────────

/// <summary>
/// Win11 样式布局：复用 Win7 经典两栏基础布局，仅在顶部追加一条「最近应用」功能栏
/// （这是它相对 Win7 唯一多出的东西）。左栏程序（固定/最近/所有程序树）+ 右栏用户/系统链接/电源
/// 全部交给内部 Win7 布局实例渲染；搜索/主机契约同样委托给它。最近应用横条跨全宽放最上方。
/// </summary>
public sealed class Win11Layout : IStartMenuLayoutProvider, IStartMenuLayoutHost
{
    private IStartMenuDataService? _service;
    private StartMenuPalette _palette = null!;
    private Win7Layout? _inner;          // 内部 Win7 两栏布局（承载搜索/系统链接/电源）
    private FrameworkElement? _recentHost; // 最近应用栏（搜索时折叠）

    /// <inheritdoc />
    public string Name => "win11";

    /// <inheritdoc />
    public TextBox SearchBox => _inner?.SearchBox ?? throw new InvalidOperationException("布局尚未构建");

    /// <inheritdoc />
    public ListBox ResultsList => _inner?.ResultsList ?? throw new InvalidOperationException("布局尚未构建");

    /// <inheritdoc />
    public FrameworkElement BuildLayout(IStartMenuDataService service)
    {
        _service = service;
        _palette = StartMenuPalette.From(service.ThemeTokens);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                // 最近应用栏
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Win7 两栏

        _recentHost = BuildRecentBar();
        Grid.SetRow(_recentHost, 0);
        root.Children.Add(_recentHost);

        _inner = new Win7Layout();
        var baseLayout = _inner.BuildLayout(service);
        Grid.SetRow(baseLayout, 1);
        root.Children.Add(baseLayout);

        var outer = new Border
        {
            CornerRadius = new CornerRadius(_palette.CornerRadius),
            Background = _palette.Surface,
            Padding = new Thickness(2),
            Child = root
        };
        return outer;
    }

    /// <inheritdoc />
    public void RefreshItems()
    {
        // 整窗重建已覆盖（StartMenuWindow.RebuildContent）；内部 Win7 清空即可。
        _inner?.RefreshItems();
    }

    /// <inheritdoc />
    public void RenderResults(string query, IReadOnlyList<SearchResult> results)
    {
        // 搜索时叠住最近应用栏，仅在内容区展示结果。
        var hasQuery = !string.IsNullOrWhiteSpace(query);
        if (_recentHost is not null)
        {
            _recentHost.Visibility = hasQuery ? Visibility.Collapsed : Visibility.Visible;
        }

        _inner?.RenderResults(query, results);
    }

    // ===== 最近应用功能栏 =====

    /// <summary>顶部「最近应用」横条：小节标题 + 横向胶囊（图标 + 名称），从 GetRecentPrograms 取。</summary>
    private FrameworkElement BuildRecentBar()
    {
        var host = new Border
        {
            Background = _palette.SurfaceAlt,
            Padding = new Thickness(12, 8, 12, 8),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = _palette.Separator
        };

        var grid = new Grid { Margin = new Thickness(4, 0, 4, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var title = new TextBlock
        {
            Text = "最近应用",
            FontSize = _service?.ThemeTokens?.FontSizeCaption ?? 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
            Foreground = _palette.Muted
        };
        Grid.SetColumn(title, 0);
        grid.Children.Add(title);

        var panel = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        PopulateRecentBar(panel);
        Grid.SetColumn(panel, 1);
        grid.Children.Add(panel);

        host.Child = grid;
        return host;
    }

    private void PopulateRecentBar(WrapPanel panel)
    {
        var items = _service!.GetRecentPrograms(8);
        foreach (var item in items)
        {
            if (item.AppItem is not { } app)
            {
                continue;
            }

            panel.Children.Add(CreateRecentChip(app, item.Name));
        }
    }

    /// <summary>最近应用胶囊：圆角块内 [20 图标 + 名称]，宽高自适应，悬浮高亮。</summary>
    private FrameworkElement CreateRecentChip(AppItem app, string displayName)
    {
        var row = new Grid
        {
            Cursor = Cursors.Hand,
            Tag = app,
            Margin = new Thickness(4, 2, 4, 2),
            Height = 36
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new Image
        {
            Width = 20,
            Height = 20,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        var iconPad = new Grid { Width = 28, Margin = new Thickness(8, 0, 0, 0) };
        iconPad.Children.Add(icon);
        Grid.SetColumn(iconPad, 0);
        row.Children.Add(iconPad);

        var name = new TextBlock
        {
            Text = displayName,
            FontSize = _service?.ThemeTokens?.FontSizeBody ?? 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 12, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = _palette.Foreground
        };
        Grid.SetColumn(name, 1);
        row.Children.Add(name);

        // B7 修复：左键启动与原生右键菜单只在外层 hoverHost 订阅一层。
        // 原先内层 row 与外层 hoverHost 同时订阅，WPF 路由事件冒泡导致一次点击执行两次
        // ActivateOrLaunch（非单实例应用被启动两个实例）、右键菜单弹两次。row 填满 hoverHost，
        // 事件必然冒泡到外层，故内层不再订阅；外层 handler 置 Handled 阻断继续冒泡。
        var hoverHost = new Border { CornerRadius = new CornerRadius(6), Background = _palette.Tile11, Child = row };
        hoverHost.MouseEnter += (_, _) => hoverHost.Background = _palette.RowHover;
        hoverHost.MouseLeave += (_, _) => hoverHost.Background = _palette.Tile11;
        hoverHost.MouseLeftButtonUp += (_, e) =>
        {
            _service?.ActivateOrLaunch(app);
            _service?.Hide();
            e.Handled = true;
        };
        if (_service is not null)
        {
            AppItemActions.AttachNative(hoverHost, app);
        }

        _ = LoadIconAsync(app, icon);
        return hoverHost;
    }

    /// <summary>异步加载应用图标（真图标高清路线）；失败静默。</summary>
    private async System.Threading.Tasks.Task LoadIconAsync(AppItem app, Image target)
    {
        try
        {
            var icon = await _service!.GetIconAsync(app, CancellationToken.None);
            if (icon is not null)
            {
                target.Source = icon;
            }
        }
        catch
        {
            // 图标失败静默（M10）。
        }
    }
}
