using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BetterDesktop.Shell.AppSource.Models;
using BetterDesktop.Shell.ContextMenus.Contracts;
using BetterDesktop.Shell.ContextMenus.Services;
using BetterDesktop.Shell.Search.Contracts;
using BetterDesktop.Shell.StartMenu.Contracts;
using BetterDesktop.Shell.StartMenu.Services;

namespace BetterDesktop.Shell.StartMenu.Windows.Layouts;

/// <summary>
/// Win10 样式布局（复刻 Win10 开始菜单三栏，CLASSIC_LAYOUTS.md 规格）：
/// 三栏＝[左侧窄边栏 rail（48px，可展开为 200px）] + [中间应用列表 ~260px（最常用+字母分组）] + [右侧磁贴区（强调扁平磁贴）]。
/// 顶部全宽搜索框（Win10 原生无框、键入即搜；此布局为可用性保留框，搜索时覆盖三栏）；
/// 底部导航栏左侧"所有应用"切换、右侧用户头像+电源。
/// 固定列宽 + 各自独立滚动，杜绝元素堆叠/重叠。底色/强调/文字色由 StartMenuPalette 换算。
/// </summary>
public sealed class Win10Layout : IStartMenuLayoutProvider, IStartMenuLayoutHost
{
    private StartMenuService? _service;
    private StartMenuPalette _palette = null!;
    private StackPanel? _groupsPanel;   // 中间应用列表（最常用 + 字母分组）
    private FrameworkElement? _railColumn;    // 左侧窄边栏
    private FrameworkElement? _listColumn;    // 中间应用列表
    private FrameworkElement? _tilesColumn;   // 右侧磁贴区
    private bool _railExpand;                 // rail 展开态（48 ↔ 200）
    private double _railWidth = 48.0;
    private ScrollViewer? _listScroll;        // 中间应用列表滚动容器（字母跳转滚动定位用）
    private readonly Dictionary<string, FrameworkElement> _groupHeaders = new(); // 字母 → 分组头元素
    // —— 固定磁贴区（拖拽重排 / 图标文件夹）——
    private readonly List<object> _pinnedItems = new(); // 条目：AppItem 或 PinnedFolder
    private Panel? _tilesBody;                          // 磁贴渲染容器（拖拽后清空重建）
    private PinnedFolder? _openFolder;                  // 文件夹钻取态（非空表示正在查看某文件夹内）

    /// <inheritdoc />
    public string Name => "win10";

    /// <summary>搜索输入框。</summary>
    public TextBox SearchBox { get; private set; } = null!;

    /// <summary>搜索结果列表（无查询时折叠）。</summary>
    public ListBox ResultsList { get; private set; } = null!;

    /// <inheritdoc />
    public FrameworkElement BuildLayout(StartMenuService service)
    {
        _service = service;
        _palette = StartMenuPalette.From(service.ThemeTokens);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // ---- 顶部：搜索框 + 右端电源 ----
        var top = BuildTopBar();
        Grid.SetRow(top, 0);
        Grid.SetColumnSpan(top, 3);
        root.Children.Add(top);

        // ---- 正文：三栏 ----
        var body = BuildBody();
        Grid.SetRow(body, 1);
        Grid.SetColumnSpan(body, 3);
        root.Children.Add(body);

        // ---- 底部导航 ----
        var bottom = BuildBottomBar();
        Grid.SetRow(bottom, 2);
        Grid.SetColumnSpan(bottom, 3);
        root.Children.Add(bottom);

        var outer = new Border
        {
            CornerRadius = new CornerRadius(_palette.CornerRadius),
            Padding = new Thickness(0),
            Child = root
        };
        return outer;
    }

    // ---- 顶部 ----

