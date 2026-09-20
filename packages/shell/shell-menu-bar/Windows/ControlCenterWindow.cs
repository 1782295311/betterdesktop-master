// BetterDesktop.Shell.MenuBar — 控制中心独立弹出面板（macOS Ventura 视觉：连接区条目 + 模块卡片）
//
// 布局：
//   顶部：连接区 —— 6 行纵向条目（macOS 控制中心风格，2026-08-30 第五轮重设计）
//     每行：圆形图标(28) + 标题 + 状态摘要 + 右侧开关（Wi‑Fi/蓝牙）或 › 指示（热点/专注/台前调度/投影）
//     整行 hover 高亮；左键：开关项切换、非开关项打开独立面板；右键：一律打开独立面板。
//     （此前 2×3 网格的右列仅 148px，功能文字被截断，反复调整仍显示不全 —— 纵向行宽度充裕，彻底解决。）
//   中部：三张统一规格的模块卡片（圆角 + 描边 + 「标题左 / 数值右」对齐的头部）
//     显示器（亮度滑杆）· 声音（主音量滑杆 + 麦克风静音）· 正在播放（SMTC 简略控制 + 「打开音乐界面」入口）。
//     【2026-09-09 声音面板改造 v2】简略卡片保留（用户要求：控制中心必须承载简略控制）；
//     完整音量/音乐控制（应用混音器 / 进度 / seek / 随机 / 循环 / 最近播放）统一由声音面板承载，
//     卡片内「打开音乐界面 ›」与功能目录「声音」行均可进入。
//
// 【2026-08-30 问题 6 重构】
//   1) 图标：此前全部写死 Segoe MDL2 Assets 码位（\uE701/\uE702/\uE7C2/\uE81E/\uEB0B/\uE7B4/
//      \uE720/\uEC4F/\uE100/\uE102/\uE101），字形与语义对不上、字体缺失显示方块。
//      统一换成 Contracts/ControlCenterGlyph.cs 的 24×24 纯自绘。
//   2) 信息对齐：三张模块卡片共用 BuildModuleCard（标题左、数值右），卡片内控件垂直居中；
//      大瓦片图标由 Top 对齐改为 Center 对齐（此前图标顶挂、文字居中 → 视觉错位）。
//   3) 功能对接：麦克风静音按钮此前只是装饰（无点击事件）；媒体区歌名写死空格 + 三个按钮
//      无点击事件。现分别接到 AudioCoreNative.SetCaptureVolume 与 SMTC（MediaPlayerCore）。
//      （2026-09-09：声音/媒体卡片整体移除，能力迁至声音面板 SoundPanelWindow。）
//   4) 描边/背景：硬编码半透明白（Color.FromArgb(120/60,255,255,255)）改走主题令牌 ThemeSeparator。
//   5) 第五轮（2026-08-30）：顶部 2×3 网格 → 纵向 6 行条目（右列 148px 截断问题的根治）。
//
// 所有状态语义均来自 shell-status；未接入的模块显示空壳（不写死任何假设备名）。

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.MenuBar.Contracts;
using BetterDesktop.Shell.MenuBar.Services;
using BetterDesktop.Shell.Music.Contracts;
using BetterDesktop.Shell.Status.Contracts;
using BetterDesktop.Shell.Status.Native;
using Windows.Devices.Radios;
using Windows.Storage.Streams;

namespace BetterDesktop.Shell.MenuBar.Windows;

// ── 本文件方法级白话索引（控制中心面板，白话 → 方法）──
//   "面板整体/快捷开关列表"         → BuildContent / BuildToggleList
//   "一个开关磁贴行"                → MakeToggleRow / AddRow；点击 OnTileLeftClick/OnTileRightClick，切换 ToggleTileAsync
//   "刷新磁贴开关状态"              → RefreshTileStatesAsync / RefreshTileStateAsync / FindFeature
//   "磁贴视觉（高亮/图标圈/悬停）"  → ApplyTileVisual / ApplyIconCircleVisual / ApplySummaryForeground / AttachTileHover
//   "模块卡片（亮度/声音/正在播放）" → BuildModuleCard / BuildCardHeader / CreateCardValue
//   各快捷开关背后的系统能力在 shell-status / shell-core；面板基类 MenuBarPopupWindow。
// ────────────────────────────────────

/// <summary>
/// 控制中心独立面板（macOS 风格：开关网格 + 模块化圆角卡片）。
/// 概览瓦片点击后唤起该功能"自己的独立完整面板"，控制中心本身只承载简略视图。
/// </summary>
internal sealed class ControlCenterWindow : MenuBarPopupWindow
{
    // 整体宽度（与 macOS 风格一致）
    private const double PanelWidth = 380;

    // 连接区纵向条目：行高与行距（StackPanel 布局，宽度 = 面板宽 - padding，文字永不截断）
    private const double RowMarginBottom = 6;

