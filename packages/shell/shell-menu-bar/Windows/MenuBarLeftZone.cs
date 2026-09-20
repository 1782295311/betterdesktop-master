// BetterDesktop.Shell.MenuBar — 菜单栏左区
//
// 【本轮改造】
//   - 原「程序菜单」入口（◈，打开开始菜单）替换为 **Logo 按钮**：
//     点击打开"快捷功能菜单"（LogoMenuWindow：关于/设置/系统入口/电源会话），
//     不再是开始菜单。开始菜单仍可由 shell-start-menu 自己的入口触达。
//   - Logo 三态动画（控件级，遵守本仓库动画纪律——不碰窗口级 RenderTransform）：
//     常态空心圆环(0°, 圆角=半宽) → 过渡菱形(45° 处自然呈现) → 展开正方形(90°, 小圆角)。
//     实现 = 描边 Border 的 RotateTransform 角度 + CornerRadius 双路插值，
//     0°→90° 旋转过程中 45° 处即菱形，一段动画天然覆盖三态，无需关键帧。
//   - 面板失焦收起（LogoMenuWindow.Hidden）时反向动画回圆环。
//   - 位置 / 下载 / 文档 导航入口保留不变。

using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.Core.Contracts;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.Desktop.Windows;
using BetterDesktop.Shell.MenuBar.Contracts;
using BetterDesktop.Shell.MenuBar.Services;
using BetterDesktop.Shell.Settings.Contracts;
using BetterDesktop.Shell.WindowTracker.Contracts;

namespace BetterDesktop.Shell.MenuBar.Windows;

/// <summary>菜单栏左区：Logo 快捷功能菜单按钮（三态动画）+ 前台窗口标题 + 常用位置（位置/下载/文档）。
/// （原文件夹工具条已移除，见构造函数末尾说明。）</summary>
internal sealed class MenuBarLeftZone : StackPanel, IDisposable
{
    /// <summary>左区按钮高度（与右区 MenuBarStatusStrip.CreateButton 一致，保持两区对齐）。</summary>
    private const double ButtonHeight = 18.0;

    /// <summary>Logo 图标边长（正方形描边）。</summary>
    private const double LogoSize = 16.0;

    /// <summary>动画时长（毫秒）：圆环 ↔ 方形形变 + 旋转。</summary>
    private const int MorphMilliseconds = 220;

    private readonly IVibrancyService _vibrancy;
    private readonly IAppearanceService? _appearance;
    private readonly ISettingsWindowService? _settingsWindow;
    private readonly IWindowTrackerService? _windowTracker;
    private readonly ISettingsService? _settings;
    private readonly IEventBus? _events;
    private readonly IMenuBarExtensionRegistry _registry;
    private TextBlock? _foregroundTitle;

    private LogoMenuWindow? _logoMenu;
    private Border? _logoButton;
    private bool _expanded;
    private bool _disposed;
    private StacksPopupWindow? _stacksPopup;

    /// <summary>Resolve logo menu provider from registry (C3: no direct new).</summary>
    private LogoMenuWindow? ResolveLogoMenu() => (_registry.Get("logo-menu") as LogoMenuBarExtension)?.GetOrCreate();

    /// <summary>Resolve stacks popup provider from registry (C3: no direct new).</summary>
    private StacksPopupWindow? ResolveStacksPopup() => (_registry.Get("stacks-popup") as StacksPopupBarExtension)?.GetOrCreate(); // 位置/下载/文档共用一个全宽条带面板（懒创建）

