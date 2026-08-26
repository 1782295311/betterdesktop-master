using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BetterDesktop.Shell.AppSource.Models;
using BetterDesktop.Shell.Search.Contracts;
using BetterDesktop.Shell.StartMenu.Contracts;
using BetterDesktop.Shell.StartMenu.Services;

namespace BetterDesktop.Shell.StartMenu.Windows.Layouts;

/// <summary>
/// 所有应用布局（第二种布局，Dock「所有应用」入口经 IEventBus 切换到本视图）：
/// 左栏 = 搜索框 + 全部应用分组列表 + 底部「返回/总数」条；右栏 = 栏目扩展点（复用 ClassicLayout.BuildRightPanel）。
/// 分组：应用按首字母 A-Z 分组（中文/符号等非拉丁首字符统一归入 “#”），右缘字母索引条悬停/点击/拖动跳转。
/// 性能：列表使用虚拟化 ListBox（回收模式），应用很多时只渲染可视行，滚动流畅。
/// 程序项支持左键启动、右键菜单（固定/管理员/位置/卸载）。底色/强调/文字色由 StartMenuPalette 换算。
/// </summary>
public sealed class AllAppsLayout : IStartMenuLayoutProvider, IStartMenuLayoutHost
{
    private StartMenuService? _service;
    private StartMenuPalette _palette = null!;
    private ListBox? _appsList;
    private readonly Dictionary<string, FrameworkElement> _groupHeaders = new(); // 字母 → 分组头元素（滚动定位用）
    private FrameworkElement? _indexOverlay;                                     // 右缘字母索引条（搜索时隐藏）

    /// <inheritdoc />
    public string Name => "allapps";

    /// <summary>搜索输入框。</summary>
    public TextBox SearchBox { get; private set; } = null!;

    /// <summary>全部应用 / 搜索结果列表。</summary>
    public ListBox AppsList => _appsList!;

