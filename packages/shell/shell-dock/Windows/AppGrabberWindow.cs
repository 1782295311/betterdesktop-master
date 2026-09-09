// BetterDesktop.Shell.Dock — 应用提取器窗口（真正的 AppGrabber）
// 对齐 MyDockFinder AppGrabberWindow 参照形态：干净模式（开始菜单+已安装）/ 全程序（磁盘盘点）双模式，
// 筛选（全部/已固定/未固定）、排序（字母/按文件夹分组 + 批量排序 dock 固定区）、图标网格、搜索防抖、
// 右键启动/固定/移除/打开目录/卸载，PinnedChanged 实时联动固定态。
// 前身 AppSourceWindow（扁平全程序列表）已删除——它只是 ScanAllPrograms 的只读视图，
// 不承担"提取器"职责（无模式/无筛选/无分组/无卸载）。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using BetterDesktop.Shell.ContextMenus.Contracts;
using BetterDesktop.Shell.ContextMenus.Services;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.Dock.Models;
using BetterDesktop.Shell.Dock.Services;

namespace BetterDesktop.Shell.Dock.Windows;

// ── 本文件方法级白话索引（应用提取器窗口，按功能分组找）──
//   "整体内容/搜索栏/分段工具条"        → BuildContent / UpdateSearchChrome / MakeToolbarGroup / MakeSegment
//   "加载应用列表 / 过滤 / 固定态"      → ReloadAsync / ApplyFilter / RefreshPinnedIds / ClearFilters
//   "列表渲染（分块/分批防卡顿）"       → RenderViewAsync / AppendBatch / AppendChunkedAsync
//   "图标分批加载（避免一次性卡死）"    → StartStagedIconLoad / StagedIconLoadAsync / LoadIconInto
//   "单个应用卡片/右键菜单/启动"        → MakeItem / ShowItemMenu / Launch / OpenContainingDirectory / RunUninstaller
//   "应用分组（新建/取名）"             → PromptNewGroup / GetFolderName（分组存储 AppGroupStore）
//   "批量排序模式"                      → ToggleBatchOrder / OnBatchItemClicked / RefreshBatchBadges / ApplyBatchOrderCore / RenderBatchViewAsync / ExitBatchOrder
//   "忙态/状态文案"                     → SetBusy / UpdateStatus
// ────────────────────────────────────

/// <summary>应用提取器：双模式全量盘点 + 筛选/分组 + 图标网格 + 固定管理。</summary>
internal sealed class AppGrabberWindow : ShellWindow
{
    /// <summary>管理窗口打开即激活（基类约定"管理窗口重写为 true"；重构回归修复——备份副本 stage1-pre-clean 中有此覆写）。</summary>
    protected override bool DefaultShowActivated => true;

    private const string ModeGroupName = "grabber.mode";
    private const string FilterGroupName = "grabber.filter";
    private const string SortGroupName = "grabber.sort";

    // 技术库资产应用：503-后台图标预加载（可见区优先+分批预热）、
    // 202-分类并行枚举与增量回流（边搜边出）、203-搜索排名（权重打分）、201-节流（取消旧任务）
    private const int FirstIconBatch = 24;   // 首屏图标并发数（可见区优先）
    private const int IconTrickleBatch = 12; // 余量图标 trickle 批大小
    private const int RenderBatchSize = 80;  // 容器分批回流批大小

    private readonly IDockAppsService _apps;
    private readonly IDockIconService _icons;
    private readonly AppGroupStore _groups = new();

    private TextBox? _searchBox;
    private Border? _searchContainer;
    private TextBlock? _searchPlaceholder;
    private Button? _clearSearchButton;
    private RadioButton? _filterAllRadio;
    private RadioButton? _filterPinnedRadio;
    private RadioButton? _filterUnpinnedRadio;

    // 批量排序（Dock 固定区，参照 MyDockFinder AppGrabber 批排序语义）：
    // 进入后按当前固定顺序展示，点击图标依次编号，全部编号完成自动提交 Reorder。
    private Button? _batchOrderButton;
    private bool _batchOrdering;
    private int _preBatchFilterMode;
    private int _batchNext = 1;
    private readonly Dictionary<DockItemId, int> _batchLabels = new();
    private readonly Dictionary<DockItemId, Border> _batchControls = new();
    private TextBlock? _status;
    private ContentControl? _listHost;
    private RadioButton? _modeClean;
    private RadioButton? _modeAll;

    private List<DockItemData> _all = new();
    private readonly HashSet<DockItemId> _pinnedIds = new();
    private readonly Dictionary<DockItemId, FrameworkElement> _containers = new();
    private readonly List<(DockItemData App, Image Target)> _pendingIcons = new();

    private bool _allProgramsMode;
    private int _filterMode; // 0=全部 1=已固定 2=未固定
    private bool _groupByFolder;
    private DateTime _lastFilterAt;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _renderCts;
    private CancellationTokenSource? _iconCts;
    private int _renderedCount;
    private DateTime _lastPinnedEventAt;

    public AppGrabberWindow(
        IDockAppsService apps,
        IDockIconService icons,
        IVibrancyService vibrancy,
        IAppearanceService? appearance)
        : base(appearance, vibrancy)
    {
        _apps = apps;
        _icons = icons;
        _apps.PinnedChanged += OnPinnedChanged;

        if (CanSetProperty("Title")) Title = "应用提取器";
        if (CanSetProperty("Width")) Width = 760;
        if (CanSetProperty("Height")) Height = 620;
        if (CanSetProperty("WindowStartupLocation")) WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (CanSetProperty("MinWidth")) MinWidth = 560;
        if (CanSetProperty("MinHeight")) MinHeight = 420;

        var chrome = new Border
        {
            CornerRadius = new CornerRadius(SystemCornerRadius),
            BorderThickness = new Thickness(AppearanceService?.CardBorderThickness ?? 1),
            Child = BuildContent()
        };
        SetThemeBinding(chrome, Border.BackgroundProperty, "ThemePanelBackground");
        SetThemeBinding(chrome, Border.BorderBrushProperty, "CardBorderBrush");
        SetThemeBinding(chrome, TextElement.ForegroundProperty, "ThemeForeground");
        ChromeBorder = chrome;
        Content = chrome;

        // 关闭即停扫/停渲染/停图标 + 退订（窗口是懒创建复用的单例，Closed 只会走到卸载/关机）
        Closed += (_, _) =>
        {
            _loadCts?.Cancel();
            _renderCts?.Cancel();
            _iconCts?.Cancel();
            _apps.PinnedChanged -= OnPinnedChanged;
        };
    }

