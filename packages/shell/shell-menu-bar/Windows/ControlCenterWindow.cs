// BetterDesktop.Shell.MenuBar — 控制中心独立弹出面板（macOS Ventura 视觉：开关网格 + 模块卡片）
// 布局（严格对齐截图）：
//   顶部：2 列 × 3 行 开关网格
//     左列(宽) ：Wi‑Fi（大）、蓝牙（大）、热点（大）
//     右列(窄) ：专注助手（小）、台前调度（小）、投影（小）
//   中部：显示器（圆角卡片 · 亮度滑块）
//        声音（圆角卡片 · 音量 + 麦克风按钮）
//   底部：媒体（圆角卡片 · 音乐 + 播放控制）
// 所有状态语义均来自 shell-status；未接入的模块显示空壳（不写死任何假设备名）。

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.Status.Contracts;
using BetterDesktop.Shell.Status.Native;

namespace BetterDesktop.Shell.MenuBar.Windows;

/// <summary>
/// 控制中心独立面板（macOS 风格：开关网格 + 模块化圆角卡片）。
/// 概览瓦片点击后唤起该功能"自己的独立完整面板"，控制中心本身只承载简略视图。
/// </summary>
internal sealed class ControlCenterWindow : MenuBarPopupWindow
{
    // 整体宽度（与 macOS 风格一致，略窄于 400 以便更精致）
    private const double PanelWidth = 340;

    // 开关网格列宽：左列大，右列小（面板宽 340 - 左右 padding 28 = 内容 312；左 200 + 间距 8 + 右 104 = 312，避免右列被截断缺角）
    private const double GridColLeft = 200;
    private const double GridColRight = 104;
    private const double GridGap = 8;
    private const double GridRowHeight = 58;

    private readonly IReadOnlyList<Services.ControlCenterFeature> _features;
    private readonly IVolumeMonitor? _vol;
    private readonly IMicrophoneMonitor? _mic;
    private readonly IBrightnessMonitor? _brightness;
    private BrightnessSliderControl? _brightnessControl;

    public ControlCenterWindow(
        IReadOnlyList<Services.ControlCenterFeature> features,
        IVolumeMonitor? vol,
        IMicrophoneMonitor? mic,
        IBrightnessMonitor? brightness,
        IVibrancyService vibrancy,
        IAppearanceService? appearance = null)
        : base(vibrancy, appearance)
    {
        _features = features; _vol = vol; _mic = mic; _brightness = brightness;
        Width = PanelWidth;
        MinWidth = PanelWidth;
        SizeToContent = SizeToContent.Height;
    }

    /// <summary>Playground/大容器 预览入口：直接取内容 UI（不走 ShellWindow 生命周期）。</summary>
    public FrameworkElement BuildPreviewContent() => BuildContent();

    protected override FrameworkElement BuildContent()
    {
        // —— 外层容器：透明，面板背景/描边/圆角由基类 ApplyContent 按主题令牌统一挂载 ——
        var root = new Border
        {
            Padding = new Thickness(14),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };

        var column = new StackPanel
        {
            Orientation = Orientation.Vertical
        };

        // 1) 顶部：开关网格
        column.Children.Add(BuildToggleGrid());

        // 2) 显示器卡片（亮度滑块）
        column.Children.Add(BuildModuleCard(BuildBrightnessSection(), margin: new Thickness(0, 10, 0, 0)));

        // 3) 声音卡片（音量 + 麦克风）
        column.Children.Add(BuildModuleCard(BuildVolumeMicSection(), margin: new Thickness(0, 10, 0, 0)));

        // 4) 媒体卡片
        column.Children.Add(BuildModuleCard(BuildMediaSection(), margin: new Thickness(0, 10, 0, 0)));

        root.Child = column;
        return root;
    }

