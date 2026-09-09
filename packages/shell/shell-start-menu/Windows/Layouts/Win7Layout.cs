using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using BetterDesktop.Shell.AppSource.Models;
using BetterDesktop.Shell.ContextMenus.Contracts;
using BetterDesktop.Shell.ContextMenus.Services;
using BetterDesktop.Shell.Search.Contracts;
using BetterDesktop.Shell.StartMenu.Contracts;
using BetterDesktop.Shell.StartMenu.Services;

namespace BetterDesktop.Shell.StartMenu.Windows.Layouts;

// ── 本文件方法级白话索引（Win7 风格开始菜单布局，白话 → 方法）──
//   "整体/左列（固定+最近程序行）"  → BuildLayout / BuildLeftColumn / AddPinned / AddRecent / CreateEntryRow
//   "底部搜索框"                    → BuildSearchBox
//   "右列（用户头像/功能链接/电源）" → PrepareRightColumn / BuildUserHeader / BuildWin7FunctionList / AddSystemLink / BuildPowerFooter / ShowPowerMenu
//   "所有程序树（文件夹/叶子/键盘激活）" → ToggleAllPrograms / PopulateTree / BuildFolderNode / BuildLeafNode(BuildLeafIcon) / OnTreeKeyDown / ActivateSelected
//   "搜索结果渲染"                  → RenderResults；滚轮辅助 ScrollList
//   其他布局对照：Win10Layout/Win11Layout/ClassicLayout/AllAppsLayout（同目录，同一契约）。
// ────────────────────────────────────

/// <summary>
/// Win7 样式布局（复刻 Win7 Aero 开始菜单经典两栏，CLASSIC_LAYOUTS.md 规格）：
/// 左栏自上而下＝[固定程序区 + 最近使用程序 + "所有程序"树切换] + 底部搜索框；
/// 右栏顶部用户头像/名称 → 系统链接列表（文档/图片/音乐/游戏/计算机/控制面板…）→ 底部"关机"电源。
/// 左栏与右栏约 1:1，中间 1px 分隔线；底色/强调/文字色一律由 StartMenuPalette 换算。
/// </summary>
public sealed class Win7Layout : IStartMenuLayoutProvider, IStartMenuLayoutHost
{
    private IStartMenuDataService? _service;
    private StartMenuPalette _palette = null!;

    // 左栏两视图容器（home 列表 <-> tree 树）互斥切换。
    private ScrollViewer? _homeScroll; // 固定 + 最近（home 视图）
    private ScrollViewer? _treeScroll; // "所有程序" 程序树（tree 视图）
    private TreeView? _tree;
    private Button? _allProgramsButton; // 左栏固定底栏的"所有程序 ▶ / 返回 ◀"切换钮（两视图常驻）
    private bool _allProgramsExpanded; // 默认折叠（home 视图）；点击"所有程序"展开树

    /// <inheritdoc />
    public string Name => "win7";

    /// <summary>搜索输入框（左栏底部）。</summary>
    public TextBox SearchBox { get; private set; } = null!;

    /// <summary>结果列表（无查询时折叠，覆盖左栏）。</summary>
    public ListBox ResultsList { get; private set; } = null!;

    /// <inheritdoc />
    public FrameworkElement BuildLayout(IStartMenuDataService service)
    {
        _service = service;
        _palette = StartMenuPalette.From(service.ThemeTokens);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.18, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 左栏与右栏间 1px 分隔线（浅 #D0D0D0 / 深 #3A3A3A）。
        var divider = new Border
        {
            Width = 1,
            Background = _palette.Separator,
            Margin = new Thickness(0, 8, 0, 8),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        Grid.SetColumn(divider, 0);
        grid.Children.Add(divider);

        var left = BuildLeftColumn();
        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        var right = PrepareRightColumn();
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);

        var outer = new Border
        {
            CornerRadius = new CornerRadius(_palette.CornerRadius),
            Background = _palette.Surface,
            Padding = new Thickness(2),
            Child = grid
        };
        return outer;
    }

