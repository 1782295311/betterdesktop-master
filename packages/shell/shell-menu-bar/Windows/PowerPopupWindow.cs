// BetterDesktop.Shell.MenuBar — 电池&性能模式独立弹出面板（零硬编码，真实系统数据）。
// UI 结构（按截图布局）：
//   - 顶部：电量百分比 + 详细状态（"100% 已接通电源 电量充满" / 剩余时长）
//   - 分隔线
//   - 性能模式列表（PowerEnumerator.EnumeratePlans → 系统真实方案名）
//     每行：图标 + 方案名 + 选中态（当前活动方案高亮）
//   - "电池偏好设置" 跳转（ms-settings:powersleep）

using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.MenuBar.Services;

namespace BetterDesktop.Shell.MenuBar.Windows;

/// <summary>电源&性能模式独立弹出面板。</summary>
internal sealed class PowerPopupWindow : MenuBarPopupWindow
{
    private const double DefaultWidth = 290;

    // 电池条几何：外框高度 14、描边 1、内腔可用高度 12
    private const double BatBodyHeight = 14;
    private const double BatInnerHeight = 12;

    /// <summary>预览模式（窗口从未 ShowAt）下的内容重建回调：切换性能模式后由外部宿主刷新已嵌入的面板引用。
    /// 真实窗口路径不依赖此回调（走 ChromeBorder 淡入淡出重建）。</summary>
    private readonly Action<FrameworkElement>? _previewRefresh;

    private Rectangle? _batFill;          // 电量填充条（按百分比充填，动画跟随）
    private SolidColorBrush? _batFillBrush;
    private TextBlock? _pctText;          // 主行：百分比 + 接通状态
    private TextBlock? _detailText;       // 次行：电量充满 / 剩余时长
    private TextBlock? _modeLabel;        // 当前性能模式标题行
    private TextBlock? _confirmText;      // 切换成功确认横幅
    private string? _pendingConfirm;      // 待显示的确认文案（内容重建后应用到横幅再淡出）
    private readonly DispatcherTimer _ticker;
    private readonly DispatcherTimer _confirmHide;