    // 激活瓦片底色：Windows 强调蓝。蓝底上恒用 MenuBarTheme.Foreground（亮/暗主题下都是可读白字）。
    private static readonly Brush TileAccent = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4));
    // 麦克风静音态底色：橙色（与系统"已静音"语义一致，跨厂商约定俗成）
    private static readonly Brush MicMutedBackground = new SolidColorBrush(Color.FromRgb(0xFF, 0x95, 0x00));
    // 静音态图标用近黑（橙底上对比度最高），非静音态用主题前景
    private static readonly Brush OnAccentForeground = new SolidColorBrush(Color.FromRgb(0x32, 0x32, 0x34));

    private readonly IReadOnlyList<ControlCenterFeature> _features;
    private readonly IVolumeMonitor? _vol;
    private readonly IBrightnessMonitor? _brightness;
    private readonly IVibrancyService _vibrancy;
    private readonly IAppearanceService? _appearance;

    private BrightnessSliderControl? _brightnessControl;
    private SoundPanelWindow? _audioPanel;

    /// <summary>预览模式（Playground）：关闭一切"需要配对释放"的资源（事件订阅）。</summary>
    private bool _isPreview;

    // —— 开关网格瓦片引用：左键切换、异步刷新状态时更新视觉 ——
    private readonly Dictionary<string, TileRef> _tileRefs = new();

    private sealed class TileRef
    {
        public Border Tile { get; init; } = null!;
        public TextBlock? SummaryText { get; init; }
        public Border IconCircle { get; init; } = null!;
        public ToggleSwitch? Switch { get; init; }
        public bool HasSummary { get; init; }
        public ControlCenterFeature Feature { get; init; } = null!;
    }

    // —— 需要随监控事件实时刷新的控件引用 ——
    private TextBlock? _brightnessValue;
    private Slider? _volumeSlider;
    private TextBlock? _volumeValue;
    private Border? _micButton;
    private TextBlock? _mediaTitle;
    private TextBlock? _mediaArtist;
    private TextBlock? _mediaApp;
    private Path? _playPausePath;
    private readonly List<Border> _mediaButtons = new();

    // —— 正在播放卡片：2s 轮询 IMediaPlaybackService 快照（原 MediaSessionController 轻量壳已删，职责内联）——
    private readonly IMediaPlaybackService? _media;
    private DispatcherTimer? _mediaTimer;
    private MediaPlaybackSnapshot? _mediaSession;
    private Border? _mediaCover;
    /// <summary>封面防重键（歌曲身份；切歌才重读，与声音面板一致）。</summary>
    private string? _mediaCoverKey;

    public ControlCenterWindow(
        IReadOnlyList<ControlCenterFeature> features,
        IVolumeMonitor? vol,
        IMicrophoneMonitor? mic,
        IBrightnessMonitor? brightness,
        IVibrancyService vibrancy,
        IAppearanceService? appearance = null,
        IMediaPlaybackService? media = null)
        : base(vibrancy, appearance)
    {
        _features = features; _vol = vol; _brightness = brightness;
        _media = media;
        _vibrancy = vibrancy; _appearance = appearance;
        // 注：mic 构造参数保留（调用点签名不变）；麦克风静音由声音卡片（本窗口）承载，
        // 完整音量/音乐控制由声音面板（SoundPanelWindow，卡片内「打开音乐界面」入口 + 功能目录「声音」行）。
        Width = PanelWidth;
        MinWidth = PanelWidth;
        SizeToContent = SizeToContent.Height;
    }

    /// <summary>Playground/大容器 预览入口：直接取内容 UI（不走 ShellWindow 生命周期）。</summary>
    /// <remarks>
    /// 预览模式下**不订阅监控事件**：
    /// BuildPreviewContent 不会走 OnClosed，退订/Dispose 永远不会执行；
    /// 若不区分，Playground 每构造一次预览就多一组常驻事件订阅。
    /// </remarks>
    public FrameworkElement BuildPreviewContent()
    {
        _isPreview = true;
        return BuildContent();
    }

    protected override FrameworkElement BuildContent()
    {
        // —— 外层容器：透明，面板背景/描边/圆角由基类 ApplyContent 按主题令牌统一挂载 ——
        var root = new Border
        {
            Padding = new Thickness(12),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };

        var column = new StackPanel { Orientation = Orientation.Vertical };

        // 1) 顶部：连接区（macOS 风格纵向条目）
        column.Children.Add(BuildToggleList());

        // 2) 显示器卡片（亮度滑块）
        column.Children.Add(BuildBrightnessCard());

        // 3) 声音卡片（主音量滑块 + 麦克风静音）——简略控制，控制中心的定位
        column.Children.Add(BuildVolumeCard());

        // 4) 正在播放卡片（SMTC 简略控制 + 「打开音乐界面」入口）
        column.Children.Add(BuildMediaCard());

        root.Child = column;

        // 启动后异步刷新 Wi‑Fi/蓝牙无线电真实状态（避免同步阻塞 UI，同时让瓦片高亮/小结更准确）
        if (!_isPreview)
        {
            Dispatcher.BeginInvoke(new Action(async () => await RefreshTileStatesAsync()), DispatcherPriority.Background);
        }

        // 订阅亮度/音量变化（控制中心是常驻窗口，事件驱动刷新优于自行轮询）
        if (!_isPreview)
        {
            if (_brightness is not null) _brightness.Changed += OnBrightnessChanged;
            if (_vol is not null) _vol.Changed += OnVolumeChanged;
        }

        return root;
    }

    // ============================================================
    //  顶部连接区（macOS 风格纵向条目：图标 + 标题/摘要 + 右侧开关）
    // ============================================================
    private FrameworkElement BuildToggleList()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };

        // 前 3 项带真实开关（Wi‑Fi / 蓝牙无线电），后 3 项点击打开对应设置/面板
        AddRow(panel, "Wi‑Fi", ControlCenterIcon.Wifi, accentOn: true);
        AddRow(panel, "蓝牙", ControlCenterIcon.Bluetooth, accentOn: true);
        AddRow(panel, "热点", ControlCenterIcon.Hotspot, accentOn: false);
        AddRow(panel, "专注助手", ControlCenterIcon.Focus, accentOn: false);
        AddRow(panel, "投影", ControlCenterIcon.Project, accentOn: false);

        return panel;
    }

    private void AddRow(StackPanel panel, string title, ControlCenterIcon icon, bool accentOn)
    {
        var feature = FindFeature(title);
        if (feature is null) return;
        panel.Children.Add(MakeToggleRow(feature, icon, accentOn));
    }

    private ControlCenterFeature? FindFeature(string title)
    {
        for (int i = 0; i < _features.Count; i++)
        {
            if (string.Equals(_features[i].Title, title, StringComparison.Ordinal))
            {
                return _features[i];
            }
        }
        return null;
    }

    /// <summary>判定某功能的小结文本是否代表"已开启/已连接"（决定条目是否上强调色）。</summary>
    private static bool IsActiveSummary(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary)) return false;
        return summary != "关闭" && summary != "已关闭" && summary != "无适配器" && summary != "—" && summary != "离线";
    }

    /// <summary>
    /// 单个纵向条目：圆形图标(28) + 标题 + 状态摘要 + 右侧开关（开关项）或 ›（非开关项）。
    /// 行宽 = 面板内容宽（356px），文字不再被右列宽度截断（此前 2×3 网格右列仅 148px）。
    /// 整行 hover 高亮；左键：开关项切换、非开关项打开面板；右键：一律打开独立面板。
    /// </summary>
    private FrameworkElement MakeToggleRow(ControlCenterFeature feature, ControlCenterIcon icon, bool accentOn)
    {
        var summary = feature.Summary() ?? string.Empty;
        bool active = accentOn && IsActiveSummary(summary);

        var tile = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 7, 10, 7),
            Margin = new Thickness(0, 0, 0, RowMarginBottom),
            BorderThickness = new Thickness(1),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        ApplyTileVisual(tile, active);

        var row = new Grid { VerticalAlignment = VerticalAlignment.Stretch };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 图标：28 圆底 + 16 自绘字形，垂直居中
        var iconCircle = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(14),
            VerticalAlignment = VerticalAlignment.Center
        };
        ApplyIconCircleVisual(iconCircle, active);
        iconCircle.Child = new Viewbox
        {
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
            Child = ControlCenterGlyph.Create(icon, MenuBarTheme.Foreground)
        };
        Grid.SetColumn(iconCircle, 0);
        row.Children.Add(iconCircle);

        // 标题 + 状态摘要（同一中心线，信息对齐）
        TextBlock? summaryText = null;
        var textCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var titleText = new TextBlock
        {
            Text = feature.Title,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = MenuBarTheme.Foreground,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        textCol.Children.Add(titleText);
        if (!string.IsNullOrEmpty(summary))
        {
            summaryText = new TextBlock
            {
                Text = summary,
                FontSize = 10.5,
                Margin = new Thickness(0, 1, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Opacity = 0.85
            };
            ApplySummaryForeground(summaryText, active);
            textCol.Children.Add(summaryText);
        }
        Grid.SetColumn(textCol, 2);
        row.Children.Add(textCol);

        // 右侧：开关项放 ToggleSwitch（点击自动翻转 + 触发切换）；非开关项放 › 指示
        ToggleSwitch? toggle = null;
        if (feature.IsToggle)
        {
            toggle = new ToggleSwitch
            {
                IsOn = active,
                VerticalAlignment = VerticalAlignment.Center
            };
            toggle.Toggled += (_, _) =>
            {
                try { _ = ToggleTileAsync(tile, feature); }
                catch { /* 切换失败静默 */ }
            };
            Grid.SetColumn(toggle, 3);
            row.Children.Add(toggle);
        }
        else
        {
            var arrow = new TextBlock
            {
                Text = "›",
                FontSize = 16,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0)
            };
            SetThemeBinding(arrow, TextBlock.ForegroundProperty, "ThemeMutedForeground");
            Grid.SetColumn(arrow, 3);
            row.Children.Add(arrow);
        }

        tile.Child = row;
        AttachTileHover(tile);
        tile.MouseLeftButtonUp += (_, _) => OnTileLeftClick(tile, feature);
        tile.MouseRightButtonUp += (_, _) => OnTileRightClick(tile, feature);

        _tileRefs[feature.Title] = new TileRef
        {
            Tile = tile,
            SummaryText = summaryText,
            IconCircle = iconCircle,
            Switch = toggle,
            HasSummary = summaryText is not null,
            Feature = feature
        };
        return tile;
    }

    /// <summary>应用瓦片底色与描边：active=true 强调蓝底；false 透明底 + 主题描边。</summary>
    private static void ApplyTileVisual(Border tile, bool active)
    {
        tile.Background = active ? TileAccent : Brushes.Transparent;
        if (active)
        {
            tile.BorderBrush = TileAccent;
        }
        else
        {
            SetThemeBinding(tile, Border.BorderBrushProperty, "ThemeSeparator");
        }
    }

    private static void ApplyIconCircleVisual(Border iconCircle, bool active)
    {
        if (!active)
        {
            SetThemeBinding(iconCircle, Border.BackgroundProperty, "ThemeContentBackground");
        }
        else
        {
            iconCircle.Background = Brushes.Transparent;
        }
    }

    private static void ApplySummaryForeground(TextBlock summaryText, bool active)
    {
        if (active)
        {
            summaryText.Foreground = MenuBarTheme.Foreground;
        }
        else
        {
            SetThemeBinding(summaryText, TextBlock.ForegroundProperty, "ThemeMutedForeground");
        }
    }

    /// <summary>瓦片左键：可切换项先切换无线电；否则打开独立面板。</summary>
    private void OnTileLeftClick(Border tile, ControlCenterFeature feature)
    {
        if (feature.IsToggle)
        {
            _ = ToggleTileAsync(tile, feature);
            return;
        }
        OnTileRightClick(tile, feature);
    }

    /// <summary>瓦片右键：始终打开该功能的独立完整面板。</summary>
    private void OnTileRightClick(Border tile, ControlCenterFeature feature)
    {
        var anchor = MenuBarScreen.ToLogical(tile, tile.PointToScreen(new Point(0, 0)));
        feature.OnActivate(anchor);
    }

    private async Task ToggleTileAsync(Border tile, ControlCenterFeature feature)
    {
        if (feature.ToggleAsync is null) return;
        var result = await feature.ToggleAsync();
        if (result is null) return;

        // 切换后立即按真实系统状态刷新该瓦片（SSID/连接设备名可能随开关变化）
        await RefreshTileStateAsync(feature.Title);
    }

    /// <summary>异步刷新所有瓦片的无线电/连接状态（控制中心打开后调用，避免同步阻塞 UI）。</summary>
    private async Task RefreshTileStatesAsync()
    {
        foreach (var title in _tileRefs.Keys.ToArray())
        {
            await RefreshTileStateAsync(title);
        }
    }

    private async Task RefreshTileStateAsync(string title)
    {
        if (!_tileRefs.TryGetValue(title, out var refs)) return;
        var feature = refs.Feature;

        string summary;
        bool? radioOn = null;
        if (title == "Wi‑Fi")
        {
            radioOn = (await RadioInterop.GetStateAsync(RadioKind.WiFi)) == RadioState.On;
            summary = feature.Summary();
            // 如果无线电关闭，小结应明确显示；如果打开但没连，显示"未连接"/SSID
            if (radioOn == false) summary = "已关闭";
        }
        else if (title == "蓝牙")
        {
            radioOn = (await RadioInterop.GetStateAsync(RadioKind.Bluetooth)) == RadioState.On;
            summary = feature.Summary();
            if (radioOn == false) summary = "已关闭";
        }
        else
        {
            summary = feature.Summary();
        }

        bool active = IsActiveSummary(summary);
        if (radioOn.HasValue) active = radioOn.Value;

        await Dispatcher.InvokeAsync(() =>
        {
            if (refs.HasSummary && refs.SummaryText is not null)
            {
                refs.SummaryText.Text = summary;
            }
            ApplyTileVisual(refs.Tile, active);
            ApplyIconCircleVisual(refs.IconCircle, active);
            if (refs.SummaryText is not null)
            {
                ApplySummaryForeground(refs.SummaryText, active);
            }
            // 同步右侧开关（真实无线电状态驱动，点击后由 ToggleAsync 的结果刷回）
            if (refs.Switch is not null)
            {
                refs.Switch.IsOn = active;
            }
        });
    }

    /// <summary>瓦片悬停反馈。激活态已有强调蓝底，不再叠加。</summary>
    private static void AttachTileHover(Border tile)
    {
        tile.MouseEnter += (_, _) =>
        {
            if (!IsTileActive(tile)) tile.Background = MenuBarTheme.Hover;
        };
        tile.MouseLeave += (_, _) =>
        {
            if (!IsTileActive(tile)) tile.Background = Brushes.Transparent;
        };
        tile.MouseLeftButtonDown += (_, _) =>
        {
            if (!IsTileActive(tile)) tile.Background = MenuBarTheme.Pressed;
        };
    }

    private static bool IsTileActive(Border tile) => ReferenceEquals(tile.Background, TileAccent);

    // ============================================================
    //  模块卡片（统一规格：头部「标题左 / 数值右」+ 内容）
    // ============================================================

    /// <summary>卡片右上角数值文本（次要前景，右对齐，过长截断）。</summary>
    private static TextBlock CreateCardValue(string text)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
            MaxWidth = 130,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        SetThemeBinding(tb, TextBlock.ForegroundProperty, "ThemeMutedForeground");
        return tb;
    }

    /// <summary>卡片头部：标题左、数值右。三张卡片共用，保证纵向对齐。</summary>
    private static FrameworkElement BuildCardHeader(string title, FrameworkElement? value)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var t = new TextBlock
        {
            Text = title,
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(t, 0);
        grid.Children.Add(t);

        if (value is not null)
        {
            Grid.SetColumn(value, 1);
            grid.Children.Add(value);
        }
        return grid;
    }

    private static FrameworkElement BuildModuleCard(string title, FrameworkElement? value, FrameworkElement body, Thickness margin)
    {
        var card = new Border
        {
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = margin,
            BorderThickness = new Thickness(1),
            Background = Brushes.Transparent,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };
        // 卡片描边走主题令牌（此前写死 Color.FromArgb(60,255,255,255)，亮色模式下几乎看不见）
        SetThemeBinding(card, Border.BorderBrushProperty, "ThemeSeparator");

        var column = new StackPanel { Orientation = Orientation.Vertical };
        column.Children.Add(BuildCardHeader(title, value));
        column.Children.Add(body);
        card.Child = column;
        return card;
    }

    // ============================================================
    //  声音卡片（主音量滑块 + 麦克风静音）——简略控制，控制中心定位
    // ============================================================
    private FrameworkElement BuildVolumeCard()
    {
        _volumeValue = CreateCardValue("—");

        var row = new Grid { VerticalAlignment = VerticalAlignment.Center };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        double initial = ReadRenderVolumePercent();
        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = initial < 0 ? 50 : initial,
            SmallChange = 1,
            LargeChange = 10,
            VerticalAlignment = VerticalAlignment.Center,
            MinHeight = 24,
            Style = NativePanelStyles.CreateCircleThumbSliderStyle()
        };
        // 流畅滑杆：拖动中只更新头部百分比，拖动结束/点击跳转才调系统 API
        NativePanelStyles.ConfigureSmoothSlider(slider,
            onValueCommitted: v =>
            {
                double pct = Math.Clamp(v, 0, 100);
                if (AudioCoreNative.IsAvailable)
                {
                    AudioCoreNative.SetMasterVolume((float)(pct / 100.0));
                }
                if (_volumeValue is not null) _volumeValue.Text = $"{(int)Math.Round(pct)}%";
            },
            onValueChanging: v =>
            {
                if (_volumeValue is not null) _volumeValue.Text = $"{(int)Math.Round(Math.Clamp(v, 0, 100))}%";
            });
        _volumeSlider = slider;
        Grid.SetColumn(slider, 0);
        row.Children.Add(slider);

        // 麦克风静音按钮（点击翻转静音，替代装饰态）
        _micButton = BuildMicButton();
        Grid.SetColumn(_micButton, 1);
        row.Children.Add(_micButton);

        _volumeValue.Text = initial < 0 ? "—" : $"{(int)Math.Round(initial)}%";
        return BuildModuleCard("声音", _volumeValue, row, new Thickness(0, 10, 0, 0));
    }

    /// <summary>读取输出端点音量百分比（0-100）。不可用时返回 -1。</summary>
    private double ReadRenderVolumePercent()
    {
        if (AudioCoreNative.IsAvailable)
        {
            var st = AudioCoreNative.GetStatus(AudioFlow.Render);
            if (st.Ok) return Math.Clamp(st.VolumeFloat * 100.0, 0, 100);
        }
        if (_vol is not null)
        {
            var snap = _vol.GetSnapshot();
            if (snap.Progress >= 0) return Math.Clamp(snap.Progress, 0, 100);
        }
        return -1;
    }

    private Border BuildMicButton()
    {
        var btn = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(16),
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        btn.ToolTip = "麦克风静音";
        ApplyMicVisual(btn);
        btn.MouseLeftButtonUp += (_, _) => ToggleMicMute();
        return btn;
    }

    /// <summary>按当前采集端点状态重画麦克风按钮（橙底=已静音，次内容底=正常）。</summary>
    private void ApplyMicVisual(Border btn)
    {
        bool muted = IsMicMuted();
        btn.Background = muted ? MicMutedBackground : null;
        if (!muted)
        {
            SetThemeBinding(btn, Border.BackgroundProperty, "ThemeContentBackground");
        }
        btn.Child = new Viewbox
        {
            Width = 17,
            Height = 17,
            Stretch = Stretch.Uniform,
            Child = ControlCenterGlyph.Create(
                ControlCenterIcon.Microphone,
                muted ? OnAccentForeground : MenuBarTheme.Foreground)
        };
        btn.ToolTip = muted ? "麦克风已静音（点击取消静音）" : "麦克风正常（点击静音）";
    }

    /// <summary>采集端点（麦克风）当前是否静音。端点不可用时按"未静音"处理（按钮不高橙）。</summary>
    private static bool IsMicMuted()
    {
        if (!AudioCoreNative.IsAvailable) return false;
        var st = AudioCoreNative.GetStatus(AudioFlow.Capture);
        return st.Ok && st.Muted;
    }

    /// <summary>切换麦克风静音：保持当前采集音量不变，只翻转静音位。</summary>
    private void ToggleMicMute()
    {
        if (!AudioCoreNative.IsAvailable) return;
        var st = AudioCoreNative.GetStatus(AudioFlow.Capture);
        if (!st.Ok) return;
        AudioCoreNative.SetCaptureVolume(st.VolumeFloat, !st.Muted);
        if (_micButton is not null)
        {
            ApplyMicVisual(_micButton);
        }
    }

    private void OnVolumeChanged(object? sender, StatusSnapshot snapshot)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            double pct = ReadRenderVolumePercent();
            if (_volumeValue is not null)
            {
                _volumeValue.Text = pct < 0 ? "—" : $"{(int)Math.Round(pct)}%";
            }
            // 外部改音量时同步滑杆；拖动中（已捕获鼠标）不覆盖，避免把用户手指拉回去
            if (_volumeSlider is not null && pct >= 0 && !_volumeSlider.IsMouseCaptureWithin)
            {
                _volumeSlider.Value = pct;
            }
            if (_micButton is not null)
            {
                ApplyMicVisual(_micButton);
            }
        }));
    }

    // ============================================================
    //  正在播放卡片（SMTC 简略控制 + 「打开音乐界面」入口）
    // ============================================================
    private FrameworkElement BuildMediaCard()
    {
        _mediaApp = CreateCardValue("无媒体");

        var grid = new Grid { VerticalAlignment = VerticalAlignment.Center };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 封面：简略卡片也显示实时封面（歌曲身份防重，异步读流；无封面回退占位字形）
        var album = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(10),
            VerticalAlignment = VerticalAlignment.Center,
            Clip = new RectangleGeometry(new Rect(0, 0, 40, 40)) { RadiusX = 10, RadiusY = 10 }
        };
        SetThemeBinding(album, Border.BackgroundProperty, "ThemeContentBackground");
        album.Child = new Viewbox
        {
            Width = 20,
            Height = 20,
            Stretch = Stretch.Uniform,
            Child = ControlCenterGlyph.Create(ControlCenterIcon.Music, MenuBarTheme.Foreground)
        };
        _mediaCover = album;
        Grid.SetColumn(album, 0);
        grid.Children.Add(album);

        var textCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _mediaTitle = new TextBlock
        {
            Text = "没有正在播放的媒体",
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        _mediaArtist = new TextBlock
        {
            Text = "打开音乐或视频应用后会显示在这里",
            FontSize = 10.5,
            Margin = new Thickness(0, 1, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        SetThemeBinding(_mediaArtist, TextBlock.ForegroundProperty, "ThemeMutedForeground");
        textCol.Children.Add(_mediaTitle);
        textCol.Children.Add(_mediaArtist);
        Grid.SetColumn(textCol, 2);
        grid.Children.Add(textCol);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(MakeMediaButton(
            ControlCenterGlyph.CreatePrev(MenuBarTheme.Foreground),
            () => _ = SendMediaAsync(MediaPlaybackCommand.Previous)));
        _playPausePath = ControlCenterGlyph.CreatePlay(MenuBarTheme.Foreground);
        buttons.Children.Add(MakeMediaButton(
            _playPausePath,
            () => _ = SendMediaAsync(MediaPlaybackCommand.Toggle)));
        buttons.Children.Add(MakeMediaButton(
            ControlCenterGlyph.CreateNext(MenuBarTheme.Foreground),
            () => _ = SendMediaAsync(MediaPlaybackCommand.Next)));
        Grid.SetColumn(buttons, 3);
        grid.Children.Add(buttons);

        // 「打开音乐界面」入口 → 完整声音面板（音乐独立界面：封面/进度/seek/随机/循环/最近播放）
        var openLink = new TextBlock
        {
            Text = "打开音乐界面 ›",
            FontSize = 10.5,
            Foreground = MenuBarTheme.Foreground,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 6, 2, 0),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        openLink.MouseLeftButtonUp += (_, _) => OpenSoundPanel();
        var body = new StackPanel();
        body.Children.Add(grid);
        body.Children.Add(openLink);

        // 启动 SMTC 会话轮询（2s 一轮，直接消费 MediaPlayerCore；命令后即时回读）
        if (!_isPreview)
        {
            _mediaTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _mediaTimer.Tick += async (_, _) => await MediaTickAsync();
            _mediaTimer.Start();

            // 【2026-09-18 电源管理】面板收起走基类 HidePopup()（只 Hide、不触发 OnClosed），
            // 而 _mediaTimer 原本只在 OnClosed 里停 → 用户点过一次控制中心后，本进程整个生命周期
            // 都在每 2s 做一次原生 SMTC 会话枚举（+ 可能的封面读取）。
            // 订阅前先退订一次，保证幂等（本方法在内容懒构建路径上）。
            IsVisibleChanged -= OnVisibilityChangedForMediaTimer;
            IsVisibleChanged += OnVisibilityChangedForMediaTimer;
        }

        return BuildModuleCard("正在播放", _mediaApp, body, new Thickness(0, 10, 0, 0));
    }

    /// <summary>打开完整音乐界面（声音面板 SoundPanelWindow，锚定控制中心位置）。</summary>
    private void OpenSoundPanel()
    {
        _audioPanel ??= new SoundPanelWindow(_vibrancy, _appearance, _media);
        _audioPanel.ShowAt(new Point(Left, Top));
        _audioPanel.Activate();
    }

    /// <param name="glyph">图标 Path。播放/暂停按钮传的是**可复用的 Path 实例**，
    /// 播放状态变化时直接改它的 Data（不重建按钮，避免闪烁与丢失悬停态）。</param>
    private Border MakeMediaButton(Path glyph, Action onClick)
    {
        var host = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(14),
            Background = Brushes.Transparent,
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        host.Child = new Viewbox
        {
            Width = 14,
            Height = 14,
            Stretch = Stretch.Uniform,
            Child = glyph
        };
        host.MouseEnter += (_, _) => host.Background = MenuBarTheme.Hover;
        host.MouseLeave += (_, _) => host.Background = Brushes.Transparent;
        host.MouseLeftButtonDown += (_, _) => host.Background = MenuBarTheme.Pressed;
        host.MouseLeftButtonUp += (_, _) => onClick();
        _mediaButtons.Add(host);
        return host;
    }

    private async Task SendMediaAsync(MediaPlaybackCommand command)
    {
        var media = _media;
        if (media is null || _mediaSession is null) return;
        await media.SendCommandAsync(command);
        await MediaTickAsync(); // 命令后即时回读，UI 立即反映
    }

    /// <summary>轮询读取媒体快照并更新卡片（命令后亦即时调用）。</summary>
    private async Task MediaTickAsync()
    {
        var media = _media;
        if (media is null) return;
        _mediaSession = await media.GetActiveSessionAsync();
        ApplyMediaState();
    }

    private void ApplyMediaState()
    {
        var session = _mediaSession;
        // 是否播放：由快照状态判定（公共契约 MediaPlaybackState）

        // 封面：歌曲身份变化才异步读流（无封面/失败回退占位字形）
        _ = LoadMediaCoverAsync(session);
        bool playing = session?.State == MediaPlaybackState.Playing;

        if (_mediaTitle is not null)
        {
            _mediaTitle.Text = session is null ? "没有正在播放的媒体" : session.Title;
        }
        if (_mediaArtist is not null)
        {
            _mediaArtist.Text = session is null
                ? "打开音乐或视频应用后会显示在这里"
                : (string.IsNullOrWhiteSpace(session.Artist) ? session.AppName : session.Artist);
        }
        if (_mediaApp is not null)
        {
            _mediaApp.Text = session is null ? "无媒体" : session.AppName;
        }
        if (_playPausePath is not null)
        {
            _playPausePath.Data = playing ? ControlCenterGlyph.PauseGeometry : ControlCenterGlyph.PlayGeometry;
        }

        // 没有会话时播放控制置灰（仍可点击，但视觉上明确不可用）
        foreach (var btn in _mediaButtons)
        {
            btn.Opacity = session is null ? 0.35 : 1.0;
            btn.Cursor = session is null ? null : System.Windows.Input.Cursors.Hand;
        }
    }

    /// <summary>加载卡片实时封面：歌曲身份防重（引用相等不可靠），读流成功后替换占位字形。</summary>
    private async Task LoadMediaCoverAsync(MediaPlaybackSnapshot? session)
    {
        var cover = _mediaCover;
        var thumbRef = session?.ThumbnailRef;
        var key = session is null ? null : $"{session.Title}|{session.Artist}|{session.AppName}";
        if (cover is null || string.Equals(_mediaCoverKey, key, StringComparison.Ordinal)) return;
        _mediaCoverKey = key;
        var bytes = await ReadThumbnailBytesAsync(thumbRef);
        if (!string.Equals(_mediaCoverKey, key, StringComparison.Ordinal)) return; // 已切歌：丢弃过期结果
        if (bytes is null || bytes.Length == 0)
        {
            cover.Child = new Viewbox
            {
                Width = 20,
                Height = 20,
                Stretch = Stretch.Uniform,
                Child = ControlCenterGlyph.Create(ControlCenterIcon.Music, MenuBarTheme.Foreground)
            };
            return;
        }
        var img = new BitmapImage();
        using (var ms = new System.IO.MemoryStream(bytes))
        {
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = ms;
            img.EndInit();
        }
        img.Freeze();
        cover.Child = new Image { Source = img, Stretch = Stretch.UniformToFill, SnapsToDevicePixels = true };
    }

    /// <summary>读取 SMTC 封面缩略图字节（后台 I/O，失败静默返回 null 由 UI 回退占位）。</summary>
    private static async Task<byte[]?> ReadThumbnailBytesAsync(IRandomAccessStreamReference? thumbRef)
    {
        if (thumbRef is null) return null;
        try
        {
            using var stream = await thumbRef.OpenReadAsync();
            var size = (uint)Math.Min(stream.Size, 10 * 1024 * 1024); // 10MB 上限
            using var reader = new DataReader(stream);
            await reader.LoadAsync(size);
            var bytes = new byte[size];
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch
        {
            return null;
        }
    }

    // ============================================================
    //  显示器卡片（亮度）
    // ============================================================
    private FrameworkElement BuildBrightnessCard()
    {
        _brightnessValue = CreateCardValue("—");

        FrameworkElement body;
        if (_brightness is not null)
        {
            // showValueLabel=false：百分比统一放到卡片头部（与声音卡片对齐），滑杆右侧不再重复显示
            _brightnessControl = new BrightnessSliderControl(_brightness, title: null, showSettingsLink: false, showValueLabel: false);
            body = _brightnessControl.Root;
            _brightnessValue.Text = FormatBrightness();
        }
        else
        {
            var unavailable = new TextBlock { Text = "亮度调节不可用", FontSize = 11 };
            SetThemeBinding(unavailable, TextBlock.ForegroundProperty, "ThemeMutedForeground");
            body = unavailable;
        }

        return BuildModuleCard("显示器", _brightnessValue, body, new Thickness(0, 10, 0, 0));
    }

    private string FormatBrightness()
    {
        if (_brightness is null || !_brightness.TryGetRange(out int min, out int cur, out int max) || max <= min)
        {
            return "—";
        }
        int pct = (int)Math.Round((cur - min) * 100.0 / (max - min));
        return $"{Math.Clamp(pct, 0, 100)}%";
    }

    private void OnBrightnessChanged(object? sender, StatusSnapshot snapshot)
    {
        // 轮询器在后台线程广播；改 WPF 控件必须切回 UI 线程
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_brightnessValue is not null)
            {
                _brightnessValue.Text = FormatBrightness();
            }
        }));
    }

    // ============================================================
    //  正在播放卡片（SMTC）——实现见上方 BuildMediaCard（简略控制 + 入口）
    // ============================================================

    /// <summary>面板显隐变化：隐藏即停表（原因见 BuildMediaCard 里的注释）。</summary>
    private void OnVisibilityChangedForMediaTimer(object? sender, DependencyPropertyChangedEventArgs e)
    {
        if (_mediaTimer is null)
        {
            return;
        }

        if (IsVisible)
        {
            if (!_mediaTimer.IsEnabled)
            {
                _mediaTimer.Start();
            }
        }
        else
        {
            _mediaTimer.Stop();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_brightness is not null) _brightness.Changed -= OnBrightnessChanged;
        if (_vol is not null) _vol.Changed -= OnVolumeChanged;
        if (_mediaTimer is not null)
        {
            _mediaTimer.Stop();
            _mediaTimer = null;
        }
        _brightnessControl?.Dispose();
        _brightnessControl = null;
        _mediaButtons.Clear();
        base.OnClosed(e);
    }
}
