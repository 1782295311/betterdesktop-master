using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BetterDesktop.Shell.AppSource.Models;
using BetterDesktop.Shell.Search.Contracts;
using BetterDesktop.Shell.StartMenu.Contracts;
using BetterDesktop.Shell.StartMenu.Services;

namespace BetterDesktop.Shell.StartMenu.Windows.Layouts;

/// <summary>
/// 经典两栏布局（默认布局，实现 IStartMenuLayoutProvider + IStartMenuLayoutHost）：
/// 左栏 = 顶部搜索框 + 搜索结果列表 + 可展开程序树（IAppSourceService.GetProgramTree）；
/// 右栏 = 栏目扩展点（最近程序等）。程序项支持左键启动、右键菜单（固定/管理员/位置/卸载）。
/// </summary>
public sealed class ClassicLayout : IStartMenuLayoutProvider, IStartMenuLayoutHost
{
    private StartMenuService? _service;
    private TreeView? _tree;

    /// <inheritdoc />
    public string Name => "classic";

    /// <summary>搜索输入框。</summary>
    public TextBox SearchBox { get; private set; } = null!;

    /// <summary>搜索结果列表（无查询时折叠）。</summary>
    public ListBox ResultsList { get; private set; } = null!;

    /// <summary>程序树（可展开文件夹）。</summary>
    public TreeView ProgramTree => _tree!;

    /// <inheritdoc />
    public FrameworkElement BuildLayout(StartMenuService service)
    {
        _service = service;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // ---- 左栏：搜索 + 结果 + 程序树 ----
        var left = new Grid { Margin = new Thickness(12) };
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        SearchBox = new TextBox
        {
            FontSize = _service.ThemeTokens?.FontSizeInput ?? 14,
            Padding = new Thickness(6, 5, 6, 5),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        Grid.SetRow(SearchBox, 0);

        ResultsList = new ListBox
        {
            Visibility = Visibility.Collapsed,
            BorderThickness = new Thickness(0),
            MaxHeight = 240,
            Margin = new Thickness(0, 6, 0, 0)
        };
        Grid.SetRow(ResultsList, 1);

        _tree = new TreeView
        {
            BorderThickness = new Thickness(0),
            Margin = new Thickness(0, 6, 0, 0)
        };
        Grid.SetRow(_tree, 2);
        PopulateTree(service.GetProgramTree(), _tree);
        _tree.KeyDown += OnTreeKeyDown;
        _tree.MouseDoubleClick += (_, _) => ActivateSelected();

        left.Children.Add(SearchBox);
        left.Children.Add(ResultsList);
        left.Children.Add(_tree);
        Grid.SetColumn(left, 0);

        // ---- 右栏：栏目扩展点 ----
        var right = BuildRightPanel(service);
        Grid.SetColumn(right, 1);

        grid.Children.Add(left);
        grid.Children.Add(right);

        // 根容器：圆角/描边接入主题令牌（圆角本由 StartMenuWindow 基类令牌驱动，这里不再硬编码；见 M6）。
        var outer = new Border
        {
            CornerRadius = new CornerRadius(_service?.ThemeTokens?.CornerRadius ?? 8),
            Padding = new Thickness(1),
            Child = grid
        };
        return outer;
    }

    /// <summary>
    /// 右栏（栏目扩展点）构建，供多布局复用。
    /// 包 ScrollViewer：最近 + 位置 + 电源等多栏目内容超出菜单高度时可滚动，
    /// 避免"区块已注入但被裁剪看不见"。单个栏目异常不拖垮整个菜单（M10）。
    /// </summary>
    internal static FrameworkElement BuildRightPanel(StartMenuService service)
    {
        var right = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Width = 150,
            Margin = new Thickness(0, 12, 12, 12)
        };
        foreach (var provider in service.GetSectionProviders())
        {
            try
            {
                var element = provider.BuildSection(service);
                if (element is not null)
                {
                    right.Children.Add(element);
                }
            }
            catch
            {
                // 单个栏目构建失败不阻断其他栏目 / 整个菜单。
            }
        }

        return new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = right
        };
    }

    /// <inheritdoc />
    public void RenderResults(string query, IReadOnlyList<SearchResult> results)
    {
        if (_tree is null)
        {
            return;
        }

        ResultsList.Items.Clear();
        var hasQuery = !string.IsNullOrWhiteSpace(query);
        if (hasQuery)
        {
            if (results.Count == 0)
            {
                ResultsList.Items.Add(new ListBoxItem
                {
                    IsEnabled = false,
                    Content = new TextBlock { Text = "无匹配结果", FontSize = _service?.ThemeTokens?.FontSizeCaption ?? 12 }
                });
            }
            else
            {
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
                    item.ContextMenu = BuildResultContextMenu(result);
                    ResultsList.Items.Add(item);
                }
            }
        }

        ResultsList.Visibility = hasQuery ? Visibility.Visible : Visibility.Collapsed;
        _tree.Visibility = hasQuery ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <inheritdoc />
    public void RefreshItems()
    {
        if (_tree is null || _service is null)
        {
            return;
        }

        _tree.Items.Clear();
        PopulateTree(_service.GetProgramTree(), _tree);
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
        if (_service is not null && _tree?.SelectedItem is TreeViewItem { Tag: AppItem app })
        {
            _service.ActivateOrLaunch(app);
            _service.Hide();
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
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(result.LaunchPath) { UseShellExecute = true });
            }
        }
        catch
        {
            // 启动失败静默（M10）。
        }

        _service.Hide();
    }

    private ContextMenu? BuildResultContextMenu(SearchResult result)
    {
        return result.AppItem is not null ? AppItemActions.BuildContextMenu(result.AppItem, _service!) : null;
    }

    private void PopulateTree(ProgramFolder root, TreeView tree)
    {
        var rootNode = new TreeViewItem { Header = root.Name, IsExpanded = true };
        foreach (var sub in root.SubFolders)
        {
            rootNode.Items.Add(BuildFolderNode(sub));
        }

        foreach (var item in root.Items)
        {
            rootNode.Items.Add(BuildLeafNode(item));
        }

        tree.Items.Add(rootNode);
    }

    private TreeViewItem BuildFolderNode(ProgramFolder folder)
    {
        var node = new TreeViewItem
        {
            Header = folder.Name,
            IsExpanded = folder.SubFolders.Count + folder.Items.Count <= 8
        };
        foreach (var sub in folder.SubFolders)
        {
            node.Items.Add(BuildFolderNode(sub));
        }

        foreach (var item in folder.Items)
        {
            node.Items.Add(BuildLeafNode(item));
        }

        return node;
    }

    private TreeViewItem BuildLeafNode(AppItem app)
    {
        var leaf = new TreeViewItem { Header = app.Name, Tag = app, Cursor = Cursors.Hand };
        leaf.MouseLeftButtonUp += (_, _) =>
        {
            _service?.ActivateOrLaunch(app);
            _service?.Hide();
        };
        if (_service is not null)
        {
            leaf.ContextMenu = AppItemActions.BuildContextMenu(app, _service);
        }

        return leaf;
    }
}