    private FrameworkElement BuildTopBar()
    {
        var top = new Grid { Margin = new Thickness(12, 12, 12, 8) };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        SearchBox = new TextBox
        {
            FontSize = _service?.ThemeTokens?.FontSizeInput ?? 14,
            Padding = new Thickness(12, 8, 12, 8),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(SearchBox, 0);
        top.Children.Add(SearchBox);

        var power = new Button
        {
            Content = "⏻",
            Width = 36,
            Height = 34,
            FontSize = 16,
            Cursor = Cursors.Hand,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        power.Background = Brushes.Transparent;
        power.BorderThickness = new Thickness(0);
        power.Click += (_, _) => ShowPowerMenu(power);
        Grid.SetColumn(power, 1);
        top.Children.Add(power);

        return top;
    }

    // ---- 正文（三栏）----

    private FrameworkElement BuildBody()
    {
        var body = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _railColumn = BuildRail();
        Grid.SetColumn(_railColumn, 0);
        body.Children.Add(_railColumn);

        _listColumn = BuildListColumn();
        Grid.SetColumn(_listColumn, 1);
        body.Children.Add(_listColumn);

        _tilesColumn = BuildTilesColumn();
        Grid.SetColumn(_tilesColumn, 2);
        body.Children.Add(_tilesColumn);

        // 搜索结果覆盖层（盖住三栏）。
        ResultsList = new ListBox
        {
            Visibility = Visibility.Collapsed,
            BorderThickness = new Thickness(0),
            Background = _palette.Surface,
            Margin = new Thickness(4),
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        Grid.SetColumnSpan(ResultsList, 3);
        body.Children.Add(ResultsList);

        return body;
    }

    /// <summary>左侧窄边栏（rail）：汉堡/头像/资源管理器/设置/电源，可展开为 200px 显示文字。</summary>
    private FrameworkElement BuildRail()
    {
        var railHost = new Border
        {
            Background = _palette.SurfaceAlt,
            Width = _railWidth
        };
        var stack = new StackPanel { Margin = new Thickness(4) };

        stack.Children.Add(BuildRailButton("≡", "展开", () => ToggleRail()));

        var avatar = BuildRailButton("👤", _service?.GetUserName() ?? "账户", () => { });
        stack.Children.Add(avatar);

        stack.Children.Add(BuildRailButton("📁", "文档", () => Explorer("shell:MyComputerFolder")));
        stack.Children.Add(BuildRailButton("⚙", "设置", () => _service?.OpenSettings()));
        stack.Children.Add(BuildRailButton("⏻", "电源", () => { }));

        railHost.Child = stack;
        return railHost;
    }

    private FrameworkElement BuildRailButton(string glyph, string label, Action onClick)
    {
        var btn = new Button
        {
            Content = glyph,
            Width = 38,
            Height = 38,
            Margin = new Thickness(2),
            FontSize = 16,
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            ToolTip = label,
            FontFamily = new FontFamily("Segoe UI, Segoe UI Symbol")
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private void ToggleRail()
    {
        _railExpand = !_railExpand;
        _railWidth = _railExpand ? 200.0 : 48.0;
        // 更新列宽后重建内容使其生效（宽以字段值重新应用）。
        _service?.RefreshLayout();
    }

    private static void Explorer(string path)
        => Process.Start("explorer.exe", path);

    /// <summary>中间应用列表（~260px 固定，最近添加 + 最常用 + 字母分组，独立滚动）。</summary>
    private FrameworkElement BuildListColumn()
    {
        _groupsPanel = new StackPanel();

        // 「最近添加」分组（规格：最多 3 项，数据 GetNewlyInstalledApps，无条件关闭项）。
        var newly = _service!.GetNewlyInstalledApps();
        if (newly.Count > 0)
        {
            AddSectionHeading(_groupsPanel, "最近添加");
            foreach (var app in newly.Take(3))
            {
                _groupsPanel.Children.Add(StartMenuAppRowBuilder.BuildAppRow(app, app.Name, _service!, _palette));
            }

            _groupsPanel.Children.Add(new Border
            {
                Height = 1,
                Background = _palette.Separator,
                Margin = new Thickness(2, 10, 2, 10)
            });
        }

        AddSectionHeading(_groupsPanel, "最常用");
        var recent = _service!.GetRecentPrograms(6);
        if (recent.Count == 0)
        {
            _groupsPanel.Children.Add(EmptyText("（暂无常用应用）"));
        }
        else
        {
            foreach (var item in recent)
            {
                if (item.AppItem is { } app)
                {
                    _groupsPanel.Children.Add(StartMenuAppRowBuilder.BuildAppRow(app, item.Name, _service!, _palette));
                }
            }
        }

        _groupsPanel.Children.Add(new Border
        {
            Height = 1,
            Background = _palette.Separator,
            Margin = new Thickness(2, 10, 2, 10)
        });

        AddSectionHeading(_groupsPanel, "所有应用");
        foreach (var child in BuildAlphabetGroups())
        {
            _groupsPanel.Children.Add(child);
        }

        _groupHeaders.Clear();
        _listScroll = new ScrollViewer
        {
            Background = _palette.Surface,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _groupsPanel
        };

        // 字母索引覆盖层（规格长尾「字母快速跳转」）：右缘竖排 A-Z/#，点击滚动定位到对应分组头。
        var overlay = BuildAlphabetOverlay();
        if (overlay is null)
        {
            return _listScroll;
        }

        var wrap = new Grid();
        wrap.Children.Add(_listScroll);
        Grid.SetColumnSpan(overlay, 1);
        wrap.Children.Add(overlay);
        return wrap;
    }

    private IEnumerable<FrameworkElement> BuildAlphabetGroups()
    {
        var list = new List<FrameworkElement>();
        var all = _service!.GetAllApps();
        string currentGroup = string.Empty;
        foreach (var app in all)
        {
            // 英文应用按首字母 A-Z 分组；中文/符号等非拉丁首字符统一归入 “#”（避免按每字分组导致索引爆炸）。
            var letter = StartMenuAppRowBuilder.NormalizeGroupLetter(app.Name);
            if (!string.Equals(letter, currentGroup, StringComparison.Ordinal))
            {
                currentGroup = letter;
                var header = new TextBlock
                {
                    Text = currentGroup,
                    FontSize = _service?.ThemeTokens?.FontSizeSmall ?? 11,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(8, 10, 0, 4), // 分组标题与上下留出呼吸空间
                    Foreground = _palette.Muted
                };
                // 记录字母 → 分组头，供右缘索引覆盖层滚动定位。
                _groupHeaders[letter] = header;
                list.Add(header);
            }

            list.Add(StartMenuAppRowBuilder.BuildAppRow(app, app.Name, _service!, _palette));
        }

        return list;
    }

    /// <summary>右缘字母索引栏：仅列出实际存在的分组字母，点击/鼠标悬停滚动到对应分组头。</summary>
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
            Margin = new Thickness(0, 12, 3, 12),
            IsHitTestVisible = true
        };
        // 索引条容器：默认透明（不遮挡右侧内容），悬停/按住时出现浅色贴纸背景，落停更清爽。
        var overlay = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(12),
            Margin = new Thickness(0, 0, 1, 0),
            Child = strip
        };
        // 仅在指针进入索引条时显示贴纸底，离开即隐藏。
        strip.MouseEnter += (_, _) => overlay.Background = _palette.SurfaceAlt;
        strip.MouseLeave += (_, _) => overlay.Background = Brushes.Transparent;

        Color accentText = _palette.AccentColor;
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
                Width = 20,
                Height = 18,
                TextAlignment = TextAlignment.Center,
                Cursor = Cursors.Hand,
                Margin = new Thickness(2, 1, 2, 1)
            };
            // 悬停即跳转（iOS 联系人索引式连续定位）+ 高亮当前字母；按住拖动顺滑扫过各分组。
            btn.MouseEnter += (_, _) => { ScrollToLetter(letter); btn.Foreground = _palette.Accent; };
            btn.MouseLeave += (_, _) => btn.Foreground = _palette.Foreground;
            btn.MouseLeftButtonDown += (_, _) => { ScrollToLetter(letter); btn.Foreground = _palette.Accent; };
            strip.Children.Add(btn);
        }

        return overlay;
    }