    /// <summary>浮层观感：面板半透明直接透出 DWM 毛玻璃/桌面（与 Dock 观感一致）。</summary>
    protected override bool UseSkinBackground => false;

    // ---------------------------------------------------------------- UI 构建

    private FrameworkElement BuildContent()
    {
        var root = new Grid { Margin = new Thickness(14) };
        for (var i = 0; i < 5; i++)
        {
            root.RowDefinitions.Add(new RowDefinition
            {
                Height = i == 3 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto
            });
        }

        // 行 0：标题行（标题 + 操作提示 + 关闭按钮）
        var header = new Grid { Margin = new Thickness(2, 0, 2, 10) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = "应用提取器",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        var hint = new TextBlock
        {
            Text = "双击启动 · 右键固定 / 分组 / 卸载",
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center
        };
        SetThemeBinding(hint, TextBlock.ForegroundProperty, "ThemeMutedForeground");
        Grid.SetColumn(hint, 1);
        header.Children.Add(hint);

        // 关闭按钮：右上角。提取器是独立无边框窗口（无系统标题栏），必须自备关闭入口。
        var closeButton = new Button
        {
            Content = "✕",
            FontSize = 11,
            Width = 22,
            Height = 22,
            Cursor = Cursors.Hand,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            ToolTip = "关闭"
        };
        SetThemeBinding(closeButton, Control.ForegroundProperty, "ThemeMutedForeground");
        closeButton.Click += (_, _) => Close();
        Grid.SetColumn(closeButton, 2);
        header.Children.Add(closeButton);

        Grid.SetRow(header, 0);
        root.Children.Add(header);

        // 行 1：搜索框（圆角输入容器 + 放大镜 + 占位提示 + 清除按钮；150ms 防抖行为不变）
        _searchBox = new TextBox
        {
            FontSize = 13,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        _searchBox.TextChanged += (_, _) =>
        {
            _lastFilterAt = DateTime.UtcNow;
            UpdateSearchChrome();
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, ApplyFilter);
        };
        _searchBox.GotKeyboardFocus += (_, _) => _searchContainer?.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        _searchBox.LostKeyboardFocus += (_, _) => _searchContainer?.SetResourceReference(Border.BorderBrushProperty, "ControlBorder");

        _searchPlaceholder = new TextBlock
        {
            Text = "搜索应用（支持拼音 / 首字母，如 微信 / wx）",
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Margin = new Thickness(6, 0, 0, 0)
        };
        SetThemeBinding(_searchPlaceholder, TextBlock.ForegroundProperty, "ThemeMutedForeground");

        var magnifier = new System.Windows.Shapes.Path
        {
            Width = 14,
            Height = 14,
            Stretch = Stretch.None,
            StrokeThickness = 1.6,
            VerticalAlignment = VerticalAlignment.Center,
            Data = Geometry.Parse("M 11.2 6.6 A 4.6 4.6 0 1 1 2 6.6 A 4.6 4.6 0 1 1 11.2 6.6 Z M 10.2 10.2 L 13.2 13.2")
        };
        SetThemeBinding(magnifier, System.Windows.Shapes.Shape.StrokeProperty, "ThemeMutedForeground");

        _clearSearchButton = new Button
        {
            Content = "✕",
            FontSize = 11,
            Width = 22,
            Height = 22,
            Cursor = Cursors.Hand,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            ToolTip = "清除搜索"
        };
        SetThemeBinding(_clearSearchButton, Control.ForegroundProperty, "ThemeMutedForeground");
        _clearSearchButton.Click += (_, _) =>
        {
            _searchBox.Clear();
            _searchBox.Focus();
        };

        var searchDock = new DockPanel();
        DockPanel.SetDock(magnifier, System.Windows.Controls.Dock.Left);
        searchDock.Children.Add(magnifier);
        DockPanel.SetDock(_clearSearchButton, System.Windows.Controls.Dock.Right);
        searchDock.Children.Add(_clearSearchButton);
        searchDock.Children.Add(_searchBox);

        _searchContainer = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6, 8, 6),
            BorderThickness = new Thickness(1),
            Child = searchDock
        };
        SetThemeBinding(_searchContainer, Border.BackgroundProperty, "ControlBackground");
        SetThemeBinding(_searchContainer, Border.BorderBrushProperty, "ControlBorder");
        Grid.SetRow(_searchContainer, 1);
        root.Children.Add(_searchContainer);

        // 行 2：工具条（分段控件：模式 / 筛选 / 排序 + 新建分组）
        _modeClean = MakeSegment(ModeGroupName, "干净模式", true, (_, _) =>
        {
            if (!_allProgramsMode) return;
            _allProgramsMode = false;
            _ = ReloadAsync();
        });
        _modeAll = MakeSegment(ModeGroupName, "全程序", false, (_, _) =>
        {
            if (_allProgramsMode) return;
            _allProgramsMode = true;
            _ = ReloadAsync();
        });
        var filterAll = MakeSegment(FilterGroupName, "全部", true, (_, _) => { _filterMode = 0; ApplyFilter(); });
        _filterAllRadio = filterAll;
        var filterPinned = MakeSegment(FilterGroupName, "已固定", false, (_, _) => { _filterMode = 1; ApplyFilter(); });
        _filterPinnedRadio = filterPinned;
        var filterUnpinned = MakeSegment(FilterGroupName, "未固定", false, (_, _) => { _filterMode = 2; ApplyFilter(); });
        _filterUnpinnedRadio = filterUnpinned;
        var sortAlpha = MakeSegment(SortGroupName, "字母", true, (_, _) => { _groupByFolder = false; ApplyFilter(); });
        var sortGroup = MakeSegment(SortGroupName, "按文件夹分组", false, (_, _) => { _groupByFolder = true; ApplyFilter(); });

