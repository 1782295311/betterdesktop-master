using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.Search.Contracts;
using BetterDesktop.Shell.StartMenu.Services;
using BetterDesktop.Shell.StartMenu.Windows.Layouts;

namespace BetterDesktop.Shell.StartMenu.Windows;

/// <summary>
/// 自绘开始菜单窗口（单例 Show/Hide，由 StartMenuService 管理）。
/// 继承 ShellWindow（毛玻璃/主题描边由基类统一驱动）；Deactivated 关闭（不用低级鼠标钩子）；
/// 内容由活动布局（classic 经典两栏 / allapps 所有应用）构建；搜索防抖 250ms → 布局渲染结果；
/// Esc 关闭；定位主屏工作区左下角。
/// 布局/数据刷新经 service.LayoutChanged / RequestRefresh 触发 RebuildContent。
/// </summary>
internal sealed class StartMenuWindow : ShellWindow
{
    protected override Thickness ChromeMargin => new(0);
    // 规格长尾功能「拖拽调整窗口边框」：开始菜单边缘/四角可拖拽拉伸。
    // 基类 ShellWindow 已内置 WM_NCHITTEST 自建命中区（ResizeBorderThickness=6），
    // 此处放开 NoResize 即可获得原生拉伸；手动拉伸由下方 hook 与贴底定位互斥。
    protected override ResizeMode DefaultResizeMode => ResizeMode.CanResize;

    private readonly StartMenuService _service;
    private readonly IKernelLogger _logger;
    private readonly DispatcherTimer _searchDebounce;
    private CancellationTokenSource? _searchCts;
    private IStartMenuLayoutHost? _host;

    /// <summary>是否正处于系统手动 resize 会话（WM_ENTERSIZEMOVE～WM_EXITSIZEMOVE）。期间暂停贴底重定位，结束后回钳工作区。</summary>
    private bool _inManualResize;
    /// <summary>本次 resize 结束后是否需要重新贴底回钳（拖拽中窗口可能越过边界）。</summary>
    private bool _needsReclampAfterResize;