    public PowerPopupWindow(IVibrancyService vibrancy, IAppearanceService? appearance = null,
        Action<FrameworkElement>? previewRefresh = null)
        : base(vibrancy, appearance)
    {
        _previewRefresh = previewRefresh;

        Width = DefaultWidth;
        MinWidth = DefaultWidth;
        SizeToContent = SizeToContent.Height;

        // 面板可见期间周期刷新电量，实时反映真实电池状态
        _ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _ticker.Tick += (_, _) => RefreshBattery();
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) { RefreshBattery(); _ticker.Start(); }
            else _ticker.Stop();
        };

        // 确认横幅展示约 2 秒后淡出
        _confirmHide = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2000) };
        _confirmHide.Tick += (_, _) => DismissConfirm();
    }

    /// <summary>按最新电源数据刷新电量头：填充条动画 + 文字。面板已打开时由定时器驱动。</summary>
    private void RefreshBattery()
    {
        if (!IsVisible) return;
        var info = PowerEnumerator.ReadStatus();
        if (_pctText is not null)
        {
            _pctText.Text = info.HasBattery && info.Percentage >= 0
                ? $"{info.Percentage}%  {info.LineStatusText}"
                : info.LineStatusText;
        }
        if (_detailText is not null) _detailText.Text = info.StatusText;

        // 填充条随百分比动画充填，颜色随档位动画渐变（若本次读不到有效电量，回退到 0，避免动画卡在旧值）
        double pct = info.HasBattery && info.Percentage >= 0 ? info.Percentage : 0;
        AnimateBatteryFill(pct);
    }

    public FrameworkElement BuildPreviewContent() => BuildContent();

    protected override FrameworkElement BuildContent()
    {
        var root = new Border
        {
            // 根容器透明：面板背景/描边/圆角由基类 ApplyContent 按主题令牌统一挂载。
            Padding = new Thickness(6, 6, 6, 8),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };

        var column = new StackPanel { Orientation = Orientation.Vertical };

        // ========== 电量信息头 ==========
        var status = PowerEnumerator.ReadStatus();
        column.Children.Add(CreatePowerHeader(status));

        column.Children.Add(CreateSeparator());

        // ========== 当前性能模式（固定标题，切换后即时更新，避免用户误以为未切换） ==========
        var plans = PowerEnumerator.EnumeratePlans();
        var active = plans.FirstOrDefault(p => p.IsActive);
        column.Children.Add(CreateModeHeader(active?.DisplayName));

        if (plans.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = "未枚举到电源方案（可能需管理员权限读取 powrprof）",
                FontSize = 11,
                Margin = new Thickness(14, 6, 14, 8),
                TextWrapping = TextWrapping.Wrap
            };
            // 次要提示：次要前景走主题令牌
            SetThemeBinding(empty, TextBlock.ForegroundProperty, "ThemeMutedForeground");
            column.Children.Add(empty);
        }
        else
        {
            foreach (var plan in plans)
            {
                column.Children.Add(CreatePlanRow(plan, () =>
                {
                    // 若点击的本来就是当前模式，不给出"已切换"的误导反馈
                    if (plan.IsActive)
                    {
                        return;
                    }
                    // 仅切换成功时会触发（CreatePlanRow 在 SetActiveScheme 成功后才调用 onSwitched）
                    _pendingConfirm = plan.DisplayName;
                    AnimatePlanSwitch();
                }));
            }
        }

        // 切换成功确认横幅（默认隐藏；由 _pendingConfirm 填充后淡出）
        column.Children.Add(CreateConfirmBanner());

        column.Children.Add(CreateSeparator());

        column.Children.Add(CreatePrefLinkRow("电池偏好设置", "ms-settings:powersleep"));

        root.Child = column;
        return root;
    }

    // ------------------- UI 工厂 -------------------

    /// <summary>按最新电源/方案数据重建面板内容（切换后刷新选中态，并更新电量头引用）。</summary>
    private void RebuildContent()
    {
        ApplyContent(BuildContent());
    }

    /// <summary>性能模式切换后的平滑过渡：先淡出当前内容，重建后淡入，避免选中高亮"啪"地跳变。</summary>
    private void AnimatePlanSwitch()
    {
        if (ChromeBorder is null)
        {
            // 预览模式（窗口从未 ShowAt，ChromeBorder 未初始化）：不触碰隐藏 Window 的
            // Content/ChromeBorder（在从未显示的 Window 上重建会 NRE），直接把重建后的内容
            // 交回宿主刷新已嵌入的面板引用。
            _previewRefresh?.Invoke(BuildContent());
            return;
        }

        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        fadeOut.Completed += (_, _) =>
        {
            RebuildContent();
            if (ChromeBorder is not null)
            {
                var fadeIn = new DoubleAnimation(0.8, 1, TimeSpan.FromMilliseconds(200))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                ChromeBorder.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            }
        };
        ChromeBorder.BeginAnimation(UIElement.OpacityProperty, fadeOut);
    }

    private static Border CreateSeparator()
    {
        // 分隔线走主题令牌，随亮/暗/无色模式自动切换。
        var sep = new Border
        {
            Height = 1,
            Margin = new Thickness(8, 6, 8, 6)
        };
        SetThemeBinding(sep, Border.BackgroundProperty, "ThemeSeparator");
        return sep;
    }

    /// <summary>当前性能模式标题行：始终显示当前生效方案，切换后随重建即时更新。</summary>
    private FrameworkElement CreateModeHeader(string? activeName)
    {
        var text = new TextBlock
        {
            Text = string.IsNullOrEmpty(activeName) ? "性能模式" : $"当前性能模式：{activeName}",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(12, 4, 12, 4)
        };
        _modeLabel = text;
        return text;
    }

    /// <summary>切换成功确认横幅：平时不可见，切换后由 _pendingConfirm 填充再淡出。</summary>
    private FrameworkElement CreateConfirmBanner()
    {
        var text = new TextBlock
        {
            Text = _pendingConfirm is null ? "" : $"已切换至「{_pendingConfirm}」",
            Foreground = new SolidColorBrush(Color.FromArgb(235, 96, 206, 152)),
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            Margin = new Thickness(12, 1, 12, 1),
            Opacity = _pendingConfirm is null ? 0 : 0.95
        };
        _confirmText = text;
        if (_pendingConfirm is not null)
        {
            ScheduleConfirmDismiss();
        }
        return text;
    }

    /// <summary>在内容重建展示确认横幅后，延迟淡出它并清除待确认状态。</summary>
    private void ScheduleConfirmDismiss()
    {
        _confirmHide.Stop();
        _confirmHide.Start();
    }

    private void DismissConfirm()
    {
        _confirmHide.Stop();
        var target = _confirmText;
        _pendingConfirm = null;
        if (target is null || target.Opacity <= 0)
        {
            return;
        }
        var fade = new DoubleAnimation(target.Opacity, 0, TimeSpan.FromMilliseconds(400));
        fade.Completed += (_, _) =>
        {
            if (target.IsVisible) { target.Text = ""; }
        };
        target.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    /// <summary>按电量百分比动画充填电池条：高度 + 颜色均平滑过渡。</summary>
    private void AnimateBatteryFill(double percent)
    {
        if (_batFill is null) return;

        double clamp = Math.Clamp(percent, 0, 100);
        double targetHeight = BatInnerHeight * clamp / 100.0;

        var heightAnim = new DoubleAnimation(targetHeight, TimeSpan.FromMilliseconds(450))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        _batFill.BeginAnimation(Rectangle.HeightProperty, heightAnim);

        // 电量档位色：>=40 绿，>=15 橙，<15 红；无电池灰
        Color color = clamp >= 40 ? Color.FromArgb(255, 76, 230, 154)
            : clamp >= 15 ? Color.FromArgb(255, 247, 186, 58)
            : Color.FromArgb(255, 247, 90, 90);
        if (_batFillBrush is not null)
        {
            _batFillBrush.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(color, TimeSpan.FromMilliseconds(450)));
        }
    }

    private FrameworkElement CreatePowerHeader(PowerStatusInfo info)
    {
        var grid = new Grid
        {
            Margin = new Thickness(10, 4, 10, 4)
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // ===== 左侧：真实电池条（填充高度随电量，动画跟随） =====
        double pctValue = info.HasBattery && info.Percentage >= 0 ? info.Percentage : 0;
        double clamp = Math.Clamp(pctValue, 0, 100);
        Color batColor = clamp >= 40 ? Color.FromArgb(255, 76, 230, 154)
            : clamp >= 15 ? Color.FromArgb(255, 247, 186, 58)
            : Color.FromArgb(255, 247, 90, 90);
        if (!info.HasBattery) batColor = Color.FromArgb(255, 150, 150, 160);

        var batGrid = new Grid { Width = 30, Height = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        // 尾部凸起（正极小口）
        batGrid.Children.Add(new Rectangle
        {
            Width = 2.5,
            Height = 5,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            RadiusX = 0.5,
            RadiusY = 0.5,
            Fill = new SolidColorBrush(batColor),
            Margin = new Thickness(0, 0, 0, 0)
        });
        // 外壳（留 3px 给凸起）：底色深灰 + 彩色描边
        var batBody = new Border
        {
            Height = BatBodyHeight,
            Margin = new Thickness(0, 0, 3, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(Color.FromArgb(55, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(batColor),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(1, 1, 1, 1),
            VerticalAlignment = VerticalAlignment.Center
        };
        var fillBrush = new SolidColorBrush(batColor);
        var batFill = new Rectangle
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            RadiusX = 2,
            RadiusY = 2,
            Fill = fillBrush,
            Height = BatInnerHeight * clamp / 100.0, // 初始即按当前电量充填
            SnapsToDevicePixels = true
        };
        batBody.Child = batFill;
        batGrid.Children.Add(batBody);
        Grid.SetColumn(batGrid, 0); grid.Children.Add(batGrid);

        // ===== 右侧：百分比 + 状态文本 =====
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var pct = new TextBlock
        {
            Text = info.HasBattery && info.Percentage >= 0
                ? $"{info.Percentage}%  {info.LineStatusText}"
                : info.LineStatusText,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold
        };
        stack.Children.Add(pct);
        var detail = new TextBlock
        {
            Text = info.StatusText,
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 0)
        };
        // 状态次要行：次要前景走主题令牌
        SetThemeBinding(detail, TextBlock.ForegroundProperty, "ThemeMutedForeground");
        stack.Children.Add(detail);
        Grid.SetColumn(stack, 1); grid.Children.Add(stack);

        // 注册实例引用，供 RefreshBattery / AnimateBatteryFill 做实时刷新与动画
        _batFill = batFill;
        _batFillBrush = fillBrush;
        _pctText = pct;
        _detailText = detail;
        return grid;
    }

    private static FrameworkElement CreatePlanRow(PowerPlanItem plan, Action onSwitched)
    {
        var row = new Grid
        {
            Height = 40,
            Margin = new Thickness(8, 0, 8, 0)
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 图标块（激活=强调色背景，非激活=内容层背景，均走主题令牌）
        var icon = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(6),
            Child = new TextBlock
            {
                Text = plan.IconGlyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            },
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        SetThemeBinding(icon, Border.BackgroundProperty, plan.IsActive ? "AccentBrush" : "ThemeContentBackground");
        Grid.SetColumn(icon, 0); row.Children.Add(icon);

        var name = new TextBlock
        {
            Text = plan.DisplayName,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(name, 1); row.Children.Add(name);

        if (plan.IsActive)
        {
            var check = new TextBlock
            {
                Text = "\uE73E",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            // 选中勾：强调色走主题令牌
            SetThemeBinding(check, TextBlock.ForegroundProperty, "AccentBrush");
            Grid.SetColumn(check, 2); row.Children.Add(check);
        }

        // 整行背景高亮（选中项）
        var outer = new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = plan.IsActive
                ? new SolidColorBrush(Color.FromArgb(60, 90, 163, 255))
                : Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = row
        };

        // 点击切换性能模式：调用真实系统 API，成功后重建面板刷新选中态。
        outer.MouseLeftButtonUp += (_, _) =>
        {
            if (plan.SchemeGuid != Guid.Empty &&
                PowerEnumerator.SetActiveScheme(plan.SchemeGuid))
            {
                onSwitched?.Invoke();
            }
        };
        outer.MouseEnter += (_, _) =>
        {
            outer.Background = plan.IsActive
                ? new SolidColorBrush(Color.FromArgb(80, 90, 163, 255))
                : new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
        };
        outer.MouseLeave += (_, _) =>
        {
            outer.Background = plan.IsActive
                ? new SolidColorBrush(Color.FromArgb(60, 90, 163, 255))
                : Brushes.Transparent;
        };

        return outer;
    }

    private static FrameworkElement CreatePrefLinkRow(string label, string settingsUri)
    {
        var text = new TextBlock
        {
            Text = label,
            FontSize = 12,
            Margin = new Thickness(14, 2, 0, 2),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        // 操作/链接入口：强调色走主题令牌
        SetThemeBinding(text, TextBlock.ForegroundProperty, "AccentBrush");
        text.MouseLeftButtonUp += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(settingsUri) { UseShellExecute = true }); }
            catch { /* ignore */ }
        };
        return text;
    }
}