        var newGroupButton = new Button
        {
            Content = "新建分组…",
            FontSize = 12,
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        newGroupButton.Click += (_, _) => PromptNewGroup(null);

        // 批量排序（Dock 固定区）：按期望顺序点击固定图标依次编号，全部编号后自动应用
        var batchOrderButton = new Button
        {
            Content = "批量排序",
            FontSize = 12,
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "给 Dock 固定区排序：按期望顺序点击固定图标（1,2,3…），全部编号后自动应用；再次点击「退出排序」放弃"
        };
        batchOrderButton.Click += (_, _) => ToggleBatchOrder();
        _batchOrderButton = batchOrderButton;

        var toolbar = new WrapPanel { Orientation = Orientation.Horizontal };
        toolbar.Children.Add(MakeToolbarGroup("模式", _modeClean, _modeAll));
        toolbar.Children.Add(MakeToolbarGroup("筛选", filterAll, filterPinned, filterUnpinned));
        toolbar.Children.Add(MakeToolbarGroup("排序", sortAlpha, sortGroup));
        toolbar.Children.Add(newGroupButton);
        toolbar.Children.Add(batchOrderButton);
        Grid.SetRow(toolbar, 2);
        root.Children.Add(toolbar);

        // 行 3：列表宿主（滚动区）
        _listHost = new ContentControl
        {
            Content = new TextBlock
            {
                Text = "正在扫描…",
                Opacity = 0.6,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        Grid.SetRow(_listHost, 3);
        root.Children.Add(_listHost);

        // 行 4：状态栏
        _status = new TextBlock
        {
            Text = "正在扫描…",
            FontSize = 11,
            Opacity = 0.7,
            Margin = new Thickness(2, 8, 0, 0)
        };
        SetThemeBinding(_status, TextBlock.ForegroundProperty, "ThemeMutedForeground");
        Grid.SetRow(_status, 4);
        root.Children.Add(_status);

        UpdateSearchChrome();
        _ = ReloadAsync();
        return root;
    }

    /// <summary>搜索框联动外观：占位提示与清除按钮随输入内容显隐。</summary>
    private void UpdateSearchChrome()
    {
        if (_searchBox is null)
        {
            return;
        }

        var empty = string.IsNullOrEmpty(_searchBox.Text);
        if (_searchPlaceholder is not null)
        {
            _searchPlaceholder.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        }

        if (_clearSearchButton is not null)
        {
            _clearSearchButton.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>工具条分组：muted 小标签 + 圆角轨道容器（ControlTrack）+ 内含同组分段按钮。</summary>
    private FrameworkElement MakeToolbarGroup(string label, params UIElement[] segments)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var segment in segments)
        {
            panel.Children.Add(segment);
        }

        var track = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(2),
            Child = panel,
            VerticalAlignment = VerticalAlignment.Center
        };
        SetThemeBinding(track, Border.BackgroundProperty, "ControlTrack");

        var labelText = new TextBlock
        {
            Text = label,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        SetThemeBinding(labelText, TextBlock.ForegroundProperty, "ThemeMutedForeground");

        var group = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0)
        };
        group.Children.Add(labelText);
        group.Children.Add(track);
        return group;
    }

    private static ControlTemplate? _segmentTemplate;

    /// <summary>分段按钮模板：默认透明、悬停浅起、选中浮起（macOS 分段控件范式）。
    /// 主题令牌经 XamlReader.Parse 的 {DynamicResource} 引用（模板工厂直传 DynamicResource 已知不可行）。</summary>
    private static ControlTemplate SegmentTemplate
    {
        get
        {
            if (_segmentTemplate is not null)
            {
                return _segmentTemplate;
            }

            const string xaml = """
                <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                                 TargetType="RadioButton">
                  <Border x:Name="Bd" CornerRadius="6" Background="Transparent" Padding="10,4" SnapsToDevicePixels="True">
                    <ContentPresenter x:Name="Cp" VerticalAlignment="Center"
                                      TextElement.FontSize="12"
                                      TextElement.Foreground="{DynamicResource ThemeMutedForeground}"/>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                      <Setter TargetName="Bd" Property="Background" Value="{DynamicResource ControlBackgroundHover}"/>
                      <Setter TargetName="Cp" Property="TextElement.Foreground" Value="{DynamicResource ThemeForeground}"/>
                    </Trigger>
                    <Trigger Property="IsChecked" Value="True">
                      <Setter TargetName="Bd" Property="Background" Value="{DynamicResource ControlBackground}"/>
                      <Setter TargetName="Cp" Property="TextElement.Foreground" Value="{DynamicResource ThemeForeground}"/>
                      <Setter TargetName="Cp" Property="TextElement.FontWeight" Value="SemiBold"/>
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
                """;
            _segmentTemplate = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(xaml);
            return _segmentTemplate;
        }
    }

    private static RadioButton MakeSegment(string groupName, string text, bool isChecked, RoutedEventHandler onCheck)
    {
        var rb = new RadioButton
        {
            Content = text,
            IsChecked = isChecked, // 先赋值再订阅：初值不触发 onCheck（与原 MakeRadio 行为一致）
            GroupName = groupName, // 同名组互斥（模式/筛选/排序三组各自独立）
            Margin = new Thickness(1),
            Template = SegmentTemplate,
            VerticalAlignment = VerticalAlignment.Center
        };
        rb.Checked += onCheck;
        return rb;
    }

    // ---------------------------------------------------------------- 数据装载

