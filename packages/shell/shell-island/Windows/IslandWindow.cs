// BetterDesktop.Shell.Island — 灵动岛表面窗口
//
// 【形态】独立无焦点浮窗，贴在菜单栏下沿、水平居中（岛文档 §6 的中置列方案在本计划里被替换成
//   独立窗口：菜单栏只有 20 DIP 高，中置列里的胶囊长不高、悬挂不了、也承载不了展开卡片）。
//
// 【三条硬约束的落地】
//   1) 不抢焦点：WS_EX_NOACTIVATE + WS_EX_TOOLWINDOW（ShellWindow.UseNoActivateWindowStyle），
//      并在 WM_MOUSEACTIVATE 返回 MA_NOACTIVATE 兜底。
//   2) 不挡点击：自建 WM_NCHITTEST —— 命中点不在当前岛轮廓（含 2 DIP 容差）内即返回 HTTRANSPARENT，
//      点击直接落到下层窗口。**不用**窗口级不透明背景，避免留下一条看不见的输入拦截带。
//   3) 空闲零重绘：唯一的 CompositionTarget.Rendering 订阅在全部弹簧都静止时自动退订（规格 §8）。
//      休眠胶囊是静止画面，所以"常驻可见"与"零重绘"并不冲突。
//   4) 存在感：菜单栏没有刘海，空闲时若完全不可见，用户根本不知道这个功能在哪——
//      因此无活动时保留一枚休眠矮胶囊（island.idle-visible 可关），只画形状、不参与命中。
//
// 【渲染分工】形状层（IslandSurfaceElement）自绘轮廓与字形；文字与按钮走 WPF 子元素，
//   从而自动继承全局字号缩放、主题前景传导与无障碍读出。

using System;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.Core.Native;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.Island.Rendering;
using BetterDesktop.Shell.Island.Services;
using Point = System.Windows.Point;

namespace BetterDesktop.Shell.Island.Windows;

/// <summary>岛表面窗口（自绘液态胶囊 + 弹簧动效 + 脉冲进度）。</summary>
internal sealed class IslandWindow : ShellWindow
{
    /// <summary>画布宽度（DIP）：只用于水平居中与空白判定，不参与命中（命中按轮廓算）。</summary>
    internal const double CanvasWidth = 560.0;

    /// <summary>画布高度（DIP）：菜单栏 20 + 展开卡片余量。</summary>
    internal const double CanvasHeight = 240.0;

    /// <summary>头部行高度（收起态胶囊高度）。</summary>
    private const double HeaderHeight = 26.0;

    /// <summary>命中容差（DIP）：轮廓描边是亚像素的，容差保证"看得见的边缘点得到"。</summary>
    private const double HitTolerance = 2.0;

    /// <summary>收起态宽度区间。</summary>
    private const double CollapsedMinWidth = 132.0;
    private const double CollapsedMaxWidth = 268.0;

    /// <summary>
    /// 休眠形态尺寸（无活动时常驻）：8 DIP 高是刻意的——① 内容层不透明度按"几何高度 20→26"推导，
    /// 8 DIP 远低于阈值，于是"只画形状、不画内容"由几何保证，不需要另一套渲染分支；
    /// ② 配合描边的融合渐变（顶部透明），它读起来是"菜单栏下沿的一枚小凸起"（向上收纳进菜单栏），
    /// 而不是一个悬在半空的独立胶囊——这是 2026-09-16 用户反馈的直接要求。
    /// </summary>
    private const double IdleWidth = 64.0;
    private const double IdleHeight = 8.0;

    /// <summary>展开态宽度区间。</summary>
    private const double ExpandedMinWidth = 244.0;
    private const double ExpandedMaxWidth = 384.0;

    /// <summary>菜单栏窗口标题（用于对齐它的下沿；找不到则回退 20 DIP + 主屏居中）。</summary>
    private const string MenuBarWindowTitle = "BetterDesktop.MenuBar";

    /// <summary>菜单栏高度兜底值（DIP，与 menu-bar 包的 MenuBarMetrics 一致）。</summary>
    private const double MenuBarHeightFallback = 20.0;

    private const int WmNcHitTest = 0x0084;
    private const int WmMouseActivate = 0x0021;
    private const int WmDisplayChange = 0x007E;
    private const int HtClient = 1;
    private const int HtTransparent = -1;
    private const int MaNoActivate = 3;

    private const double TitleFontSize = 12.5;
    private const double SubtitleFontSize = 11.0;
    private const double PercentFontSize = 10.5;