    /// <summary>把指定字母分组头滚动到可视区上缘。</summary>
    private void ScrollToLetter(string letter)
    {
        if (!_groupHeaders.TryGetValue(letter, out var header) || _listScroll is null)
        {
            return;
        }

        try
        {
            header.BringIntoView();
        }
        catch
        {
            // 滚动定位失败静默（M10）。
        }
    }

    /// <summary>右侧磁贴区（强调扁平磁贴，3 列，独立滚动）。</summary>
    private FrameworkElement BuildTilesColumn()
    {
        var col = new Grid { Background = _palette.SurfaceAlt };
        col.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var panel = new StackPanel { Margin = new Thickness(12) };
        AddSectionHeading(panel, "已固定");
        _tilesBody = new Grid
        {
            Margin = new Thickness(0, 4, 0, 0),
            AllowDrop = true
        };
        panel.Children.Add(_tilesBody);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = panel
        };
        col.Children.Add(scroll);

        // 首次构建时用固定应用快照填充（仅一次；拖拽/建夹后的本地编排保留在 _pinnedItems）。
        if (_pinnedItems.Count == 0 && _service is not null)
        {
            foreach (var app in _service.GetPinnedStartMenuApps(12))
            {
                _pinnedItems.Add(app);
            }
        }

        RenderTiles();
        return col;
    }

    /// <summary>重建固定磁贴区（初始构建 / 拖拽重排 / 建夹 / 文件夹钻取后调用）。</summary>
    private void RenderTiles()
    {
        if (_tilesBody is not Grid host)
        {
            return;
        }

        host.Children.Clear();
        host.RowDefinitions.Clear();
        host.ColumnDefinitions.Clear();
        for (var c = 0; c < 3; c++)
        {
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        // 文件夹钻取态：渲染其成员 + 返回导航。
        var source = _openFolder is not null ? _openFolder.Items.Cast<object>() : _pinnedItems;
        var carouselPairs = new List<(FrameworkElement A, FrameworkElement B)>();
        var index = 0;
        foreach (var item in source)
        {
            FrameworkElement tile = item is AppItem app
                ? BuildAppTile(app, index, carouselPairs)
                : item is PinnedFolder folder
                    ? BuildFolderTile(folder)
                    : BuildBackTile();

            var row = index / 3;
            var col = index % 3;
            while (host.RowDefinitions.Count <= row)
            {
                host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
            }

            Grid.SetRow(tile, row);
            Grid.SetColumn(tile, col);
            host.Children.Add(tile);
            index++;
        }

        LiveTileCarousel.Register(carouselPairs);
    }

    /// <summary>单个应用磁贴（Live Tile 双帧 + 拖拽源 + 落下目标）。</summary>
    private FrameworkElement BuildAppTile(AppItem app, int index, List<(FrameworkElement A, FrameworkElement B)> carouselPairs)
    {
        var tile = new Grid
        {
            Margin = new Thickness(3),
            Cursor = Cursors.Hand,
            Tag = app
        };
        tile.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        tile.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var chip = new Border
        {
            Background = _palette.Tile10,
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(0, 0, 0, 2)
        };
        var faces = new Grid();

        var icon = new Image
        {
            Width = 32,
            Height = 32,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        var faceA = new Grid();
        faceA.Children.Add(icon);
        faces.Children.Add(faceA);

        var letterText = new TextBlock
        {
            Text = StartMenuAppRowBuilder.NormalizeGroupLetter(app.Name),
            FontSize = 44,
            FontWeight = FontWeights.Black,
            Foreground = _palette.Foreground,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.9
        };
        var faceB = new Grid();
        faceB.Children.Add(letterText);
        faces.Children.Add(faceB);

        chip.Child = faces;
        Grid.SetRow(chip, 0);
        tile.Children.Add(chip);

        var label = new TextBlock
        {
            Text = app.Name,
            FontSize = _service?.ThemeTokens?.FontSizeSmall ?? 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = 92
        };
        label.Foreground = _palette.Foreground;
        Grid.SetRow(label, 1);
        tile.Children.Add(label);

        tile.MouseEnter += (_, _) => chip.Background = _palette.RowSelected;
        tile.MouseLeave += (_, _) => chip.Background = _palette.Tile10;
        tile.MouseLeftButtonUp += (_, _) =>
        {
            if (_dragOccurred)
            {
                _dragOccurred = false;
                return;
            }

            _service?.ActivateOrLaunch(app);
            _service?.Hide();
        };
        if (_service is not null)
        {
            MenuSurface.Attach(tile, () => AppItemActions.BuildItems(app, _service!), _service?.Menus);
        }

        // 仅主网格支持拖拽重排/建夹；文件夹钻取视图只读，避免跨层复杂编排。
        if (_openFolder is null)
        {
            AttachDragSource(tile, app);
            AttachDropTarget(tile, app);
        }

        faceA.Visibility = (index % 2 == 0) ? Visibility.Visible : Visibility.Collapsed;
        faceB.Visibility = (index % 2 == 0) ? Visibility.Collapsed : Visibility.Visible;
        carouselPairs.Add((faceA, faceB));

        _ = LoadIconAsync(app, icon);
        return tile;
    }

    /// <summary>文件夹磁贴：文件夹字形 + 名称 + 成员数徽标；点击钻入查看成员。可作落下目标收纳应用。</summary>
    private FrameworkElement BuildFolderTile(PinnedFolder folder)
    {
        var tile = new Grid
        {
            Margin = new Thickness(3),
            Cursor = Cursors.Hand,
            Tag = folder
        };
        tile.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        tile.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var chip = new Border
        {
            Background = _palette.TileFolder,
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(0, 0, 0, 2)
        };
        var stack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        stack.Children.Add(new TextBlock
        {
            Text = "\uD83D\uDCC1", // 📁
            FontSize = 34,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        stack.Children.Add(new Border
        {
            Background = _palette.SurfaceAlt,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6, 0, 6, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0),
            Child = new TextBlock
            {
                Text = folder.Items.Count.ToString(),
                FontSize = _service?.ThemeTokens?.FontSizeSmall ?? 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = _palette.Foreground
            }
        });
        chip.Child = stack;
        Grid.SetRow(chip, 0);
        tile.Children.Add(chip);

        var label = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(folder.Name) ? "文件夹" : folder.Name,
            FontSize = _service?.ThemeTokens?.FontSizeSmall ?? 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = 92
        };
        label.Foreground = _palette.Foreground;
        Grid.SetRow(label, 1);
        tile.Children.Add(label);

        tile.MouseEnter += (_, _) => chip.Background = _palette.RowSelected;
        tile.MouseLeftButtonUp += (_, _) =>
        {
            if (_dragOccurred)
            {
                _dragOccurred = false;
                return;
            }

            _openFolder = folder;
            RenderTiles();
        };
        tile.MouseLeave += (_, _) => chip.Background = _palette.TileFolder;

        // 文件夹是落下目标（收纳应用进夹）。
        if (_openFolder is null)
        {
            AttachDropTarget(tile, folder);
        }

        return tile;
    }

    /// <summary>文件夹钻取时的“返回主网格”导航磁贴。</summary>
    private FrameworkElement BuildBackTile()
    {
        var tile = new Border
        {
            Margin = new Thickness(3),
            CornerRadius = new CornerRadius(2),
            Background = _palette.SurfaceAlt,
            Cursor = Cursors.Hand
        };
        tile.Child = new TextBlock
        {
            Text = "← 返回",
            FontSize = _service?.ThemeTokens?.FontSizeBody ?? 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = _palette.Accent
        };
        tile.MouseLeftButtonUp += (_, _) =>
        {
            _openFolder = null;
            RenderTiles();
        };
        return tile;
    }

    // ---- 拖拽重排 / 图标文件夹 ----

    private const string TileDragFormat = "betterdt.pinned.tile";
    private bool _dragOccurred;                                    // 本轮是否发生拖拽（抑制随后的误触点击）
    private Point _dragStart;                                      // 拖拽起点（判定超过最小拖拽阈值）

    /// <summary>把应用磁贴接为拖拽源：按住左键拖动超过阈值则发起 DoDragDrop。</summary>
    private void AttachDragSource(UIElement tile, AppItem app)
    {
        tile.PreviewMouseLeftButtonDown += (_, e) => _dragStart = e.GetPosition(tile);
        tile.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            var cur = e.GetPosition(tile);
            var d = _dragStart - cur;
            if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            _dragOccurred = true;
            var data = new DataObject(TileDragFormat, app);
            DragDrop.DoDragDrop(tile, data, DragDropEffects.Move);
        };
    }

    /// <summary>把磁贴接为落下目标：按落下对象与目标类型执行“重排 / 并入文件夹 / 收纳进夹”。</summary>
    private void AttachDropTarget(UIElement tile, object target)
    {
        tile.AllowDrop = true;
        tile.Drop += (_, e) =>
        {
            if (!e.Data.GetDataPresent(TileDragFormat))
            {
                return;
            }

            var dragged = e.Data.GetData(TileDragFormat) as AppItem;
            if (dragged is null)
            {
                return;
            }

            if (_openFolder is not null)
            {
                return; // 钻取视图只读
            }

            var ctrlHeld = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

            if (target is AppItem targetApp)
            {
                // 同一应用（拖到自己身上）忽略；按 Id 判同最可靠（重建后实例不同）。
                if (dragged.Id == targetApp.Id)
                {
                    return;
                }

                if (ctrlHeld)
                {
                    MergeIntoFolder(dragged, targetApp);
                }
                else
                {
                    Reorder(dragged, targetApp);
                }
            }
            else if (target is PinnedFolder folder)
            {
                AddToFolder(dragged, folder);
            }

            e.Handled = true;
        };
    }

    /// <summary>把被拖应用移动到目标应用所在位置（同层重排）。</summary>
    private void Reorder(AppItem dragged, AppItem target)
    {
        var targetIndex = IndexOfItem(target);
        var sourceIndex = IndexOfItem(dragged);
        if (targetIndex < 0 || sourceIndex < 0 || targetIndex == sourceIndex)
        {
            return;
        }

        _pinnedItems.RemoveAt(sourceIndex);
        // sourceIndex < targetIndex 时删掉 source 会让原 target 位置前移一位，需回退。
        var insertAt = sourceIndex < targetIndex ? targetIndex - 1 : targetIndex;
        _pinnedItems.Insert(Math.Clamp(insertAt, 0, _pinnedItems.Count), dragged);
        RenderTiles();
    }

    /// <summary>按住 Ctrl 拖到另一应用上：把两者归入一个新文件夹。</summary>
    private void MergeIntoFolder(AppItem dragged, AppItem target)
    {
        var folder = new PinnedFolder();
        folder.Items.Add(target);
        folder.Items.Add(dragged);
        folder.Name = $"{target.Name} 等 {folder.Items.Count} 项";

        var targetIndex = IndexOfItem(target);
        var sourceIndex = IndexOfItem(dragged);
        if (targetIndex < 0)
        {
            return;
        }

        // 用文件夹替换 target，并移除被拖应用原位置。
        _pinnedItems[targetIndex] = folder;
        if (sourceIndex >= 0)
        {
            _pinnedItems.RemoveAt(sourceIndex);
        }

        RenderTiles();
    }

    /// <summary>把被拖应用收纳进某文件夹（已存在则跳过）。</summary>
    private void AddToFolder(AppItem dragged, PinnedFolder folder)
    {
        if (folder.Items.Any(item => item.Id == dragged.Id))
        {
            return;
        }

        folder.Items.Add(dragged);
        var sourceIndex = IndexOfItem(dragged);
        if (sourceIndex >= 0)
        {
            _pinnedItems.RemoveAt(sourceIndex);
        }

        RenderTiles();
    }

    private int IndexOfItem(object item)
        => item is AppItem app
            ? _pinnedItems.FindIndex(x => x is AppItem a && a.Id == app.Id)
            : _pinnedItems.FindIndex(x => ReferenceEquals(x, item));

    // ---- 底部 ----

    private FrameworkElement BuildBottomBar()
    {
        var bar = new Border
        {
            Background = _palette.SurfaceAlt,
            Padding = new Thickness(16, 4, 16, 4),
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = _palette.Separator
        };
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var allAppsLabel = new TextBlock
        {
            Text = "所有应用 ▾",
            FontSize = _service?.ThemeTokens?.FontSizeBody ?? 13,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Foreground = _palette.Foreground
        };
        allAppsLabel.MouseLeftButtonUp += (_, _) => _service?.ShowLayout("allapps");
        Grid.SetColumn(allAppsLabel, 0);
        row.Children.Add(allAppsLabel);

        var cluster = BuildUserCluster();
        Grid.SetColumn(cluster, 1);
        row.Children.Add(cluster);

        bar.Child = row;
        return bar;
    }

    private FrameworkElement BuildUserCluster()
    {
        var cluster = new StackPanel { Orientation = Orientation.Horizontal };
        var name = _service?.GetUserName() ?? "User";

        var avatar = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(13),
            Background = _palette.UserArea,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0)
        };
        avatar.Child = new TextBlock
        {
            Text = string.IsNullOrEmpty(name) ? "?" : name.Substring(0, 1).ToUpperInvariant(),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        cluster.Children.Add(avatar);

        var power = new Button
        {
            Content = "⏻",
            Width = 34,
            Height = 30,
            FontSize = 16,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center
        };
        power.Background = Brushes.Transparent;
        power.BorderThickness = new Thickness(0);
        power.Click += (_, _) => ShowPowerMenu(power);
        cluster.Children.Add(power);

        return cluster;
    }

    // ---- 共用 ----

    private void AddSectionHeading(StackPanel panel, string title)
    {
        var heading = new TextBlock
        {
            Text = title,
            FontSize = _service?.ThemeTokens?.FontSizeBody ?? 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(8, 12, 0, 6), // 顶部留白拉开与上一组的距离，避免标题贴在上组行上
            Foreground = _palette.Foreground
        };
        panel.Children.Add(heading);
    }

    private TextBlock EmptyText(string text)
        => new()
        {
            Text = text,
            FontSize = _service?.ThemeTokens?.FontSizeCaption ?? 12,
            Foreground = _palette.Muted,
            Margin = new Thickness(8, 2, 0, 0)
        };

    // ---- 电源 / 刷新 / 搜索 ----

    private void ShowPowerMenu(FrameworkElement placementTarget)
    {
        // 统一弹层呈现（ShellWindow + 主题令牌）；弹层抢激活由 StartMenuWindow 的 IsOpen/Closed 豁免兜底。
        var items = new List<MenuItemDef>
        {
            PowerItem("start.power.sleep", "睡眠", () => _ = PowerCommands.Sleep()),
            PowerItem("start.power.restart", "重新启动", () => _ = PowerCommands.Restart()),
            PowerItem("start.power.shutdown", "关机", () => _ = PowerCommands.Shutdown()),
            PowerItem("start.power.lock", "锁定", () => _ = PowerCommands.Lock()),
        };
        _ = _service?.Menus?.ShowAsync(items, MenuSurface.BelowOf(placementTarget));
    }

    private static MenuItemDef PowerItem(string id, string text, Action action) => new()
    {
        Id = id,
        Text = text,
        Command = action,
    };

    /// <inheritdoc />
    public void RefreshItems()
    {
        if (_service is null)
        {
            return;
        }
    }

    /// <inheritdoc />
    public void RenderResults(string query, IReadOnlyList<SearchResult> results)
    {
        ResultsList.Items.Clear();
        var hasQuery = !string.IsNullOrWhiteSpace(query);
        ResultsList.Visibility = hasQuery ? Visibility.Visible : Visibility.Collapsed;

        if (_listColumn is not null && _tilesColumn is not null && _railColumn is not null)
        {
            var show = !hasQuery;
            _listColumn.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            _tilesColumn.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            _railColumn.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        if (!hasQuery)
        {
            return;
        }

        if (results.Count == 0)
        {
            ResultsList.Items.Add(new ListBoxItem
            {
                IsEnabled = false,
                Content = new TextBlock
                {
                    Text = "无匹配结果",
                    FontSize = _service?.ThemeTokens?.FontSizeCaption ?? 12,
                    Foreground = _palette.Muted
                }
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

            var item = new ListBoxItem { Content = label, Tag = result, Cursor = Cursors.Hand };
            item.MouseLeftButtonUp += (_, _) => ExecuteResult(result);
            if (result.AppItem is not null && _service is not null)
            {
                MenuSurface.Attach(item, () => AppItemActions.BuildItems(result.AppItem, _service!), _service?.Menus);
            }

            ResultsList.Items.Add(item);
        }
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
                Process.Start(new ProcessStartInfo(result.LaunchPath) { UseShellExecute = true });
            }
        }
        catch
        {
            // 启动失败静默（M10）。
        }

        _service.Hide();
    }

    private async System.Threading.Tasks.Task LoadIconAsync(AppItem app, Image target)
    {
        try
        {
            var icon = await _service!.GetIconAsync(app, System.Threading.CancellationToken.None);
            if (icon is not null)
            {
                target.Source = icon;
            }
        }
        catch
        {
            // 图标失败静默
        }
    }
}

