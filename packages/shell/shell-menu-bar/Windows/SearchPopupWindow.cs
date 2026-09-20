// BetterDesktop.Shell.MenuBar — 搜索独立弹出面板（继承 MenuBarPopupWindow：失焦关闭）
// 复用 shell-search 的聚合搜索服务（IStartMenuSearchService：程序 / 设置 / 文件三类）。
// 设计：
//   - 顶部搜索框（TextBox）；输入后防抖 350ms 触发搜索（SearchAsync 同步执行各 Provider，
//     必须后台 Task 执行避免卡 UI 线程；防抖由 UI 层负责，shell-search README 明示）。
//   - 结果按 Category（App / Settings / File）分组展示；点击行启动：
//       结果.Execute() → AppItem(ShortcutPath/TargetPath，UWP 走 shell:AppsFolder AUMID) → LaunchPath。
//   - 服务缺失（null）时显示占位"搜索不可用"，不崩溃（M10 降级）。
//   - 颜色/字体/动画全部走主题令牌（基类 ApplyContent 统一挂载）。

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using BetterDesktop.Shell.AppSource.Contracts;
using BetterDesktop.Shell.AppSource.Models;
using BetterDesktop.Shell.ContextMenus.Contracts;
using BetterDesktop.Shell.Core.Services;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.MenuBar.Contracts;
using BetterDesktop.Shell.Pinning.Contracts;
using BetterDesktop.Shell.Search.Contracts;

namespace BetterDesktop.Shell.MenuBar.Windows;

// ── 本文件方法级白话索引（全局搜索面板，白话 → 方法）──
//   "面板整体 / 执行搜索 / 渲染结果" → BuildContent / RunSearchAsync / RenderResults
//   "分组标题 / 单条结果行 / 详情文案" → CreateGroupHeader / CreateResultRow / ResolveDetailText；结果图标异步加载 LoadResultIconAsync
//   "结果右键菜单 / 打开 / 在资源管理器定位" → ShowResultMenu（AppendAppEntryItems 走共用构建器）/ Launch / ResolveRevealPath（系统级动作在 shell-core/Services/AppEntryActions.cs）
//   "空结果态 / 类型字形"             → ShowEmpty / CreateSettingsGlyph / CreateFolderGlyph
//   搜索数据源（应用/设置/文件）在 StartMenuService.SearchAsync 与各搜索 Provider。
// ────────────────────────────────────

/// <summary>菜单栏搜索面板：搜索程序 / 设置 / 文件（复用 shell-search 聚合服务）。</summary>
internal sealed class SearchPopupWindow : MenuBarPopupWindow
{
    // 含搜索输入框：禁用 WS_EX_NOACTIVATE，否则点击后窗口不获焦点、键盘输入落不进 TextBox。
    protected override bool UseNoActivateWindowStyle => false;

    private const double DefaultWidth = 440;

    /// <summary>组内默认展示条数（2026-09-17 分组展示改造）：超过即折叠，组尾提供「展开全部」入口——
    /// 所有适配结果全量返回，展示层默认收敛，由用户主动展开/筛选减少显示。</summary>
    private const int CollapsedGroupMax = 8;

    private readonly IStartMenuSearchService? _search;
    private readonly IAppIconService? _appIcon;
    /// <summary>固定服务。已由 <c>MenuBarPlugin.Inject</c> 声明为硬依赖 → 正常路径恒非 null；
    /// 可空只作兜底（依赖未满足时面板仍可用，仅省略固定项，不整面板失效）。</summary>
    private readonly IPinningService? _pinning;
    /// <summary>应用源服务（LNK/URL/EXE → AppItem，<c>ResolveFromPath</c>）：搜索结果右键把
    /// 文件类命中的程序（如引擎索引出的 MAA.exe）解析成应用条目，补上「固定到 Dock」（2026-09-17）。</summary>
    private readonly IAppSourceService? _appSource;
    private readonly BetterDesktop.Shell.Clipboard.Contracts.IClipboardService? _clipboard;
    private readonly DispatcherTimer _debounce;
    private TextBox? _queryBox;
    private StackPanel? _resultHost;
    private FrameworkElement? _recentClipboardBlock;
    private Border? _filterBar;
    private int _generation; // 丢弃过期结果：每次新搜索递增，异步回写前比对
    private IReadOnlyList<SearchResult>? _lastResults; // 最近一次渲染的结果集（筛选/展开重渲染用）
    private string? _activeCategoryFilter;             // null=全部；"App"/"Settings"/"File"=只看该类
    private readonly HashSet<string> _expandedCategories = new(StringComparer.OrdinalIgnoreCase);

