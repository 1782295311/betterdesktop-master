// BetterDesktop.Shell.Clipboard — 剪贴板历史面板（Phase B 6.3，迁移源 ClipboardQuickAccessWindow.xaml.cs 1588 行精简移植）
// 覆盖：搜索/类型 chips/分类/来源筛选/日期分组/键盘导航/多选合并/拖拽导出/暂停条/收藏视图/缩略图异步解码/按序粘贴状态条。
// 纯代码 UI（迁移源同款），ShellWindow 主题绑定；失焦自动隐藏。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BetterDesktop.Shell.Clipboard.Contracts;
using BetterDesktop.Shell.Clipboard.Native;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;

namespace BetterDesktop.Shell.Clipboard;

/// <summary>剪贴板历史面板：单实例（Manager 懒创建），失焦即隐藏，行为同迁移源。</summary>
internal sealed class ClipboardHistoryWindow : ShellWindow
{
    private readonly ClipboardManager _service;

    private readonly List<ClipboardEntry> _currentList = new();
    private readonly HashSet<ClipboardEntry> _selectedEntries = new();
    private int _selectedIndex;
    private string _searchFilter = string.Empty;
    private string _currentFilter = "all"; // all|text|image|files|code|pinned
    private string? _currentSourceFilter;
    private bool _multiSelectMode;
    private string _mergeSeparator = "\n";
    private bool _isInitializing = true;
    private DispatcherTimer? _pauseCountdownTimer;
    private bool _dragStarted;

    private TextBox _searchBox = null!;
    private StackPanel _entryList = null!;
    private TextBlock _countLabel = null!;
    private WrapPanel _sourceFilterBar = null!;
    private WrapPanel _filterChipsPanel = null!;
    private Border _pauseIndicator = null!;
    private TextBlock _pauseText = null!;
    private ToggleButton _pauseBtn = null!;
    private Border _multiSelectBar = null!;
    private TextBlock _multiSelectCount = null!;
    private Button _mergePasteBtn = null!;
    private Button _sequentialPasteBtn = null!;
    private StackPanel _separatorPanel = null!;
    private Border _sequentialBar = null!;
    private TextBlock _sequentialText = null!;

    public bool PinnedOnlyMode { get; private set; }

    /// <summary>收藏视图开关（Manager ShowFavoritesOnly 同步）。</summary>
    public bool ShowFavoritesOnly
    {
        get => PinnedOnlyMode;
        set
        {
            PinnedOnlyMode = value;
            if (IsLoaded && !_isInitializing)
            {
                _currentFilter = value ? "pinned" : "all";
                _sourceFilterBar.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
                RefreshList();
            }
        }
    }

    public ClipboardHistoryWindow(ClipboardManager service, IAppearanceService? appearance, IVibrancyService? vibrancy)
        : base(appearance, vibrancy)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        Width = 430;
        Height = 560;
        MinWidth = 380;
        MinHeight = 320;
        WindowStyle = WindowStyle.None;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.Manual;

        var root = BuildLayout();
        Content = root;

        _searchBox!.TextChanged += SearchBox_TextChanged;
        _pauseBtn.Checked += (_, _) => { if (!_isInitializing && !_service.IsTemporarilyPaused) _service.PauseTemporarily(60); };
        _pauseBtn.Unchecked += (_, _) => { if (!_isInitializing && _service.IsTemporarilyPaused) _service.Resume(); };
        _mergePasteBtn.Click += (_, _) => MergePasteSelected();
        _sequentialPasteBtn.Click += (_, _) => StartSequentialPaste();

        PreviewKeyDown += OnPreviewKeyDown;
        MouseDoubleClick += (_, e) => e.Handled = true;
        Deactivated += (_, _) =>
        {
            if (IsVisible)
            {
                Hide();
            }
        };