    /// <inheritdoc />
    public FrameworkElement BuildLayout(StartMenuService service)
    {
        _service = service;
        _palette = StartMenuPalette.From(service.ThemeTokens);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // ---- 左栏：搜索 + 分组列表 + 底部条 ----
        var left = new Grid { Margin = new Thickness(16) };
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                // 搜索框
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 列表
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                // 底部条

        SearchBox = new TextBox
        {
            FontSize = _service.ThemeTokens?.FontSizeInput ?? 14,
            Padding = new Thickness(6, 5, 6, 5),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        Grid.SetRow(SearchBox, 0);
        left.Children.Add(SearchBox);

        var allApps = service.GetAllApps();

        _appsList = new ListBox
        {
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Margin = new Thickness(0, 6, 0, 0),
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        // 虚拟化：回收可视行之外的应用行，滚动更流畅（应尽量多且行高统一）。
        VirtualizingPanel.SetIsVirtualizing(_appsList, true);
        VirtualizingPanel.SetVirtualizationMode(_appsList, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(_appsList, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(_appsList, ScrollBarVisibility.Disabled);
        _appsList.ItemContainerStyle = BuildContainerStyle();
        _appsList.KeyDown += OnListKeyDown;
        _appsList.MouseDoubleClick += (_, _) => LaunchSelected();
        PopulateAllApps(allApps);

        // 列表 + 右缘字母索引覆盖层（只在有分组时叠加）。
        var listWrap = new Grid();
        listWrap.Children.Add(_appsList);
        var overlay = BuildAlphabetOverlay();
        if (overlay is not null)
        {
            overlay.HorizontalAlignment = HorizontalAlignment.Right;
            overlay.VerticalAlignment = VerticalAlignment.Stretch;
            _indexOverlay = overlay;
            listWrap.Children.Add(overlay);
        }

        Grid.SetRow(listWrap, 1);
        left.Children.Add(listWrap);

        var footer = BuildFooter(allApps.Count);
        Grid.SetRow(footer, 2);
        left.Children.Add(footer);

        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        // ---- 右栏：栏目扩展点 ----
        var right = ClassicLayout.BuildRightPanel(service);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);

        var outer = new Border
        {
            CornerRadius = new CornerRadius(_palette.CornerRadius),
            Padding = new Thickness(1),
            Child = grid
        };
        return outer;
    }

    // ===== 列表项 / 分组 / 索引 =====

    /// <summary>ListBox 容器去系统高亮：行元素自身带悬浮/点击反馈，容器仅承载且横向拉伸。</summary>
    private Style BuildContainerStyle()
    {
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));

        var selected = new Trigger
        {
            Property = ListBoxItem.IsSelectedProperty,
            Value = true
        };
        selected.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Triggers.Add(selected);

        var hover = new Trigger
        {
            Property = Control.IsMouseOverProperty,
            Value = true
        };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Triggers.Add(hover);

        return style;
    }

    /// <summary>按首字母 A-Z（非拉丁归 “#”）填入分组头 + 应用行。</summary>
    private void PopulateAllApps(IReadOnlyList<AppItem> apps)
    {
        if (_appsList is null)
        {
            return;
        }

        _appsList.Items.Clear();
        _groupHeaders.Clear();

        var all = apps ?? Array.Empty<AppItem>();
        string currentGroup = string.Empty;
        foreach (var app in all)
        {
            var letter = StartMenuAppRowBuilder.NormalizeGroupLetter(app.Name);
            if (!string.Equals(letter, currentGroup, StringComparison.Ordinal))
            {
                currentGroup = letter;
                var header = BuildGroupHeader(letter);
                _groupHeaders[letter] = header;
                _appsList.Items.Add(header);
            }

            _appsList.Items.Add(StartMenuAppRowBuilder.BuildAppRow(app, app.Name, _service!, _palette));
        }
    }

    /// <summary>字母分组标题（小号、次要色、带上下留白）。</summary>
    private FrameworkElement BuildGroupHeader(string letter)
        => new TextBlock
        {
            Text = letter,
            FontSize = _service?.ThemeTokens?.FontSizeSmall ?? 11,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(6, 12, 0, 4),
            Foreground = _palette.Muted
        };

    /// <summary>右缘字母索引条：仅列实际存在的分组字母，悬停即跳转、按住拖动顺滑扫过。</summary>
    private FrameworkElement? BuildAlphabetOverlay()
    {
        if (_groupHeaders.Count == 0)
        {
            return null;
        }

        var strip = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(0, 12, 4, 12),
            IsHitTestVisible = true
        };
        // 默认透明不遮挡右侧内容；指针进入索引条才显浅色贴纸底。
        var overlay = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(12),
            Margin = new Thickness(0, 6, 0, 6),
            Child = strip
        };
        strip.MouseEnter += (_, _) => overlay.Background = _palette.SurfaceAlt;
        strip.MouseLeave += (_, _) => overlay.Background = Brushes.Transparent;

        foreach (var letter in _groupHeaders.Keys)
        {
            if (string.IsNullOrEmpty(letter))
            {
                continue;
            }

            var btn = new TextBlock
            {
                Text = letter,
                FontSize = _service?.ThemeTokens?.FontSizeSmall ?? 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = _palette.Foreground,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Width = 20,
                Height = 18,
                TextAlignment = TextAlignment.Center,
                Cursor = Cursors.Hand,
                Margin = new Thickness(2, 1, 2, 1)
            };
            // 悬停即跳转（iOS 联系人索引式连续定位）；按住拖动顺滑扫过各分组。
            btn.MouseEnter += (_, _) => { ScrollToLetter(letter); btn.Foreground = _palette.Accent; };
            btn.MouseLeave += (_, _) => btn.Foreground = _palette.Foreground;
            btn.MouseLeftButtonDown += (_, _) => { ScrollToLetter(letter); btn.Foreground = _palette.Accent; };
            strip.Children.Add(btn);
        }