    public MenuBarLeftZone(
        IVibrancyService vibrancy,
        IAppearanceService? appearance,
        ISettingsWindowService? settingsWindow,
        IMenuBarExtensionRegistry registry,
        IWindowTrackerService? windowTracker = null,
        ISettingsService? settings = null,
        IEventBus? events = null)
    {
        _vibrancy = vibrancy;
        _appearance = appearance;
        _settingsWindow = settingsWindow;
        _registry = registry;
        _windowTracker = windowTracker;
        _settings = settings;
        _events = events;

        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;

        // 1) Logo 按钮：三态动画图标 → 快捷功能菜单
        Children.Add(CreateLogoButton());

        // 2) 前台（置顶）窗口标题：Logo 右侧实时显示当前前台窗口（WinEvent 事件驱动，已在 UI 线程回抛）
        if (_windowTracker is not null)
        {
            _windowTracker.ForegroundWindowChanged += OnForegroundChanged;
            _foregroundTitle = new TextBlock
            {
                Foreground = MenuBarTheme.Foreground,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
                MaxWidth = 260,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Children.Add(_foregroundTitle);
        }

        // 3) 常用位置：位置 / 下载 / 文档 —— 三入口共用**同一个全宽条带面板**（cairoshell Stacks 形态）：
        //    宽度=所在显示器工作区全宽、贴菜单栏正下方、横向单排卡片（32px 图标+80px 名称）。
        //    「位置」→ known folders 固定项条带，点击在同一条带内进入对应文件夹（← 可回退）；
        //    「下载/文档」→ 直接展开该文件夹内容条带。界面完全对齐。
        AddPlacesListButton("位置", "位置导航（桌面/下载/文档/图片/音乐/视频）");
        AddStacksButton("下载", "下载文件夹", ResolveDownloads(), GetKnownDir("Downloads"));
        AddStacksButton("文档", "文档文件夹", SafeGetFolderPath(Environment.SpecialFolder.Personal), SafeGetFolderPath(Environment.SpecialFolder.Personal));

        // 【2026-09-17 用户拍板】原 4)「文件夹工具条」（折叠钮 + 刷新 + 剪切/复制/粘贴/重命名/删除）整条移除：
        //   这些操作在**自绘桌面右键菜单**里已经有了（图标菜单 = 剪切/复制/粘贴/重命名/删除/属性；
        //   空白菜单 = 刷新/粘贴/新建/排序），工具条属重复入口；且桌面不再站内导航后路径/导航行也是死 UI。
    }

    private void OnForegroundChanged(object? sender, IntPtr hwnd)
    {
        // WinEventHook 已捕获 UI SynchronizationContext 回抛（shell-window-tracker README），
        // 但为稳妥仍走 Dispatcher 兜底。
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed || _foregroundTitle is null) return;
            var title = _windowTracker?.GetWindowTitle(hwnd) ?? string.Empty;
            _foregroundTitle.Text = title;
            _foregroundTitle.ToolTip = string.IsNullOrEmpty(title) ? null : title;
        }));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_windowTracker is not null)
        {
            _windowTracker.ForegroundWindowChanged -= OnForegroundChanged;
        }
    }

    // ======== Logo 按钮（三态动画） ========

    /// <summary>
    /// Logo 图标：描边正方形 Border。
    /// 常态 = 圆角半宽（视觉为空心圆环）；展开 = 旋转 90° + 圆角收紧（视觉为正方形）；
    /// 45° 处旋转中的方形即菱形——一段双路插值覆盖三态。
    /// </summary>
    private Border CreateLogoButton()
    {
        _logoButton = new Border
        {
            Width = LogoSize,
            Height = LogoSize,
            CornerRadius = new CornerRadius(LogoSize / 2), // 常态：圆环
            BorderThickness = new Thickness(1.4),
            BorderBrush = MenuBarTheme.Foreground,
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            ToolTip = "快捷功能",
            Cursor = System.Windows.Input.Cursors.Hand,
            // 绕中心旋转：默认 RenderTransformOrigin 是 (0,0)（左上角），
            // 不设这个旋转后图标会整体跑偏（菱形/方形不在原位）。
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform(0),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };
        _logoButton.MouseLeftButtonUp += (_, _) => ToggleLogoMenu();
        return _logoButton;
    }

    /// <summary>切换快捷功能菜单：展开（动画→方形）并弹面板；收起交给面板 Hidden 回调。</summary>
    private void ToggleLogoMenu()
    {
        if (_expanded)
        {
            // 点击已展开的图标：收起面板（IsVisibleChanged → Hidden → 回圆环动画）
            _logoMenu?.Hide();
            return;
        }

        PlayLogoMorph(expanded: true);
        _logoMenu ??= ResolveLogoMenu();
        if (_logoMenu is null)
        {
            return; // M10: registry miss -> silent
        }
        _logoMenu.Hidden -= OnLogoMenuHidden;
        _logoMenu.Hidden += OnLogoMenuHidden;

        var pos = PopupAnchor.Compute(_logoButton!, LogoSize, new Size(220, 430), MenuBarMetrics.MenuBarHeight);
        _logoMenu.ShowAt(pos);
    }

    private void OnLogoMenuHidden()
    {
        if (_expanded)
        {
            PlayLogoMorph(expanded: false);
        }
    }

    /// <summary>播放 Logo 形变动画：圆环(0°, 6.5) ↔ 方形(90°, 2.5)，45° 处即菱形过渡态。</summary>
    private void PlayLogoMorph(bool expanded)
    {
        _expanded = expanded;
        if (_logoButton is null) return;

        var duration = new Duration(TimeSpan.FromMilliseconds(MorphMilliseconds));
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseInOut };

        var angle = new DoubleAnimation
        {
            To = expanded ? 90 : 0,
            Duration = duration,
            EasingFunction = ease
        };
        var radius = new CornerRadiusAnimation
        {
            To = new CornerRadius(expanded ? 2.5 : LogoSize / 2),
            Duration = duration
        };

        Storyboard.SetTarget(angle, _logoButton);
        Storyboard.SetTargetProperty(angle, new PropertyPath("RenderTransform.Angle"));
        Storyboard.SetTarget(radius, _logoButton);
        Storyboard.SetTargetProperty(radius, new PropertyPath(Border.CornerRadiusProperty));

        var storyboard = new Storyboard();
        storyboard.Children.Add(angle);
        storyboard.Children.Add(radius);
        storyboard.Begin(_logoButton);
    }

    // ======== 常用位置 ========

    /// <summary>「位置」按钮：点击弹出 known folders 固定项条带（与下载/文档同一全宽条带面板）。</summary>
    private void AddPlacesListButton(string label, string tooltip)
    {
        Border? anchor = null;
        var btn = CreateTextButton(label, tooltip, () =>
        {
            _stacksPopup ??= ResolveStacksPopup();
            if (_stacksPopup is null)
            {
                return; // M10: registry miss -> silent
            }
            if (_stacksPopup is null)
            {
                return; // M10: registry miss -> silent
            }
            _stacksPopup.OpenPlaces(anchor!.PointToScreen(new Point(0, 0)));
        });
        anchor = btn;
        Children.Add(btn);
    }

    /// <summary>
    /// 添加 Stacks 式文件夹入口（下载/文档）。
    /// realPath 非空 → 点击弹出**全宽条带**（宽度=工作区全宽、贴菜单栏正下方、横向单排文件卡片，
    /// cairoshell Stacks 范式，不再开居中独立窗口）；否则 fallbackTarget（shell: 协议等）交 explorer 打开。
    /// </summary>
    private void AddStacksButton(string label, string tooltip, string? fallbackTarget, string? realPath)
    {
        var hasReal = !string.IsNullOrEmpty(realPath) && Directory.Exists(realPath);
        if (string.IsNullOrEmpty(fallbackTarget) && !hasReal)
        {
            // 目录解析失败（权限/特殊环境）：不添加入口，避免点了抛异常
            return;
        }

        Border? anchor = null;
        var btn = CreateTextButton(label, tooltip, () =>
        {
            if (hasReal)
            {
                ShowStacksPopup(anchor!, realPath!);
                return;
            }
            if (!string.IsNullOrEmpty(fallbackTarget))
            {
                OpenInExplorer(fallbackTarget);
            }
        });
        anchor = btn;
        Children.Add(btn);
    }

    /// <summary>懒创建并打开全宽条带面板（锚点取按钮所在显示器工作区）。</summary>
    private void ShowStacksPopup(FrameworkElement anchor, string path)
    {
        _stacksPopup ??= ResolveStacksPopup();
        if (_stacksPopup is null)
        {
            return; // M10: registry miss -> silent
        }
        var physical = anchor.PointToScreen(new Point(0, 0));
        _stacksPopup.Open(physical, path);
    }

    /// <summary>已知用户目录的完整路径（存在才返回；Downloads 无 SpecialFolder 项，按惯例拼接）。</summary>
    private static string? GetKnownDir(string name)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), name);
            return Directory.Exists(dir) ? dir : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>用资源管理器打开目标（目录路径或 shell: 协议）。失败静默（M10 降级：菜单栏不弹错误框）。</summary>
    private static void OpenInExplorer(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", target) { UseShellExecute = true });
        }
        catch
        {
            // 目标不存在/被策略拦截：静默忽略，不影响菜单栏其余部分
        }
    }

    private static string? ResolveDownloads()
    {
        // .NET 的 Environment.SpecialFolder 没有 Downloads 项，但用户目录在中文 Windows 上已被本地化，
        // 直接拼 "Downloads" 会失效。因此优先用 shell: 协议交给 explorer 解析，零本地化问题。
        return "shell:Downloads";
    }

    private static string? SafeGetFolderPath(Environment.SpecialFolder folder)
    {
        try
        {
            var path = Environment.GetFolderPath(folder);
            return string.IsNullOrEmpty(path) || !Directory.Exists(path) ? null : path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>文字按钮（位置/下载/文档）：菜单栏仅 16px 高，字号取 9 并压缩内边距。</summary>
    private static Border CreateTextButton(string label, string tooltip, Action onClick)
    {
        var text = new TextBlock
        {
            Text = label,
            Foreground = MenuBarTheme.Foreground,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        return Build(double.NaN, tooltip, text, onClick);
    }

    private static Border Build(double width, string tooltip, TextBlock content, Action onClick)
    {
        var text = content;
        if (double.IsNaN(width))
        {
            // 文字按钮按内容自适应宽度，左右各留 6px 呼吸
            text.Margin = new Thickness(6, 0, 6, 0);
        }

        var btn = new Border
        {
            Width = width,
            Height = ButtonHeight,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = text,
            ToolTip = tooltip,
            SnapsToDevicePixels = true,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        MenuBarTheme.AttachHoverFeedback(btn);
        btn.MouseLeftButtonUp += (_, _) =>
        {
            btn.Background = MenuBarTheme.Hover;
            onClick();
        };
        return btn;
    }
}

/// <summary>
/// CornerRadius 插值动画。WPF 未内置 CornerRadius 的动画类（属性可动画但无官方 AnimationTimeline），
/// 自定义 From/To 线性插值；From/To 用 DependencyProperty 保证 Storyboard Begin 时克隆不丢值。
/// Easing 由调用侧的 clock 进度天然线性——220ms 短动画下足够顺滑。
/// </summary>
internal sealed class CornerRadiusAnimation : AnimationTimeline
{
    public static readonly DependencyProperty FromProperty = DependencyProperty.Register(
        nameof(From), typeof(CornerRadius), typeof(CornerRadiusAnimation));

    public static readonly DependencyProperty ToProperty = DependencyProperty.Register(
        nameof(To), typeof(CornerRadius), typeof(CornerRadiusAnimation));

    public CornerRadius From
    {
        get => (CornerRadius)GetValue(FromProperty);
        set => SetValue(FromProperty, value);
    }

    public CornerRadius To
    {
        get => (CornerRadius)GetValue(ToProperty);
        set => SetValue(ToProperty, value);
    }

    public override Type TargetPropertyType => typeof(CornerRadius);

    protected override Freezable CreateInstanceCore() => new CornerRadiusAnimation();

    public override object GetCurrentValue(object defaultOriginValue, object defaultDestinationValue, AnimationClock clock)
    {
        var p = clock.CurrentProgress ?? 1.0;
        var from = From != default ? From : (CornerRadius)defaultOriginValue;
        var to = To != default ? To : (CornerRadius)defaultDestinationValue;
        return new CornerRadius(
            from.TopLeft + (to.TopLeft - from.TopLeft) * p,
            from.TopRight + (to.TopRight - from.TopRight) * p,
            from.BottomRight + (to.BottomRight - from.BottomRight) * p,
            from.BottomLeft + (to.BottomLeft - from.BottomLeft) * p);
    }
}