/// <summary>
/// Live Tile 轮播器（规格长尾功能）：单例静态调度器，定期把已注册的每个磁贴帧对
/// （图标面 A / 字母强调面 B）错落切换，形成涟漪式轮播。布局重建时经 <see cref="Register"/>
/// 整体替换条目，旧帧引用随之释放，不累积泄漏；条目为空时 tick 直接跳过（近零开销）。
/// </summary>
internal static class LiveTileCarousel
{
    private static readonly System.Windows.Threading.DispatcherTimer _timer = CreateTimer();
    private static volatile IReadOnlyList<(FrameworkElement A, FrameworkElement B)> _entries =
        Array.Empty<(FrameworkElement A, FrameworkElement B)>();
    private static int _phase;

    private static System.Windows.Threading.DispatcherTimer CreateTimer()
    {
        var t = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(2400)
        };
        t.Tick += (_, _) => Tick();
        // 惰性常驻：仅在有条目时运行，窗口常开也不空转。
        return t;
    }

    /// <summary>注册（替换）当前活磁贴帧对集合，并确保调度器随条目存在而运行。</summary>
    public static void Register(IReadOnlyList<(FrameworkElement A, FrameworkElement B)> entries)
    {
        _entries = entries ?? Array.Empty<(FrameworkElement A, FrameworkElement B)>();
        if (_entries.Count > 0 && !_timer.IsEnabled)
        {
            _timer.Start();
        }
    }

    private static void Tick()
    {
        var entries = _entries;
        if (entries.Count == 0)
        {
            return;
        }

        _phase++;
        // 按 (序号 + 相位) 奇偶错落翻转两帧，形成逐块交错推进的轮播。
        for (var i = 0; i < entries.Count; i++)
        {
            var showA = ((i + _phase) & 1) == 0;
            var (a, b) = entries[i];
            a.Visibility = showA ? Visibility.Visible : Visibility.Collapsed;
            b.Visibility = showA ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}