    /// <summary>按当前模式重扫数据源（磁盘 IO 在后台线程，结果回 UI 线程）。</summary>
    private async Task ReloadAsync()
    {
        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        var token = cts.Token;

        SetBusy($"正在扫描{(_allProgramsMode ? "磁盘全程序" : "开始菜单与已安装程序")}…");
        try
        {
            var result = await Task.Run(async () =>
            {
                token.ThrowIfCancellationRequested();
                if (_allProgramsMode)
                {
                    return _apps.ScanAllPrograms();
                }

                // 202-分类并行枚举：开始菜单与已安装注册表两源并行扫描再合并
                // （服务内部缓存自带锁，并行只读无竞态；任一源慢不拖累另一源）
                var startMenuTask = Task.Run(() => _apps.ScanStartMenu(), token);
                var installedTask = Task.Run(() => _apps.ScanInstalledApps(), token);
                await Task.WhenAll(startMenuTask, installedTask);

                // 合并：按业务主键去重，剔除已排除项
                var merged = new Dictionary<DockItemId, DockItemData>();
                foreach (var app in startMenuTask.Result.Concat(installedTask.Result))
                {
                    if (_apps.IsExcluded(app.Id))
                    {
                        continue;
                    }

                    merged.TryAdd(app.Id, app);
                }

                return (IReadOnlyList<DockItemData>)merged.Values.ToList();
            }, token);

            if (token.IsCancellationRequested)
            {
                return;
            }

            _all = result
                .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            RefreshPinnedIds();
            ApplyFilter();
            // 图标不再全量 Prefetch：由 StartStagedIconLoad 按可见区优先/小批量 trickle 拉取
            // （503：全量并发预取对数百 exe 是一次磁盘/Shell 提取风暴，反而制造卡顿）
        }
        catch (OperationCanceledException)
        {
            // 被新一次装载取代：静默
        }
        catch
        {
            // 扫描失败不阻断（M10）：保留旧列表
            if (!token.IsCancellationRequested)
            {
                if (_status is not null)
                {
                    _status.Text = "扫描失败，显示上次结果";
                }
            }
        }
    }

    private void RefreshPinnedIds()
    {
        _pinnedIds.Clear();
        foreach (var p in _apps.Pinned)
        {
            _pinnedIds.Add(p.Id);
        }
    }

    // ---------------------------------------------------------------- 过滤与渲染

    private void ApplyFilter()
    {
        if (_searchBox is null || _listHost is null)
        {
            return;
        }

        // 防抖：距上次输入不足 100ms 且焦点在搜索框时跳过中间态（最后一帧必然 >100ms 会进来）
        if (_searchBox.IsFocused && (DateTime.UtcNow - _lastFilterAt).TotalMilliseconds < 100)
        {
            return;
        }

        // 批量排序模式独占视图：按 Dock 当前固定顺序展示 + 序号点选
        if (_batchOrdering)
        {
            _ = RenderBatchViewAsync();
            return;
        }

        var kw = _searchBox.Text.Trim();
        IEnumerable<DockItemData> view = _all;
        if (!string.IsNullOrEmpty(kw))
        {
            // 1201-拼音模糊搜索（C# 变体）：名称直配 / 全拼包含 / 首字母包含（微信→wx/weixin）
            view = view.Where(a => PinyinMatcher.Matches(a.Name, kw));
        }

        view = _filterMode switch
        {
            1 => view.Where(a => _pinnedIds.Contains(a.Id)),
            2 => view.Where(a => !_pinnedIds.Contains(a.Id)),
            _ => view
        };

        var list = view.ToList();

        // 203-搜索排名算法（分类权重打分）：有关键词时按分数降序——
        // 名称前缀(3) > 拼音前缀(2) > 子串命中(1)，已固定再 +1；同分保持字母序。
        if (!string.IsNullOrEmpty(kw))
        {
            int Score(DockItemData a) =>
                (a.Name.StartsWith(kw, StringComparison.OrdinalIgnoreCase)
                    ? 3
                    : PinyinMatcher.PinyinPrefixMatch(a.Name, kw)
                        ? 2
                        : 1) +
                (_pinnedIds.Contains(a.Id) ? 1 : 0);
            list.Sort((a, b) =>
            {
                var byScore = Score(b).CompareTo(Score(a));
                return byScore != 0
                    ? byScore
                    : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
        }

        _ = RenderViewAsync(list);
    }

    /// <summary>
    /// 202-增量回流渲染：先挂滚动容器立刻上屏，容器按 RenderBatchSize 分批追加，
    /// 每批之间让出一帧（Task.Delay(1)）——全程序千级条目不再整树同步重建卡死 UI。
    /// 渲染挂独立取消令牌：快速切换筛选/模式时旧渲染帧作废，不残留。
    /// </summary>
    private async Task RenderViewAsync(List<DockItemData> list)
    {
        if (_listHost is null)
        {
            return;
        }

        _renderCts?.Cancel();
        var cts = new CancellationTokenSource();
        _renderCts = cts;
        var token = cts.Token;

        _containers.Clear();
        _pendingIcons.Clear();
        _batchControls.Clear();
        _renderedCount = 0;

        var scroller = MakeScroller();
        _listHost.Content = scroller;

        if (list.Count == 0)
        {
            // 508-空态不置死角：给出原因线索与可执行的下一步（清除筛选回全部列表）。
            var emptyPanel = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 46, 0, 0)
            };
            emptyPanel.Children.Add(new TextBlock
            {
                Text = _all.Count == 0 ? "未扫描到程序" : "没有匹配的应用",
                FontSize = 13,
                TextAlignment = TextAlignment.Center,
                Opacity = 0.75,
                HorizontalAlignment = HorizontalAlignment.Center
            });

            if (_all.Count > 0 && !_batchOrdering)
            {
                var hint = new TextBlock
                {
                    Text = "试试换个关键词或调整筛选条件",
                    FontSize = 11,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 6, 0, 0)
                };
                SetThemeBinding(hint, TextBlock.ForegroundProperty, "ThemeMutedForeground");
                emptyPanel.Children.Add(hint);

                var clearButton = new Button
                {
                    Content = "清除筛选",
                    FontSize = 12,
                    Padding = new Thickness(14, 3, 14, 3),
                    Margin = new Thickness(0, 12, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Cursor = Cursors.Hand
                };
                clearButton.Click += (_, _) => ClearFilters();
                emptyPanel.Children.Add(clearButton);
            }

            scroller.Content = emptyPanel;
            UpdateStatus(0);
            return;
        }

        try
        {
            if (_groupByFolder)
            {
                // 分组视图：自定义分组（506 范式）优先，其余按所在文件夹分组（原语义）。
                // 空自定义组也渲染标题（新建后有即时反馈）。排名序仅在平铺搜索态有意义，分组态保持目录语义。
                var root = new StackPanel();
                scroller.Content = root;

                var consumed = new HashSet<DockItemId>();
                var customGroups = _groups.Groups;
                if (customGroups.Count > 0)
                {
                    foreach (var groupName in customGroups)
                    {
                        var members = list
                            .Where(a => string.Equals(_groups.GetGroupOf(a.Id), groupName, StringComparison.OrdinalIgnoreCase))
                            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        foreach (var member in members)
                        {
                            consumed.Add(member.Id);
                        }

                        root.Children.Add(new TextBlock
                        {
                            Text = $"{groupName}（{members.Count}）",
                            FontSize = 12,
                            FontWeight = FontWeights.SemiBold,
                            Opacity = 0.75,
                            Margin = new Thickness(4, 10, 0, 6)
                        });
                        if (members.Count == 0)
                        {
                            continue;
                        }

                        var groupPanel = new WrapPanel { Orientation = Orientation.Horizontal };
                        root.Children.Add(groupPanel);
                        await AppendChunkedAsync(groupPanel, members, token);
                        if (token.IsCancellationRequested)
                        {
                            return;
                        }
                    }

                    list = list.Where(a => !consumed.Contains(a.Id)).ToList();
                }

                // 余量按所在文件夹分组
                var folders = new SortedDictionary<string, List<DockItemData>>(StringComparer.OrdinalIgnoreCase);
                foreach (var app in list)
                {
                    var folder = GetFolderName(app);
                    if (!folders.TryGetValue(folder, out var bucket))
                    {
                        bucket = new List<DockItemData>();
                        folders[folder] = bucket;
                    }

                    bucket.Add(app);
                }

                if (customGroups.Count > 0 && folders.Count > 0)
                {
                    root.Children.Add(new TextBlock
                    {
                        Text = $"未分组（按文件夹，{list.Count}）",
                        FontSize = 12,
                        FontWeight = FontWeights.SemiBold,
                        Opacity = 0.55,
                        Margin = new Thickness(4, 14, 0, 6)
                    });
                }

                foreach (var pair in folders)
                {
                    var apps = pair.Value;
                    root.Children.Add(new TextBlock
                    {
                        Text = $"{pair.Key}（{apps.Count}）",
                        FontSize = 12,
                        FontWeight = FontWeights.SemiBold,
                        Opacity = 0.75,
                        Margin = new Thickness(4, 10, 0, 6)
                    });
                    var panel = new WrapPanel { Orientation = Orientation.Horizontal };
                    root.Children.Add(panel);
                    await AppendChunkedAsync(panel, apps.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase), token);
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }
                }
            }
            else
            {
                var panel = new WrapPanel { Orientation = Orientation.Horizontal };
                scroller.Content = panel;
                await AppendChunkedAsync(panel, list, token);
                if (token.IsCancellationRequested)
                {
                    return;
                }
            }