    private readonly IslandMotionController _motion;
    private readonly IslandSurfaceElement _surface = new();
    private readonly Canvas _contentRoot = new();
    private readonly Grid _contentHost;
    private readonly Grid _header;
    private readonly StackPanel _detail;
    private readonly TextBlock _title;
    private readonly TextBlock _subtitle;
    private readonly PulseRing _ring;
    private readonly TextBlock _percent;
    private readonly StackPanel _actions;
    private readonly DispatcherTimer _collapseTimer;
    private readonly IKernelLogger? _logger;

    private IslandOptions _options;
    private IslandContent? _content;
    private bool _idle;
    private IslandSize _collapsed = new(CollapsedMinWidth, HeaderHeight);
    private IslandSize _expanded = new(ExpandedMinWidth, HeaderHeight + 26.0);
    private double _attachY = MenuBarHeightFallback;
    private bool _clockOn;
    private bool _hovered;
    private long _lastTimestamp;
    private int _frameCount;
    private string _lastPercentText = string.Empty;
    private HwndSource? _source;
    private HwndSourceHook? _hook;

    public IslandWindow(
        IVibrancyService vibrancy,
        IAppearanceService? appearance,
        IEventBus? events,
        IslandOptions options,
        IKernelLogger? logger = null)
        : base(appearance, vibrancy)
    {
        _options = options;
        _logger = logger;
        _motion = new IslandMotionController(options.Tier);

        Title = "BetterDesktop.Island";
        Width = CanvasWidth;
        Height = CanvasHeight;
        // 空白区必须完全透明：命中与穿透由 WM_NCHITTEST + 轮廓判定负责（不靠窗口级背景占位）。
        Background = null;

        // ---- 头部行：字形占位（形状层自绘字形）+ 标题 + 进度环 + 百分比 ----
        _header = new Grid();
        _header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(37.0) });
        _header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
        _header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _title = new TextBlock
        {
            FontSize = TitleFontSize,
            FontWeight = FontWeights.Medium,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        SetThemeBinding(_title, TextBlock.ForegroundProperty, "ThemeForeground");
        Grid.SetColumn(_title, 1);

        _ring = new PulseRing
        {
            Width = 18,
            Height = 18,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            Visibility = Visibility.Collapsed,
        };
        Grid.SetColumn(_ring, 2);

        _percent = new TextBlock
        {
            FontSize = PercentFontSize,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
            MinWidth = 30,
            Visibility = Visibility.Collapsed,
        };
        // 等宽数字：百分比跳动时不抖（规格 §5）
        Typography.SetNumeralAlignment(_percent, FontNumeralAlignment.Tabular);
        SetThemeBinding(_percent, TextBlock.ForegroundProperty, "ThemeForeground");
        Grid.SetColumn(_percent, 3);

        _header.Children.Add(_title);
        _header.Children.Add(_ring);
        _header.Children.Add(_percent);
        Grid.SetRow(_header, 0);

        // ---- 细节区（展开才可见）：副标题 + 动作按钮 ----
        _subtitle = new TextBlock
        {
            FontSize = SubtitleFontSize,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            Opacity = 0.78,
            Visibility = Visibility.Collapsed,
        };
        SetThemeBinding(_subtitle, TextBlock.ForegroundProperty, "ThemeForeground");

        _actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 7, 0, 0),
            Visibility = Visibility.Collapsed,
        };

        _detail = new StackPanel
        {
            Margin = new Thickness(37, 0, 12, 0),
            Orientation = Orientation.Vertical,
            Opacity = 0,
        };
        _detail.Children.Add(_subtitle);
        _detail.Children.Add(_actions);
        Grid.SetRow(_detail, 1);

        _contentHost = new Grid { Visibility = Visibility.Collapsed, ClipToBounds = true };
        _contentHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(HeaderHeight) });
        _contentHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.0, GridUnitType.Star) });
        _contentHost.Children.Add(_header);
        _contentHost.Children.Add(_detail);

        _contentRoot.Width = CanvasWidth;
        _contentRoot.Height = CanvasHeight;
        _contentRoot.Background = null;
        _contentRoot.Children.Add(_contentHost);

        var root = new Grid { Background = null };
        root.Children.Add(_surface);
        root.Children.Add(_contentRoot);

        var chrome = new Border
        {
            Background = null,
            BorderThickness = new Thickness(0),
            Child = root,
        };
        ChromeBorder = chrome;
        Content = chrome;

        // 悬停展开 / 离开延迟收起（避免边缘抖动导致反复展开收起）
        _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320) };
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            _motion.SetExpanded(false);
            EnsureClock();
        };

        _surface.MouseEnter += (_, _) => OnHoverChanged(true);
        _surface.MouseLeave += (_, _) => OnHoverChanged(false);
        _contentHost.MouseEnter += (_, _) => OnHoverChanged(true);
        _contentHost.MouseLeave += (_, _) => OnHoverChanged(false);
        _contentHost.MouseLeftButtonUp += OnContentClicked;

        Events = events;
    }

    /// <summary>已渲染的动画帧数（诊断用：空闲期必须停止增长，对应 DoD D8）。</summary>
    internal int FrameCount => _frameCount;

    /// <summary>当前姿态（诊断/测试用）。</summary>
    internal IslandPose CurrentPose => _surface.Pose;

    /// <summary>应用选项（启停、档位、来源、位置微调）。</summary>
    public void ApplyOptions(IslandOptions options)
    {
        _options = options;
        _motion.Tier = options.Tier;
        if (options.Tier != IslandMotionTier.Full)
        {
            _ring.PulseEnabled = false;
        }
        else
        {
            _ring.PulseEnabled = true;
        }

        ResolveAnchor();

        // 总开关关闭，或用户关掉了休眠胶囊而岛此刻正处在休眠态 → 收出
        if (!options.Enabled || (!options.IdleVisible && _idle))
        {
            HideActivity();
        }
    }

    /// <summary>
    /// 休眠形态：没有活动时也让岛"在线"——一枚矮胶囊常驻菜单栏中置。
    /// <para>
    /// 【为什么需要它】菜单栏不像 iPhone 的刘海，没有"岛的位置"这个天然标记；空闲时若彻底不可见，
    /// 用户会以为功能不存在（2026-09-16 真机实证反馈）。休眠胶囊只画形状不画内容（见 <see cref="IdleHeight"/>），
    /// 且**不参与交互**（无活动时命中测试一律 HTTRANSPARENT），因此不会挡住菜单栏的点击。
    /// </para>
    /// </summary>
    public void ShowIdle()
    {
        _content = null;
        _idle = true;
        _hovered = false;
        _collapseTimer.Stop();
        ClearContentTexts();

        _motion.PulseActive = false;
        _motion.HasDetail = false;
        _motion.SetExpanded(false);

        // 目标尺寸 = 休眠尺寸：宽度高度由尺寸弹簧补间（收成休眠是"缩回去"而不是瞬间跳变）
        _collapsed = new IslandSize(IdleWidth, IdleHeight);
        _expanded = _collapsed;
        _motion.SetVisible(true);
        EnsureClock();
    }

    /// <summary>展示一条活动（内容变化即重算尺寸与命中区；同一活动重复调用是幂等的）。</summary>
    public void ShowActivity(IslandContent content)
    {
        if (content is null)
        {
            throw new ArgumentNullException(nameof(content));
        }

        var previous = _content;
        _content = content;
        _idle = false;

        _title.Text = content.Title;
        _subtitle.Text = content.Subtitle ?? string.Empty;
        _subtitle.Visibility = content.Subtitle is null ? Visibility.Collapsed : Visibility.Visible;

        var hasRing = content.Progress.HasValue || content.Indeterminate;
        _ring.Visibility = hasRing ? Visibility.Visible : Visibility.Collapsed;
        _ring.SetState(content.Progress ?? 0.0, content.Indeterminate, content.Failed);

        _percent.Visibility = content.Progress.HasValue && !content.Indeterminate
            ? Visibility.Visible
            : Visibility.Collapsed;
        _lastPercentText = string.Empty;

        RebuildActions(content.Actions);

        _collapsed = ComputeCollapsedSize(content);
        _expanded = ComputeExpandedSize(content, _collapsed);
        _motion.HasDetail = content.Subtitle is not null || content.Actions.Count > 0;
        _motion.PulseActive = hasRing;

        AutomationProperties.SetName(this, content.AutomationName); // 无障碍：屏幕阅读器可读出当前活动

        // 终态（失败）给一次呼吸脉冲，作为"单次抖动"的收敛版（规格 §5：不做循环闪烁）
        if (content.Failed && (previous is null || !previous.Failed))
        {
            _motion.Breathe();
        }

        _motion.SetVisible(true);
        EnsureClock();
    }

    /// <summary>步进呼吸：同源合并计数增加（"转换完成 ×3"）时给一次脉冲，让累计被看见。</summary>
    public void Breathe()
    {
        _motion.Breathe();
        EnsureClock();
    }

    /// <summary>收起岛（无活动且不显示休眠胶囊：收出动画结束后彻底静止）。</summary>
    public void HideActivity()
    {
        _content = null;
        _idle = false;
        _motion.PulseActive = false;
        _motion.SetVisible(false);
        EnsureClock();
    }

    /// <summary>清空内容层文字（休眠态复用同一套元素，必须擦干净，否则休眠胶囊里会留着上一条活动的文字）。</summary>
    private void ClearContentTexts()
    {
        _title.Text = string.Empty;
        _subtitle.Text = string.Empty;
        _subtitle.Visibility = Visibility.Collapsed;
        _ring.Visibility = Visibility.Collapsed;
        _percent.Visibility = Visibility.Collapsed;
        _lastPercentText = string.Empty;
        _actions.Children.Clear();
        _actions.Visibility = Visibility.Collapsed;
        AutomationProperties.SetName(this, "灵动岛");
    }

    /// <summary>重新解析菜单栏锚点并摆放窗口（分辨率/DPI/菜单栏位置变化后调用）。</summary>
    public void ResolveAnchor()
    {
        var dpi = GetDpiScale();
        var handle = NativeMethods.FindWindow(null, MenuBarWindowTitle);
        double barBottomDip;
        double centerDip;

        if (handle != IntPtr.Zero
            && NativeMethods.GetWindowRect(handle, out var rect)
            && rect.Right > rect.Left)
        {
            barBottomDip = rect.Bottom / dpi.Y;
            centerDip = (rect.Left + rect.Right) / 2.0 / dpi.X;
        }
        else
        {
            // 菜单栏未加载（或标题变了）：回退到约定高度 + 主屏居中，绝不阻塞岛自身工作。
            barBottomDip = MenuBarHeightFallback;
            centerDip = SystemParameters.PrimaryScreenWidth / 2.0;
        }

        _attachY = barBottomDip + _options.OffsetY;
        Left = centerDip + _options.OffsetX - (CanvasWidth / 2.0);
        Top = 0;
    }

    /// <inheritdoc />
    protected override bool DefaultTopmost => true;

    /// <inheritdoc />
    protected override bool DefaultShowActivated => false;

    /// <inheritdoc />
    protected override ResizeMode DefaultResizeMode => ResizeMode.NoResize;

    /// <inheritdoc />
    protected override bool UseSkinBackground => false;

    /// <inheritdoc />
    protected override bool UseNoActivateWindowStyle => true;

    /// <summary>
    /// 不做 DWM 材质：分层窗口下 DWM 背景本就被忽略（DwmHelper 既有结论），
    /// 而"透明浮层"最怕 DWM 在整窗矩形上画出一块背景——那会变成一条看得见的带子。
    /// 岛的玻璃感由自绘填充 + 菜单栏同源令牌提供（"融入"由同底色保证）。
    /// </summary>
    protected override bool UseWindowMaterial => false;

    /// <inheritdoc />
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(hwnd);
        // 钩子委托必须持成字段：AddHook/RemoveHook 传方法组会各生成一个委托实例，退订必然配不上。
        _hook = WndProc;
        _source?.AddHook(_hook);
        ResolveAnchor();
    }

    /// <inheritdoc />
    protected override void OnLoadedCore()
    {
        ResolveAnchor();
        ApplyThemeTokens();
    }

    /// <inheritdoc />
    protected override void OnAppearanceContentChanged(AppearanceChangedArgs e) => ApplyThemeTokens();

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        StopClock();
        _collapseTimer.Stop();
        if (_source is not null && _hook is not null)
        {
            _source.RemoveHook(_hook);
        }

        _hook = null;
        _source = null;
        base.OnClosed(e);
    }

    // ===================== 内部：帧时钟 =====================

    /// <summary>说明：两弹簧都静止且无脉冲时才停表——空闲期完全不重绘（规格 §8 零重绘口径）。</summary>
    private void EnsureClock()
    {
        if (_clockOn)
        {
            return;
        }

        _lastTimestamp = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += OnRendering;
        _clockOn = true;
    }

    private void StopClock()
    {
        if (!_clockOn)
        {
            return;
        }

        CompositionTarget.Rendering -= OnRendering;
        _clockOn = false;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        var dt = (now - _lastTimestamp) / (double)Stopwatch.Frequency;
        _lastTimestamp = now;
        _frameCount++;

        _motion.Advance(dt);

        var pose = _motion.ComputePose(_attachY, CanvasWidth, _collapsed, _expanded);
        // 无内容时显式"不画字形"：用 Dot 兜底会在收薄过程闪出一枚白点（见 IslandGlyph.None 注释）。
        _surface.SetPose(pose, _content?.Glyph ?? IslandGlyph.None);
        if (_ring.Visibility == Visibility.Visible)
        {
            _ring.Pulse = _motion.PulsePhase;
        }

        LayoutContent(pose);

        if (!_motion.IsAnimating)
        {
            StopClock();
            if (_logger is not null && _content is null)
            {
                // 动画结束：留一条可核对的"已静止"证据（此后不再有帧——休眠胶囊同样零重绘）。
                _logger.Info(_idle
                    ? $"shell.island: 岛已收为休眠形态并静止（累计 {_frameCount} 帧，此后零重绘）"
                    : $"shell.island: 岛已收出并静止（本次动画累计 {_frameCount} 帧，此后零重绘）");
            }
        }
    }

    private void LayoutContent(IslandPose pose)
    {
        if (!pose.HasShape)
        {
            _contentHost.Visibility = Visibility.Collapsed;
            return;
        }

        _contentHost.Visibility = Visibility.Visible;
        Canvas.SetLeft(_contentHost, pose.Left);
        Canvas.SetTop(_contentHost, pose.Top);
        _contentHost.Width = pose.Width;
        _contentHost.Height = pose.Height;
        _contentHost.Opacity = pose.ContentOpacity;
        _detail.Opacity = pose.DetailOpacity;

        var percentText = FormatPercent(_content);
        if (!string.Equals(percentText, _lastPercentText, StringComparison.Ordinal))
        {
            _lastPercentText = percentText;
            _percent.Text = percentText;
        }
    }

    private static string FormatPercent(IslandContent? content)
        => content?.Progress is { } progress
            ? ((int)Math.Round(progress * 100.0, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture) + "%"
            : string.Empty;

    // ===================== 内部：内容布局 =====================

    private void RebuildActions(System.Collections.Generic.IReadOnlyList<IslandAction> actions)
    {
        _actions.Children.Clear();
        foreach (var action in actions)
        {
            _actions.Children.Add(CreateChip(action.Label, action.Invoke));
        }

        _actions.Visibility = actions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private Border CreateChip(string label, Action onClick)
    {
        var text = new TextBlock { Text = label, FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center };
        SetThemeBinding(text, TextBlock.ForegroundProperty, "ThemeForeground");

        var idle = ThemeBrushes.Tint("ThemeForeground", 0.12);
        var hot = ThemeBrushes.AccentTint(0.30);
        var chip = new Border
        {
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(10, 3, 10, 4),
            Margin = new Thickness(0, 0, 6, 0),
            Background = idle,
            Child = text,
            Cursor = Cursors.Hand,
        };
        chip.MouseEnter += (_, _) => chip.Background = hot;
        chip.MouseLeave += (_, _) => chip.Background = idle;
        chip.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true; // 不冒泡成"点击切换展开态"
            onClick();
        };
        return chip;
    }

    private IslandSize ComputeCollapsedSize(IslandContent content)
    {
        var titleWidth = MeasureText(content.Title, TitleFontSize, FontWeights.Medium);
        var hasRing = content.Progress.HasValue || content.Indeterminate;
        var hasPercent = content.Progress.HasValue && !content.Indeterminate;

        var width = 37.0 + titleWidth + 12.0;
        if (hasRing)
        {
            width += 18.0 + 6.0;
        }

        if (hasPercent)
        {
            width += Math.Max(30.0, MeasureText("100%", PercentFontSize, FontWeights.Normal) + 2.0);
        }

        return new IslandSize(Math.Clamp(width, CollapsedMinWidth, CollapsedMaxWidth), HeaderHeight);
    }

    private IslandSize ComputeExpandedSize(IslandContent content, IslandSize collapsed)
    {
        var rows = 0.0;
        if (content.Subtitle is not null)
        {
            rows += 16.0;
        }

        if (content.Actions.Count > 0)
        {
            rows += 29.0;
        }

        var wanted = collapsed.Width + 32.0;
        if (content.Subtitle is not null)
        {
            wanted = Math.Max(wanted, 37.0 + MeasureText(content.Subtitle, SubtitleFontSize, FontWeights.Normal) + 24.0);
        }

        var width = Math.Clamp(wanted, ExpandedMinWidth, ExpandedMaxWidth);
        return new IslandSize(width, HeaderHeight + rows + 8.0);
    }

    private double MeasureText(string text, double fontSize, FontWeight weight)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0.0;
        }

        try
        {
            var typeface = new Typeface(FontFamily, FontStyles.Normal, weight, FontStretches.Normal);
            var formatted = new FormattedText(
                text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                fontSize,
                Brushes.White,
                GetDpiScale().PixelsPerDip);
            return formatted.Width;
        }
        catch
        {
            // 极端情况下（未连接视觉树）按字数估算，保证尺寸仍可计算而不是抛穿 UI 线程。
            return text.Length * fontSize * 0.62;
        }
    }

    private DpiScale GetDpiScale()
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            return new DpiScale(dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0, dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0);
        }
        catch
        {
            return new DpiScale(1.0, 1.0);
        }
    }

    private void ApplyThemeTokens()
    {
        var panelBrush = ThemeBrushes.Get("ThemePanelBackground", new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x29)));
        var fill = panelBrush is SolidColorBrush solid
            ? SolidColorBrushOf(Color.FromArgb(0xE6, solid.Color.R, solid.Color.G, solid.Color.B))
            : panelBrush;
        var stroke = ThemeBrushes.Get("CardBorderBrush", new SolidColorBrush(Color.FromArgb(0x4F, 0xFF, 0xFF, 0xFF)));
        var foreground = ThemeBrushes.Get("ThemeForeground", new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2)));
        _surface.SetPalette(fill, stroke, foreground);

        var accent = ThemeBrushes.TryGetColor("SkinAccentFromSkin") ?? Color.FromRgb(0x0A, 0x84, 0xFF);
        var track = ThemeBrushes.TryGetColor("ThemeForeground") is { } fg
            ? Color.FromArgb(0x2E, fg.R, fg.G, fg.B)
            : Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF);
        _ring.SetPalette(accent, track, ThemeBrushes.DangerColor);
    }

    private static SolidColorBrush SolidColorBrushOf(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    // ===================== 内部：交互 =====================

    private void OnHoverChanged(bool hovered)
    {
        if (_content is null || _hovered == hovered)
        {
            return;
        }

        _hovered = hovered;
        if (!_options.HoverExpand)
        {
            return;
        }

        if (hovered)
        {
            _collapseTimer.Stop();
            _motion.SetExpanded(true);
        }
        else
        {
            _collapseTimer.Stop();
            _collapseTimer.Start();
        }

        EnsureClock();
    }

    private void OnContentClicked(object sender, MouseButtonEventArgs e)
    {
        if (_content is null || !_motion.HasDetail)
        {
            return;
        }

        e.Handled = true;
        _collapseTimer.Stop();
        _motion.SetExpanded(!_motion.Expanded);
        EnsureClock();
    }

    // ===================== 内部：Win32 钩子 =====================

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WmNcHitTest:
                // 命中判据与绘制共用同一份轮廓（看不出"看得见却点不到"的破洞）。
                // 休眠胶囊是纯装饰：无活动时一律放行，别让一枚静止胶囊挡住菜单栏的点击。
                var screenPoint = new Point(
                    (short)(lParam.ToInt32() & 0xFFFF),
                    (short)((lParam.ToInt32() >> 16) & 0xFFFF));
                handled = true;
                return (IntPtr)(_content is not null && IsPointInIsland(PointFromScreen(screenPoint))
                    ? HtClient
                    : HtTransparent);

            case WmMouseActivate:
                handled = true;
                return (IntPtr)MaNoActivate;

            case WmDisplayChange:
                ResolveAnchor();
                break;

            default:
                break;
        }

        return IntPtr.Zero;
    }

    private bool IsPointInIsland(Point clientPoint)
    {
        var pose = _surface.Pose;
        if (!pose.HasShape)
        {
            return false;
        }

        if (_surface.HitTestIsland(clientPoint))
        {
            return true;
        }

        // 容差带：轮廓描边是亚像素的，边缘 2 DIP 也算岛内（否则边缘点击会被放行到下层）。
        return new Rect(
            pose.Left - HitTolerance,
            pose.Top - HitTolerance,
            pose.Width + (HitTolerance * 2.0),
            pose.Height + (HitTolerance * 2.0)).Contains(clientPoint);
    }

    private readonly record struct DpiScale(double X, double Y)
    {
        /// <summary>每 DIP 的像素数（FormattedText 需要）。</summary>
        public double PixelsPerDip => Y;
    }
}