    // ===== 左栏 =====

    private FrameworkElement BuildLeftColumn()
    {
        var left = new Grid { Margin = new Thickness(4) };
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 中部：home 视图与 tree 视图互斥。
        var content = new Grid();
        Grid.SetRow(content, 0);

        // ---- home 视图：固定 + 最近 ----
        var home = new StackPanel();
        AddPinned(home);
        home.Children.Add(new Border
        {
            Height = 1,
            Background = _palette.Separator,
            Margin = new Thickness(2, 4, 2, 4)
        });
        AddRecent(home);

        _homeScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = home
        };
        _homeScroll.PreviewMouseWheel += (_, e) => ScrollList(_homeScroll, e);
        content.Children.Add(_homeScroll);

        // ---- tree 视图：程序树（点击"所有程序"后显示）----
        _treeScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(4, 6, 4, 6),
            Visibility = Visibility.Collapsed
        };
        _tree = new TreeView
        {
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = _palette.Foreground
        };
        PopulateTree(_service!.GetProgramTree(), _tree, expandAll: false);
        _tree.KeyDown += OnTreeKeyDown;
        _tree.MouseDoubleClick += (_, _) => ActivateSelected();
        _treeScroll.Content = _tree;
        _treeScroll.PreviewMouseWheel += (_, e) => ScrollList(_treeScroll, e);
        content.Children.Add(_treeScroll);

        left.Children.Add(content);

        // 搜索结果覆盖层（搜索时盖住 left 内容区；后添加 → 显示在上层）。
        ResultsList = new ListBox
        {
            Visibility = Visibility.Collapsed,
            BorderThickness = new Thickness(0),
            MaxWidth = 320,
            Margin = new Thickness(2, 0, 2, 0),
            Background = Brushes.Transparent
        };
        content.Children.Add(ResultsList);

        // 左栏固定底栏："所有程序 ▶ / 返回 ◀" 切换钮（两视图常驻，Win7 原生亦为底栏常驻）。
        // 置于内容区之下、搜索框之上，独立于 home/tree 视图，保证树视图下仍可返回。
        var toggleBar = BuildToggleBar();
        Grid.SetRow(toggleBar, 1);
        left.Children.Add(toggleBar);

        // 底部搜索框（Win7 规格：左栏最底部，跨左栏宽）。
        SearchBox = BuildSearchBox();
        Grid.SetRow(SearchBox, 2);
        left.Children.Add(SearchBox);

        return left;
    }

    /// <summary>左栏固定底栏：切换 home 视图与"所有程序"树视图，并随状态改标为"返回 ◀"。</summary>
    private FrameworkElement BuildToggleBar()
    {
        var bar = new Border
        {
            Background = _palette.SurfaceAlt,
            BorderBrush = _palette.Separator,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(6, 4, 6, 4),
            Margin = new Thickness(2, 2, 2, 2)
        };
        _allProgramsButton = new Button
        {
            Content = "所有程序 ▶",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(6, 4, 8, 4),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = _palette.Foreground,
            Cursor = Cursors.Hand,
            FontSize = _service?.ThemeTokens?.FontSizeBody ?? 13
        };
        _allProgramsButton.Click += (_, _) => ToggleAllPrograms();
        bar.Child = _allProgramsButton;
        return bar;
    }

    /// <summary>左栏列表滚轮滚动：绕过内层控件（如 TreeView 自带滚动容器）对滚轮事件的拦截，
    /// 保证悬停列表上滚轮始终滚动该 ScrollViewer（修复"所有程序"树视图滚轮失效）。</summary>
    private static void ScrollList(ScrollViewer? sv, MouseWheelEventArgs e)
    {
        if (sv is null)
        {
            return;
        }

        const double lineOffset = 48; // 每格滚动行高（像素），固定手感独立于系统行数设置
        var delta = e.Delta > 0 ? -lineOffset : +lineOffset;
        sv.ScrollToVerticalOffset(sv.VerticalOffset + delta);
        e.Handled = true;
    }

    /// <summary>固定程序区（顶部）。</summary>
    private void AddPinned(StackPanel panel)
    {
        foreach (var app in _service!.GetPinnedStartMenuApps(9))
        {
            panel.Children.Add(CreateEntryRow(app, app.Name));
        }
    }

    /// <summary>最近使用程序区（MFU，最多 9 项）。</summary>
    private void AddRecent(StackPanel panel)
    {
        var recent = _service!.GetRecentPrograms(9);
        if (recent.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "最近使用程序会显示在这里",
                FontSize = _service?.ThemeTokens?.FontSizeCaption ?? 12,
                Foreground = _palette.Muted,
                Margin = new Thickness(8, 6, 4, 6)
            });
            return;
        }

        foreach (var item in recent)
        {
            if (item.AppItem is { } app)
            {
                panel.Children.Add(CreateEntryRow(app, item.Name));
            }
        }
    }

    /// <summary>左栏条目行：20px 图标 + 13px 文字，36px 高（放大后不拥挤），右键菜单。</summary>
    private FrameworkElement CreateEntryRow(AppItem app, string displayName)
    {
        var row = new Grid
        {
            Height = 36,
            Cursor = Cursors.Hand,
            Tag = app,
            Margin = new Thickness(2, 2, 2, 2)
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new Image
        {
            Width = 20,
            Height = 20,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        var iconPad = new Grid { Width = 32, Margin = new Thickness(4, 0, 0, 0) };
        iconPad.Children.Add(icon);
        Grid.SetColumn(iconPad, 0);
        row.Children.Add(iconPad);

        var name = new TextBlock
        {
            Text = displayName,
            FontSize = _service?.ThemeTokens?.FontSizeSmall ?? 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 4, 0),
            Foreground = _palette.Foreground
        };
        Grid.SetColumn(name, 1);
        row.Children.Add(name);

        row.MouseLeftButtonUp += (_, _) =>
        {
            _service?.ActivateOrLaunch(app);
            _service?.Hide();
        };
        if (_service is not null)
        {
            AppItemActions.AttachNative(row, app);
        }

        var host = new Border { CornerRadius = new CornerRadius(3), Child = row };
        host.MouseEnter += (_, _) => host.Background = _palette.RowHover;
        host.MouseLeave += (_, _) => host.Background = Brushes.Transparent;

        _ = LoadIconAsync(app, icon);
        return host;
    }

    private TextBox BuildSearchBox()
    {
        var box = new TextBox
        {
            FontSize = _service?.ThemeTokens?.FontSizeInput ?? 14,
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(2, 4, 2, 2),
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = _palette.SurfaceAlt,
            Foreground = _palette.Foreground,
            CaretBrush = _palette.Foreground,
            BorderBrush = _palette.Separator
        };
        return box;
    }

    // ===== 右栏 =====

    private FrameworkElement PrepareRightColumn()
    {
        var right = new Grid { Margin = new Thickness(2, 8, 2, 8) };
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var user = BuildUserHeader();
        Grid.SetRow(user, 0);
        right.Children.Add(user);

        var functions = BuildWin7FunctionList();
        Grid.SetRow(functions, 1);
        right.Children.Add(functions);

        var power = BuildPowerFooter();
        Grid.SetRow(power, 2);
        right.Children.Add(power);

        return right;
    }

    /// <summary>用户头像（48 圆）+ 用户名，Win7 右栏顶部。</summary>
    private FrameworkElement BuildUserHeader()
    {
        var name = _service?.GetUserName() ?? "User";
        var letter = string.IsNullOrWhiteSpace(name) ? "U" : name.Substring(0, 1).ToUpperInvariant();

        var avatar = new Ellipse
        {
            Width = 48,
            Height = 48,
            Fill = _palette.Tile11,
            Stroke = _palette.Separator,
            StrokeThickness = 1,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        var avatarText = new TextBlock
        {
            Text = letter,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = _palette.Foreground,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var avatarGrid = new Grid { Width = 48, Height = 48 };
        avatarGrid.Children.Add(avatar);
        avatarGrid.Children.Add(avatarText);

        var label = new TextBlock
        {
            Text = name,
            FontSize = _service?.ThemeTokens?.FontSizeBody ?? 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 4, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = _palette.Foreground
        };

        var header = new Grid { Margin = new Thickness(6, 2, 6, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(avatarGrid, 0);
        Grid.SetColumn(label, 1);
        header.Children.Add(avatarGrid);
        header.Children.Add(label);
        return header;
    }

    /// <summary>系统链接列表（规格：个人文件夹→帮助和支持，每项 32px 高、24 图标 + 12 文字）。</summary>
    private FrameworkElement BuildWin7FunctionList()
    {
        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        var panel = new StackPanel();

        AddSystemLink(panel, "🏠", _service?.GetUserName() ?? "个人文件夹", "$USERPROFILE$", () => Explorer(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), hasChevron: false);
        AddSystemLink(panel, "📄", "文档", "$USERPROFILE$\\Documents", () => Explorer(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)));
        AddSystemLink(panel, "🖼", "图片", "$USERPROFILE$\\Pictures", () => Explorer(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)));
        AddSystemLink(panel, "🎵", "音乐", "$USERPROFILE$\\Music", () => Explorer(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)));
        AddSystemLink(panel, "🎮", "游戏", "shell:GamesFolder", () => Explorer("shell:GamesFolder"));
        AddSystemLink(panel, "💻", "计算机", "shell:MyComputerFolder", () => Explorer("shell:MyComputerFolder"), hasChevron: true);
        AddSystemLink(panel, "⚙", "控制面板", "shell:ControlPanelFolder", () => Explorer("shell:ControlPanelFolder"));
        AddSystemLink(panel, "🖨", "设备和打印机", "shell:PrintersFolder", () => Explorer("shell:PrintersFolder"));
        AddSystemLink(panel, "🔧", "默认程序", "shell:DefaultsFolder", () => Explorer("shell:DefaultsFolder"));
        AddSystemLink(panel, "❓", "帮助和支持", "shell:HelpFolder", () => Explorer("shell:HelpFolder"));

        scroll.Content = panel;
        return scroll;
    }

    /// <summary>系统链接行：32px 高，24px 图标区 + 12px 文字，右侧可挂 ▶ 箭头。</summary>
    private void AddSystemLink(StackPanel panel, string glyph, string label, string? tooltip, Action action, bool hasChevron = false)
    {
        var row = new Grid
        {
            Height = 32,
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 0, 0, 0),
            ToolTip = tooltip
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (hasChevron)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        var icon = new TextBlock
        {
            Text = glyph,
            FontSize = 17,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = _palette.Foreground
        };
        Grid.SetColumn(icon, 0);
        row.Children.Add(icon);

        var labelBlock = new TextBlock
        {
            Text = label,
            FontSize = _service?.ThemeTokens?.FontSizeBody ?? 12,
            FontFamily = new FontFamily("Segoe UI"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = _palette.Foreground
        };
        Grid.SetColumn(labelBlock, 1);
        row.Children.Add(labelBlock);

        if (hasChevron)
        {
            var chevron = new TextBlock
            {
                Text = "▶",
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
                Foreground = _palette.Muted
            };
            Grid.SetColumn(chevron, 2);
            row.Children.Add(chevron);
        }

        var host = new Border { CornerRadius = new CornerRadius(3), Child = row };
        host.MouseEnter += (_, _) => host.Background = _palette.RowHover;
        host.MouseLeave += (_, _) => host.Background = Brushes.Transparent;
        host.MouseLeftButtonUp += (_, _) =>
        {
            try
            {
                action();
            }
            catch
            {
                // 打开失败静默（M10）。
            }
        };
        panel.Children.Add(host);
    }

    private FrameworkElement BuildPowerFooter()
    {
        var footer = new Grid { Margin = new Thickness(6, 6, 6, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var shutdown = new Button
        {
            Content = "关机",
            Height = 28,
            MinWidth = 48,
            FontSize = _service?.ThemeTokens?.FontSizeBody ?? 13,
            Cursor = Cursors.Hand,
            Background = _palette.SurfaceAlt,
            Foreground = _palette.Foreground,
            BorderBrush = _palette.Separator
        };
        shutdown.Click += (_, _) => PowerCommands.Shutdown();
        Grid.SetColumn(shutdown, 0);
        footer.Children.Add(shutdown);

        var arrow = new Button
        {
            Content = "▾",
            Width = 28,
            Height = 28,
            FontSize = 12,
            Cursor = Cursors.Hand,
            Margin = new Thickness(2, 0, 0, 0),
            Background = _palette.SurfaceAlt,
            Foreground = _palette.Foreground,
            BorderBrush = _palette.Separator
        };
        arrow.Click += (_, _) => ShowPowerMenu(arrow);
        Grid.SetColumn(arrow, 1);
        footer.Children.Add(arrow);

        return footer;
    }

    private static void Explorer(string path)
        => Process.Start("explorer.exe", path);

    // ===== 电源菜单 =====

    private void ShowPowerMenu(FrameworkElement placementTarget)
    {
        // 统一弹层呈现（ShellWindow + 主题令牌）；弹层抢激活由 StartMenuWindow 的 IsOpen/Closed 豁免兜底。
        var items = new List<MenuItemDef>
        {
            // 规格：切换用户 / 注销 / 锁定 / 重新启动 / 睡眠 / 关机。
            PowerItem("start.power.switchuser", "切换用户", () => _ = PowerCommands.SwitchUser()),
            PowerItem("start.power.logoff", "注销", () => _ = PowerCommands.LogOff()),
            PowerItem("start.power.lock", "锁定", () => _ = PowerCommands.Lock()),
            PowerItem("start.power.restart", "重新启动", () => _ = PowerCommands.Restart()),
            PowerItem("start.power.sleep", "睡眠", () => _ = PowerCommands.Sleep()),
            PowerItem("start.power.shutdown", "关机", () => _ = PowerCommands.Shutdown()),
        };
        StartMenuPopup.ShowBelow(items, placementTarget);
    }

    private static MenuItemDef PowerItem(string id, string text, Action action) => new()
    {
        Id = id,
        Text = text,
        Command = action,
    };

    // ===== 所有程序树切换 =====

    private void ToggleAllPrograms()
    {
        if (_homeScroll is null || _treeScroll is null || _allProgramsButton is null)
        {
            return;
        }

        _allProgramsExpanded = !_allProgramsExpanded;
        _homeScroll.Visibility = _allProgramsExpanded ? Visibility.Collapsed : Visibility.Visible;
        _treeScroll.Visibility = _allProgramsExpanded ? Visibility.Visible : Visibility.Collapsed;
        _allProgramsButton.Content = _allProgramsExpanded ? "返回 ◀" : "所有程序 ▶";
    }

    // ===== 程序树 =====

    private void PopulateTree(ProgramFolder root, TreeView tree, bool expandAll)
    {
        var rootNode = new TreeViewItem
        {
            Header = BuildTreeNodeHeader(root.Name, null),
            IsExpanded = true
        };
        foreach (var sub in root.SubFolders)
        {
            rootNode.Items.Add(BuildFolderNode(sub, expandAll));
        }

        foreach (var item in root.Items)
        {
            rootNode.Items.Add(BuildLeafNode(item));
        }

        tree.Items.Add(rootNode);
    }

    private TreeViewItem BuildFolderNode(ProgramFolder folder, bool expandAll)
    {
        var node = new TreeViewItem
        {
            Header = BuildTreeNodeHeader(folder.Name, null),
            IsExpanded = expandAll
        };
        foreach (var sub in folder.SubFolders)
        {
            node.Items.Add(BuildFolderNode(sub, expandAll));
        }

        foreach (var item in folder.Items)
        {
            node.Items.Add(BuildLeafNode(item));
        }

        return node;
    }

    private FrameworkElement BuildTreeNodeHeader(string name, FrameworkElement? icon)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 3, 0, 3) // 节点行上下留白，降低"所有程序"树的拥挤感
        };
        if (icon is not null)
        {
            icon.Width = 20;
            icon.Height = 20;
            icon.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(icon);
        }

        row.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = _service?.ThemeTokens?.FontSizeBody ?? 14,
            Foreground = _palette.Foreground,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0)
        });
        return row;
    }

    private TreeViewItem BuildLeafNode(AppItem app)
    {
        var leaf = new TreeViewItem
        {
            Header = BuildTreeNodeHeader(app.Name, BuildLeafIcon(app)),
            Tag = app,
            Cursor = Cursors.Hand
        };
        leaf.MouseLeftButtonUp += (_, _) =>
        {
            if (_service is not null)
            {
                _service.ActivateOrLaunch(app);
                _service.Hide();
            }
        };
        if (_service is not null)
        {
            AppItemActions.AttachNative(leaf, app);
        }

        return leaf;
    }

    private FrameworkElement? BuildLeafIcon(AppItem app)
    {
        if (app.Id.IsEmpty || _service is null)
        {
            return null;
        }

        try
        {
            var image = new Image
            {
                Width = 16,
                Height = 16,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            _ = LoadIconAsync(app, image);
            return image;
        }
        catch
        {
            return null;
        }
    }

    private void OnTreeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ActivateSelected();
            e.Handled = true;
        }
    }

    private void ActivateSelected()
    {
        if (_tree?.SelectedItem is TreeViewItem { Tag: AppItem app })
        {
            _service?.ActivateOrLaunch(app);
            _service?.Hide();
        }
    }

    // ===== 搜索 =====

    /// <inheritdoc />
    public void RenderResults(string query, IReadOnlyList<SearchResult> results)
    {
        ResultsList.Items.Clear();
        var hasQuery = !string.IsNullOrWhiteSpace(query);
        ResultsList.Visibility = hasQuery ? Visibility.Visible : Visibility.Collapsed;
        if (hasQuery)
        {
            // 搜索时盖住主/树视图。
            if (_homeScroll is not null)
            {
                _homeScroll.Visibility = Visibility.Collapsed;
            }

            if (_treeScroll is not null)
            {
                _treeScroll.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            // 清除搜索时恢复当前所处视图（尊重"所有程序"切换状态），避免左栏空白。
            _homeScroll!.Visibility = _allProgramsExpanded ? Visibility.Collapsed : Visibility.Visible;
            _treeScroll!.Visibility = _allProgramsExpanded ? Visibility.Visible : Visibility.Collapsed;
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
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var icon = BuildResultIcon(result);
            if (icon is not null)
            {
                row.Children.Add(icon);
            }

            var label = new TextBlock
            {
                Text = result.Title,
                FontSize = _service?.ThemeTokens?.FontSizeBody ?? 13,
                Foreground = _palette.Foreground,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            if (!string.IsNullOrWhiteSpace(result.Subtitle))
            {
                label.ToolTip = result.Subtitle;
            }

            row.Children.Add(label);

            var item = new ListBoxItem { Content = row, Tag = result, Cursor = Cursors.Hand };
            item.MouseLeftButtonUp += (_, _) => ExecuteResult(result);
            if (result.AppItem is not null)
            {
                AppItemActions.AttachNative(item, result.AppItem);
            }

            ResultsList.Items.Add(item);
        }
    }

    private FrameworkElement? BuildResultIcon(SearchResult result)
    {
        var app = result.AppItem;
        if (app is null || app.Id.IsEmpty || _service is null)
        {
            return null;
        }

        try
        {
            var image = new Image
            {
                Width = 16,
                Height = 16,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            _ = LoadIconAsync(app, image);
            return image;
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc />
    public void RefreshItems()
    {
        // 数据刷新：请求整窗重建（布局引用为 null 保护）。
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
            // 图标失败静默（M10）。
        }
    }
}