    public SearchPopupWindow(
        IStartMenuSearchService? search,
        IAppIconService? appIcon,
        IVibrancyService vibrancy,
        IAppearanceService? appearance = null,
        IPinningService? pinning = null,
        BetterDesktop.Shell.Clipboard.Contracts.IClipboardService? clipboard = null,
        IAppSourceService? appSource = null)
        : base(vibrancy, appearance)
    {
        _search = search;
        _appIcon = appIcon;
        _pinning = pinning;
        _clipboard = clipboard;
        _appSource = appSource;
        Width = DefaultWidth;
        MinWidth = DefaultWidth;
        SizeToContent = SizeToContent.Height;
        // 防抖：输入停止 350ms 后才搜索，避免逐键触发 SearchAsync（同步枚举，卡 UI）。
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            _ = RunSearchAsync();
        };
    }

    public FrameworkElement BuildPreviewContent() => BuildContent();

    protected override FrameworkElement BuildContent()
    {
        var root = new Border
        {
            Padding = new Thickness(10),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };

        var column = new StackPanel { Orientation = Orientation.Vertical };

        // ---- 搜索框 ----
        // 白底黑字：显式设置 Foreground（否则基类 BindThemeForeground 会把未设色的 Control
        // 绑到 ThemeForeground——暗色主题下即白字，白字白底不可读）。显式设色后被基类跳过。
        _queryBox = new TextBox
        {
            Height = 30,
            FontSize = 13,
            Foreground = Brushes.Black,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(8, 0, 8, 0),
            CaretBrush = Brushes.Black
        };
        var inputBorder = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = Brushes.White,
            Child = _queryBox,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };
        _queryBox.TextChanged += (_, _) =>
        {
            // 防抖：每次输入重启 350ms 计时；空串则立即清空结果
            _debounce.Stop();
            if (string.IsNullOrWhiteSpace(_queryBox.Text))
            {
                ShowEmpty("输入关键字开始搜索");
                SetRecentClipboardVisible(true);
                return;
            }
            SetRecentClipboardVisible(false);
            _debounce.Start();
        }; _queryBox.PreviewKeyDown += (_, e) =>
        {
            // Esc 关闭面板
            if (e.Key == System.Windows.Input.Key.Escape) { Close(); e.Handled = true; }
        };
        column.Children.Add(inputBorder);

        column.Children.Add(CreateSeparator());

        // ---- 最近复制区块（I7）：未输入时展示最近复制内容 + 面板入口 ----
        _recentClipboardBlock = CreateRecentClipboardBlock();
        if (_recentClipboardBlock is not null)
        {
            column.Children.Add(_recentClipboardBlock);
            column.Children.Add(CreateSeparator());
        }

        // ---- 结果区 ----
        _resultHost = new StackPanel { Orientation = Orientation.Vertical };

        // 类别筛选条（2026-09-17）：全部 / 应用 / 设置 / 文件 + 计数，点击切换只看某类。
        // 全量结果由这里交给用户主动收敛，而不是引擎/聚合层替他截断。
        _filterBar = new Border
        {
            Margin = new Thickness(0, 2, 0, 6),
            Padding = new Thickness(2, 0, 2, 0),
            Visibility = Visibility.Collapsed,
            Child = new WrapPanel { Orientation = Orientation.Horizontal }
        };
        column.Children.Add(_filterBar);

        var scroll = new ScrollViewer
        {
            Content = _resultHost,
            MaxHeight = 500,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        column.Children.Add(scroll);

        root.Child = column;

        if (_search is null)
        {
            ShowEmpty("搜索服务不可用（shell.search 未加载）");
        }
        else
        {
            ShowEmpty("输入关键字开始搜索");
            // 面板打开即聚焦搜索框（不抢输入法焦点，只抢键盘焦点到输入框）
            Dispatcher.BeginInvoke(new Action(() => _queryBox?.Focus()), DispatcherPriority.Input);
        }

        return root;
    }

    /// <summary>后台执行搜索（SearchAsync 同步枚举，后台 Task 防卡 UI）。</summary>
    private async Task RunSearchAsync()
    {
        var query = _queryBox?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(query) || _search is null) return;
        var gen = ++_generation;
        try
        {
            var results = await Task.Run(() => _search.SearchAsync(query, CancellationToken.None)).ConfigureAwait(true);
            // 丢弃过期结果：搜索期间用户又输入了
            if (gen != _generation || _resultHost is null) return;
            if (results.Count == 0)
            {
                ShowEmpty("没有找到匹配结果");
                return;
            }
            RenderResults(results);
        }
        catch
        {
            // M10：搜索失败静默降级，不打断菜单栏其余部分
            if (gen == _generation) ShowEmpty("搜索失败，请重试");
        }
    }

    /// <summary>
    /// 渲染结果：**按组渲染**（2026-09-17 分组展示改造）。聚合层已按「App 固定第一 →
    /// 其余类别按命中数升序」排好组序，组内 Score 降序；本方法保持该顺序逐组渲染：
    /// 组标题（名称 + 计数 + 折叠/展开箭头，可点击切换）、组内行（默认前
    /// <see cref="CollapsedGroupMax"/> 条，组尾「展开全部 N 条」）、顶部筛选条（全部/应用/设置/文件）。
    /// 全量结果不做截断——展示层默认收敛 + 用户主动筛选/展开。
    /// </summary>
    private void RenderResults(IReadOnlyList<SearchResult> results)
    {
        if (_resultHost is null) return;
        _lastResults = results;
        _resultHost.Children.Clear();

        // 类别计数（全量，非筛选后）——筛选条显示真实规模
        var counts = results
            .GroupBy(x => x.Category, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        RenderFilterBar(counts);

        var filtered = _activeCategoryFilter is null
            ? results
            : results.Where(r => string.Equals(r.Category, _activeCategoryFilter, StringComparison.OrdinalIgnoreCase)).ToList();
        if (filtered.Count == 0)
        {
            // 保留筛选条：用户可切回「全部」或其它类别（此时不隐藏，避免回到无结果的死胡同）
            ShowEmpty(_activeCategoryFilter is null ? "没有找到匹配结果" : "该类别没有匹配结果", keepFilterBar: true);
            return;
        }

        // 保持聚合层组序（GroupBy 保序），逐组渲染
        string? currentCategory = null;
        var bucket = new List<SearchResult>();
        void Flush()
        {
            if (bucket.Count > 0)
            {
                RenderCategory(currentCategory!, bucket);
            }
        }

        foreach (var result in filtered)
        {
            if (!string.Equals(result.Category, currentCategory, StringComparison.OrdinalIgnoreCase))
            {
                Flush();
                currentCategory = result.Category;
                bucket.Clear();
            }

            bucket.Add(result);
        }

        Flush();
    }

    /// <summary>渲染单个类别组：标题（计数 + 折叠箭头）→ 组内行（默认前 N 条）→ 展开入口。</summary>
    private void RenderCategory(string category, List<SearchResult> items)
    {
        _resultHost!.Children.Add(CreateGroupHeader(category, items.Count));

        var expanded = _expandedCategories.Contains(category);
        var visibleCount = expanded ? items.Count : Math.Min(CollapsedGroupMax, items.Count);
        for (var i = 0; i < visibleCount; i++)
        {
            _resultHost.Children.Add(CreateResultRow(items[i]));
        }

        if (!expanded && items.Count > CollapsedGroupMax)
        {
            _resultHost.Children.Add(CreateExpandRow(category, items.Count));
        }
    }

    /// <summary>组尾「展开全部 N 条」入口（组内超过折叠上限时显示）。</summary>
    private FrameworkElement CreateExpandRow(string category, int totalCount)
    {
        var row = new Border
        {
            MinHeight = 30,
            Margin = new Thickness(2, 1, 2, 1),
            Padding = new Thickness(12, 4, 8, 4),
            CornerRadius = new CornerRadius(6),
            Cursor = System.Windows.Input.Cursors.Hand,
            Background = Brushes.Transparent
        };
        var label = new TextBlock
        {
            Text = $"展开全部 {totalCount} 条",
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        SetThemeBinding(label, TextBlock.ForegroundProperty, "ThemeMutedForeground");
        row.Child = label;
        row.MouseEnter += (_, _) => row.Background = MenuBarTheme.Hover;
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, _) =>
        {
            _expandedCategories.Add(category);
            if (_lastResults is not null)
            {
                RenderResults(_lastResults);
            }
        };
        return row;
    }

    /// <summary>分组标题（可点击展开/折叠该组）：显示「▸/▾ 类别名 (计数)」。</summary>
    private FrameworkElement CreateGroupHeader(string category, int count)
    {
        var expanded = _expandedCategories.Contains(category);
        var label = new TextBlock
        {
            Text = $"{(expanded ? "▾ " : "▸ ")}{CategoryDisplayName(category)} ({count})",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(4, 8, 0, 2),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "点击展开 / 折叠该类别"
        };
        SetThemeBinding(label, TextBlock.ForegroundProperty, "ThemeMutedForeground");
        label.MouseLeftButtonUp += (_, _) =>
        {
            if (expanded)
            {
                _expandedCategories.Remove(category);
            }
            else
            {
                _expandedCategories.Add(category);
            }

            if (_lastResults is not null)
            {
                RenderResults(_lastResults);
            }
        };
        return label;
    }

    /// <summary>渲染类别筛选条（全部/应用/设置/文件 + 计数），点击切换当前类别过滤。</summary>
    private void RenderFilterBar(IReadOnlyDictionary<string, int> counts)
    {
        if (_filterBar is null || _filterBar.Child is not WrapPanel panel)
        {
            return;
        }

        panel.Children.Clear();
        var total = counts.Values.Sum();
        AddFilterChip(panel, "全部", null, total);
        AddFilterChip(panel, "应用", "App", counts.TryGetValue("App", out var a) ? a : 0);
        AddFilterChip(panel, "设置", "Settings", counts.TryGetValue("Settings", out var s) ? s : 0);
        AddFilterChip(panel, "文件", "File", counts.TryGetValue("File", out var f) ? f : 0);
        _filterBar.Visibility = Visibility.Visible;
    }

    /// <summary>单个筛选 Chip：选中高亮（主题强调背景 + 加粗），点击切换过滤类别（null=全部）。</summary>
    private void AddFilterChip(WrapPanel panel, string name, string? category, int count)
    {
        var selected = string.Equals(category ?? string.Empty, _activeCategoryFilter ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        var chip = new Border
        {
            Margin = new Thickness(0, 0, 6, 4),
            Padding = new Thickness(8, 2, 8, 2),
            CornerRadius = new CornerRadius(10),
            Background = selected ? MenuBarTheme.Hover : Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        var label = new TextBlock
        {
            Text = $"{name} ({count})",
            FontSize = 11,
            FontWeight = selected ? FontWeights.Bold : FontWeights.Normal
        };
        SetThemeBinding(label, TextBlock.ForegroundProperty, "ThemeForeground");
        chip.Child = label;
        chip.MouseEnter += (_, _) =>
        {
            if (!selected)
            {
                chip.Background = MenuBarTheme.Hover;
            }
        };
        chip.MouseLeave += (_, _) =>
        {
            if (!selected)
            {
                chip.Background = Brushes.Transparent;
            }
        };
        chip.MouseLeftButtonUp += (_, _) =>
        {
            _activeCategoryFilter = category;
            if (_lastResults is not null)
            {
                RenderResults(_lastResults);
            }
        };
        panel.Children.Add(chip);
    }

    private static string CategoryDisplayName(string category) => category.ToLowerInvariant() switch
    {
        "app" => "应用",
        "settings" => "设置",
        "file" => "文件",
        _ => category
    };

    /// <summary>
    /// 一条结果行（图标列 + 两行文本，适用于所有类别）：上行主标题（加粗）、下行路径/说明（次要色，可截断）。
    /// 副标题为空时从 AppItem 补真实目标路径（TargetPath → ShortcutPath），保证每条结果都能看到来源。
    /// 图标按类别取：App=真实应用图标（IAppIconService 异步提取）；File=关联图标/文件夹自绘；
    /// Settings=自绘齿轮（字体码位不可信，一律自绘几何）；提取失败=空白占位（对齐不乱）。
    /// </summary>
    private FrameworkElement CreateResultRow(SearchResult result)
    {
        var row = new Border
        {
            MinHeight = 44,
            Margin = new Thickness(2, 1, 2, 1),
            Padding = new Thickness(10, 5, 8, 5),
            CornerRadius = new CornerRadius(6),
            Cursor = System.Windows.Input.Cursors.Hand,
            Background = Brushes.Transparent
        };

        var grid = new Grid { VerticalAlignment = VerticalAlignment.Center };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 图标列（始终占位，保证列对齐）
        var iconHost = new Grid { Width = 22, Height = 22, VerticalAlignment = VerticalAlignment.Center };
        if (string.Equals(result.Category, "Settings", StringComparison.OrdinalIgnoreCase))
        {
            iconHost.Children.Add(CreateSettingsGlyph());
        }
        else if (result.LaunchPath is not null && Directory.Exists(result.LaunchPath))
        {
            iconHost.Children.Add(CreateFolderGlyph());
        }
        else
        {
            var image = new Image
            {
                Width = 20,
                Height = 20,
                Stretch = Stretch.Uniform,
                SnapsToDevicePixels = true
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            iconHost.Children.Add(image);
            _ = LoadResultIconAsync(result, image);
        }
        Grid.SetColumn(iconHost, 0);
        grid.Children.Add(iconHost);

        var column = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        // 上行：主标题
        var title = new TextBlock
        {
            Text = result.Title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        column.Children.Add(title);

        // 下行：路径 / 说明（Subtitle 缺省时从 AppItem 补真实路径）
        var detail = ResolveDetailText(result);
        if (!string.IsNullOrWhiteSpace(detail))
        {
            var sub = new TextBlock
            {
                Text = detail,
                FontSize = 10.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 1, 0, 0)
            };
            SetThemeBinding(sub, TextBlock.ForegroundProperty, "ThemeMutedForeground");
            column.Children.Add(sub);
        }

        Grid.SetColumn(column, 2);
        grid.Children.Add(column);
        row.Child = grid;

        row.MouseEnter += (_, _) => row.Background = MenuBarTheme.Hover;
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonDown += (_, _) => row.Background = MenuBarTheme.Pressed;
        row.MouseLeftButtonUp += (_, _) =>
        {
            Launch(result);
            Hide(); // 点结果即收起面板
        };
        row.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            ShowResultMenu(row, result);
        };
        return row;
    }

    // ============================================================
    //  结果右键菜单（"谁的菜单谁管理"：本面板自建 WPF ContextMenu）
    // ============================================================

    /// <summary>
    /// 结果右键菜单。**应用类**走共用构建器 <see cref="AppEntryMenuBuilder"/>（与 dock 应用提取器同一套
    /// 项集与出现条件，2026-09-14 S3）；**设置 / 文件类不是「应用条目」**，保留本面板自己的通用项
    /// （打开 / 位置 / 复制路径 / 复制链接）。
    /// </summary>
    private void ShowResultMenu(FrameworkElement anchor, SearchResult result)
    {
        var menu = new ContextMenu();
        var hasItem = false;

        if (result.AppItem is not null)
        {
            hasItem |= AppendAppEntryItems(menu, result);
        }
        else
        {
            // 非应用条目：打开（默认动作，加粗；与左键行为一致）+ 位置/路径
            hasItem |= AddMenuItem(menu, "打开", isDefault: true, () =>
            {
                Launch(result);
                Hide();
            });

            // 文件类命中里的「程序」（.exe/.lnk/.url）解析成应用条目 → 补「固定到 Dock」
            //（2026-09-17：引擎索引命中的 MAA.exe 等此前只有文件分支三项，无法固定；规划
            //  2026-09-13 D3「搜一个应用→右键有固定到 Dock」对文件来源的程序同样生效）。
            var fileAsApp = _appSource?.ResolveFromPath(result.LaunchPath ?? string.Empty);
            if (fileAsApp is not null && _pinning is not null)
            {
                if (IsPinnedInDock(fileAsApp))
                {
                    hasItem |= AddMenuItem(menu, "从 Dock 移除", isDefault: false, () => _pinning.Unpin("dock", fileAsApp.Id));
                }
                else
                {
                    hasItem |= AddMenuItem(menu, "固定到 Dock", isDefault: false, () => _pinning.Pin("dock", fileAsApp));
                }
            }

            var path = ResolveRevealPath(result);
            if (!string.IsNullOrEmpty(path))
            {
                hasItem |= AddMenuItem(menu, "打开所在位置", isDefault: false, () => AppEntryActions.RevealInExplorer(path));
                hasItem |= AddMenuItem(menu, "复制路径", isDefault: false, () => AppEntryActions.CopyToClipboard(path));
            }
            else if (result.LaunchPath is not null && result.LaunchPath.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase))
            {
                hasItem |= AddMenuItem(menu, "复制链接", isDefault: false, () => AppEntryActions.CopyToClipboard(result.LaunchPath));
            }
        }

        if (!hasItem)
        {
            return;
        }

        // 弹菜单期间挂起"外点收起"：点菜单项在几何上位于面板外，不挂起会菜单未执行就收面板
        SetOutsideClickHideSuppressed(true);
        menu.Closed += (_, _) => SetOutsideClickHideSuppressed(false);

        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.AbsolutePoint;
        var physical = anchor.PointToScreen(new Point(0, anchor.ActualHeight));
        var dpi = VisualTreeHelper.GetDpi(anchor).PixelsPerDip;
        menu.HorizontalOffset = physical.X / dpi;
        menu.VerticalOffset = physical.Y / dpi;
        menu.IsOpen = true;
    }

    /// <summary>
    /// 应用类结果 → 共用构建器的项集并渲染。事实（路径 / 固定态 / 卸载命令）在这里采集，
    /// 动作回调由本面板实现（构建器不含 UI，也不执行动作）。
    /// </summary>
    private bool AppendAppEntryItems(ContextMenu menu, SearchResult result)
    {
        var appItem = result.AppItem!;
        var path = ResolveRevealPath(result);
        var pinning = _pinning;

        var items = AppEntryMenuBuilder.Build(new AppEntryMenuContext
        {
            Path = path,
            IsPinned = IsPinnedInDock(appItem),
            // 搜索结果的默认动作叫「打开」（加粗）；dock 图标叫「启动」——语义确有差异，故可配。
            LaunchText = "打开",
            LaunchIsDefault = true,
            UninstallCommand = appItem.UninstallCommand,
            Actions = new AppEntryMenuActions
            {
                Launch = () =>
                {
                    Launch(result);
                    Hide();
                },
                // 固定服务缺失（兜底路径）时传 null → 构建器整项省略，不显示点了没反应的项
                Pin = pinning is null ? null : () => pinning.Pin("dock", appItem),
                Unpin = pinning is null ? null : () => pinning.Unpin("dock", appItem.Id),
                // 系统级动作统一走 shell-core 公共实现（与 dock 应用提取器同一套行为）
                RevealInExplorer = path is null ? null : () => AppEntryActions.RevealInExplorer(path),
                CopyPath = path is null ? null : () => AppEntryActions.CopyToClipboard(path),
                RunAsAdmin = () => AppEntryActions.RunAsAdmin(path),
                OpenInTerminal = () => AppEntryActions.OpenInTerminal(path),
                ShowProperties = () => AppEntryActions.ShowProperties(path),
                // 卸载：命令与动作需同时具备（构建器负责该判定），无命令的搜索结果不会出现该项
                Uninstall = string.IsNullOrWhiteSpace(appItem.UninstallCommand)
                    ? null
                    : () => AppEntryActions.RunUninstaller(appItem.UninstallCommand),
            },
        });

        var added = false;
        foreach (var item in items)
        {
            added |= AddMenuItemDef(menu, item);
        }

        return added;
    }

    /// <summary>已固定判定：服务缺失或判定抛异常一律按「未固定」处理（不因此让整个菜单失效）。</summary>
    private bool IsPinnedInDock(AppItem appItem)
    {
        if (_pinning is null)
        {
            return false;
        }

        try
        {
            return _pinning.IsPinned("dock", appItem.Id);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>把构建器产出的 <see cref="MenuItemDef"/> 渲染成 WPF 菜单项（渲染仍由本面板自理）。</summary>
    private static bool AddMenuItemDef(ContextMenu menu, MenuItemDef def)
    {
        var item = new MenuItem { Header = def.Text };
        if (def.IsDefault)
        {
            item.FontWeight = FontWeights.Bold;
        }

        if (def.Kind == MenuItemKind.Submenu)
        {
            foreach (var child in def.Children ?? Array.Empty<MenuItemDef>())
            {
                item.Items.Add(BuildCommandItem(child));
            }
        }
        else
        {
            item.Click += (_, _) => InvokeSafely(def.Command);
        }

        _ = menu.Items.Add(item);
        return true;
    }

    /// <summary>二级子菜单项（构建器目前只产出命令类子项）。</summary>
    private static MenuItem BuildCommandItem(MenuItemDef def)
    {
        var item = new MenuItem { Header = def.Text };
        item.Click += (_, _) => InvokeSafely(def.Command);
        return item;
    }

    private static void InvokeSafely(Action? action)
    {
        try
        {
            action?.Invoke();
        }
        catch
        {
            // 菜单动作失败静默（M10）
        }
    }

    private static bool AddMenuItem(ContextMenu menu, string header, bool isDefault, Action action)
    {
        var item = new MenuItem { Header = header };
        if (isDefault)
        {
            item.FontWeight = FontWeights.Bold;
        }
        item.Click += (_, _) =>
        {
            try { action(); }
            catch { /* 菜单动作失败静默（M10） */ }
        };
        _ = menu.Items.Add(item);
        return true;
    }

    /// <summary>可"打开所在位置/复制"的真实文件路径：应用取 TargetPath→ShortcutPath，文件取 LaunchPath（排除 ms-settings: URI）。</summary>
    private static string? ResolveRevealPath(SearchResult result)
    {
        if (result.AppItem is not null)
        {
            if (!string.IsNullOrWhiteSpace(result.AppItem.TargetPath) && File.Exists(result.AppItem.TargetPath))
            {
                return result.AppItem.TargetPath;
            }
            if (!string.IsNullOrWhiteSpace(result.AppItem.ShortcutPath) && File.Exists(result.AppItem.ShortcutPath))
            {
                return result.AppItem.ShortcutPath;
            }
        }

        if (!string.IsNullOrWhiteSpace(result.LaunchPath)
            && !result.LaunchPath.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase)
            && (File.Exists(result.LaunchPath) || Directory.Exists(result.LaunchPath)))
        {
            return result.LaunchPath;
        }

        return null;
    }



    /// <summary>
    /// 异步提取结果图标（大图标缩小显示，保证清晰）：应用走 IAppIconService（ExtraLarge/Jumbo 高清源），
    /// 文件走 ManagedShell IconHelper（ExtraLarge 48px 原生帧，与 App 类同管线）。
    /// 刻意不用 Icon.ExtractAssociatedIcon——它只有 32×32，高 DPI 下缩小显示会发糊。
    /// 失败保持空白占位（列对齐不乱，M10 降级）。
    /// </summary>
    private async Task LoadResultIconAsync(SearchResult result, Image target)
    {
        try
        {
            ImageSource? source = null;

            if (result.AppItem is not null)
            {
                if (_appIcon is not null)
                {
                    source = await _appIcon.GetIconAsync(result.AppItem, CancellationToken.None).ConfigureAwait(true);
                }
            }
            else if (!string.IsNullOrWhiteSpace(result.LaunchPath) && File.Exists(result.LaunchPath))
            {
                var path = result.LaunchPath;
                // 文件类：与 shell-app-source 的图标服务同管线（ManagedShell）取 ExtraLarge(48) 原生帧。
                // 关键：IconHelper 必须在其专用 COM 任务调度器（IconScheduler）上执行——
                // 普通线程池线程 COM 初始化不对，提取会静默失败（此前图标空白的根因）。
                // GetImageFromHIcon 内部会自动 Freeze 并 DestroyIcon(hIcon)。
                source = await Task.Factory.StartNew(() =>
                {
                    var hIcon = ManagedShell.Common.Helpers.IconHelper.GetIconByFilename(
                        path, ManagedShell.Common.Enums.IconSize.ExtraLarge);
                    return hIcon == IntPtr.Zero
                        ? null
                        : ManagedShell.Common.Helpers.IconImageConverter.GetImageFromHIcon(hIcon);
                }, CancellationToken.None, TaskCreationOptions.None, ManagedShell.Common.Helpers.IconHelper.IconScheduler)
                    .ConfigureAwait(true);

                // 回退：IconScheduler 提取失败（无关联图标等）时用 ExtractAssociatedIcon（32px，聊胜于无）
                if (source is null)
                {
                    try
                    {
                        using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                        if (icon is not null)
                        {
                            source = Imaging.CreateBitmapSourceFromHIcon(
                                icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                            source.Freeze();
                        }
                    }
                    catch { /* 回退也失败：保持空白占位 */ }
                }
            }

            if (source is not null)
            {
                target.Source = source;
            }
        }
        catch
        {
            // 图标提取失败：保持空白占位（列对齐不乱，M10 降级）
        }
    }

    /// <summary>设置类结果的自绘齿轮占位图标（圆环 + 4 齿 + 中心点）。</summary>
    private static FrameworkElement CreateSettingsGlyph()
    {
        var brush = MenuBarTheme.Foreground;
        var canvas = new Canvas { Width = 16, Height = 16 };

        var ring = new Ellipse
        {
            Width = 7.5,
            Height = 7.5,
            Stroke = brush,
            StrokeThickness = 1.3,
            Fill = Brushes.Transparent
        };
        Canvas.SetLeft(ring, 4.25);
        Canvas.SetTop(ring, 4.25);
        canvas.Children.Add(ring);

        var dot = new Ellipse { Width = 2.2, Height = 2.2, Fill = brush };
        Canvas.SetLeft(dot, 6.9);
        Canvas.SetTop(dot, 6.9);
        canvas.Children.Add(dot);

        // 4 齿：上/下/左/右短线（从圆环向外延伸）
        void AddTooth(double x, double y, double w, double h)
        {
            var tooth = new Rectangle { Width = w, Height = h, RadiusX = 0.5, RadiusY = 0.5, Fill = brush };
            Canvas.SetLeft(tooth, x);
            Canvas.SetTop(tooth, y);
            canvas.Children.Add(tooth);
        }
        AddTooth(7.2, 1.2, 1.6, 2.6);   // 上
        AddTooth(7.2, 12.2, 1.6, 2.6);  // 下
        AddTooth(1.2, 7.2, 2.6, 1.6);   // 左
        AddTooth(12.2, 7.2, 2.6, 1.6);  // 右

        return new Viewbox { Child = canvas, Width = 16, Height = 16, Stretch = Stretch.Uniform };
    }

    /// <summary>文件夹自绘图标（标签 + 主体轮廓），用于文件类结果中目录条目的占位。</summary>
    private static FrameworkElement CreateFolderGlyph()
    {
        var brush = MenuBarTheme.Foreground;
        var canvas = new Canvas { Width = 16, Height = 14 };

        // 标签
        var tab = new Rectangle
        {
            Width = 6,
            Height = 3,
            RadiusX = 0.8,
            RadiusY = 0.8,
            Fill = brush
        };
        Canvas.SetLeft(tab, 1.0);
        Canvas.SetTop(tab, 2.0);
        canvas.Children.Add(tab);

        // 主体（描边圆角矩形）
        var body = new Rectangle
        {
            Width = 14,
            Height = 9,
            RadiusX = 1.2,
            RadiusY = 1.2,
            Stroke = brush,
            StrokeThickness = 1.2,
            Fill = Brushes.Transparent
        };
        Canvas.SetLeft(body, 1.0);
        Canvas.SetTop(body, 4.0);
        canvas.Children.Add(body);

        return new Viewbox { Child = canvas, Width = 16, Height = 14, Stretch = Stretch.Uniform };
    }

    /// <summary>结果详情文本：优先 Subtitle；为空且是应用结果时取真实目标路径（TargetPath → ShortcutPath）。</summary>
    private static string ResolveDetailText(SearchResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.Subtitle)) return result.Subtitle;
        if (result.AppItem is not null)
        {
            if (!string.IsNullOrWhiteSpace(result.AppItem.TargetPath)) return result.AppItem.TargetPath!;
            if (!string.IsNullOrWhiteSpace(result.AppItem.ShortcutPath)) return result.AppItem.ShortcutPath!;
        }
        return string.Empty;
    }

    /// <summary>启动结果：Execute → AppItem → LaunchPath（自包含，不依赖开始菜单服务）。</summary>
    private static void Launch(SearchResult result)
    {
        try
        {
            if (result.Execute is not null) { result.Execute(); return; }

            if (result.AppItem is not null)
            {
                // UWP 应用：通过 AppUserModelId 经 shell:AppsFolder 启动
                if (!string.IsNullOrWhiteSpace(result.AppItem.AppUserModelId))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                        "shell:AppsFolder\\" + result.AppItem.AppUserModelId)
                    { UseShellExecute = true });
                    return;
                }
                var appPath = !string.IsNullOrWhiteSpace(result.AppItem.TargetPath)
                    ? result.AppItem.TargetPath
                    : result.AppItem.ShortcutPath;
                if (!string.IsNullOrWhiteSpace(appPath))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(appPath) { UseShellExecute = true });
                    return;
                }
            }

            if (!string.IsNullOrWhiteSpace(result.LaunchPath))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(result.LaunchPath) { UseShellExecute = true });
            }
        }
        catch
        {
            // 启动失败静默，不打断菜单栏（M10）
        }
    }

    private void ShowEmpty(string text, bool keepFilterBar = false)
    {
        if (_resultHost is null) return;
        _resultHost.Children.Clear();
        if (!keepFilterBar && _filterBar is not null)
        {
            _filterBar.Visibility = Visibility.Collapsed;
        }

        var hint = new TextBlock
        {
            Text = text,
            FontSize = 12,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 18, 0, 18),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        SetThemeBinding(hint, TextBlock.ForegroundProperty, "ThemeMutedForeground");
        _resultHost.Children.Add(hint);
    }

    private void SetRecentClipboardVisible(bool visible)
    {
        if (_recentClipboardBlock is not null)
        {
            _recentClipboardBlock.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>最近复制区块：最近一条内容 + 「查看历史面板」入口（点击开面板）。</summary>
    private FrameworkElement? CreateRecentClipboardBlock()
    {
        if (_clipboard is null)
        {
            return null;
        }

        var last = _clipboard.GetLastCopiedContent();
        if (last is null)
        {
            return null;
        }

        var block = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = Brushes.White,
            Padding = new Thickness(10, 8, 10, 8),
            Cursor = Cursors.Hand,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock
        {
            Text = "📋",
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        });

        var textColumn = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        string preview = last.ContentOrPath ?? "(空)";
        if (preview.Length > 60)
        {
            preview = preview[..60] + "…";
        }

        textColumn.Children.Add(new TextBlock
        {
            Text = "最近复制",
            FontSize = 10,
            Foreground = Brushes.Gray,
        });
        textColumn.Children.Add(new TextBlock
        {
            Text = preview.Replace('\n', ' '),
            FontSize = 12,
            Foreground = Brushes.Black,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 300,
            Margin = new Thickness(0, 2, 0, 0),
        });
        row.Children.Add(textColumn);

        var hint = new TextBlock
        {
            Text = "查看历史 →",
            FontSize = 10,
            Foreground = ThemeBrushes.Get("SkinAccentFromSkin"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };
        row.Children.Add(hint);
        block.Child = row;

        block.MouseLeftButtonUp += (_, e) =>
        {
            _clipboard!.OpenHistoryWindow();
            Close();
            e.Handled = true;
        };
        return block;
    }

    private static Border CreateSeparator()
    {
        var sep = new Border { Height = 1, Margin = new Thickness(4, 6, 4, 6) };
        SetThemeBinding(sep, Border.BackgroundProperty, "ThemeSeparator");
        return sep;
    }

    protected override void OnClosed(EventArgs e)
    {
        _debounce.Stop();
        _generation++; // 使在途搜索结果作废
        base.OnClosed(e);
    }
}