        _service.HistoryChanged += OnHistoryChanged;
        _service.PauseStateChanged += OnPauseStateChanged;
        _pauseCountdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _pauseCountdownTimer.Tick += (_, _) => UpdatePauseState();
    }

    protected override void OnLoadedCore()
    {
        ChromeBorder = (Border)Content;
        _isInitializing = true;
        if (PinnedOnlyMode)
        {
            _currentFilter = "pinned";
        }

        UpdatePauseState();
        UpdateSourceFilterBar();
        RefreshList();
        UpdateSequentialBar();
        _pauseCountdownTimer!.Start();
        _searchBox.Focus();
        _searchBox.SelectAll();
        _isInitializing = false;
    }

    protected override bool UseSkinBackground => false;

    protected override void OnClosed(EventArgs e)
    {
        _pauseCountdownTimer?.Stop();
        _service.HistoryChanged -= OnHistoryChanged;
        _service.PauseStateChanged -= OnPauseStateChanged;
        base.OnClosed(e);
    }

    // ---------- 布局 ----------

    private FrameworkElement BuildLayout()
    {
        var root = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Child = CreatePanel()
        };
        SetThemeBinding(root, Border.BorderBrushProperty, "CardBorderBrush");
        SetThemeBinding(root, Border.BackgroundProperty, "ThemePanelBackground");
        return root;
    }

    private FrameworkElement CreatePanel()
    {
        var panel = new Grid();
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 顶部：标题 + 搜索 + 关闭
        panel.Children.Add(CreateHeader());

        // 筛选 chips + 来源
        var filters = new StackPanel { Margin = new Thickness(10, 6, 10, 0) };
        filters.Children.Add(CreateFilterChips());
        filters.Children.Add(_sourceFilterBar = CreateSourceFilterBar());
        Grid.SetRow(filters, 1);
        panel.Children.Add(filters);

        // 多选操作条 + 按序条
        var ops = new StackPanel();
        ops.Children.Add(CreateMultiSelectBar());
        ops.Children.Add(CreateSequentialBar());
        Grid.SetRow(ops, 2);
        panel.Children.Add(ops);

        // 列表
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(6, 4, 6, 0),
            Padding = new Thickness(0, 0, 6, 0)
        };
        _entryList = new StackPanel();
        _entryList.Name = "EntryList";
        scroll.Content = _entryList;
        Grid.SetRow(scroll, 3);
        panel.Children.Add(scroll);

        // 底部状态条
        panel.Children.Add(CreateFooter());

        return panel;
    }

    private FrameworkElement CreateHeader()
    {
        var header = new Grid { Margin = new Thickness(12, 10, 12, 4) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock { Text = "剪贴板历史", FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        SetThemeBinding(title, TextElement.ForegroundProperty, "ThemeForeground");
        header.Children.Add(title);

        _searchBox = new TextBox
        {
            Name = "SearchBox",
            Margin = new Thickness(10, 0, 6, 0),
            Padding = new Thickness(8, 4, 8, 4),
            VerticalContentAlignment = VerticalAlignment.Center,
            FontSize = 12
        };
        Grid.SetColumn(_searchBox, 1);
        header.Children.Add(_searchBox);

        var close = new Button
        {
            Content = "✕",
            Width = 26,
            Height = 26,
            FontSize = 12,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            ToolTip = "关闭（Esc）"
        };
        SetThemeBinding(close, Control.ForegroundProperty, "ThemeForeground");
        close.Click += (_, _) => Hide();
        Grid.SetColumn(close, 2);
        header.Children.Add(close);
        return header;
    }

    private FrameworkElement CreateFilterChips()
    {
        // WrapPanel：6 类 chips 在窄窗口（MinWidth 380）下自动换行，避免横向溢出与相邻元素重叠。
        _filterChipsPanel = new WrapPanel { Name = "FilterChips" };
        var defs = new[] { ("全部", "all"), ("文本", "text"), ("图片", "image"), ("文件", "files"), ("代码", "code"), ("收藏", "pinned") };
        foreach (var (label, tag) in defs)
        {
            var chip = CreateChip(label);
            chip.Tag = tag;
            chip.MouseLeftButtonUp += FilterChip_Click;
            _filterChipsPanel.Children.Add(chip);
        }

        return _filterChipsPanel;
    }

    private WrapPanel CreateSourceFilterBar()
    {
        // WrapPanel：应用来源 chips 数量不定，窄窗口自动换行防溢出重叠。
        var bar = new WrapPanel { Name = "SourceFilterBar", Margin = new Thickness(0, 6, 0, 0) };
        var all = CreateChip("全部来源");
        all.Tag = null;
        all.MouseLeftButtonUp += SourceFilterChip_Click;
        bar.Children.Add(all);
        return bar;
    }

    private FrameworkElement CreateMultiSelectBar()
    {
        var bar = new Border
        {
            Name = "MultiSelectBar",
            Margin = new Thickness(10, 6, 10, 0),
            Padding = new Thickness(8, 4, 8, 4),
            CornerRadius = new CornerRadius(6),
            Visibility = Visibility.Collapsed
        };
        SetThemeBinding(bar, Border.BackgroundProperty, "ThemePanelBackground");
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        _multiSelectCount = new TextBlock { Name = "MultiSelectCount", Text = "已选择 0 项", FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        SetThemeBinding(_multiSelectCount, TextElement.ForegroundProperty, "ThemeForeground");
        row.Children.Add(_multiSelectCount);

        _mergePasteBtn = CreateActionButton("合并粘贴", "将所选条目以分隔符合并后一次粘贴");
        _mergePasteBtn.Name = "MergePasteBtn";
        _mergePasteBtn.Margin = new Thickness(8, 0, 0, 0);
        row.Children.Add(_mergePasteBtn);

        _sequentialPasteBtn = CreateActionButton("依次粘贴", "进入按序粘贴模式：Ctrl+Shift+V 逐条粘贴");
        _sequentialPasteBtn.Name = "SequentialPasteBtn";
        _sequentialPasteBtn.Margin = new Thickness(6, 0, 0, 0);
        row.Children.Add(_sequentialPasteBtn);

        var sepLabel = new TextBlock { Text = "分隔符:", FontSize = 10, Margin = new Thickness(10, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        SetThemeBinding(sepLabel, TextElement.ForegroundProperty, "ThemeForeground");
        row.Children.Add(sepLabel);

        _separatorPanel = new StackPanel { Orientation = Orientation.Horizontal, Name = "SeparatorPanel" };
        foreach (var (label, sep) in new[] { ("换行", "\n"), ("空格", " "), ("逗号", ", "), ("分号", "; "), ("无", "") })
        {
            var chip = CreateChip(label);
            chip.Tag = sep;
            chip.MouseLeftButtonUp += SeparatorChip_Click;
            _separatorPanel.Children.Add(chip);
        }

        row.Children.Add(_separatorPanel);
        bar.Child = row;
        return bar;
    }

    private FrameworkElement CreateSequentialBar()
    {
        var bar = new Border
        {
            Name = "SequentialBar",
            Margin = new Thickness(10, 6, 10, 0),
            Padding = new Thickness(8, 4, 8, 4),
            CornerRadius = new CornerRadius(6),
            Visibility = Visibility.Collapsed
        };
        SetThemeBinding(bar, Border.BackgroundProperty, "ThemePanelBackground");
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        _sequentialText = new TextBlock { Name = "SequentialText", FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        SetThemeBinding(_sequentialText, TextElement.ForegroundProperty, "ThemeForeground");
        row.Children.Add(_sequentialText);
        var cancel = CreateActionButton("取消", "取消按序粘贴");
        cancel.Margin = new Thickness(8, 0, 0, 0);
        cancel.Click += (_, _) => { _service.CancelSequentialPaste(); UpdateSequentialBar(); };
        row.Children.Add(cancel);
        bar.Child = row;
        return bar;
    }

    private FrameworkElement CreateFooter()
    {
        var footer = new Grid { Margin = new Thickness(12, 6, 12, 10) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var hint = new TextBlock
        {
            Text = "Enter 粘贴 · Ctrl+Enter 纯文本 · 1-9 快捷 · P 收藏 · T 标签 · Delete 删除 · 多选模式可合并/依次粘贴",
            FontSize = 9,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        SetThemeBinding(hint, TextElement.ForegroundProperty, "ThemeForeground");
        footer.Children.Add(hint);

        _countLabel = new TextBlock { Name = "CountLabel", Text = "0 条", FontSize = 11, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        SetThemeBinding(_countLabel, TextElement.ForegroundProperty, "ThemeForeground");
        Grid.SetColumn(_countLabel, 1);
        footer.Children.Add(_countLabel);

        _pauseBtn = new ToggleButton
        {
            Name = "PauseBtn",
            Content = "⏸",
            Width = 26,
            Height = 24,
            FontSize = 12,
            ToolTip = "暂停捕获 60 秒（Ctrl+Shift+Backspace）",
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0)
        };
        SetThemeBinding(_pauseBtn, Control.ForegroundProperty, "ThemeForeground");
        Grid.SetColumn(_pauseBtn, 2);
        footer.Children.Add(_pauseBtn);

        _pauseIndicator = new Border
        {
            Name = "PauseIndicator",
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 4, 10, 4),
            Background = new SolidColorBrush(Color.FromArgb(0x28, 0xE8, 0x6B, 0x4D)),
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 4)
        };
        _pauseText = new TextBlock { Name = "PauseText", FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0x6B, 0x4D)) };
        _pauseIndicator.Child = _pauseText;

        var footerStack = new StackPanel { Margin = new Thickness(12, 6, 12, 10) };
        footerStack.Children.Add(_pauseIndicator);
        footerStack.Children.Add(footer);
        return footerStack;
    }

    private static Border CreateChip(string text)
    {
        var chip = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(9, 3, 9, 3),
            Margin = new Thickness(0, 0, 6, 0),
            Background = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
            Cursor = Cursors.Hand
        };
        chip.Child = new TextBlock
        {
            Text = text,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)),
            VerticalAlignment = VerticalAlignment.Center
        };
        return chip;
    }

    private static Button CreateActionButton(string text, string tooltip)
    {
        return new Button
        {
            Content = text,
            FontSize = 10,
            Padding = new Thickness(8, 3, 8, 3),
            ToolTip = tooltip,
            Cursor = Cursors.Hand
        };
    }

    // ---------- 数据与列表 ----------

    private void OnHistoryChanged(ClipboardHistoryChangedEventArgs _)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            UpdateSourceFilterBar();
            RefreshList();
        }), DispatcherPriority.Background);
    }

    private void OnPauseStateChanged(bool _) => Dispatcher.BeginInvoke(new Action(UpdatePauseState), DispatcherPriority.Background);

    private void RefreshList()
    {
        _entryList.Children.Clear();
        _currentList.Clear();
        _currentList.AddRange(GetFilteredList());

        if (_currentList.Count == 0)
        {
            _entryList.Children.Add(CreateEmptyState());
            _countLabel.Text = "0 条";
            UpdateMultiSelectBar();
            return;
        }

        if (_selectedIndex >= _currentList.Count)
        {
            _selectedIndex = Math.Max(0, _currentList.Count - 1);
        }

        _countLabel.Text = $"{_currentList.Count} 条";

        string? currentGroup = null;
        for (int i = 0; i < _currentList.Count; i++)
        {
            string group = GetDateGroupLabel(_currentList[i].Timestamp);
            if (group != currentGroup)
            {
                currentGroup = group;
                _entryList.Children.Add(CreateDateGroupHeader(group));
            }

            _entryList.Children.Add(CreateEntryRow(_currentList[i], i));
        }

        UpdateSelection();
        UpdateMultiSelectBar();
    }

    private List<ClipboardEntry> GetFilteredList()
    {
        ClipboardItemKind? kind = null;
        ContentCategory? category = null;
        string keyword = string.IsNullOrWhiteSpace(_searchFilter) ? null! : _searchFilter;

        switch (_currentFilter)
        {
            case "text": kind = ClipboardItemKind.Text; break;
            case "image": kind = ClipboardItemKind.Image; break;
            case "files": kind = ClipboardItemKind.Files; break;
            case "code": category = ContentCategory.Code; break;
            case "pinned": kind = null; break;
        }

        var result = _service.GetFilteredEntries(kind, category, keyword, _currentSourceFilter);
        if (_currentFilter == "pinned")
        {
            result = result.Where(e => e.IsPinned).ToList();
        }

        return result.ToList();
    }

    private void UpdateSourceFilterBar()
    {
        if (_sourceFilterBar is null)
        {
            return;
        }

        foreach (UIElement child in _sourceFilterBar.Children.OfType<UIElement>().Skip(1).ToList())
        {
            _sourceFilterBar.Children.Remove(child);
        }

        foreach (string app in _service.GetSourceApps())
        {
            var chip = CreateChip(TrimExe(app));
            chip.Tag = app;
            chip.MouseLeftButtonUp += SourceFilterChip_Click;
            _sourceFilterBar.Children.Add(chip);
        }
    }

    private static string TrimExe(string name) => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    private FrameworkElement CreateEmptyState()
    {
        var text = new TextBlock
        {
            Text = "暂无剪贴板记录\n复制任意内容后自动出现",
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 60, 0, 60),
            TextAlignment = TextAlignment.Center
        };
        SetThemeBinding(text, TextElement.ForegroundProperty, "ThemeForeground");
        return text;
    }

    private static string GetDateGroupLabel(DateTime timestamp)
    {
        DateTime now = DateTime.Now;
        DateTime today = now.Date;
        DateTime entryDate = timestamp.Date;
        if (entryDate == today)
        {
            return "今天";
        }

        if (entryDate == today.AddDays(-1))
        {
            return "昨天";
        }

        if (entryDate >= today.AddDays(-7))
        {
            return $"{(int)(today - entryDate).TotalDays}天前";
        }

        return entryDate.Year == now.Year ? timestamp.ToString("M月d日") : timestamp.ToString("yyyy年M月d日");
    }

    private FrameworkElement CreateDateGroupHeader(string label)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 8, 10, 2) };
        var text = new TextBlock { Text = label, FontSize = 10, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        SetThemeBinding(text, TextElement.ForegroundProperty, "ThemeForeground");
        panel.Children.Add(text);
        panel.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(0x20, 0x4A, 0x90, 0xD9)), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    private Border CreateEntryRow(ClipboardEntry entry, int index)
    {
        bool isImage = entry.ContentType == ClipboardItemKind.Image;
        bool isFile = entry.ContentType == ClipboardItemKind.Files;

        var row = new Border
        {
            Tag = entry,
            Padding = new Thickness(8, isImage ? 8 : 4, 8, isImage ? 8 : 4),
            Margin = new Thickness(0, 1, 0, 1),
            CornerRadius = new CornerRadius(6),
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 数字徽章
        if (index < 9)
        {
            var badge = new TextBlock
            {
                Text = (index + 1).ToString(),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = isImage ? VerticalAlignment.Top : VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromArgb(0x90, 0x4A, 0x90, 0xD9))
            };
            grid.Children.Add(badge);
        }

        // 类型指示 / 图片缩略图
        var indicator = new Border
        {
            Width = isImage ? 56 : 30,
            Height = isImage ? 56 : 30,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x18, 0x4A, 0x90, 0xD9)),
            VerticalAlignment = isImage ? VerticalAlignment.Top : VerticalAlignment.Center
        };
        if (isImage)
        {
            LoadImageThumbnail(entry.ImagePath, indicator);
        }
        else
        {
            indicator.Child = new TextBlock
            {
                Text = TypeGlyph(entry),
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        Grid.SetColumn(indicator, 1);
        grid.Children.Add(indicator);

        // 内容区
        var contentPanel = new StackPanel { Margin = new Thickness(8, 0, 6, 0), VerticalAlignment = isImage ? VerticalAlignment.Top : VerticalAlignment.Center };
        string preview = PreviewText(entry);
        var previewText = new TextBlock
        {
            Text = preview,
            FontSize = isImage ? 12 : 12,
            FontWeight = entry.IsPinned ? FontWeights.SemiBold : FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 36,
            Foreground = new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF))
        };
        contentPanel.Children.Add(previewText);

        // 元数据
        var meta = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
        meta.Children.Add(CreateTypeChip(entry));
        if (!string.IsNullOrEmpty(entry.SourceProcessName))
        {
            meta.Children.Add(CreateMetaText($"· {TrimExe(entry.SourceProcessName)}", 0x70));
        }

        if (entry.CopyCount > 0)
        {
            meta.Children.Add(CreateMetaText($"· {entry.CopyCount}次", 0xB0));
        }

        meta.Children.Add(CreateMetaText($"· {GetRelativeTime(entry.Timestamp)}", 0x70));
        contentPanel.Children.Add(meta);

        if (!string.IsNullOrWhiteSpace(entry.Tags))
        {
            var tags = new TextBlock
            {
                Text = entry.Tags,
                FontSize = 9,
                Margin = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = new SolidColorBrush(Color.FromRgb(0x90, 0xCA, 0xF9))
            };
            contentPanel.Children.Add(tags);
        }

        Grid.SetColumn(contentPanel, 2);
        grid.Children.Add(contentPanel);

        // 右侧操作（多选 checkbox / 操作按钮）
        if (_multiSelectMode)
        {
            var check = new TextBlock
            {
                Text = _selectedEntries.Contains(entry) ? "☑" : "☐",
                FontSize = 16,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = _selectedEntries.Contains(entry) ? new SolidColorBrush(Color.FromRgb(0x66, 0xBB, 0x6A)) : new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF))
            };
            Grid.SetColumn(check, 3);
            grid.Children.Add(check);
        }
        else
        {
            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            actions.Children.Add(CreateRowButton("📌", "收藏/取消收藏（P）", (_, _) => TogglePin(entry)));
            actions.Children.Add(CreateRowButton("⧉", "复制（Ctrl+C）", (_, _) => { _service.CopyEntryToClipboard(entry); TouchEntryVisual(entry); }));
            actions.Children.Add(CreateRowButton("🗑", "删除（Delete）", (_, _) => DeleteEntry(entry)));
            Grid.SetColumn(actions, 3);
            grid.Children.Add(actions);
        }

        row.Child = grid;
        row.ToolTip = CreateEntryToolTip(entry);

        // 鼠标：单击选择、双击粘贴、右键菜单、拖拽
        row.MouseLeftButtonUp += (_, e) =>
        {
            int idx = _currentList.IndexOf(entry);
            if (_multiSelectMode)
            {
                if (e.ClickCount == 2)
                {
                    ToggleMultiSelect(entry);
                }
                else if (idx >= 0)
                {
                    SelectIndex(idx);
                    ToggleMultiSelect(entry);
                }

                RefreshList();
            }
            else if (idx >= 0)
            {
                SelectIndex(idx);
                if (e.ClickCount == 2)
                {
                    PasteEntryAndClose(entry);
                }
            }

            e.Handled = true;
        };

        row.ContextMenu = CreateEntryContextMenu(entry);

        row.MouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || _dragStarted || !IsMouseCaptured)
            {
                return;
            }

            if (Math.Abs(e.GetPosition(this).X - _dragStartPoint.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(e.GetPosition(this).Y - _dragStartPoint.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                _dragStarted = true;
                StartDragDrop(entry);
            }
        };

        return row;
    }

    private Point _dragStartPoint;

    private void SelectIndex(int index)
    {
        if (_selectedIndex == index)
        {
            return;
        }

        _selectedIndex = index;
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        int entryIndex = 0;
        Border? selectedRow = null;
        foreach (UIElement child in _entryList.Children)
        {
            if (child is Border row && row.Tag is ClipboardEntry)
            {
                bool isSelected = entryIndex == _selectedIndex;
                bool isMulti = _multiSelectMode && row.Tag is ClipboardEntry e && _selectedEntries.Contains(e);
                row.Background = isMulti ? new SolidColorBrush(Color.FromArgb(0x50, 0x4A, 0x90, 0xD9))
                    : isSelected ? new SolidColorBrush(Color.FromArgb(0x3A, 0x4A, 0x90, 0xD9))
                    : Brushes.Transparent;
                if (isSelected)
                {
                    selectedRow = row;
                }

                entryIndex++;
            }
        }

        selectedRow?.BringIntoView();
    }

    private void UpdateMultiSelectBar()
    {
        if (_multiSelectBar is null)
        {
            return;
        }

        _multiSelectBar.Visibility = _multiSelectMode ? Visibility.Visible : Visibility.Collapsed;
        _multiSelectCount.Text = $"已选择 {_selectedEntries.Count} 项";
        _mergePasteBtn.IsEnabled = _selectedEntries.Count > 0;
        _sequentialPasteBtn.IsEnabled = _selectedEntries.Count > 0;
    }

    private void ToggleMultiSelect(ClipboardEntry entry)
    {
        if (!_selectedEntries.Add(entry))
        {
            _selectedEntries.Remove(entry);
        }
    }

    private void UpdateSequentialBar()
    {
        if (_sequentialBar is null)
        {
            return;
        }

        bool active = _service.IsSequentialPasteActive;
        _sequentialBar.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        _sequentialText.Text = active ? $"按序粘贴中：剩余 {_service.SequentialRemaining} 条（Ctrl+Shift+V 粘下一条，或在此取消）" : string.Empty;
    }

    // ---------- 条目行为 ----------

    private void PasteEntryAndClose(ClipboardEntry entry, bool asPlainText = false)
    {
        if (entry is null)
        {
            return;
        }

        Hide();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (asPlainText)
            {
                _service.PasteEntryAsPlainTextToActiveWindow(entry);
            }
            else
            {
                _service.PasteEntryToActiveWindow(entry);
            }
        };
        timer.Start();
    }

    private void TogglePin(ClipboardEntry entry)
    {
        _service.TogglePin(entry);
        if (_currentFilter == "pinned")
        {
            RefreshList();
        }
        else
        {
            RefreshList();
        }
    }

    private void DeleteEntry(ClipboardEntry entry)
    {
        _service.DeleteEntry(entry);
        _selectedEntries.Remove(entry);
        if (_selectedIndex >= _currentList.Count - 1 && _selectedIndex > 0)
        {
            _selectedIndex--;
        }

        RefreshList();
    }

    private void TouchEntryVisual(ClipboardEntry entry)
    {
        _service.PinEntry(entry);
        _service.UnpinEntry(entry);
    }

    private void MergePasteSelected()
    {
        if (_selectedEntries.Count == 0)
        {
            return;
        }

        var entries = _currentList.Where(_selectedEntries.Contains).ToList();
        Hide();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _service.MergePasteToActiveWindow(entries, _mergeSeparator);
            _multiSelectMode = false;
            _selectedEntries.Clear();
        };
        timer.Start();
    }

    private void StartSequentialPaste()
    {
        if (_selectedEntries.Count == 0)
        {
            return;
        }

        var entries = _currentList.Where(_selectedEntries.Contains).ToList();
        Hide();
        _service.BeginSequentialPaste(entries);
    }

    private void EditEntryTags(ClipboardEntry entry)
    {
        var dialog = new Window
        {
            Title = "编辑标签",
            Width = 320,
            Height = 140,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false
        };
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock { Text = "标签（空格分隔，可搜索）", FontSize = 12 });
        var input = new TextBox { Text = entry.Tags, Margin = new Thickness(0, 8, 0, 8), Padding = new Thickness(6, 3, 6, 3) };
        panel.Children.Add(input);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var save = new Button { Content = "保存", Width = 70, Margin = new Thickness(0, 0, 8, 0) };
        save.Click += (_, _) =>
        {
            _service.SetEntryTags(entry, input.Text.Trim());
            dialog.Close();
            RefreshList();
        };
        var cancel = new Button { Content = "取消", Width = 70 };
        cancel.Click += (_, _) => dialog.Close();
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        dialog.Content = panel;
        dialog.ShowDialog();
    }

    private ContextMenu CreateEntryContextMenu(ClipboardEntry entry)
    {
        var menu = new ContextMenu();
        AddMenu(menu, "粘贴（Enter）", () => PasteEntryAndClose(entry));
        AddMenu(menu, "纯文本粘贴（Ctrl+Enter）", () => PasteEntryAndClose(entry, true));
        AddMenu(menu, "复制", () => _service.CopyEntryToClipboard(entry));
        AddMenu(menu, entry.IsPinned ? "取消收藏（P）" : "收藏（P）", () => TogglePin(entry));
        if (entry.ContentType == ClipboardItemKind.Files)
        {
            AddMenu(menu, "打开位置（O）", () =>
            {
                _service.OpenFileLocation(entry);
                Hide();
            });
        }

        AddMenu(menu, "编辑标签（T）", () => EditEntryTags(entry));
        AddMenu(menu, "删除（Delete）", () => DeleteEntry(entry));
        return menu;
    }

    private static void AddMenu(ContextMenu menu, string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private void StartDragDrop(ClipboardEntry entry)
    {
        DataObject data = new();
        switch (entry.ContentType)
        {
            case ClipboardItemKind.Text:
            case ClipboardItemKind.Html:
            case ClipboardItemKind.RichText:
                data.SetData(DataFormats.UnicodeText, entry.PlainText);
                if (!string.IsNullOrEmpty(entry.HtmlContent))
                {
                    data.SetData(DataFormats.Html, ClipboardNative.WrapHtmlForClipboard(entry.HtmlContent));
                }

                break;

            case ClipboardItemKind.Image:
                if (!string.IsNullOrEmpty(entry.ImagePath) && File.Exists(entry.ImagePath))
                {
                    data.SetData(DataFormats.FileDrop, new[] { entry.ImagePath });
                }

                break;

            case ClipboardItemKind.Files:
                if (entry.FilePaths is { Length: > 0 })
                {
                    data.SetData(DataFormats.FileDrop, entry.FilePaths);
                }

                break;
        }

        try
        {
            DragDrop.DoDragDrop(this, data, DragDropEffects.Copy);
        }
        catch (Exception)
        {
            // 拖拽取消/失败不冒泡
        }
        finally
        {
            _dragStarted = false;
        }
    }

    // ---------- 小部件 ----------

    private static string TypeGlyph(ClipboardEntry entry) => entry.ContentType switch
    {
        ClipboardItemKind.Image => "🖼️",
        ClipboardItemKind.Files => "📁",
        ClipboardItemKind.Html or ClipboardItemKind.RichText => "🌐",
        _ => entry.Category == ContentCategory.Code ? "📝" : "📄"
    };

    private string PreviewText(ClipboardEntry entry)
    {
        switch (entry.ContentType)
        {
            case ClipboardItemKind.Image:
                string size = entry.ImageWidth > 0 && entry.ImageHeight > 0 ? $" {entry.ImageWidth}×{entry.ImageHeight}" : string.Empty;
                return $"🖼️ 图片{size}";
            case ClipboardItemKind.Files:
                return $"📁 {entry.FilePaths.Length} 个文件";
            case ClipboardItemKind.Html:
            case ClipboardItemKind.RichText:
                if (!string.IsNullOrEmpty(entry.HtmlContent))
                {
                    string plain = ClipboardSegmenter.StripToPlainTextForWrite(entry.HtmlContent);
                    return string.IsNullOrEmpty(plain) ? "(富文本)" : plain;
                }

                return entry.Content;
            default:
                return entry.Content;
        }
    }

    private FrameworkElement CreateTypeChip(ClipboardEntry entry)
    {
        string label = entry.ContentType switch
        {
            ClipboardItemKind.Text => entry.Category == ContentCategory.Code ? "代码" : "文本",
            ClipboardItemKind.Image => "图片",
            ClipboardItemKind.Files => "文件",
            ClipboardItemKind.Html => "网页",
            ClipboardItemKind.RichText => "富文本",
            _ => "未知"
        };
        var chip = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4, 1, 4, 1),
            Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            VerticalAlignment = VerticalAlignment.Center
        };
        chip.Child = new TextBlock { Text = label, FontSize = 9, Foreground = new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF)) };
        return chip;
    }

    private static TextBlock CreateMetaText(string text, byte alpha)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 9,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF))
        };
    }

    private static Button CreateRowButton(string glyph, string tooltip, RoutedEventHandler click)
    {
        var btn = new Button
        {
            Content = glyph,
            Width = 24,
            Height = 24,
            FontSize = 11,
            Margin = new Thickness(2, 0, 0, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            ToolTip = tooltip
        };
        btn.Click += click;
        return btn;
    }

    private void LoadImageThumbnail(string imagePath, Border target)
    {
        if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
        {
            target.Child = new TextBlock { Text = "🖼️", FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            return;
        }

        Task.Run(() =>
        {
            try
            {
                using var stream = File.OpenRead(imagePath);
                var frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                var source = new BitmapImage();
                source.BeginInit();
                source.CacheOption = BitmapCacheOption.OnLoad;
                source.StreamSource = new MemoryStream(File.ReadAllBytes(imagePath));
                source.DecodePixelWidth = 56;
                source.DecodePixelHeight = 56;
                source.EndInit();
                source.Freeze();
                return (ImageSource)source;
            }
            catch (Exception)
            {
                return null;
            }
        }).ContinueWith(t =>
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (t.Result is not null && target.IsVisible)
                {
                    target.Child = new Image { Source = t.Result, Stretch = Stretch.Uniform, Width = 52, Height = 52 };
                }
            }), DispatcherPriority.Background);
        });
    }

    private object CreateEntryToolTip(ClipboardEntry entry)
    {
        var panel = new StackPanel { MaxWidth = 320 };
        panel.Children.Add(new TextBlock { Text = PreviewText(entry), FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxHeight = 80, TextTrimming = TextTrimming.CharacterEllipsis });
        panel.Children.Add(new TextBlock
        {
            Text = $"{entry.ContentType} · {(entry.CopyCount > 0 ? $"{entry.CopyCount}次" : "未使用")} · {GetRelativeTime(entry.Timestamp)}",
            FontSize = 9,
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF))
        });
        return panel;
    }

    private static string GetRelativeTime(DateTime timestamp)
    {
        TimeSpan diff = DateTime.Now - timestamp;
        if (diff.TotalSeconds < 30)
        {
            return "刚刚";
        }

        if (diff.TotalMinutes < 1)
        {
            return $"{(int)diff.TotalSeconds}秒前";
        }

        if (diff.TotalHours < 1)
        {
            return $"{(int)diff.TotalMinutes}分钟前";
        }

        if (diff.TotalDays < 1)
        {
            return $"{(int)diff.TotalHours}小时前";
        }

        if (diff.TotalDays < 7)
        {
            return $"{(int)diff.TotalDays}天前";
        }

        return timestamp.ToString("MM/dd");
    }

    private void UpdatePauseState()
    {
        bool isPaused = _service.IsTemporarilyPaused;
        int remaining = _service.PauseRemainingSeconds;
        _pauseIndicator.Visibility = isPaused ? Visibility.Visible : Visibility.Collapsed;
        _pauseText.Text = $"⏸ 已暂停 {remaining}s";
        _pauseBtn.IsChecked = isPaused;
        if (isPaused)
        {
            _pauseBtn.Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0x6B, 0x4D));
        }
        else
        {
            _pauseBtn.ClearValue(Control.ForegroundProperty);
        }
    }

    // ---------- 事件 ----------

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchFilter = _searchBox.Text.Trim();
        _selectedIndex = 0;
        RefreshList();
    }

    private void FilterChip_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border chip && chip.Tag is string tag)
        {
            _currentFilter = tag;
            if (tag == "pinned")
            {
                PinnedOnlyMode = true;
            }
            else
            {
                PinnedOnlyMode = false;
            }

            UpdateFilterChips();
            RefreshList();
            e.Handled = true;
        }
    }

    private void UpdateFilterChips()
    {
        foreach (UIElement child in _filterChipsPanel.Children)
        {
            if (child is Border chip && chip.Tag is string tag)
            {
                bool active = tag == _currentFilter;
                chip.Background = active ? new SolidColorBrush(Color.FromArgb(0x3A, 0x4A, 0x90, 0xD9)) : new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
                if (chip.Child is TextBlock tb)
                {
                    tb.Foreground = active ? new SolidColorBrush(Color.FromArgb(0xE8, 0xFF, 0xFF, 0xFF)) : new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));
                }
            }
        }
    }

    private void SourceFilterChip_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border chip)
        {
            _currentSourceFilter = chip.Tag as string;
            UpdateSourceFilterBar();
            RefreshList();
            e.Handled = true;
        }
    }

    private void SeparatorChip_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border chip && chip.Tag is string sep)
        {
            _mergeSeparator = sep;
            UpdateSeparatorChips();
            e.Handled = true;
        }
    }

    private void UpdateSeparatorChips()
    {
        if (_separatorPanel is null)
        {
            return;
        }

        foreach (UIElement child in _separatorPanel.Children)
        {
            if (child is Border chip && chip.Tag is string sep)
            {
                bool active = sep == _mergeSeparator;
                chip.Background = active ? new SolidColorBrush(Color.FromArgb(0x3A, 0x4A, 0x90, 0xD9)) : new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
            }
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyboardDevice.Modifiers == ModifierKeys.Control && e.Key == Key.V && !_multiSelectMode)
        {
            if (_currentList.Count > 0)
            {
                PasteEntryAndClose(_currentList[Math.Max(0, _selectedIndex)]);
            }

            e.Handled = true;
            return;
        }

        if (e.KeyboardDevice.Modifiers == ModifierKeys.Control && e.Key == Key.C && !_multiSelectMode)
        {
            if (_selectedIndex >= 0 && _selectedIndex < _currentList.Count)
            {
                _service.CopyEntryToClipboard(_currentList[_selectedIndex]);
            }

            e.Handled = true;
            return;
        }

        if (e.KeyboardDevice.Modifiers == ModifierKeys.Control && e.Key == Key.Enter)
        {
            if (_selectedIndex >= 0 && _selectedIndex < _currentList.Count)
            {
                PasteEntryAndClose(_currentList[_selectedIndex], true);
            }

            e.Handled = true;
            return;
        }

        if (e.KeyboardDevice.Modifiers == ModifierKeys.Control && e.Key == Key.A && _multiSelectMode)
        {
            foreach (var entry in _currentList)
            {
                _selectedEntries.Add(entry);
            }

            RefreshList();
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                if (_multiSelectMode)
                {
                    _multiSelectMode = false;
                    _selectedEntries.Clear();
                    RefreshList();
                }
                else
                {
                    Hide();
                }

                e.Handled = true;
                break;

            case Key.Down:
                if (_currentList.Count > 0 && _selectedIndex < _currentList.Count - 1)
                {
                    _selectedIndex++;
                    UpdateSelection();
                }

                e.Handled = true;
                break;

            case Key.Up:
                if (_currentList.Count > 0 && _selectedIndex > 0)
                {
                    _selectedIndex--;
                    UpdateSelection();
                }

                e.Handled = true;
                break;

            case Key.Home:
                if (_currentList.Count > 0)
                {
                    _selectedIndex = 0;
                    UpdateSelection();
                }

                e.Handled = true;
                break;

            case Key.End:
                if (_currentList.Count > 0)
                {
                    _selectedIndex = _currentList.Count - 1;
                    UpdateSelection();
                }

                e.Handled = true;
                break;

            case Key.Enter:
                if (_multiSelectMode)
                {
                    if (_selectedEntries.Count > 0)
                    {
                        MergePasteSelected();
                    }
                }
                else if (_selectedIndex >= 0 && _selectedIndex < _currentList.Count)
                {
                    PasteEntryAndClose(_currentList[_selectedIndex]);
                }

                e.Handled = true;
                break;

            case Key.Delete:
            case Key.Back:
                if (_multiSelectMode && _selectedEntries.Count > 0)
                {
                    _service.DeleteEntries(_selectedEntries);
                    _selectedEntries.Clear();
                    RefreshList();
                }
                else if (_selectedIndex >= 0 && _selectedIndex < _currentList.Count)
                {
                    DeleteEntry(_currentList[_selectedIndex]);
                }

                e.Handled = true;
                break;

            case Key.P:
                if (!_multiSelectMode && _selectedIndex >= 0 && _selectedIndex < _currentList.Count)
                {
                    TogglePin(_currentList[_selectedIndex]);
                }

                e.Handled = true;
                break;

            case Key.T:
                if (!_multiSelectMode && _selectedIndex >= 0 && _selectedIndex < _currentList.Count)
                {
                    EditEntryTags(_currentList[_selectedIndex]);
                }

                e.Handled = true;
                break;

            case Key.O:
                if (!_multiSelectMode && _selectedIndex >= 0 && _selectedIndex < _currentList.Count)
                {
                    var entry = _currentList[_selectedIndex];
                    if (entry.ContentType == ClipboardItemKind.Files)
                    {
                        _service.OpenFileLocation(entry);
                        Hide();
                    }
                }

                e.Handled = true;
                break;

            case Key.Space:
                if (_multiSelectMode && _selectedIndex >= 0 && _selectedIndex < _currentList.Count)
                {
                    ToggleMultiSelect(_currentList[_selectedIndex]);
                    RefreshList();
                }

                e.Handled = true;
                break;

            default:
                if (!_multiSelectMode && e.Key >= Key.D1 && e.Key <= Key.D9)
                {
                    int num = (int)e.Key - (int)Key.D1;
                    if (num >= 0 && num < _currentList.Count)
                    {
                        PasteEntryAndClose(_currentList[num]);
                        e.Handled = true;
                    }
                }

                break;
        }
    }

    /// <summary>鼠标拖拽起点（MouseMove 用）。</summary>
    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        _dragStartPoint = e.GetPosition(this);
        _dragStarted = false;
        CaptureMouse();
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }
    }

    /// <summary>面板展示：跟随光标，越界回推（迁移源 ShowAtCursor 行为）。</summary>
    public void ShowAtCursor()
    {
        var wa = SystemParameters.WorkArea;
        double cursorX = wa.Left + wa.Width / 2;
        double cursorY = wa.Top + wa.Height / 2;
        try
        {
            if (ClipboardNative.GetCursorPos(out ClipboardNative.POINT p))
            {
                cursorX = p.X;
                cursorY = p.Y;
            }
        }
        catch (Exception)
        {
            // 保持工作区中心兜底
        }

        double left = cursorX + 15;
        double top = cursorY + 15;
        if (left + Width > wa.Right)
        {
            left = cursorX - Width - 15;
        }

        if (top + Height > wa.Bottom)
        {
            top = cursorY - Height - 15;
        }

        left = Math.Max(wa.Left + 5, left);
        top = Math.Max(wa.Top + 5, top);
        Left = left;
        Top = top;
        Show();
        Activate();
        _searchBox.Focus();
        _searchBox.SelectAll();
    }
}