            // 渲染完成后启动图标两阶段加载（503：可见区优先 + 余量 trickle）
            StartStagedIconLoad();
        }
        catch (OperationCanceledException)
        {
            // 被新渲染帧取代：静默
        }
    }

    /// <summary>把一批条目容器追加进面板（MakeItem 纯 UI 构建，不再触发图标提取）。</summary>
    private void AppendBatch(Panel target, IEnumerable<DockItemData> batch)
    {
        foreach (var app in batch)
        {
            target.Children.Add(MakeItem(app));
        }
    }

    /// <summary>202-增量回流：分批追加 + 批间让出一帧 + 进度状态实时刷新。</summary>
    private async Task AppendChunkedAsync(Panel target, IEnumerable<DockItemData> items, CancellationToken token)
    {
        var batch = new List<DockItemData>(RenderBatchSize);
        foreach (var app in items)
        {
            batch.Add(app);
            if (batch.Count < RenderBatchSize)
            {
                continue;
            }

            AppendBatch(target, batch);
            _renderedCount += batch.Count;
            UpdateStatus(_renderedCount);
            batch = new List<DockItemData>(RenderBatchSize);
            await Task.Delay(1, token); // 让出一帧：输入/合成优先
        }

        if (batch.Count > 0)
        {
            AppendBatch(target, batch);
            _renderedCount += batch.Count;
            UpdateStatus(_renderedCount);
        }
    }

    private static ScrollViewer MakeScroller()
    {
        return new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Background = Brushes.Transparent
        };
    }

    private static string GetFolderName(DockItemData app)
    {
        var path = !string.IsNullOrWhiteSpace(app.ShortcutPath) ? app.ShortcutPath : app.TargetPath;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                return Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar));
            }
        }
        catch
        {
            // 非法路径按"未知来源"归类
        }

        return "未知来源";
    }

    private void UpdateStatus(int visibleCount)
    {
        if (_status is null)
        {
            return;
        }

        if (_batchOrdering)
        {
            _status.Text = $"排序模式：已编号 {_batchLabels.Count} / 共 {_pinnedIds.Count} —— 按期望顺序点击图标，全部编号后自动应用；「退出排序」放弃";
            return;
        }

        _status.Text = _allProgramsMode
            ? $"全程序：可见 {visibleCount} / 共 {_all.Count} · 已固定 {_pinnedIds.Count}（双击启动，右键更多）"
            : $"干净模式：可见 {visibleCount} / 共 {_all.Count} · 已固定 {_pinnedIds.Count}（双击启动，右键更多）";
    }

    private void SetBusy(string text)
    {
        if (_listHost is not null)
        {
            _listHost.Content = new TextBlock
            {
                Text = text,
                Opacity = 0.6,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        if (_status is not null)
        {
            _status.Text = text;
        }
    }

    // ---------------------------------------------------------------- 单个条目

    private FrameworkElement MakeItem(DockItemData app)
    {
        var isPinned = _pinnedIds.Contains(app.Id);

        var icon = new Image
        {
            Width = 44,
            Height = 44,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        // 图标不在此处即时拉取：入队后由 503 两阶段加载器按可见区优先/小批量 trickle 处理
        _pendingIcons.Add((app, icon));

        var name = new TextBlock
        {
            Text = app.Name,
            FontSize = 11,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            MaxWidth = 88,
            Margin = new Thickness(0, 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center
        };

        // 固定角标（右上 8px 圆点，主题主色）：钉/拔只翻 Visibility，不重建条目
        var pinDot = new System.Windows.Shapes.Ellipse
        {
            Width = 8,
            Height = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 2, 0),
            Visibility = isPinned ? Visibility.Visible : Visibility.Collapsed
        };
        SetThemeBinding(pinDot, System.Windows.Shapes.Shape.FillProperty, "AccentBrush");

        // 批量排序序号徽标（左上角圆号牌，仅排序模式可见；IsHitTestVisible=false 不挡点选）
        var batchBadge = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            Child = new TextBlock
            {
                FontSize = 11,
                Foreground = Brushes.White,
                TextAlignment = TextAlignment.Center
            }
        };
        SetThemeBinding(batchBadge, Border.BackgroundProperty, "AccentBrush");

        var body = new Grid { Width = 88 };
        body.Children.Add(new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { icon, name }
        });
        body.Children.Add(pinDot);
        body.Children.Add(batchBadge);

        var border = new Border
        {
            Width = 96,
            Height = 96,
            Margin = new Thickness(4),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(4),
            Child = body,
            Tag = pinDot, // 供 PinnedChanged 联动翻角标
            ToolTip = $"{app.Name}\n{(isPinned ? "已固定到 Dock" : "未固定")}\n{(!string.IsNullOrWhiteSpace(app.TargetPath) ? app.TargetPath : app.ShortcutPath)}"
        };
        // 悬浮高亮：显式半透明灰（不引主题令牌——"CardHoverBackground"未在主题表核实，禁止编造）
        var hoverBrush = new SolidColorBrush(Color.FromArgb(0x28, 0x80, 0x80, 0x80));
        border.MouseEnter += (_, _) => border.Background = hoverBrush;
        border.MouseLeave += (_, _) =>
        {
            border.Background = Brushes.Transparent;
            border.Opacity = 1;
        };
        border.MouseLeftButtonDown += (_, e) =>
        {
            border.Opacity = 0.7; // 按下反馈
            if (_batchOrdering)
            {
                return; // 排序模式：单击编号接管，不触发启动
            }

            if (e.ClickCount == 2)
            {
                Launch(app);
            }
        };
        border.MouseLeftButtonUp += (_, _) =>
        {
            border.Opacity = 1;
            if (_batchOrdering)
            {
                OnBatchItemClicked(app.Id);
            }
        };
        border.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (!_batchOrdering)
            {
                ShowItemMenu(app, border); // 排序模式下不弹条目菜单，避免中途改动固定集合
            }
        };

        _containers[app.Id] = border;
        if (_batchOrdering)
        {
            _batchControls[app.Id] = batchBadge;
            if (_batchLabels.TryGetValue(app.Id, out var labeled))
            {
                batchBadge.Visibility = Visibility.Visible;
                if (batchBadge.Child is TextBlock batchText)
                {
                    batchText.Text = labeled.ToString();
                }
            }
        }

        return border;
    }

    /// <summary>
    /// 503-后台图标预加载：两阶段——首屏（前 FirstIconBatch 个，即当前可见区）并发立即拉取，
    /// 余量按 IconTrickleBatch 小批量 trickle，批间让 20ms。此前对全列表并发拉取，
    /// 全程序模式下是数百个 exe/LNK 图标提取同时打磁盘+Shell，是列表卡顿主因。
    /// </summary>
    private void StartStagedIconLoad()
    {
        _iconCts?.Cancel();
        var cts = new CancellationTokenSource();
        _iconCts = cts;
        _ = StagedIconLoadAsync(cts.Token);
    }

    private async Task StagedIconLoadAsync(CancellationToken token)
    {
        try
        {
            // 队列快照：渲染期间 MakeItem 会继续入队，这里只消费当前批次视图
            var queue = new Queue<(DockItemData App, Image Target)>(_pendingIcons);

            // 阶段一：首屏并发
            var first = new List<(DockItemData App, Image Target)>(FirstIconBatch);
            while (first.Count < FirstIconBatch && queue.Count > 0)
            {
                first.Add(queue.Dequeue());
            }

            foreach (var pair in first)
            {
                _ = LoadIconInto(pair.App, pair.Target);
            }

            if (queue.Count > 0)
            {
                await Task.Delay(30, token); // 让首屏图标先落地
            }

            // 阶段二：余量小批量 trickle
            while (queue.Count > 0 && !token.IsCancellationRequested)
            {
                for (var i = 0; i < IconTrickleBatch && queue.Count > 0; i++)
                {
                    var pair = queue.Dequeue();
                    _ = LoadIconInto(pair.App, pair.Target);
                }

                await Task.Delay(20, token);
            }
        }
        catch (OperationCanceledException)
        {
            // 视图已更换：静默
        }
    }

    private async Task LoadIconInto(DockItemData app, Image target)
    {
        try
        {
            // 未挂载的 Image 也可以先赋 Source（挂入可视树后即显示）；服务层自带缓存命中
            var icon = await _icons.GetIconAsync(app);
            if (icon is not null)
            {
                target.Source = icon;
            }
        }
        catch
        {
            // 图标加载失败留空（不阻断列表）
        }
    }

    // ---------------------------------------------------------------- 交互

    private void ShowItemMenu(DockItemData app, FrameworkElement target)
    {
        var isPinned = _pinnedIds.Contains(app.Id);
        var items = new List<MenuItemDef>
        {
            new() { Id = "grab.launch", Text = "启动", Command = () => Launch(app) },
        };

        if (isPinned)
        {
            items.Add(new()
            {
                Id = "grab.remove",
                Text = "从 Dock 移除",
                Command = () => { _apps.RemoveById(app.Id); _apps.Save(); },
            });
        }
        else
        {
            items.Add(new()
            {
                Id = "grab.pin",
                Text = "固定到 Dock",
                Command = () =>
                {
                    _apps.AddByPath(string.IsNullOrWhiteSpace(app.ShortcutPath) ? app.TargetPath : app.ShortcutPath);
                    _apps.Save();
                },
            });
        }

        items.Add(new() { Id = "grab.dir", Text = "打开所在目录", Command = () => OpenContainingDirectory(app) });

        // 自定义分组（506 范式）：从分组移出 / 移动到分组 ▸（含新建）
        var currentGroup = _groups.GetGroupOf(app.Id);
        if (currentGroup is not null)
        {
            items.Add(new()
            {
                Id = "grab.ungroup",
                Text = $"从「{currentGroup}」移出",
                Command = () => { _groups.RemoveFromGroup(app.Id); ApplyFilter(); },
            });
        }

        var moveChildren = new List<MenuItemDef>();
        foreach (var groupName in _groups.Groups)
        {
            if (string.Equals(groupName, currentGroup, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var captured = groupName;
            moveChildren.Add(new()
            {
                Id = $"grab.moveto.{captured}",
                Text = captured,
                Command = () => { _groups.MoveToGroup(app.Id, captured); ApplyFilter(); },
            });
        }
        moveChildren.Add(new() { Id = "grab.newgroup", Text = "新建分组…", Command = () => PromptNewGroup(app) });
        items.Add(new() { Id = "grab.moveto", Text = "移动到分组", Kind = MenuItemKind.Submenu, Children = moveChildren });

        // 卸载入口仅"已安装"来源有 UninstallCommand；全程序模式直接删 exe 不清理残留，不暴露
        if (!string.IsNullOrWhiteSpace(app.UninstallCommand))
        {
            items.Add(new() { Id = "grab.uninstall", Text = "卸载…", Command = () => RunUninstaller(app.UninstallCommand) });
        }

        DockMenuPopup.ShowAtCursor(items.Cast<object>().ToList(), this);
    }

    /// <summary>弹输入框建分组；assign 非空时把该应用移入新分组（工具条入口传 null 仅建空组）。</summary>
    private void PromptNewGroup(DockItemData? assign)
    {
        var input = new TextBox { Width = 240, Margin = new Thickness(0, 8, 0, 0) };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var ok = new Button { Content = "确定", Width = 72, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "取消", Width = 72, IsCancel = true };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = "分组名称：" });
        panel.Children.Add(input);
        panel.Children.Add(buttons);

        var dlg = new Window
        {
            Title = "新建分组",
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Content = panel
        };

        string? name = null;
        ok.Click += (_, _) =>
        {
            var text = input.Text.Trim();
            if (text.Length == 0)
            {
                return;
            }

            name = text;
            dlg.Close();
        };
        input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                ok.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            }
        };

        dlg.ShowDialog();
        if (name is null)
        {
            return;
        }

        if (assign is not null)
        {
            _groups.MoveToGroup(assign.Id, name);
        }
        else
        {
            _groups.CreateGroup(name);
        }

        ApplyFilter();
    }

    /// <summary>清除搜索关键词与筛选（回到全部/字母视图），从空态一键恢复。</summary>
    private void ClearFilters()
    {
        _filterMode = 0;
        if (_filterAllRadio is not null)
        {
            _filterAllRadio.IsChecked = true; // 已选中时不触发事件，由末尾 ApplyFilter 兜底
        }

        if (_searchBox is not null)
        {
            _searchBox.Text = string.Empty; // 触发 TextChanged → UpdateSearchChrome + ApplyFilter
        }

        ApplyFilter();
    }

    // ---------------------------------------------------------------- 批量排序（Dock 固定区）

    /// <summary>进入/退出批量排序模式（参照 MyDockFinder AppGrabber 批排序语义）。</summary>
    private void ToggleBatchOrder()
    {
        if (_batchOrdering)
        {
            ExitBatchOrder(apply: false);
            ApplyFilter();
            return;
        }

        if (_pinnedIds.Count == 0)
        {
            if (_status is not null)
            {
                _status.Text = "暂无固定应用，先在列表中固定几个应用再排序";
            }

            return;
        }

        _batchOrdering = true;
        _batchLabels.Clear();
        _batchControls.Clear();
        _batchNext = 1;
        _preBatchFilterMode = _filterMode;

        // 强制切到「已固定」视图——排序对象就是 Dock 固定区
        _filterMode = 1;
        if (_filterPinnedRadio is not null)
        {
            _filterPinnedRadio.IsChecked = true; // 触发 Checked → ApplyFilter → 批量排序视图
        }
        else
        {
            ApplyFilter();
        }

        if (_batchOrderButton is not null)
        {
            _batchOrderButton.Content = "退出排序";
        }
    }

    /// <summary>退出批量排序。apply=true 按已编号顺序提交（全部编号自动应用走这里），false 丢弃进度。</summary>
    private void ExitBatchOrder(bool apply)
    {
        _batchOrdering = false;
        if (apply && _batchLabels.Count > 0)
        {
            ApplyBatchOrderCore();
        }

        _batchLabels.Clear();
        _batchControls.Clear();
        _batchNext = 1;

        // 恢复进入前的筛选视图
        _filterMode = _preBatchFilterMode;
        var radio = _filterMode switch
        {
            1 => _filterPinnedRadio,
            2 => _filterUnpinnedRadio,
            _ => _filterAllRadio
        };
        if (radio is not null)
        {
            radio.IsChecked = true; // 已选中时不触发事件，由调用方 ApplyFilter 兜底
        }

        if (_batchOrderButton is not null)
        {
            _batchOrderButton.Content = "批量排序";
        }
    }

    /// <summary>排序模式点击一个固定图标：未编号则打下一个序号，已编号则撤销并前移后续编号；
    /// 全部固定项都编号完成后自动提交顺序。</summary>
    private void OnBatchItemClicked(DockItemId id)
    {
        if (!_batchOrdering)
        {
            return;
        }

        if (_batchLabels.TryGetValue(id, out var existing))
        {
            _batchLabels.Remove(id);
            if (_batchControls.TryGetValue(id, out var badge))
            {
                badge.Visibility = Visibility.Collapsed;
            }

            foreach (var kvp in _batchLabels.Where(k => k.Value > existing).ToList())
            {
                _batchLabels[kvp.Key] = kvp.Value - 1;
            }

            _batchNext = _batchLabels.Count + 1;
            RefreshBatchBadges();
            UpdateStatus(_batchLabels.Count);
            return;
        }

        var n = _batchNext++;
        _batchLabels[id] = n;
        if (_batchControls.TryGetValue(id, out var target))
        {
            target.Visibility = Visibility.Visible;
            if (target.Child is TextBlock text)
            {
                text.Text = n.ToString();
            }
        }

        UpdateStatus(_batchLabels.Count);

        if (_pinnedIds.Count > 0 && _batchLabels.Count == _pinnedIds.Count)
        {
            ExitBatchOrder(apply: true);
            ApplyFilter();
        }
    }

    /// <summary>把已渲染控件上的序号徽标按当前编号刷新（重渲染后回填）。</summary>
    private void RefreshBatchBadges()
    {
        foreach (var pair in _batchControls)
        {
            if (_batchLabels.TryGetValue(pair.Key, out var n))
            {
                pair.Value.Visibility = Visibility.Visible;
                if (pair.Value.Child is TextBlock text)
                {
                    text.Text = n.ToString();
                }
            }
        }
    }

    /// <summary>按编号顺序提交 Dock 固定区重排并持久化。</summary>
    private void ApplyBatchOrderCore()
    {
        try
        {
            var order = _batchLabels.OrderBy(kvp => kvp.Value).Select(kvp => kvp.Key).ToList();
            _apps.Reorder(order);
            _apps.Save();
        }
        catch
        {
            // 重排提交失败不阻断（M10）
        }
    }

    /// <summary>
    /// 批量排序视图：按 Dock 当前固定顺序展示（顺序即点选参照），搜索仍可用缩小点选范围；
    /// 被搜索滤掉的项无法编号，因此有过滤时不会自动应用（需无过滤点满全部）。
    /// </summary>
    private async Task RenderBatchViewAsync()
    {
        if (_listHost is null)
        {
            return;
        }

        _renderCts?.Cancel();
        var cts = new CancellationTokenSource();
        _renderCts = cts;
        var token = cts.Token;

        _containers.Clear();
        _pendingIcons.Clear();
        _batchControls.Clear();
        _renderedCount = 0;

        var scroller = MakeScroller();
        _listHost.Content = scroller;

        var kw = _searchBox?.Text.Trim();
        IEnumerable<DockItemData> items = _apps.Pinned;
        if (!string.IsNullOrEmpty(kw))
        {
            items = items.Where(a => PinyinMatcher.Matches(a.Name, kw));
        }

        var list = items.ToList();
        if (list.Count == 0)
        {
            scroller.Content = new TextBlock
            {
                Text = "没有已固定应用",
                Opacity = 0.6,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 40, 0, 0)
            };
            UpdateStatus(0);
            return;
        }

        try
        {
            var panel = new WrapPanel { Orientation = Orientation.Horizontal };
            scroller.Content = panel;
            await AppendChunkedAsync(panel, list, token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            RefreshBatchBadges();
            StartStagedIconLoad();
        }
        catch (OperationCanceledException)
        {
            // 被新渲染帧取代：静默
        }
    }

    private void Launch(DockItemData app)
    {
        var target = string.IsNullOrWhiteSpace(app.TargetPath) ? app.ShortcutPath : app.TargetPath;
        if (string.IsNullOrWhiteSpace(target) || (!File.Exists(target) && app.AppType != DockAppType.Uwp))
        {
            return;
        }

        try
        {
            if (app.AppType == DockAppType.Uwp && !string.IsNullOrEmpty(app.AppUserModelId))
            {
                _ = System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{app.AppUserModelId}")
                    {
                        UseShellExecute = true
                    });
            }
            else
            {
                _ = System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
            }
        }
        catch
        {
            // 启动失败静默（M10）
        }
    }

    private void OpenContainingDirectory(DockItemData app)
    {
        var path = !string.IsNullOrWhiteSpace(app.ShortcutPath) ? app.ShortcutPath : app.TargetPath;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                _ = System.Diagnostics.Process.Start("explorer.exe", dir);
            }
        }
        catch
        {
            // 打开目录失败不阻断
        }
    }

    private static void RunUninstaller(string command)
    {
        try
        {
            // UninstallString 常带参数（MsiExec.exe /X{GUID}、"…\unins000.exe" /S）：
            // 经 cmd /c 执行最稳；按 FileName 拆参会破坏带引号路径。
            _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {command}",
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch
        {
            // 卸载器启动失败静默（M10）
        }
    }

    // ---------------------------------------------------------------- 固定态联动

    private void OnPinnedChanged(object? sender, EventArgs e)
    {
        // 固定操作经 Save 触发可能连发：记录时间戳，Background 帧只处理最后一发
        _lastPinnedEventAt = DateTime.UtcNow;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            if ((DateTime.UtcNow - _lastPinnedEventAt).TotalMilliseconds > 50)
            {
                return; // 已有更新的一帧排在后面，本帧跳过
            }

            RefreshPinnedIds();
            if (_batchOrdering)
            {
                // 排序期间固定集合被外部改动（dock 侧拖动/增删）：重渲染排序视图
                ApplyFilter();
                return;
            }

            foreach (var (id, element) in _containers)
            {
                var pinned = _pinnedIds.Contains(id);
                if (element.Tag is System.Windows.Shapes.Ellipse dot)
                {
                    dot.Visibility = pinned ? Visibility.Visible : Visibility.Collapsed;
                }
            }

            UpdateStatus(_containers.Count);
        });
    }
}