    // ============================================================
    //  顶部开关网格（2 列 × 3 行）
    // ============================================================
    private FrameworkElement BuildToggleGrid()
    {
        var grid = new Grid
        {
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };
        // 两列：左(大) 间距 右(小)
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(GridColLeft) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(GridGap) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(GridColRight) });
        // 三行
        for (int i = 0; i < 3; i++)
        {
            if (i > 0)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(GridGap) });
            }
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(GridRowHeight) });
        }

        // —— 由 Catalog 按固定顺序取前 6 个：0..2 左列大，3..5 右列小 ——
        // [0 Wi-Fi, 1 蓝牙, 2 热点]  → 大尺寸；图标 + 两行文字
        // [3 专注助手, 4 台前调度, 5 投影] → 小尺寸；仅图标 + 标题行
        static int Row(int r) => r * 2;

        var wifi = FindFeature("Wi‑Fi");
        if (wifi is not null)
        {
            var tile = MakeLargeToggleTile(wifi, glyph: "\uE701", accentOn: true);  // WiFi 段
            Grid.SetColumn(tile, 0); Grid.SetRow(tile, Row(0)); grid.Children.Add(tile);
        }
        var focus = FindFeature("专注助手");
        if (focus is not null)
        {
            var tile = MakeSmallToggleTile(focus, glyph: "\uE7C2", accentOn: false);
            Grid.SetColumn(tile, 2); Grid.SetRow(tile, Row(0)); grid.Children.Add(tile);
        }

        var bt = FindFeature("蓝牙");
        if (bt is not null)
        {
            var tile = MakeLargeToggleTile(bt, glyph: "\uE702", accentOn: true);
            Grid.SetColumn(tile, 0); Grid.SetRow(tile, Row(1)); grid.Children.Add(tile);
        }
        var stage = FindFeature("台前调度");
        if (stage is not null)
        {
            var tile = MakeSmallToggleTile(stage, glyph: "\uE81E", accentOn: false);
            Grid.SetColumn(tile, 2); Grid.SetRow(tile, Row(1)); grid.Children.Add(tile);
        }

        var hotspot = FindFeature("热点");
        if (hotspot is not null)
        {
            var tile = MakeLargeToggleTile(hotspot, glyph: "\uEB0B", accentOn: false);
            Grid.SetColumn(tile, 0); Grid.SetRow(tile, Row(2)); grid.Children.Add(tile);
        }
        var proj = FindFeature("投影");
        if (proj is not null)
        {
            var tile = MakeSmallToggleTile(proj, glyph: "\uE7B4", accentOn: false);
            Grid.SetColumn(tile, 2); Grid.SetRow(tile, Row(2)); grid.Children.Add(tile);
        }

        return grid;
    }

    private Services.ControlCenterFeature? FindFeature(string title)
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

    /// <summary>左列大开关：左图标 + 标题 + 副标题，带激活态蓝色背景。</summary>
    private FrameworkElement MakeLargeToggleTile(
        Services.ControlCenterFeature feature,
        string glyph,
        bool accentOn)
    {
        var summary = feature.Summary() ?? string.Empty;
        bool active = accentOn && !string.IsNullOrEmpty(summary) && summary != "关闭" && summary != "已关闭" && summary != "无适配器" && summary != "—";

        var tile = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 10, 10, 10),
            BorderThickness = new Thickness(1),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        // 激活态：固定蓝色背景（Windows 强调色 #0078D4），确保已连接/已开启功能明显高亮
        // 非激活态：透明背景，仅靠描边显示功能边界，无黑色色块
        tile.Background = active
            ? new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4))
            : Brushes.Transparent;
        // 胶囊描边：激活态蓝色描边，非激活态用半透明白色描边确保边界可见
        tile.BorderBrush = active
            ? new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4))
            : new SolidColorBrush(Color.FromArgb(120, 255, 255, 255));

        var row = new Grid
        {
            VerticalAlignment = VerticalAlignment.Stretch
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 图标（蓝色激活态 白字；灰态 高亮白字）
        var iconCircle = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(14),
            Margin = new Thickness(0, 2, 10, 0),
            VerticalAlignment = VerticalAlignment.Top
        };
        // 非激活图标底：半透明白色圆底（与描边同色系），保证透明背景下图标可读
        if (!active)
        {
            iconCircle.Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
        }
        var iconText = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            SnapsToDevicePixels = true
        };
        // 激活态图标恒白字（强调色底上可读）；非激活随主题前景
        if (active)
        {
            iconText.Foreground = Brushes.White;
        }
        else
        {
            SetThemeBinding(iconText, TextBlock.ForegroundProperty, "ThemeForeground");
        }
        iconCircle.Child = iconText;
        Grid.SetColumn(iconCircle, 0);
        row.Children.Add(iconCircle);

        // 右侧标题 + 副标题
        var rightCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var titleText = new TextBlock
        {
            Text = feature.Title,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold
        };
        // 激活瓦片标题恒白字；非激活随主题前景
        if (active)
        {
            titleText.Foreground = Brushes.White;
        }
        rightCol.Children.Add(titleText);
        if (!string.IsNullOrEmpty(summary))
        {
            var summaryText = new TextBlock
            {
                Text = summary,
                FontSize = 11,
                Margin = new Thickness(0, 1, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Opacity = 0.9
            };
            // 激活瓦片副标题恒白字；非激活用次要前景
            SetThemeBinding(summaryText, TextBlock.ForegroundProperty, "ThemeMutedForeground");
            if (active)
            {
                summaryText.Foreground = Brushes.White;
            }
            rightCol.Children.Add(summaryText);
        }
        Grid.SetColumn(rightCol, 1);
        row.Children.Add(rightCol);

        tile.Child = row;
        tile.MouseLeftButtonUp += (_, _) =>
        {
            var anchor = tile.PointToScreen(new Point(0, 0));
            feature.OnActivate(anchor);
        };
        return tile;
    }

    /// <summary>右列小开关：图标居中 + 单行标题在下，激活态蓝底白字。</summary>
    private FrameworkElement MakeSmallToggleTile(
        Services.ControlCenterFeature feature,
        string glyph,
        bool accentOn)
    {
        var summary = feature.Summary() ?? string.Empty;
        bool active = accentOn && !string.IsNullOrEmpty(summary) && summary != "关闭" && summary != "已关闭";

        var tile = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(8, 8, 8, 8),
            BorderThickness = new Thickness(1),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        // 激活态：固定蓝色背景；非激活态：透明背景，仅靠描边显示边界
        tile.Background = active
            ? new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4))
            : Brushes.Transparent;
        // 胶囊描边：激活态蓝色，非激活态半透明白色确保边界可见
        tile.BorderBrush = active
            ? new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4))
            : new SolidColorBrush(Color.FromArgb(120, 255, 255, 255));
        var glyphText = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 15,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 2),
            TextAlignment = TextAlignment.Center
        };
        var titleText = new TextBlock
        {
            Text = feature.Title,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        // 激活瓦片文字恒白字；非激活随主题前景
        if (active)
        {
            glyphText.Foreground = Brushes.White;
            titleText.Foreground = Brushes.White;
        }
        else
        {
            SetThemeBinding(glyphText, TextBlock.ForegroundProperty, "ThemeForeground");
            SetThemeBinding(titleText, TextBlock.ForegroundProperty, "ThemeForeground");
        }
        tile.Child = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children = { glyphText, titleText }
        };

        tile.MouseLeftButtonUp += (_, _) =>
        {
            var anchor = tile.PointToScreen(new Point(0, 0));
            feature.OnActivate(anchor);
        };
        return tile;
    }

    // ============================================================
    //  模块卡片：统一圆角 + 半透明背景
    // ============================================================
    private static FrameworkElement BuildModuleCard(FrameworkElement content, Thickness margin)
    {
        var card = new Border
        {
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = margin,
            BorderThickness = new Thickness(1),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            Child = content
        };
        // 模块卡片：透明背景，仅靠描边显示模块边界，无黑色色块
        card.Background = Brushes.Transparent;
        // 胶囊描边：半透明白色确保模块边界可见
        card.BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255));
        return card;
    }

    private FrameworkElement BuildBrightnessSection()
    {
        var column = new StackPanel { Orientation = Orientation.Vertical };
        if (_brightness is not null)
        {
            // 不显示标题、不显示"显示设置"链接：卡片已在独立的"显示器"模块中
            _brightnessControl = new BrightnessSliderControl(_brightness, title: "显示器", showSettingsLink: false);
            column.Children.Add(_brightnessControl.Root);
        }
        else
        {
            column.Children.Add(new TextBlock
            {
                Text = "显示器",
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                Margin = new Thickness(0, 0, 0, 8)
            });
            var unavailable = new TextBlock
            {
                Text = "亮度调节不可用",
                FontSize = 11
            };
            // 次要提示：次要前景走主题令牌
            SetThemeBinding(unavailable, TextBlock.ForegroundProperty, "ThemeMutedForeground");
            column.Children.Add(unavailable);
        }
        return column;
    }

    private FrameworkElement BuildVolumeMicSection()
    {
        var column = new StackPanel();
        column.Children.Add(new TextBlock
        {
            Text = "声音",
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            Margin = new Thickness(0, 0, 0, 8)
        });

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 音量滑块：与声音面板主音量滑杆复用同一样式（两端半圆轨道 + 白色圆球拇指），支持拖动设置
        var volProgress = _vol is null ? -1 : Math.Clamp(_vol.GetSnapshot().Progress, 0, 100);
        var volSlider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = volProgress < 0 ? 50 : volProgress,
            SmallChange = 1,
            LargeChange = 10,
            VerticalAlignment = VerticalAlignment.Center,
            MinHeight = 24,
            Style = NativePanelStyles.CreateCircleThumbSliderStyle()
        };
        // 流畅滑杆：拖动过程中不调系统 API，拖动结束/点击跳转时才设置主音量
        NativePanelStyles.ConfigureSmoothSlider(volSlider, v =>
        {
            try
            {
                if (AudioCoreNative.IsAvailable)
                {
                    float vol = (float)(Math.Clamp(v, 0, 100) / 100.0);
                    AudioCoreNative.SetMasterVolume(vol);
                }
            }
            catch { /* 音频服务异常时静默忽略 */ }
        });
        Grid.SetColumn(volSlider, 0);
        row.Children.Add(volSlider);

        // 麦克风静音按钮：静音态为橙色填充，非静音为半透明灰色
        var micSnap = _mic?.GetSnapshot();
        var muted = micSnap is not null && micSnap.IconKey == "mic-muted";
        var micBtn = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(15),
            Background = muted
                ? new SolidColorBrush(Color.FromRgb(255, 149, 0))
                : new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)),
            Margin = new Thickness(10, 0, 0, 0),
            Child = new TextBlock
            {
                Text = "\uE720",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                Foreground = muted ? Brushes.White : new SolidColorBrush(Color.FromArgb(255, 50, 50, 52)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            }
        };
        Grid.SetColumn(micBtn, 1);
        row.Children.Add(micBtn);

        column.Children.Add(row);
        return column;
    }

    private static FrameworkElement BuildRoundedProgressTrack(double progressPercent)
    {
        var progress = Math.Clamp(progressPercent, 0, 100);
        var trackGrid = new Grid { Height = 18, UseLayoutRounding = true, SnapsToDevicePixels = true };
        var pCol = (double.IsNaN(progress) || progress < 0) ? 0 : progress;
        trackGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(pCol, GridUnitType.Star) });
        trackGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0, 100 - pCol), GridUnitType.Star) });

        var fillCell = new Border
        {
            CornerRadius = new CornerRadius(9, 0, 0, 9)
        };
        // 进度填充色：主题前景（浅主题深条、深主题浅条），保证面板内可读
        SetThemeBinding(fillCell, Border.BackgroundProperty, "ThemeForeground");
        Grid.SetColumn(fillCell, 0);
        trackGrid.Children.Add(fillCell);
        trackGrid.Background = new SolidColorBrush(Color.FromArgb(130, 120, 120, 128));

        var wrap = new Border
        {
            CornerRadius = new CornerRadius(9),
            Height = 18,
            Clip = new RectangleGeometry(new Rect(0, 0, 10000, 18)) { RadiusX = 9, RadiusY = 9 },
            Child = trackGrid
        };
        return wrap;
    }

    private static FrameworkElement BuildMediaSection()
    {
        var row = new Grid { VerticalAlignment = VerticalAlignment.Center };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = "\uEC4F",
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 18,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 10, 0)
                },
                new TextBlock
                {
                    // 歌名不写死；后续接入 SMTC 时替换为真实 Title/Artist
                    Text = " ",
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center
                }
            }
        };
        Grid.SetColumn(left, 0);
        row.Children.Add(left);

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(MakeMediaGlyphButton("\uE100")); // 上一首 (Prev)
        right.Children.Add(MakeMediaGlyphButton("\uE102")); // 播放/暂停
        right.Children.Add(MakeMediaGlyphButton("\uE101")); // 下一首 (Next)
        Grid.SetColumn(right, 1);
        row.Children.Add(right);

        return row;
    }

    private static FrameworkElement MakeMediaGlyphButton(string glyph)
    {
        var b = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(14),
            Background = Brushes.Transparent,
            Margin = new Thickness(4, 0, 0, 0),
            Child = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            }
        };
        return b;
    }

    protected override void OnClosed(EventArgs e)
    {
        _brightnessControl?.Dispose();
        _brightnessControl = null;
        base.OnClosed(e);
    }
}