    public StartMenuWindow(
        StartMenuService service,
        IVibrancyService vibrancy,
        IAppearanceService? appearance,
        IKernelLogger logger)
    {
        _service = service;
        _logger = logger;
        VibrancyService = vibrancy;
        // 全局外观服务（主题圆角/描边/字号）：交给基类统一驱动。
        AppearanceService = appearance;

        Width = 420;
        Height = Math.Min(620, SystemParameters.WorkArea.Height - 16);
        MinWidth = 340;
        MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = false;
        Topmost = true;

        // 手动 resize 会话 hook（与基类 WM_NCHITTEST 命中区配合）：
        // 进入/退出 resize 时切换 _inManualResize，避免拖拽中贴底重定位与用户手势打架，结束后回钳工作区。
        SourceInitialized += (_, _) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero && System.Windows.Interop.HwndSource.FromHwnd(hwnd) is { } src)
            {
                src.AddHook(OnResizeTrackingHook);
            }
        };

        // 打开动画（startmenu.menu-animation：none / fade / slide）。
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
            {
                return;
            }

            // 单例窗口每次 Show 都重新贴底：隐藏期间切换布局改高尺寸后，重新显示也必须按新高度定位，
            // 否则沿用上次的 Top 会把更高布局的底部挤出屏幕。
            PositionBottomLeft();

            var animation = _service.GetMenuAnimation();
            if (animation == "none")
            {
                Opacity = 1;
                RenderTransform = Transform.Identity;
                return;
            }

            Opacity = 0;
            if (animation == "slide")
            {
                RenderTransform = new TranslateTransform(0, 14);
            }

            var duration = TimeSpan.FromMilliseconds(140);
            var fade = new System.Windows.Media.Animation.DoubleAnimation(0, 1, duration)
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };
            BeginAnimation(OpacityProperty, fade);
            if (RenderTransform is TranslateTransform translate)
            {
                var slide = new System.Windows.Media.Animation.DoubleAnimation(14, 0, duration)
                {
                    EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                };
                translate.BeginAnimation(TranslateTransform.YProperty, slide);
            }
        };

        // 布局切换 / 数据刷新（卸载后）→ 重建内容。
        _service.LayoutChanged += OnServiceLayoutChanged;
        _service.RequestRefresh += OnServiceRefresh;

        Deactivated += (_, _) =>
        {
            // 手动拉伸会话中不因 Deactivated 关闭（系统 resize 可能短暂触发失焦），
            // 否则一拖边框菜单就收起。
            if (IsVisible && !_inManualResize)
            {
                _service.Hide();
            }
        };
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += (_, _) => PositionBottomLeft();

        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            _ = RunSearchAsync();
        };

        // 布局切换会改变窗口尺寸（Win7↔Win10 高度不同）；尺寸变化时重新贴底定位，
        // 避免沿用旧布局的 Top 导致更高布局向下生长、底部被挤出工作区。
        // 手动 resize 会话中除外——贴底定位会与用户手势打架，交由 hook 结束后回钳。
        SizeChanged += (_, _) =>
        {
            if (IsVisible && !_inManualResize)
            {
                PositionBottomLeft();
            }
        };

        RebuildContent();
    }

    protected override void OnLoadedCore()
    {
    }

    private void OnServiceLayoutChanged() => RebuildContent();

    private void OnServiceRefresh() => RebuildContent();

    /// <summary>
    /// 按当前活动布局重建内容（布局切换 / 卸载刷新时调用）；旧控件随 GC 回收。
    /// </summary>
    private void RebuildContent()
    {
        _searchCts?.Cancel();
        _searchDebounce.Stop();

        // 尺寸按布局规格（CLASSIC_LAYOUTS.md）区分：
        //  - Win11：640×720 固定；Win10：640×640（三栏）；
        //  - Win7：两栏紧凑柜台 400×~520，宽跟随 startmenu.width 设置。
        var layout = _service.ActiveLayout;
        ApplyLayoutSize(layout.Name);
        Content = layout.BuildLayout(_service);
        _host = layout as IStartMenuLayoutHost;

        // 根 Border 接入基类外观令牌：圆角 8 / 1.5px 描边随主题。
        if (Content is Border root)
        {
            SetThemeBinding(root, Border.BorderBrushProperty, "CardBorderBrush");
            SetThemeBinding(root, Border.BackgroundProperty, "SkinBackgroundBrush");
            ChromeBorder = root;
        }

        // 菜单内文本统一绑主题前景色（不硬编码颜色）。
        if (Content is DependencyObject contentRoot)
        {
            BindThemeForeground(contentRoot);
        }

        if (_host is not null)
        {
            _host.SearchBox.TextChanged += (_, _) => ScheduleSearch();
        }

        if (IsVisible)
        {
            PositionBottomLeft();
        }
    }

    /// <summary>
    /// 按布局名称应用规格尺寸（CLASSIC_LAYOUTS.md，96 DPI 基准）：
    ///  - win11：640×720 固定；win10：640×640（三栏：rail+列表+磁贴）；
    ///  - win7 及其它：两栏紧凑柜台，宽随 startmenu.width，高 ~520。
    /// 均限制不超过主工作区（小屏回退）。
    /// </summary>
    private void ApplyLayoutSize(string name)
    {
        var wa = SystemParameters.WorkArea;
        switch (name)
        {
            case "win11":
                Width = Math.Min(640, wa.Width);
                Height = Math.Min(720, wa.Height);
                break;
            case "win10":
                Width = Math.Min(640, wa.Width);
                Height = Math.Min(640, wa.Height);
                break;
            default:
                var configuredWidth = _service.GetMenuWidth();
                Width = Math.Clamp(configuredWidth, 400, Math.Min(700, wa.Width));
                Height = Math.Min(520, wa.Height);
                break;
        }
    }

    /// <summary>
    /// 定位到鼠标光标所在监视器的工作区左下角（多显示器 / 跨 DPI 感知）。
    /// 工作区以物理像素返回，需按窗口当前 DPI 缩放到 WPF 设备无关单位（DIP）。
    /// 失败时回退主工作区；高度用预置 Height（首开时 ActualHeight 可能为 0，避免错位）。
    /// </summary>
    private void PositionBottomLeft()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var fallbackX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        var fallbackY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;

        try
        {
            // 取光标所在监视器的物理工作区 + 该监视器自身的有效 DPI（而非窗口的滞后 DPI），
            // 二者必须同源折算 —— 否则跨 DPI 监测器会被压得过低、底部探出屏幕。
            var monitor = Native.MonitorInterop.GetMonitorInfoUnderCursor();
            if (monitor is not null)
            {
                var scaleX = monitor.Value.DpiX > 0 ? monitor.Value.DpiX / 96.0 : fallbackX;
                var scaleY = monitor.Value.DpiY > 0 ? monitor.Value.DpiY / 96.0 : fallbackY;
                var a = monitor.Value.Area;
                PlaceInArea(a.Left / scaleX, a.Top / scaleY, a.Right / scaleX, a.Bottom / scaleY);
                return;
            }
        }
        catch
        {
            // 定位失败回退主工作区（M10）。
        }

        var wa = SystemParameters.WorkArea;
        PlaceInArea(wa.Left, wa.Top, wa.Right, wa.Bottom);
    }

    /// <summary>
    /// 把窗口贴到指定工作区左下角，并整体收紧在工作区内（防止跨 DPI/残留几何导致底部或右侧越界）。
    /// 尺寸优先用目标 Height（布局规格），但绝不超出工作区高度。
    /// </summary>
    private void PlaceInArea(double areaLeft, double areaTop, double areaRight, double areaBottom)
    {
        var availableH = Math.Max(0, areaBottom - areaTop);
        var height = Math.Min(Math.Max(ActualHeight, Height), availableH);

        var top = areaBottom - height - _service.GetVerticalOffset();
        if (top < areaTop)
        {
            top = areaTop;
        }

        if (top + height > areaBottom)
        {
            height = areaBottom - top;
            if (height < MinHeight)
            {
                height = MinHeight;
            }
        }

        var left = areaLeft + 8;
        if (left + Width > areaRight)
        {
            left = areaRight - Width;
        }

        if (left < areaLeft)
        {
            left = areaLeft;
        }

        Top = top;
        Left = left;
        if (height > 0)
        {
            Height = height;
        }
    }

    // ===== 手动 resize 会话跟踪 =====

    /// <summary>
    /// 跟踪系统 resize 会话（与基类 WM_NCHITTEST 命中区配套）：
    /// WM_ENTERSIZEMOVE 置位 _inManualResize（暂停贴底/失焦关闭），WM_EXITSIZEMOVE 复位并在
    /// KIgnoreLevel 缩放的 SizeChanged 排空后再回钳工作区，保证手动拉伸后窗口仍完整落在屏幕内。
    /// </summary>
    private IntPtr OnResizeTrackingHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_ENTERSIZEMOVE = 0x0231;
        const int WM_EXITSIZEMOVE = 0x0232;
        switch (msg)
        {
            case WM_ENTERSIZEMOVE:
                _inManualResize = true;
                _needsReclampAfterResize = true;
                handled = true;
                break;
            case WM_EXITSIZEMOVE:
                _inManualResize = false;
                if (_needsReclampAfterResize && IsVisible)
                {
                    _needsReclampAfterResize = false;
                    // 回钳一次（窗口整体收入工作区，底部贴回）。
                    PositionBottomLeft();
                }

                handled = true;
                break;
        }

        return IntPtr.Zero;
    }

    // ===== 搜索 =====

    private void ScheduleSearch()
    {
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private async System.Threading.Tasks.Task RunSearchAsync()
    {
        if (_host is null)
        {
            return;
        }

        var query = _host.SearchBox.Text;
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        try
        {
            var results = await _service.SearchAsync(query, _searchCts.Token);
            if (query != _host.SearchBox.Text)
            {
                return; // 过期结果丢弃
            }

            _host.RenderResults(query, results);
        }
        catch (OperationCanceledException)
        {
            // 取消是预期的
        }
        catch (Exception ex)
        {
            _logger.Error($"[StartMenu] 搜索失败：{ex.Message}");
        }
    }

    // ===== 全局键盘 =====

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _service.Hide();
            e.Handled = true;
        }
    }

    /// <summary>
    /// 递归把菜单内 TextBlock 前景绑定到主题令牌（不硬编码颜色）。
    /// 仅对「未显式设置前景」的 TextBlock 兜底绑定，避免覆盖布局内已用
    /// StartMenuPalette 精细设置的 Foreground/Muted/Accent 三层颜色层次。
    /// </summary>
    private void BindThemeForeground(DependencyObject root)
    {
        // 显式赋值（含代码赋值、样式 Setter 之外）时 ReadLocalValue 返回非 UnsetValue，
        // 表示布局已用调色板指定了前景，应保留其层次不被统一主题色覆盖。
        if (root is TextBlock textBlock
            && textBlock.ReadLocalValue(TextBlock.ForegroundProperty) == DependencyProperty.UnsetValue)
        {
            SetThemeBinding(textBlock, TextBlock.ForegroundProperty, "ThemeForeground");
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            BindThemeForeground(VisualTreeHelper.GetChild(root, i));
        }
    }
}