        return overlay;
    }

    /// <summary>把指定字母分组头滚动到可视区（虚拟化下经 ListBox.ScrollIntoView 可靠定位）。</summary>
    private void ScrollToLetter(string letter)
    {
        if (!_groupHeaders.TryGetValue(letter, out var header) || _appsList is null)
        {
            return;
        }

        try
        {
            _appsList.ScrollIntoView(header);
        }
        catch
        {
            // 滚动定位失败静默（M10）。
        }
    }

    // ===== 底部条 =====

    /// <summary>底部条：左侧「← 返回主菜单」，右侧应用总数。</summary>
    private FrameworkElement BuildFooter(int totalCount)
    {
        var bar = new Border
        {
            Background = _palette.SurfaceAlt,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 12, 8),
            Margin = new Thickness(0, 8, 0, 0)
        };
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var back = new Button
        {
            Content = "← 返回",
            FontSize = _service?.ThemeTokens?.FontSizeBody ?? 13,
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = _palette.Accent,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        // 回到当前样式对应的主菜单（win7 / win10 / win11）。
        back.Click += (_, _) =>
        {
            if (_service is not null)
            {
                _service.ShowLayout(StartMenuService.StyleToLayoutName(_service.GetMenuStyle()));
            }
        };
        Grid.SetColumn(back, 0);
        row.Children.Add(back);

        var count = new TextBlock
        {
            Text = totalCount > 0 ? $"共 {totalCount} 个应用" : string.Empty,
            FontSize = _service?.ThemeTokens?.FontSizeCaption ?? 12,
            Foreground = _palette.Muted,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(count, 1);
        row.Children.Add(count);

        bar.Child = row;
        return bar;
    }

    // ===== 搜索 / 刷新 =====

    /// <inheritdoc />
    public void RenderResults(string query, IReadOnlyList<SearchResult> results)
    {
        if (_appsList is null)
        {
            return;
        }

        var hasQuery = !string.IsNullOrWhiteSpace(query);
        if (_indexOverlay is not null)
        {
            _indexOverlay.Visibility = hasQuery ? Visibility.Collapsed : Visibility.Visible;
        }

        _appsList.Items.Clear();
        if (!hasQuery)
        {
            if (_service is not null)
            {
                PopulateAllApps(_service.GetAllApps());
            }

            return;
        }

        if (results.Count == 0)
        {
            _appsList.Items.Add(new ListBoxItem
            {
                IsEnabled = false,
                Content = new TextBlock { Text = "无匹配结果", FontSize = _service?.ThemeTokens?.FontSizeCaption ?? 12 }
            });
            return;
        }

        foreach (var result in results)
        {
            var label = new TextBlock
            {
                Text = result.Title,
                FontSize = _service?.ThemeTokens?.FontSizeBody ?? 13,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            if (!string.IsNullOrWhiteSpace(result.Subtitle))
            {
                label.ToolTip = result.Subtitle;
            }

            var item = new ListBoxItem
            {
                Content = label,
                Tag = result,
                Cursor = Cursors.Hand
            };
            item.MouseLeftButtonUp += (_, _) => ExecuteResult(result);
            if (result.AppItem is not null && _service is not null)
            {
                item.ContextMenu = AppItemActions.BuildContextMenu(result.AppItem, _service);
            }

            _appsList.Items.Add(item);
        }
    }

    /// <inheritdoc />
    public void RefreshItems()
    {
        if (_service is not null)
        {
            RenderResults(SearchBox.Text, Array.Empty<SearchResult>());
        }
    }

    // ===== 键盘 / 启动 =====

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            LaunchSelected();
            e.Handled = true;
        }
    }

    private void LaunchSelected()
    {
        // 分组列表里的是应用行宿主（Tag=AppItem）；搜索结果里才是 ListBoxItem（Tag=SearchResult）。
        if (_service is null || _appsList?.SelectedItem is not FrameworkElement { Tag: AppItem app })
        {
            return;
        }

        _service.ActivateOrLaunch(app);
        _service.Hide();
    }

    private void ExecuteResult(SearchResult result)
    {
        if (_service is null)
        {
            return;
        }

        try
        {
            if (result.Execute is not null)
            {
                result.Execute();
            }
            else if (result.AppItem is not null)
            {
                _service.ActivateOrLaunch(result.AppItem);
            }
            else if (!string.IsNullOrWhiteSpace(result.LaunchPath))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(result.LaunchPath) { UseShellExecute = true });
            }
        }
        catch
        {
            // 启动失败静默（M10）。
        }

        _service.Hide();
    }
}