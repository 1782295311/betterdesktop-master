using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using BetterDesktop.Shell.AppSource.Contracts;
using BetterDesktop.Shell.AppSource.Services;
using BetterDesktop.Shell.Core.Animation;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.Dock.Native;
using BetterDesktop.Shell.Dock.Models;
using BetterDesktop.Shell.Dock.Services;
using BetterDesktop.Shell.Dock.Windows;
using BetterDesktop.Shell.Settings.Contracts;
using BetterDesktop.Shell.WindowTracker;
using BetterDesktop.Shell.WindowTracker.Native;
using BetterDesktop.Shell.WindowTracker.Thumbnail;
using ShellLinkResolver = BetterDesktop.Shell.AppSource.Services.ShellLinkResolver;
using Path = System.IO.Path;

namespace BetterDesktop.Shell.Dock;

/// <summary>
/// Dock 窗口：左右分区（固定应用 / 运行中应用）。
/// 继承 ShellWindow 统一基类，窗口属性与毛玻璃由基类集中管理。
/// 支持：固定项右键菜单、文件拖放固定、订阅固定列表变更即时刷新、自动隐藏。
/// </summary>
public partial class DockWindow : ShellWindow
{
    // Dock 浮动浮条：当前阶段暂停描边/阴影尝试，根 Border 直接铺满窗口（不内缩），修复"向内缩"问题。
    // 此前 ChromeMargin=12,12,12,17 让根 Border 在固定窗口内缩进一圈、露出透明边，观感异常。
    // 抽成 public static 便于测试直接断言（无需实例化 DockWindow 的沉重依赖）。
    public static readonly Thickness DockChromeMargin = new(0);
    protected override Thickness ChromeMargin => DockChromeMargin;
    // dock 悬浮胶囊：尺寸由图标数量/停靠位置决定（SizeToContent 驱动），resize 会破坏布局，钉死不可缩放。
    protected override ResizeMode DefaultResizeMode => ResizeMode.NoResize;

    // 平时不置顶：dock 显示在窗口下层，不盖在窗口上；
    // 鼠标贴底部热区或悬停 dock 时经 SetDockVisible(topmost:true) 临时置顶。
    protected override bool DefaultTopmost => false;

    private readonly IDockAppsService _dockAppsService;
    private readonly IDockIconService _dockIconService;
    private readonly IAppIconService _iconProvider;
    private readonly IAnimationService _animation;
    private readonly IDockService _dockService;
    private readonly IDockLayoutService _layout;
    private readonly DockPlugin? _dockPlugin;
    private readonly ISettingsService? _settings;
    private readonly DockVisualSettings? _visual;

    private readonly List<(DockItemData Item, Ellipse Dot)> _pinnedDots = new();
    private readonly DispatcherTimer _refreshTimer = new()
    {
        Interval = TimeSpan.FromSeconds(1)
    };
    // 太阳同步倒影：每 60 秒检查一次倾斜角变化（余弦轨迹 30° 范围跨 12h，60s 变化约 5°，平滑渐变）。
    private readonly DispatcherTimer _sunSyncTimer = new()
    {
        Interval = TimeSpan.FromSeconds(60)
    };
    private double _lastSunSkew;
    private readonly DispatcherTimer _autoHideTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(60)
    };
    private readonly List<string> _lastRunningKeys = new();
    // 空状态占位是否已渲染：与去重键集合(_lastRunningKeys)解耦，避免双职责导致状态机脆弱。
    private bool _runningEmptyRendered;
    private ThumbnailWindow? _flyout;

    // 左右分区一屏可视容量基准（仅用于计算 ScrollViewer 可视宽度，不截断集合）。
    // 语义：内容少于此容量时宽=内容宽（贴合不留空位）；超出则固定此容量宽 + 滚动。
    private const int PinnedScreenCapacity = 6;
    private const int RunningScreenCapacity = 6;

    // 倒影/面板重建防抖：拖动滑块(OnVisualSettingsChanged 高频触发)时合并重建，
    // 避免 BeginInvoke 排队堆积 + 反复创建 RenderTargetBitmap 耗尽渲染线程内存（OOM 崩溃）。
    private bool _settingsRebuildPending;
    private System.Windows.Threading.DispatcherTimer? _visualDebounce; // 视觉设置 250ms 防抖（拖滑块高频 Set 的合并点）

    // 自动隐藏状态：初始可见，经过首次亮相保留期后再启用空闲隐藏判定。
    // _topmostBoosted：是否因鼠标悬停/贴边热区而临时置顶（平时非置顶，不盖在窗口上）。
    private bool _isDockVisible = true;
    private bool _autoHideEnabled;
    private bool _topmostBoosted;

    public DockWindow(
        IVibrancyService vibrancy,
        IAnimationService animation,
        IDockService dockService,
        IDockAppsService dockAppsService,
        IDockIconService dockIconService,
        IDockLayoutService dockLayoutService,
        DockPlugin? dockPlugin = null,
        IAppIconService? iconProvider = null,
        ISettingsService? settings = null,
        IAppearanceService? appearance = null,
        DockVisualSettings? visual = null)
    {
        VibrancyService = vibrancy;
        // 全局外观服务（主题圆角/描边/字号）：交给基类统一驱动，Dock 窗口与设置窗口观感一致。
        AppearanceService = appearance;
        _animation = animation;
        _dockService = dockService;
        _dockAppsService = dockAppsService;
        _dockIconService = dockIconService;
        _iconProvider = iconProvider ?? NullIconService.Instance;
        _layout = dockLayoutService;
        _dockPlugin = dockPlugin;
        _settings = settings;
        _visual = visual;

        // 注意：不在 Window 根级设置 RenderTransform 做滑动动画。
        // 分层透明窗口 + DWM 圆角下做 RenderTransform 位移动画会错位、累积偏移，
        // 且与窗口 Top/Left 定位冲突（"主程序被动画修坏"的根因）。
        // 显隐改用 Opacity 淡入淡出（见 SetDockVisible），安全且不干扰布局。

        InitializeComponent();

        // 根 Border 接入基类外观令牌：描边由主题页统一驱动（取代下方 XAML 写死的 BorderBrush）。
        ChromeBorder = RootBorder;

        // 支持将文件/快捷方式拖放到 Dock 上以固定
        AllowDrop = true;
        DragOver += OnDragOver;
        Drop += OnDrop;

        SizeChanged += OnSizeChanged;
        PinnedScrollViewer.ScrollChanged += (_, _) => UpdateEdgeFadeFor(PinnedScrollViewer);
        RunningScrollViewer.ScrollChanged += (_, _) => UpdateEdgeFadeFor(RunningScrollViewer);
        _refreshTimer.Tick += (_, _) => RefreshAll();
        _sunSyncTimer.Tick += OnSunSyncTick;
        _sunSyncTimer.Start();

        // 首次亮相保留期后启用自动隐藏（与 dock 规格一致：保留约 1800ms）。
        var dwell = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
        dwell.Tick += (_, _) =>
        {
            _autoHideEnabled = true;
            dwell.Stop();
        };
        dwell.Start();
        _autoHideTimer.Tick += OnAutoHideTick;
        _autoHideTimer.Start();

        // 订阅 dock 视觉配置变更：图标大小/间距/名称/倒影/材质/底距变化需重建面板或重新定位。
        if (_visual is not null)
        {
            _visual.Changed += OnVisualSettingsChanged;
            Closed += (_, _) =>
            {
                if (_visual is not null) _visual.Changed -= OnVisualSettingsChanged;
            };
        }

        // 订阅设置变更：系统功能图标（此电脑/网络/回收站/控制面板）的显示开关实时生效。
        if (_settings is not null)
        {
            _settings.Changed += OnSettingsChangedForSystemPanel;
            Closed += (_, _) =>
            {
                if (_settings is not null) _settings.Changed -= OnSettingsChangedForSystemPanel;
            };
        }
    }

    /// <summary>系统功能开关变化：重建系统功能区（合并到消息循环，避免后台线程直碰可视树）。</summary>
    private void OnSettingsChangedForSystemPanel(object? sender, SettingsChangedEventArgs e)
    {
        var key = e.Key;
        if (key is null || !key.StartsWith("dock.system", StringComparison.Ordinal))
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (!IsLoaded)
            {
                return;
            }

            RebuildSystemPanel();
            // 重建后尺寸可能变化：重新定位到底部居中 + 更新边缘渐隐。
            Dispatcher.BeginInvoke(() =>
            {
                SyncAppBarPosition();
                UpdateEdgeFade();
            });
        });
    }

    /// <summary>dock 视觉配置变更：重建面板（图标大小/间距/名称/倒影）+ 重新定位（底距/材质）。
    /// 经 Dispatcher 回到 UI 线程执行，避免后台设置线程直接碰可视树。
    /// ⚠️ 双层防抖：
    ///   1) 时间防抖（250ms 合并）：滑块拖动时 Set 每 tick 一次 → Changed 每 tick 一次，
    ///      若每次都全量重建（pinned/start/running/system 四面板 + 反射 + 定位）会卡顿乃至卡死。
    ///      先合并到"拖动停止后 250ms 重建一次"，期间只重置计时器。
    ///   2) pending 标志：防抖到期后与其它来源的重建请求合并到同一消息循环。</summary>
    private void OnVisualSettingsChanged(object? sender, EventArgs e)
    {
        if (_visualDebounce is null)
        {
            _visualDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _visualDebounce.Tick += (_, _) =>
            {
                _visualDebounce!.Stop();
                DoVisualSettingsRebuild();
            };
        }

        // 拖动中不断重置计时器：只有停顿超过 250ms 才真正重建
        _visualDebounce.Stop();
        _visualDebounce.Start();
    }

    private void DoVisualSettingsRebuild()
    {
        if (_settingsRebuildPending)
        {
            return;
        }

        _settingsRebuildPending = true;
        Dispatcher.BeginInvoke(() =>
        {
            _settingsRebuildPending = false;
            // 视觉参数变化：清空运行区去重键，强制 RefreshRunningPanel 下次重建。
            // 否则调图标大小/间距/名称/倒影时运行项集合(keys)未变,去重提前 return,
            // 右侧图标永远不跟随设置（「图标控制只在左侧生效」根因）。
            _lastRunningKeys.Clear();
            RebuildPinnedPanel();
            RebuildStartPanel();
            RefreshRunningPanel();
            // 系统功能区（此电脑/网络/回收站/控制面板）的图标大小/间距/标签同样跟随视觉设置。
            RebuildSystemPanel();
            ApplyDockMaterial();
            if (_visual is not null) _layout.BottomMargin = _visual.BottomMargin;
            SyncAppBarPosition();
        });
    }

    /// <summary>Dock 窗口材质：覆盖基类，按 dock 独立配置 dock.material 决定毛玻璃。
    /// blur=透亮模糊（BlurBehind，默认），clear=无磨砂清晰态（VibrancyStyle.None）。与全局外观
    /// Material 解耦，使 dock 可单独设清晰或模糊，不影响其他窗口。
    /// 注意：blur 档不能用 VibrancyStyle.Acrylic——系统亚克力(DWMSBT_TRANSIENTWINDOW)自带暗色调
    /// 偏暗，用户实测 dock 显得发灰；BlurBehind 只做高斯模糊不叠色调，与全局默认窗口一致、透亮。</summary>
    protected override void ApplyWindowMaterial()
    {
        if (VibrancyService is null) return;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        // clear=无磨砂清晰态（VibrancyStyle.None → DwmHelper.Disable 关闭 DWM 模糊）；
        // 其他（含 blur）= VibrancyStyle.Transparent → BlurBehind 透亮毛玻璃（ACCENT_ENABLE_BLURBEHIND）。
        var style = _visual?.DockMaterial is "clear" ? VibrancyStyle.None : VibrancyStyle.Transparent;
        VibrancyService.Apply(hwnd, style, roundCorners: true);
    }

    /// <summary>供配置变更时重新应用 dock 材质（重写 ApplyWindowMaterial 后，基类 MaterialChanged 路径不再覆盖 dock）。</summary>
    private void ApplyDockMaterial() => ApplyWindowMaterial();

    protected override void OnLoadedCore()
    {
        DebugLog.Trace("Dock", "OnLoadedCore 开始");
        try
        {
        // 初始：恢复用户固定列表。
        // 注意：不自动用开始菜单填充——左区固定应用仅由用户显式固定（应用管理中心 / 拖放），
        // 避免出现"不知道从哪里来"的应用。
        _dockAppsService.Load();
        DebugLog.Trace("Dock", $"OnLoadedCore Load 完成 pinned={_dockAppsService.Pinned.Count}");

        // 订阅固定列表变更，来自应用管理中心/拖拽的改动在此即时重建
        _dockAppsService.PinnedChanged += OnPinnedChanged;
        Closed += (_, _) => _dockAppsService.PinnedChanged -= OnPinnedChanged;

        RebuildPinnedPanel();
        DebugLog.Trace("Dock", "OnLoadedCore RebuildPinnedPanel 完成");

        // Dock 左端「开始」图标：自绘旗标 + 倒影 + 名称标签，随开始菜单视觉配置重建。
        RebuildStartPanel();

        RefreshAll();
        DebugLog.Trace("Dock", "OnLoadedCore RefreshAll 完成");

        // 系统功能区：此电脑/网络/回收站/控制面板（按设置显隐）。
        RebuildSystemPanel();

        // 根 Border 背景：有激活皮肤时透出皮肤图，无皮肤时改为透明，
        // 让 DWM 毛玻璃(Acrylic/BlurBehind)主导视觉，避免叠灰 tint 显得不透明。
        SyncRootBackground();
        if (AppearanceService is not null)
            AppearanceService.Changed += OnAppearanceChangedForBackground;

        _refreshTimer.Start();

        // 布局完成后：定位(SizeToContent 下 Actual 尺寸才就绪) + 渐隐蒙层
        // （此时 ActualWidth / ExtentWidth 才有效）
        Dispatcher.BeginInvoke(() =>
        {
            DebugLog.Trace("Dock", "布局定位 BeginInvoke 进入");
            try
            {
                SyncAppBarPosition();
                DebugLog.Trace("Dock", "布局定位 PositionToBottomCenter 完成");
                UpdateEdgeFade();
                DebugLog.Trace("Dock", "布局定位 UpdateEdgeFade 完成");
            }
            catch (Exception ex)
            {
                DebugLog.Trace("Dock", $"布局定位异常：{ex.GetType().Name}: {ex.Message}");
                throw;
            }
            DebugLog.Trace("Dock", "OnLoadedCore 布局定位完成");
        });
        DebugLog.Trace("Dock", "OnLoadedCore 完成");
        }
        catch (Exception ex)
        {
            DebugLog.Trace("Dock", $"OnLoadedCore 异常：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>根 Border 背景策略：有激活皮肤(图片/配色)时绑 SkinBackgroundBrush 透出皮肤；
    /// 无皮肤时改为透明，让 DWM 毛玻璃主导，避免无皮肤态叠灰 tint 显得灰不透明。</summary>
    private void SyncRootBackground()
    {
        if (RootBorder is null) return;
        if (AppearanceService is not null && !string.IsNullOrEmpty(AppearanceService.SkinActive))
            RootBorder.SetResourceReference(Border.BackgroundProperty, "SkinBackgroundBrush");
        else
            RootBorder.Background = Brushes.Transparent;
    }

    private void OnAppearanceChangedForBackground(object? sender, AppearanceChangedArgs e)
    {
        if (e.SkinChanged) SyncRootBackground();
    }

    /// <summary>从 App 资源读取主题令牌画刷（代码生成控件跟随外观模式）。缺失时回退白色。</summary>
    private static Brush ThemeBrush(string key)
        => Application.Current?.TryFindResource(key) as Brush ?? Brushes.White;

    /// <summary>把主题令牌以 DynamicResource 绑定到元素属性（与设置窗口右侧一致：模式切换自动更新，不依赖重建/事件链）。</summary>
    private static void BindTheme(FrameworkElement target, DependencyProperty prop, string key)
        => target.SetResourceReference(prop, key);

    /// <summary>外观变化：文字已 DynamicResource 绑定（模式切换自动更新）；根 ChromeBorder 的高光/阴影由基类统一处理。
    /// 图标项不再外包描边+阴影卡片，故描边/阴影档位变化无需在此重建图标面板。</summary>
    protected override void OnAppearanceContentChanged(AppearanceChangedArgs e)
    {
    }

    /// <summary>
    /// 自动隐藏（空闲阈值制）：
    ///   - 用户有任何输入（鼠标/键盘，GetLastInputInfo 系统级空闲）→ **绝不隐藏**，常驻显示；
    ///   - 鼠标悬停 dock 上 / 贴底部热区 → 置顶显示（用到时才升到窗口之上）；
    ///   - 无输入 ≥ <see cref="DockVisualSettings.IdleHideMinutes"/>（默认 20 分钟）→ 自动隐藏；
    ///   - 前台全屏（视频/游戏）仍无条件隐藏。
    /// </summary>
    private void OnAutoHideTick(object? sender, EventArgs e)
    {
        try
        {
            if (_layout.ShouldHideOnFullscreen())
            {
                SetDockVisible(false, topmost: false);
                return;
            }

            if (!_autoHideEnabled)
            {
                return;
            }

            var cursor = GetCursorScreenPoint();

            // 悬停 dock 上：绝不隐藏且置顶可交互
            if (IsCursorOverDock(cursor))
            {
                SetDockVisible(true, topmost: true);
                return;
            }

            // 贴底部热区：唤出（置顶）
            if (_layout.ShouldShowOnEdgeHover(cursor))
            {
                SetDockVisible(true, topmost: true);
                return;
            }

            // 系统级空闲判定：GetLastInputInfo 覆盖鼠标移动/点击/键盘，任何输入即"操作中"
            var idleMinutes = GetSystemIdleMs() / 60000.0;
            var threshold = _visual?.IdleHideMinutes ?? 20d;

            if (idleMinutes >= threshold)
            {
                SetDockVisible(false, topmost: false);
                return;
            }

            // 用户活跃 → 常驻显示，但**不置顶**（在窗口下层，不盖在窗口上；
            // 需要时鼠标贴底边热区或悬停 dock 即临时置顶）
            SetDockVisible(true, topmost: false);
        }
        catch
        {
            // 自动隐藏轮询失败不阻断主流程。
        }
    }

    private void SetDockVisible(bool visible, bool topmost = false)
    {
        // 健壮状态机：以窗口实际 IsVisible 为准（字段可能因启动时序/外部 Hide 失同步）。
        var actuallyVisible = IsVisible;
        if (visible == actuallyVisible && topmost == Topmost)
        {
            return;
        }

        _isDockVisible = visible;
        _topmostBoosted = topmost;
        if (visible)
        {
            DebugLog.Trace("Dock", $"SetDockVisible -> Show (topmost={topmost})");
            // 先定层级再显示：置顶态（热区/悬停唤出）与常驻态（窗口下层）分别正确落位。
            Topmost = topmost;
            Show();
            EnsureAppBar();
            SyncAppBarPosition();
            // 淡入（Opacity 0 -> 1），不触碰窗口定位。
            _animation.CreateFadeInAnimation(this, TimeSpan.FromMilliseconds(220))?.Begin();
        }
        else
        {
            RefreshAll();
            // 淡出（Opacity 1 -> 0），动画结束再隐藏，避免瞬间消失且不影响 Top/Left。
            var fade = _animation.CreateFadeOutAnimation(this, TimeSpan.FromMilliseconds(220));
            if (fade is not null)
            {
                fade.Completed += (_, _) => { Hide(); ReleaseAppBar(); };
                fade.Begin();
            }
            else
            {
                Hide();
                ReleaseAppBar();
            }
        }
    }

    /// <summary>光标是否落在 dock 窗口矩形内（物理像素 → 窗口 DPI 逻辑坐标换算）。</summary>
    private bool IsCursorOverDock(Point cursorPhysical)
    {
        if (double.IsNaN(cursorPhysical.X) || !IsVisible)
        {
            return false;
        }

        var scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice;
        var dpiX = scale?.M11 ?? 1.0;
        var dpiY = scale?.M22 ?? 1.0;
        var lx = cursorPhysical.X / dpiX;
        var ly = cursorPhysical.Y / dpiY;
        return lx >= Left && lx <= Left + ActualWidth && ly >= Top && ly <= Top + ActualHeight;
    }

    /// <summary>系统级用户空闲毫秒数（最后一次鼠标/键盘输入至今；GetLastInputInfo）。</summary>
    private static double GetSystemIdleMs()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref info)
            ? unchecked(Environment.TickCount - (int)info.dwTime)
            : 0; // 检测失败按"刚有输入"处理 → 不隐藏（宁可常驻不可误隐）
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    private static Point GetCursorScreenPoint()
    {
        if (GetCursorPos(out var pt))
        {
            return new Point(pt.X, pt.Y);
        }
        return new Point(double.NaN, double.NaN);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    // ---- 系统功能区（此电脑/网络/回收站/控制面板）----

    // 图标主路径：SHParseDisplayName(CLSID) → PIDL → SHGetFileInfo(SHGFI_PIDL)，
    // 走 Shell 命名空间提取图标，与桌面/资源管理器中的系统图标样式完全一致（含当前主题）。
    // 兜底：SHGetStockIconInfo（系统 stock 图标）——PIDL 提取失败时保证仍能显示。
    private const uint ShgfiIcon = 0x00000100;
    private const uint ShgfiLargeIcon = 0x00000000;
    private const uint ShgfiPidl = 0x00000008;
    private const uint ShgsiIcon = 0x00000100;
    private const uint ShgsiLargeIcon = 0x00000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfo")]
    private static extern IntPtr SHGetFileInfoPidl(IntPtr pidl, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHSTOCKICONINFO
    {
        public uint cbSize;
        public IntPtr hIcon;
        public int iSysIconIndex;
        public int iIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szPath;
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetStockIconInfo(uint siid, uint uFlags, ref SHSTOCKICONINFO psii);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    // 系统功能条目：设置键 / 显示名 / Shell CLSID（图标提取 + 启动目标）/ 兜底 stock ID / 是否走 control.exe。
    private static readonly (string Key, string Name, string Clsid, uint FallbackStockId, bool IsControlPanel)[] SystemEntries =
    {
        ("dock.systemComputer", "此电脑", "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", 15u, false),
        ("dock.systemNetwork", "网络", "::{208D2C60-3AEA-1069-A2D8-08002B30309D}", 92u, false),
        ("dock.systemRecycleBin", "回收站", "::{645FF040-5081-101B-9F08-00AA002F954E}", 8u, false),
        ("dock.systemControlPanel", "控制面板", "::{26EE0668-A00A-44D7-9371-BEB064C98683}", 21u, true),
    };

    /// <summary>取系统图标（桌面同款）：先按 CLSID 经 Shell 命名空间提取，失败回退 stock 图标。失败返回 null。</summary>
    private static ImageSource? GetSystemIcon(string clsid, uint fallbackStockId)
    {
        var icon = GetShellIconByClsid(clsid);
        if (icon is not null)
        {
            return icon;
        }

        return GetStockIcon(fallbackStockId);
    }

    /// <summary>SHParseDisplayName → SHGetFileInfo(PIDL)：与桌面/资源管理器一致的 shell 主题图标。</summary>
    private static ImageSource? GetShellIconByClsid(string clsid)
    {
        IntPtr pidl = IntPtr.Zero;
        try
        {
            if (SHParseDisplayName(clsid, IntPtr.Zero, out pidl, 0, out _) != 0 || pidl == IntPtr.Zero)
            {
                return null;
            }

            var info = new SHFILEINFO();
            var ret = SHGetFileInfoPidl(pidl, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), ShgfiIcon | ShgfiLargeIcon | ShgfiPidl);
            if (ret == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return Imaging.CreateBitmapSourceFromHIcon(
                    info.hIcon,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
            }
            finally
            {
                DestroyIcon(info.hIcon);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (pidl != IntPtr.Zero)
            {
                CoTaskMemFree(pidl);
            }
        }
    }

    /// <summary>SHGetStockIconInfo：系统 stock 图标（当前主题）兜底。</summary>
    private static ImageSource? GetStockIcon(uint stockIconId)
    {
        try
        {
            var info = new SHSTOCKICONINFO { cbSize = (uint)Marshal.SizeOf<SHSTOCKICONINFO>() };
            var ret = SHGetStockIconInfo(stockIconId, ShgsiIcon | ShgsiLargeIcon, ref info);
            if (ret != 0 || info.hIcon == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return Imaging.CreateBitmapSourceFromHIcon(
                    info.hIcon,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
            }
            finally
            {
                DestroyIcon(info.hIcon);
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>重建系统功能区：按设置键逐个决定显示/隐藏，全部关闭时整体折叠。</summary>
    private void RebuildSystemPanel()
    {
        if (SystemPanel is null)
        {
            return;
        }

        SystemPanel.Children.Clear();
        var iconSize = _visual?.IconSize ?? 44d;
        var spacing = _visual?.IconSpacing ?? 12d;
        var showLabel = _visual?.ShowLabel ?? true;

        foreach (var entry in SystemEntries)
        {
            var enabled = _settings?.Get(entry.Key, true) ?? true;
            if (!enabled)
            {
                continue;
            }

            var icon = GetSystemIcon(entry.Clsid, entry.FallbackStockId);
            if (icon is null)
            {
                continue;
            }

            var captured = entry;
            var container = new Grid
            {
                Width = iconSize + 16,
                Margin = new Thickness(spacing / 2, 0, spacing / 2, 0),
                ToolTip = entry.Name
            };
            // 行结构必须与固定区/运行区完全一致：图标/倒影/名称 三行（倒影插在图标与名称之间）。
            container.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
            container.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            container.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var iconImage = new Image
            {
                Width = iconSize,
                Height = iconSize,
                Stretch = Stretch.Uniform,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Source = icon
            };
            RenderOptions.SetBitmapScalingMode(iconImage, BitmapScalingMode.HighQuality);
            Grid.SetRow(iconImage, 0);
            container.Children.Add(iconImage);

            // 倒影：与固定区/运行区同一开关（ReflectionEnabled==true 显示，绝不错接），
            // BuildReflection 内部自动处理 强度/透明度/距离/渐变/倾斜(sunSync 或手动)。
            if (_visual?.ReflectionEnabled ?? false)
            {
                var reflection = BuildReflection(iconImage, iconSize);
                if (reflection is not null)
                {
                    Grid.SetRow(reflection, 1);
                    container.Children.Add(reflection);
                }
            }

            var label = new TextBlock
            {
                Text = entry.Name,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0),
                MaxWidth = iconSize + 16,
                Visibility = showLabel ? Visibility.Visible : Visibility.Collapsed
            };
            BindTheme(label, TextBlock.ForegroundProperty, "ThemeForeground");
            Grid.SetRow(label, 2);
            container.Children.Add(label);

            // 与 Dock 其它图标同一套交互逻辑（AttachItemInteractions）：
            // 悬停放大上浮、点击回弹启动、右键菜单（打开 / 从 Dock 隐藏）。
            AttachItemInteractions(
                container,
                iconImage,
                onClick: () => LaunchSystemEntry(captured),
                onRightClick: () => ShowSystemEntryMenu(captured));
            SystemPanel.Children.Add(container);
        }

        // 全部关闭或取不到图标时折叠整块（不留空白），与运行区 showRunning=false 联动由调用方处理。
        SystemPanel.Visibility = SystemPanel.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 重建 Dock 左端「开始」图标：完全沿用 Dock 图标规格（悬停放大回弹 + 下方倒影 + 名称标签 + 相同间距）。
    /// 图标为自绘的 Windows 四格旗标，颜色取自主题前景（随明暗自适应）；点击切换自绘开始菜单，
    /// 右键唤出系统开始菜单（Win+X）样式右键菜单。倒影/名称开关与其它 Dock 图标同一视觉配置。
    /// </summary>
    private void RebuildStartPanel()
    {
        if (StartPanel is null)
        {
            return;
        }

        StartPanel.Children.Clear();
        var iconSize = _visual?.IconSize ?? 44d;
        var spacing = _visual?.IconSpacing ?? 12d;
        var showLabel = _visual?.ShowLabel ?? true;

        var container = new Grid
        {
            Width = iconSize + 16,
            Margin = new Thickness(spacing / 2, 0, spacing / 2, 0),
            ToolTip = "开始"
        };
        container.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
        container.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        container.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var iconImage = new Image
        {
            Width = iconSize,
            Height = iconSize,
            Stretch = Stretch.Uniform,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Source = CreateStartFlag(iconSize)
        };
        RenderOptions.SetBitmapScalingMode(iconImage, BitmapScalingMode.HighQuality);
        Grid.SetRow(iconImage, 0);
        container.Children.Add(iconImage);

        // 倒影：与固定/运行/系统区同一开关（ReflectionEnabled），其余参数由 BuildReflection 内部读取。
        if (_visual?.ReflectionEnabled ?? false)
        {
            var reflection = BuildReflection(iconImage, iconSize);
            if (reflection is not null)
            {
                Grid.SetRow(reflection, 1);
                container.Children.Add(reflection);
            }
        }

        var label = new TextBlock
        {
            Text = "开始",
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0),
            MaxWidth = iconSize + 16,
            Visibility = showLabel ? Visibility.Visible : Visibility.Collapsed
        };
        BindTheme(label, TextBlock.ForegroundProperty, "ThemeForeground");
        Grid.SetRow(label, 2);
        container.Children.Add(label);

        // 左键切换自绘开始菜单；右键唤出系统开始菜单样式右键菜单（Win+X）。
        AttachItemInteractions(
            container,
            iconImage,
            onClick: () => _dockPlugin?.ToggleStartMenu(),
            onRightClick: ShowStartContextMenu);
        StartPanel.Children.Add(container);
    }

    /// <summary>自绘 Windows 四格旗标（开始图标）：4 个圆角方块 2x2 排布，颜色取主题前景，随明暗换肤。</summary>
    private BitmapSource CreateStartFlag(double size)
    {
        var solid = ThemeBrush("ThemeForeground") as SolidColorBrush;
        var color = solid?.Color ?? Colors.White;

        var dv = new DrawingVisual();
        using (var ctx = dv.RenderOpen())
        {
            var gap = size * 0.10;
            var cell = (size - gap * 3) / 2;
            var radius = Math.Max(1d, size * 0.06);
            var brush = new SolidColorBrush(color);

            var top = gap;
            var left = gap;
            var mid = gap + cell + gap;
            ctx.DrawRoundedRectangle(brush, null, new Rect(left, top, cell, cell), radius, radius);
            ctx.DrawRoundedRectangle(brush, null, new Rect(mid, top, cell, cell), radius, radius);
            ctx.DrawRoundedRectangle(brush, null, new Rect(left, mid, cell, cell), radius, radius);
            ctx.DrawRoundedRectangle(brush, null, new Rect(mid, mid, cell, cell), radius, radius);
        }

        var pixel = Math.Max(1, (int)size);
        var rtb = new RenderTargetBitmap(pixel, pixel, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>
    /// 开始图标右键：优先唤出**系统原生**电源用户菜单（Win+X，即 Windows 开始按钮右键菜单，
    /// 其条目天然全部对接真实系统功能）。SendInput 被拒（UIPI 前台锁）时降级到自绘复刻菜单。
    /// </summary>
    private void ShowStartContextMenu()
    {
        if (KeyboardInterop.ShowWindowsXMenu())
        {
            return;
        }

        _showFallbackStartContextMenu();
    }

    /// <summary>自绘复刻 Win+X 菜单（SendInput 失败时的降级路径，M9/M10）。</summary>
    private void _showFallbackStartContextMenu()
    {
        var menu = new ContextMenu();

        AddWinX(menu, "系统", () => LaunchUri("ms-settings:system"));
        AddWinX(menu, "设备管理器", () => LaunchFile("devmgmt.msc"));
        AddWinX(menu, "网络连接", () => LaunchFile("ncpa.cpl"));
        AddWinX(menu, "磁盘管理", () => LaunchFile("diskmgmt.msc"));
        AddWinX(menu, "计算机管理", () => LaunchFile("compmgmt.msc"));
        AddWinX(menu, "Windows PowerShell (管理员)", LaunchPowerShellAdmin);
        menu.Items.Add(new Separator());
        AddWinX(menu, "任务管理器", () => LaunchFile("taskmgr.exe"));
        menu.Items.Add(new Separator());
        AddWinX(menu, "设置", () => LaunchUri("ms-settings:"));
        AddWinX(menu, "文件资源管理器", () => LaunchUri("explorer.exe"));
        AddWinX(menu, "搜索", () => LaunchUri("ms-search:search"));
        AddWinX(menu, "运行", () => KeyboardInterop.OpenRunDialog());
        AddWinX(menu, "关机或注销", null, BuildPowerSubmenu);
        AddWinX(menu, "桌面", () => KeyboardInterop.ShowDesktop());

        menu.PlacementTarget = this;
        menu.IsOpen = true;
    }

    /// <summary>构造「关机或注销」子菜单（注销 / 睡眠 / 关机 / 重启）。</summary>
    private void BuildPowerSubmenu(MenuItem parent)
    {
        AddWinX(parent, "注销", () => RunShell("shutdown.exe", "/l"));
        AddWinX(parent, "睡眠", () => RunShell("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0"));
        AddWinX(parent, "关机", () => RunShell("shutdown.exe", "/s /t 0"));
        AddWinX(parent, "重启", () => RunShell("shutdown.exe", "/r /t 0"));
    }

    /// <summary>把一项加到菜单；若提供 buildChildren，则该项成为带子菜单的父项。</summary>
    private static void AddWinX(ContextMenu menu, string header, Action? onClick, Action<MenuItem>? buildChildren = null)
    {
        var item = new MenuItem { Header = header };
        if (onClick is not null)
        {
            item.Click += (_, _) => onClick();
        }

        if (buildChildren is not null)
        {
            buildChildren(item);
        }

        menu.Items.Add(item);
    }

    private static void AddWinX(MenuItem parent, string header, Action onClick)
    {
        var child = new MenuItem { Header = header };
        child.Click += (_, _) => onClick();
        parent.Items.Add(child);
    }

    private static void LaunchUri(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch
        {
            // 启动失败静默（M10）。
        }
    }

    private static void LaunchFile(string file)
    {
        try
        {
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
        }
        catch
        {
            // 启动失败静默（M10）。
        }
    }

    private static void RunShell(string file, string args)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch
        {
            // 启动失败静默（M10）。
        }
    }

    private static void LaunchPowerShellAdmin()
    {
        try
        {
            Process.Start(new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = true,
                Verb = "runas"
            });
        }
        catch
        {
            // 提权被取消/失败静默（M10）。
        }
    }

    /// <summary>系统功能右键菜单：打开 / 从 Dock 隐藏（关闭对应设置开关，图标即时消失）。</summary>
    private void ShowSystemEntryMenu((string Key, string Name, string Clsid, uint FallbackStockId, bool IsControlPanel) entry)
    {
        var menu = new ContextMenu();

        var openItem = new MenuItem { Header = "打开" };
        openItem.Click += (_, _) => LaunchSystemEntry(entry);
        menu.Items.Add(openItem);

        var hideItem = new MenuItem { Header = $"隐藏「{entry.Name}」" };
        hideItem.Click += (_, _) => _settings?.Set(entry.Key, false);
        menu.Items.Add(hideItem);

        menu.PlacementTarget = this;
        menu.IsOpen = true;
    }

    private static void LaunchSystemEntry((string Key, string Name, string Clsid, uint FallbackStockId, bool IsControlPanel) entry)
    {
        try
        {
            if (entry.IsControlPanel)
            {
                Process.Start(new ProcessStartInfo { FileName = "control.exe", UseShellExecute = true });
            }
            else
            {
                // explorer.exe ::{CLSID} 直接打开特殊文件夹（此电脑/网络/回收站）。
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = entry.Clsid,
                    UseShellExecute = true
                });
            }
        }
        catch
        {
            // 启动失败静默忽略（ShellExecute 偶发失败不阻断 dock）。
        }
    }

    private void OnPinnedChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            RebuildPinnedPanel();
            RefreshPinnedRunningState();
        });
    }

    /// <summary>
    /// 应用源变化（开始菜单创建/删除/改名）后刷新固定面板。
    /// 由 DockPlugin 在 IAppSourceService.AppSourceChanged 时调用（须在 UI 线程）。
    /// </summary>
    internal void RefreshPinnedPanelFromSource()
    {
        RebuildPinnedPanel();
        RefreshPinnedRunningState();
    }

    /// <summary>
    /// 重建固定应用面板（清理后按当前固定列表重绘）。
    /// 图标大小/间距/名称显示/倒影均从 <see cref="_visual"/> 读取；支持拖拽重排（见 AttachReorderDrag）。
    /// </summary>
    private void RebuildPinnedPanel()
    {
        PinnedPanel.Children.Clear();
        _pinnedDots.Clear();

        var iconSize = _visual?.IconSize ?? 44d;
        var spacing = _visual?.IconSpacing ?? 12d;
        var showLabel = _visual?.ShowLabel ?? true;

        // 固定区可视宽度（动态贴合）：内容少于一屏(6个)时宽=内容宽（不留 6 个空位，美观）；
        // 内容超一屏时才固定为一屏 6 个宽，由 PinnedScrollViewer 横向滚动查看（滚动+边缘半隐备着）。
        var oneScreenW = (iconSize + 16 + spacing) * PinnedScreenCapacity;
        var pinnedContentW = _dockAppsService.Pinned.Count * (iconSize + 16 + spacing);
        PinnedScrollViewer.Width = Math.Min(pinnedContentW, oneScreenW);

        foreach (var item in _dockAppsService.Pinned)
        {
            // 容器：图标区（含可选倒影） + 名称行。名称关闭时仅图标区。
            var container = new Grid
            {
                Width = iconSize + 16,
                Margin = new Thickness(spacing / 2, 0, spacing / 2, 0),
                Cursor = System.Windows.Input.Cursors.Hand
            };

            var rowIcon = new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) };
            var rowReflection = new RowDefinition { Height = GridLength.Auto };
            var rowLabel = new RowDefinition { Height = GridLength.Auto };
            container.RowDefinitions.Add(rowIcon);
            container.RowDefinitions.Add(rowReflection);
            container.RowDefinitions.Add(rowLabel);

            // 图标区（含倒影堆叠）：用一个内层 Grid 承载图标 + 其下方倒影。
            var iconZone = new Grid();
            var iconImage = BuildIconImage(iconSize);
            iconZone.Children.Add(iconImage);

            // 倒影（启用时）紧随图标下方，位于 container 第 1 行（图标与名称之间）。
            Grid.SetRow(iconZone, 0);
            container.Children.Add(iconZone);
            if (_visual?.ReflectionEnabled ?? false)
            {
                var reflection = BuildReflection(iconImage, iconSize);
                if (reflection is not null)
                {
                    Grid.SetRow(reflection, 1);
                    container.Children.Add(reflection);
                }
            }

            // 软件名称（可关闭）。
            var label = new TextBlock
            {
                Text = item.Name,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 3, 0, 0),
                Visibility = showLabel ? Visibility.Visible : Visibility.Collapsed
            };
            BindTheme(label, TextBlock.ForegroundProperty, "ThemeForeground");
            Grid.SetRow(label, 2);
            container.Children.Add(label);

            // 运行指示点（保持原逻辑）。
            var dot = new Ellipse
            {
                Width = 6,
                Height = 6,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 3),
                Visibility = Visibility.Collapsed
            };
            BindTheme(dot, Shape.FillProperty, "ThemeForeground");
            iconZone.Children.Add(dot);

            var capturedItem = item;
            AttachItemInteractions(
                container,
                iconImage,
                onClick: () => LaunchApp(capturedItem),
                onRightClick: () => ShowItemContextMenu(capturedItem, null),
                onHover: () => ScheduleOpenPreview(capturedItem, GetItemScreenAnchor(container)));

            // 图标项直接承载于容器，不再外包"描边+阴影卡片"。
            var card = new Border
            {
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(0),
                Child = container,
                Tag = item.Id // 供拖拽提交时反查 DockItemId
            };

            PinnedPanel.Children.Add(card);
            _pinnedDots.Add((item, dot));

            container.MouseLeave += (_, _) => ScheduleClosePreview();

            // 拖拽重排：在卡片级拦截（container 直接承载交互），与点击/右键/悬停共存。
            AttachReorderDrag(card, container, iconImage, capturedItem);

            _ = LoadIconAsync(item, iconImage);
        }

        UpdateEdgeFadeDeferred();
    }

    /// <summary>构造标准图标 Image（尺寸由 iconSize 控制，替代硬编码 44）。</summary>
    private static Image BuildIconImage(double iconSize)
    {
        return new Image
        {
            Width = iconSize,
            Height = iconSize,
            Stretch = Stretch.Uniform,
            SnapsToDevicePixels = true,
            Source = null,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    // 拖拽重排状态：拖拽中记录被拖的 Border 与起点，移动到阈值以上才正式进入拖拽。
    private Border? _dragCard;
    private Point _dragStart;
    private bool _dragActive;
    private const double DragThreshold = 6d;

    /// <summary>为固定项容器附加拖拽重排：在 dock 上按住左键拖动图标即可调整顺序。
    /// 与点击启动/右键菜单/悬停预览共存——只有移动超过阈值才视为拖拽，否则仍是点击。
    /// 拖拽中按鼠标 X 与各卡片中心比较实时调整 PinnedPanel.Children 顺序（视觉即时反馈），
    /// 释放时按视觉顺序提交 DockPinnedService.Reorder 持久化。</summary>
    private void AttachReorderDrag(Border card, Grid container, Image iconImage, DockItemData item)
    {
        container.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _dragCard = card;
            _dragStart = e.GetPosition(PinnedPanel);
            _dragActive = false;
        };

        container.PreviewMouseMove += (_, e) =>
        {
            if (_dragCard is null || _dragActive)
            {
                if (_dragActive) ReorderVisualByMouse(e.GetPosition(PinnedPanel));
                return;
            }

            var pos = e.GetPosition(PinnedPanel);
            if (Math.Abs(pos.X - _dragStart.X) < DragThreshold &&
                Math.Abs(pos.Y - _dragStart.Y) < DragThreshold)
            {
                return;
            }

            _dragActive = true;
            // 进入拖拽：取消本次点击启动（置标记由 AttachItemInteractions 的 click 判定读取）。
            _suppressClick = true;
        };

        container.PreviewMouseLeftButtonUp += (_, _) =>
        {
            if (_dragActive && _dragCard is not null)
            {
                CommitReorder();
            }
            _dragCard = null;
            _dragActive = false;
            // 延迟清除 suppress 标记，避免与 click 事件竞争（click 在 MouseUp 之后触发）。
            Dispatcher.BeginInvoke(() => _suppressClick = false);
        };
    }

    /// <summary>拖拽中：根据鼠标 X 与被拖卡片在各兄弟卡片中的位置，实时插入到正确顺序。</summary>
    private void ReorderVisualByMouse(Point mousePos)
    {
        if (_dragCard is null || PinnedPanel is null) return;
        // 计算目标插入索引：鼠标 X 落在其后第一个卡片中心左侧即插到它前面。
        var children = PinnedPanel.Children;
        var targetIndex = children.Count;
        for (var i = 0; i < children.Count; i++)
        {
            if (children[i] is not FrameworkElement fe || fe == _dragCard) continue;
            var center = fe.TranslatePoint(new Point(fe.ActualWidth / 2, 0), PinnedPanel).X;
            if (mousePos.X < center)
            {
                targetIndex = i;
                break;
            }
        }

        var currentIndex = children.IndexOf(_dragCard);
        if (currentIndex < 0 || currentIndex == targetIndex || targetIndex > children.Count) return;

        // 调整到目标位置（移除再插入，保持视觉顺序与鼠标一致）。
        children.RemoveAt(currentIndex);
        var insertAt = targetIndex > currentIndex ? targetIndex - 1 : targetIndex;
        insertAt = Math.Max(0, Math.Min(insertAt, children.Count));
        children.Insert(insertAt, _dragCard);
    }

    /// <summary>拖拽结束：按 PinnedPanel.Children 的当前视觉顺序提取 DockItemId 列表提交 Reorder。</summary>
    private void CommitReorder()
    {
        try
        {
            var order = new List<DockItemId>();
            foreach (var child in PinnedPanel.Children)
            {
                if (child is not Border { Tag: DockItemId id }) continue;
                order.Add(id);
            }

            if (order.Count > 0)
            {
                _dockAppsService.Reorder(order);
                _dockAppsService.Save();
            }
        }
        catch
        {
            // 重排提交失败不阻断（下次 PinnedChanged 会重建为正确顺序）。
        }
    }

    /// <summary>拖拽进行中时抑制点击启动，避免拖完误触发 LaunchApp。</summary>
    private bool _suppressClick;

    /// <summary>
    /// 构造图标倒影：原始图标经 RenderTargetBitmap 在图像内容层垂直翻转，再附加 OpacityMask
    /// (从贴近图标端=不透明渐变到远端=透明)，与 LayoutTransform 翻转相比 brush bbox 不确定性消失,
    /// 方向恒等于「物理水面倒影」(贴近水面端清晰,远端淡出)。distance=0 时倒影紧贴图标底端。
    /// 倾斜作为整体 SkewTransform 应用到 element RenderTransform,Origin=(0.5, 1.0) 让倾斜基准线
    /// 落在 zone bottom,视觉上像水面被风吹歪的透视效果。
    /// 监听 iconImage.SourceChanged: 异步重建 RTB(原图标异步加载完后倒影自动同步)。
    /// 返回承载倒影的 Grid(已设 Row=1 于 container 内,位于图标下方、名称上方);不需要时返回 null。
    /// </summary>
    private FrameworkElement? BuildReflection(Image iconImage, double iconSize)
    {
        if (_visual is null) return null;
        var distance = _visual.ReflectionDistance;
        var gradientDistance = _visual.ReflectionGradientDistance;
        var opacity = _visual.ReflectionOpacity;
        var darken = 1 - _visual.ReflectionIntensity; // 0=不暗化(最实) 1=全暗(最虚)
        // 太阳同步(日升日落)开启时倾斜角由时间驱动，忽略手动倾斜值。
        var skewDeg = _visual.SunSync ? ComputeSunSkew() : _visual.ReflectionSkew; // -30~+30 度

        // 倒影元素:普通 Image(无 LayoutTransform 翻转,因翻转在 bitmap 层完成)。
        // VerticalAlignment=Top 让 slot 紧贴 zone top=接近图标端=贴近水线端=应当清晰端。
        var reflectionImage = new Image
        {
            Width = iconSize,
            Height = iconSize,
            Stretch = Stretch.Uniform,
            SnapsToDevicePixels = true,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Opacity = opacity * (1 - darken * 0.7),
        };
        // 倾斜:RenderTransform SkewTransform,Origin=(0.5, 0.0) 锚定**顶部中央**(水面线=贴近图标端)——
        // 水面斜切时水面线处图标与倒影始终衔接不错位,倒影向远端斜切。
        // 符号:锚顶后底部位移 = H*tan(θ),θ>0 = 底部右移 = 右倾,与设置标注「负=左倾,正=右倾」一致。
        if (Math.Abs(skewDeg) > 0.01)
        {
            reflectionImage.RenderTransform = new SkewTransform(skewDeg, 0);
            reflectionImage.RenderTransformOrigin = new Point(0.5, 0.0);
        }
        RenderOptions.SetBitmapScalingMode(reflectionImage, BitmapScalingMode.HighQuality);

        // OpacityMask:visual top(slot 顶端,贴近图标端=物理水面=应最清晰)=White → 渐变到 visual bottom(远端)=Transparent。
        // 因为图像已经在 RTB 层翻转,这里直接对视觉位置设 mask,方向与物理水面倒影一致。
        var mask = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0), // 元素 top=接近图标端
            EndPoint = new Point(0, 1)    // 元素 bottom=远端
        };
        var fadeFrac = Math.Min(1, gradientDistance / iconSize);
        mask.GradientStops.Add(new GradientStop(Colors.White, 0.0));
        mask.GradientStops.Add(new GradientStop(Colors.White, Math.Max(0, 1 - fadeFrac)));
        mask.GradientStops.Add(new GradientStop(Colors.Transparent, 1.0));
        mask.Freeze();
        reflectionImage.OpacityMask = mask;

        // 异步重建翻转后的位图:DrawingVisual + ScaleTransform(1,-1) 在 bitmap 层垂直翻转原图标,
        // 再渲染到 RenderTargetBitmap 赋给 reflectionImage.Source。
        // 监听 iconImage.Source DP 实际值变化(WPF Image 类未提供 SourceChanged 事件,用
        // DependencyPropertyDescriptor.AddValueChanged 监听 DP 变化是社区通用做法)。
        void RebuildFromCurrentSource()
        {
            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    if (iconImage.Source is not BitmapSource src)
                    {
                        reflectionImage.Source = null;
                        return;
                    }
                    // 关键：RTB 按【显示尺寸 iconSize】渲染，而非原图像素尺寸。
                    // 原图可能远大于图标显示尺寸（自定义图标/系统大图/高清源），直接按原图建 RTB
                    // 会占用巨量显存/内存 → 渲染线程 OutOfMemoryException（HwndTarget.UpdateWindowSettings）崩溃。
                    var w = Math.Max(1, (int)iconSize);
                    var h = Math.Max(1, (int)iconSize);
                    var dv = new DrawingVisual();
                    using (var ctx = dv.RenderOpen())
                    {
                        var brush = new ImageBrush(src) { Stretch = Stretch.Uniform };
                        // 翻转图像:ScaleY(-1) 后绘制到 (0, -h, w, h) 矩形,使翻转图占满 DV 的 0~h 区域。
                        // DV 坐标系原点在左上角:源 Y=0 翻转到 Y=-h,源 Y=h 翻转到 Y=0——视觉上图像上下颠倒。
                        ctx.PushTransform(new ScaleTransform(1, -1));
                        ctx.DrawRectangle(brush, null, new Rect(0, -h, w, h));
                    }
                    var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(dv);
                    rtb.Freeze();
                    reflectionImage.Source = rtb;
                }
                catch
                {
                    reflectionImage.Source = null;
                }
            }, DispatcherPriority.Background);
        }
        var srcDpd = DependencyPropertyDescriptor.FromProperty(Image.SourceProperty, typeof(Image));
        srcDpd.AddValueChanged(iconImage, (_, _) => RebuildFromCurrentSource());
        // 初次加载立即触发一次(图标可能已 Source,或在异步加载即将就绪)。
        RebuildFromCurrentSource();

        // zone 紧贴图标下方(distance 间距),高度=图标尺寸(足够放下整条镜像)。VerticalAlignment=Top 让 zone 自身紧贴容器,不留白。
        var zone = new Grid
        {
            Margin = new Thickness(0, distance, 0, 0),
            Height = iconSize,
            VerticalAlignment = VerticalAlignment.Top
        };
        zone.Children.Add(reflectionImage);
        return zone;
    }

    /// <summary>太阳同步倒影定时刷新（每 60 秒）：倾斜角随时间平滑变化，变化超过阈值才重建固定区面板让倒影跟随。
    /// 仅影响倒影倾斜（固定区图标），绝不触碰运行区——运行区重建职责独占于 OnVisualSettingsChanged。</summary>
    private void OnSunSyncTick(object? sender, EventArgs e)
    {
        if (_visual is null || !_visual.SunSync)
        {
            return;
        }

        var skew = ComputeSunSkew();
        if (Math.Abs(skew - _lastSunSkew) < 0.5)
        {
            return;
        }

        _lastSunSkew = skew;
        Dispatcher.BeginInvoke(() =>
        {
            RebuildPinnedPanel();
        });
    }

    /// <summary>按当前时间计算太阳同步倾斜角（依赖系统时钟，分钟级平滑）。</summary>
    private static double ComputeSunSkew()
    {
        var now = DateTime.Now;
        return SunSkewForHour(now.Hour + now.Minute / 60.0 + now.Second / 3600.0);
    }

    /// <summary>太阳日升日落 → 倒影倾斜角映射（全天 24h 完整正弦周期，模拟阳光方位）：
    /// 6:00 日出（太阳在东/右）→ 右倾 +30°（峰值）；12:00 正午 → 垂直 0°；
    /// 18:00 日落（太阳在西/左）→ 左倾 -30°（谷值）；18→24 反向倾斜逐步回正，
    /// 24:00/0:00 → 垂直 0°，再平滑衔接次日 6:00 日出动画（无缝循环）。
    /// 独立为纯函数便于单元测试。</summary>
    public static double SunSkewForHour(double hours)
    {
        const double maxSkew = 30;
        // 2π·t/24 一个完整周期：t=6→sin(π/2)=1(+30)、t=12→sin(π)=0、t=18→sin(3π/2)=-1(-30)、t=24→sin(2π)=0。
        // 6-18 区间与旧「白天半周期 cos(π·(h-6)/12)」取值完全一致，夜间改为继续正弦过渡而非置 0。
        return maxSkew * Math.Sin(2 * Math.PI * hours / 24.0);
    }


    private void ShowItemContextMenu(DockItemData item, MouseButtonEventArgs? e)
    {
        var menu = new ContextMenu();

        var launchItem = new MenuItem { Header = "启动" };
        launchItem.Click += (_, _) => LaunchApp(item);
        menu.Items.Add(launchItem);

        var removeItem = new MenuItem { Header = "从 Dock 移除" };
        removeItem.Click += (_, _) =>
        {
            _dockAppsService.RemoveById(item.Id);
            _dockAppsService.Save();
        };
        menu.Items.Add(removeItem);

        var openDirItem = new MenuItem { Header = "打开所在目录" };
        openDirItem.Click += (_, _) => OpenContainingDirectory(item);
        menu.Items.Add(openDirItem);

        menu.Items.Add(new Separator());

        // 开始菜单（shell.start-menu 经典布局，Win 键 / Dock 共用 toggle 契约）
        var startMenuItem = new MenuItem { Header = "开始菜单" };
        startMenuItem.Click += (_, _) => _dockPlugin?.ToggleStartMenu();
        menu.Items.Add(startMenuItem);

        // 应用提取器（shell-app-source 双模式盘点：干净/全程序 + 筛选/分组/固定/卸载，
        // AppGrabberWindow 承接 MyDockFinder AppGrabber 形态）
        var managerItem = new MenuItem { Header = "应用提取器" };
        managerItem.Click += (_, _) => ShowAppGrabber();
        menu.Items.Add(managerItem);

        menu.PlacementTarget = e?.OriginalSource as UIElement ?? this;
        menu.IsOpen = true;
    }

    /// <summary>打开应用提取器窗口（懒创建复用）。</summary>
    private AppGrabberWindow? _appGrabberWindow;
    private void ShowAppGrabber()
    {
        _appGrabberWindow ??= new AppGrabberWindow(_dockAppsService, _dockIconService, VibrancyService!, AppearanceService);
        _appGrabberWindow.Show();
        _appGrabberWindow.Activate();
    }

    private void LaunchApp(DockItemData item)
    {
        try
        {
            var path = !string.IsNullOrWhiteSpace(item.TargetPath)
                ? item.TargetPath
                : item.ShortcutPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            // 已运行则激活最前窗口（对齐 Windows 任务栏标准语义：已运行点击=激活）。
            // ⚠️ explorer 特殊：单实例程序，二次 ShellExecute 的新进程检测到已有实例直接退出——
            // 既不开新窗口也不激活 → "点固定区资源管理器没反应"（实测回归；运行区因 pinned
            // 排除已固定应用，此路径是 explorer 唯一的点击入口）。
            var matches = RunningAppDetector.GetRunningWindows()
                .Where(w => string.Equals(w.ExePath, path, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count > 0)
            {
                var best = matches.FirstOrDefault(w => IsVisibleAndNotMinimized(w.Hwnd));
                if (best.Hwnd == IntPtr.Zero)
                {
                    best = matches[0];
                }

                if (best.Hwnd != IntPtr.Zero)
                {
                    // 同 ActivateFirstWindow：MouseUp 处理中鼠标仍被捕获，SetForegroundWindow 会被拒——延迟激活。
                    Dispatcher.BeginInvoke(
                        () => RunningAppDetector.ActivateWindow(best.Hwnd),
                        System.Windows.Threading.DispatcherPriority.Background);
                    return;
                }
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch
        {
            // 启动失败不阻断
        }
    }

    private void OpenContainingDirectory(DockItemData item)
    {
        try
        {
            var path = !string.IsNullOrWhiteSpace(item.TargetPath) ? item.TargetPath : item.ShortcutPath;
            if (!string.IsNullOrWhiteSpace(path))
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    System.Diagnostics.Process.Start("explorer.exe", dir);
                }
            }
        }
        catch
        {
            // 打开目录失败不阻断
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        try
        {
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files)
            {
                return;
            }

            var changed = false;
            foreach (var file in files)
            {
                if (!ShellLinkResolver.IsSupportedFile(file))
                {
                    continue;
                }

                _dockAppsService.AddByPath(file);
                changed = true;
            }

            if (changed)
            {
                _dockAppsService.Save();
                RebuildPinnedPanel();
            }
        }
        catch
        {
            // 拖放失败不阻断
        }
    }

    private void RefreshAll()
    {
        RefreshPinnedRunningState();
        RefreshRunningPanel();
    }

    private void RefreshPinnedRunningState()
    {
        try
        {
            var runningPaths = RunningAppDetector.GetRunningExecutablePaths();

            foreach (var entry in _pinnedDots)
            {
                var isRunning =
                    (!string.IsNullOrWhiteSpace(entry.Item.TargetPath) && runningPaths.Contains(entry.Item.TargetPath)) ||
                    (!string.IsNullOrWhiteSpace(entry.Item.ShortcutPath) && runningPaths.Contains(entry.Item.ShortcutPath));

                entry.Dot.Visibility = isRunning ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        catch
        {
            // 运行状态刷新失败不阻断主流程。
        }
    }

    private void RefreshRunningPanel()
    {
        // 开关控制:关闭「显示运行区」后整段(running+分隔线)折叠,dock 退化为单区(仅左半 fixed 应用)。
        // 系统功能区（此电脑/网络/回收站/控制面板）属于运行区旁侧，随其一起折叠。
        var showRunning = _visual?.ShowRunning ?? true;
        if (!showRunning)
        {
            DebugLog.Trace("Running", $"refresh showRunning=false → 运行区折叠");
            if (Separator.Visibility != Visibility.Collapsed) Separator.Visibility = Visibility.Collapsed;
            if (RunningScrollViewer.Visibility != Visibility.Collapsed) RunningScrollViewer.Visibility = Visibility.Collapsed;
            if (SystemPanel.Visibility != Visibility.Collapsed) SystemPanel.Visibility = Visibility.Collapsed;
            return;
        }

        // 运行区恢复显示时，确保系统功能区同步恢复（无子项则重建一次，避免残留折叠态）。
        if (SystemPanel.Children.Count == 0)
        {
            RebuildSystemPanel();
        }

        try
        {
            var windows = RunningAppDetector.GetRunningWindows();
            DebugLog.Trace("Running", $"refresh windows={windows.Count}");

            // 一屏可视约 6 个运行图标（对齐左侧固定区）；运行窗口再多时由 ScrollViewer 横向滚动查看
            // （备着：一般不会开那么多窗口，但滚动+边缘半隐能力与左侧一致）。
            var iconSize = _visual?.IconSize ?? 44d;
            var spacing = _visual?.IconSpacing ?? 12d;
            var showLabel = _visual?.ShowLabel ?? true;

            // 同进程多窗口按可执行路径去重（折叠为一个图标）；保留最早窗口句柄
            var runningApps = new Dictionary<string, (IntPtr Hwnd, string Title)>(StringComparer.OrdinalIgnoreCase);
            foreach (var window in windows)
            {
                if (!runningApps.ContainsKey(window.ExePath))
                {
                    runningApps[window.ExePath] = (window.Hwnd, window.Title);
                }
            }

            // 排除「已显示在左区」的运行中应用，避免左右重复显示。
            // 固定区现在显示全部固定应用（滚动查看），故运行区排除全部 Pinned（含 TargetPath + ShortcutPath）。
            var pinnedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in _dockAppsService.Pinned)
            {
                if (!string.IsNullOrWhiteSpace(item.TargetPath))
                {
                    pinnedTargets.Add(item.TargetPath);
                }

                if (!string.IsNullOrWhiteSpace(item.ShortcutPath))
                {
                    pinnedTargets.Add(item.ShortcutPath);
                }
            }

            var orderedItems = new List<DockItemData>();
            foreach (var (exePath, info) in runningApps)
            {
                if (pinnedTargets.Contains(exePath))
                {
                    continue;
                }

                var (displayName, targetPath, source) = ShellLinkResolver.Resolve(exePath);
                orderedItems.Add(new DockItemData
                {
                    Id = new DockItemId(targetPath ?? exePath),
                    Name = string.IsNullOrWhiteSpace(displayName) ? Path.GetFileNameWithoutExtension(exePath) : displayName,
                    ShortcutPath = exePath,
                    TargetPath = targetPath ?? exePath,
                    AppType = AppSourceConverter.ToDockAppType(source, targetPath ?? exePath),
                    IsPinned = false,
                    IsRunning = true
                });
            }

            orderedItems = orderedItems
                .OrderBy(i => Path.GetFileNameWithoutExtension(i.TargetPath), StringComparer.OrdinalIgnoreCase)
                .ToList();

            // 诊断：运行区不显示时据此定位断点（windows 数 / 显示的固定目标数 / 最终 items 数）。
            DebugLog.Trace("Running", $"refresh apps={runningApps.Count} pinnedShown={pinnedTargets.Count} items={orderedItems.Count}");

            // 将运行项同步进 DockService 运行时模型（统一由 IDockService 维护当前可见的运行集合）。
            SyncRunningIntoService(orderedItems);

            // 无运行窗口：直接保证占位可见。空状态处理必须放在 keys 去重之前——
            // 否则空集合与初始 _lastRunningKeys=[] 的 SequenceEqual 恒为 true 会提前 return，
            // 占位（乃至整个运行区）永不渲染（「运行区不知所踪」历史根因）。
            if (orderedItems.Count == 0)
            {
                // 已是占位则跳过重建，避免每秒 Clear+Add 抖动面板（用专属标志判定，不蹭去重键集合）。
                if (_runningEmptyRendered && RunningPanel.Children.Count == 1)
                {
                    return;
                }

                DebugLog.Trace("Running", "refresh 空状态 → 渲染「无运行窗口」占位");
                _runningEmptyRendered = true;
                _lastRunningKeys.Clear(); // 复位去重键：从空恢复后有运行项时 keys 必变化 → 触发重建
                RunningPanel.Children.Clear();
                Separator.Visibility = Visibility.Visible;
                RunningScrollViewer.Visibility = Visibility.Visible;
                // 空状态宽度贴合占位文字（不预留 6 个空位）。
                RunningScrollViewer.Width = double.NaN;
                var empty = new TextBlock
                {
                    Text = "（无运行窗口）",
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 8, 0)
                };
                BindTheme(empty, TextBlock.ForegroundProperty, "MutedForeground");
                RunningPanel.Children.Add(empty);
                UpdateEdgeFadeDeferred();
                return;
            }

            // 运行区显示全部运行应用（对齐左侧固定区：一屏可视约 6 个，超出由 ScrollViewer 滚动查看，
            // 不截断——运行窗口再多也能滚动浏览，滚动+边缘半隐能力备着）。
            var displayItems = orderedItems;

            // 运行集合未变化时跳过重建，避免每秒重建导致缩略图/图标抖动
            var keys = displayItems.Select(i => i.TargetPath).ToList();
            if (keys.SequenceEqual(_lastRunningKeys))
            {
                return;
            }

            DebugLog.Trace("Running", $"refresh 重建运行区 items={displayItems.Count}");
            _runningEmptyRendered = false;
            _lastRunningKeys.Clear();
            _lastRunningKeys.AddRange(keys);
            RunningPanel.Children.Clear();
            Separator.Visibility = Visibility.Visible;
            RunningScrollViewer.Visibility = Visibility.Visible;
            // 运行区可视宽度动态贴合：窗口数少于一屏(6个)时宽=内容宽（不留空位）；超一屏才固定 6 个宽滚动。
            RunningScrollViewer.Width = Math.Min(displayItems.Count * (iconSize + 16 + spacing), (iconSize + 16 + spacing) * RunningScreenCapacity);

            foreach (var item in displayItems)
            {
                var capturedItem = item;

                var container = new Grid
                {
                    Width = iconSize + 16,
                    Margin = new Thickness(spacing / 2, 0, spacing / 2, 0),
                    Cursor = System.Windows.Input.Cursors.Hand
                };

                // 行结构必须与左侧固定区(RebuildPinnedPanel)完全一致：图标/倒影/名称 三行显式。
                // 曾把倒影塞进 iconZone 内部——iconZone.RowDefinitions.Add 添加的是索引 0(Row0) 定义，
                // 而 Grid.SetRow(reflection,1) 让倒影落在未定义的隐式行，WPF 布局把倒影排到图标顶部
                // （headless 实测：右侧 ref Y=4 与 icon Y=2 重叠，左侧正确 Y=48）。
                container.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
                container.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                container.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var iconZone = new Grid();
                var iconImage = BuildIconImage(iconSize);
                iconZone.Children.Add(iconImage);

                Grid.SetRow(iconZone, 0);
                container.Children.Add(iconZone);

                if (_visual?.ReflectionEnabled ?? false)
                {
                    var reflection = BuildReflection(iconImage, iconSize);
                    if (reflection is not null)
                    {
                        Grid.SetRow(reflection, 1);
                        container.Children.Add(reflection);
                    }
                }

                var label = new TextBlock
                {
                    Text = item.Name,
                    FontSize = 11,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    Margin = new Thickness(0, 2, 0, 0),
                    Visibility = showLabel ? Visibility.Visible : Visibility.Collapsed
                };
                BindTheme(label, TextBlock.ForegroundProperty, "ThemeForeground");
                Grid.SetRow(label, 2);
                container.Children.Add(label);

                // ⚠️ 运行区图标点击异常：explorer 的 onClick 不触发（onHover 正常）。
                // 直接在 iconZone 上挂点击（最底层容器，绕过可能的遮挡）。
                iconZone.MouseLeftButtonUp += (_, _) =>
                {
                    DebugLog.Trace("Activate", $"running zone clicked exe={capturedItem.TargetPath ?? capturedItem.ShortcutPath}");
                    ActivateFirstWindow(capturedItem);
                };

                // 悬停即弹出该应用的多窗口缩略图预览（紧贴图标上方，参考 cairoshell 行为）。
                AttachItemInteractions(
                    container,
                    iconImage,
                    onClick: () => { }, // 空操作，点击已由 iconZone 处理
                    onHover: () => ScheduleOpenPreview(capturedItem, GetItemScreenAnchor(container)));

                // 鼠标离开运行项后延迟关闭预览（留出移动到预览层的时间）。
                container.MouseLeave += (_, _) => ScheduleClosePreview();

                // 运行项同样直接承载于容器，不外包描边+阴影卡片（高光/阴影只加在大窗口）。
                var card = new Border
                {
                    CornerRadius = new CornerRadius(10),
                    BorderThickness = new Thickness(0),
                    Child = container
                };

                RunningPanel.Children.Add(card);
                _ = LoadIconAsync(item, iconImage);
            }

            UpdateEdgeFadeDeferred();
        }
        catch
        {
            // 运行面板刷新失败不阻断主流程。
        }
    }

    /// <summary>
    /// 将当前运行项同步到 <see cref="_dockService"/> 运行时模型，并清理已退出的项。
    /// 使 IDockService 成为"当前可见运行项"的单一事实来源。
    /// </summary>
    private void SyncRunningIntoService(IReadOnlyList<DockItemData> running)
    {
        var runningIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in running)
        {
            runningIds.Add(item.Id.ToString());
            var existing = _dockService.Items.FirstOrDefault(i => i.Id == item.Id);
            if (existing is null)
            {
                try
                {
                    _dockService.AddItem(item);
                }
                catch
                {
                    // 重复保护：极端并发下可能已存在，忽略。
                }
            }
            else
            {
                _dockService.UpdateItem(item);
            }
        }

        foreach (var existing in _dockService.Items)
        {
            if (!runningIds.Contains(existing.Id.ToString()))
            {
                try
                {
                    _dockService.RemoveItem(existing.Id);
                }
                catch
                {
                    // 移除失败不阻断。
                }
            }
        }
    }

    /// <summary>
    /// 判断左右面板内容是否超出可视宽度，超出的面板套上"边缘渐隐"蒙层，
    /// 使滚动列表两端图标半隐，超出部分可横向滚轮查看。
    /// </summary>
    private void UpdateEdgeFade()
    {
        UpdateEdgeFadeFor(PinnedScrollViewer);
        UpdateEdgeFadeFor(RunningScrollViewer);
    }

    /// <summary>
    /// 面板重建（Children.Clear + 重加）后调用：布局尚未结算，ExtentWidth/HorizontalOffset 可能失准，
    /// 延迟到下一布局帧再计算蒙层，避免"到头仍被半隐"的误判。
    /// </summary>
    private void UpdateEdgeFadeDeferred()
    {
        Dispatcher.BeginInvoke(UpdateEdgeFade, DispatcherPriority.Loaded);
    }

    private void UpdateEdgeFadeFor(ScrollViewer viewer)
    {
        if (viewer is null || viewer.ActualWidth <= 0)
        {
            return;
        }

        var extent = viewer.ExtentWidth;
        var viewport = viewer.ViewportWidth;
        var offset = viewer.HorizontalOffset;

        // 内容未超出可视区时，不需要任何渐隐
        if (extent <= viewport + 0.5)
        {
            viewer.OpacityMask = null;
            return;
        }

        // 边缘阈值放宽到 1.5px，避免 DPI/子像素四舍五入导致"已滚到头却仍判定为未到头"，
        // 从而让首/末列持续半隐。
        const double edgeEps = 1.5;
        // 已滚动到左端：左边缘不再渐隐，首列完全可见（避免"列表到头仍被半隐"）。
        var atLeftEdge = offset <= edgeEps;
        // 已滚动到右端：右边缘不再渐隐，末列完全可见。
        var atRightEdge = (offset + viewport) >= (extent - edgeEps);

        // 水平线性渐变作为 OpacityMask 蒙在列表上；仅对未到达的端点做两端渐隐。
        // 渐隐带收窄到 3%（0.0–0.03 透明、0.03–0.05 透明→不透明），避免半个图标被吃。
        var mask = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0)
        };

        if (atLeftEdge)
        {
            mask.GradientStops.Add(new GradientStop(Colors.White, 0.0));
        }
        else
        {
            mask.GradientStops.Add(new GradientStop(Colors.Transparent, 0.0));
            mask.GradientStops.Add(new GradientStop(Colors.Transparent, 0.02));
            mask.GradientStops.Add(new GradientStop(Colors.White, 0.05));
        }

        if (atRightEdge)
        {
            mask.GradientStops.Add(new GradientStop(Colors.White, 1.0));
        }
        else
        {
            mask.GradientStops.Add(new GradientStop(Colors.White, 0.95));
            mask.GradientStops.Add(new GradientStop(Colors.Transparent, 0.98));
            mask.GradientStops.Add(new GradientStop(Colors.Transparent, 1.0));
        }

        mask.Freeze();
        viewer.OpacityMask = mask;
    }

    /// <summary>
    /// 滚轮横向滚动（列表为横向布局，默认垂直滚轮无效，这里映射为横向滚动）。
    /// </summary>
    private void OnHorizontalWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer viewer)
        {
            return;
        }

        e.Handled = true;
        viewer.ScrollToHorizontalOffset(viewer.HorizontalOffset - e.Delta);
    }

    /// <summary>
    /// 打开某运行应用的多窗口缩略图预览（紧贴图标上方的小预览，参考 cairoshell）。
    /// </summary>
    // 预览悬停开/关计时器，对齐 cairoshell 的 thumbTimer / closeThumbTimer：
    // - 打开延迟用系统 MouseHoverTime（更顺滑）；
    // - 关闭延迟：鼠标离开图标或预览窗后稍候，若仍不在任一之上才真正关闭，保证能停留并点击。
    private DispatcherTimer? _previewOpenTimer;
    private DispatcherTimer? _previewCloseTimer;
    private DockItemData? _pendingPreviewItem;
    private Point? _previewAnchorScreen;

    private static TimeSpan PreviewOpenDelay => SystemParameters.MouseHoverTime;

    private void ScheduleOpenPreview(DockItemData item, Point anchorScreen)
    {
        _pendingPreviewItem = item;
        _previewAnchorScreen = anchorScreen;
        DebugLog.Trace("Preview", $"hover item={item.Name} exe={item.TargetPath ?? item.ShortcutPath}");
        // 进入图标：取消正在进行的关闭延迟（鼠标回来了），但绝不取消打开计时器。
        _previewCloseTimer?.Stop();
        _previewCloseTimer = null;
        _previewOpenTimer?.Stop();
        _previewOpenTimer = new DispatcherTimer { Interval = PreviewOpenDelay };
        _previewOpenTimer.Tick += (_, _) =>
        {
            _previewOpenTimer?.Stop();
            _previewOpenTimer = null;
            if (_pendingPreviewItem is { } pending && _previewAnchorScreen is { } a)
            {
                OpenRunningPreview(pending, a);
            }
        };
        _previewOpenTimer.Start();
    }

    /// <summary>
    /// 计算 Dock 项容器在屏幕上的真实左上角坐标（对齐 cairoshell GetThumbnailAnchor）：
    /// 容器相对 dock 窗口客户区坐标 + dock 窗口屏幕 Top/Left。
    /// 不依赖子元素 PointToScreen（分层窗下偏差），确保预览窗定位准确。
    /// </summary>
    private Point GetItemScreenAnchor(Grid container)
    {
        try
        {
            var transform = container.TransformToAncestor(this);
            var rel = transform.Transform(new Point(0, 0));
            return new Point(rel.X + Left, rel.Y + Top);
        }
        catch
        {
            return new Point(Left, Top);
        }
    }

    private void ScheduleClosePreview()
    {
        // 鼠标离开图标：仅启动「关闭延迟」；绝不取消打开计时器（否则预览永远打不开）。
        // 预览窗定位在图标上方，鼠标必须先离开图标才能移入预览窗；若此处 kill 打开计时器，
        // 则打开延迟内一离开图标就被取消，预览永不出现（对齐 cairoshell 的 closeThumbTimer 不碰 thumbTimer）。
        ScheduleDeferClose();
    }

    /// <summary>
    /// 读取缩略图质量设置（system.thumbnailQuality，缺省 medium）供预览窗使用。
    /// 原逻辑自 DockThumbWindow 移入：ThumbnailWindow 已通用化、不依赖设置服务，由调用方读取后传档位。
    /// </summary>
    private ThumbnailQuality GetPreviewThumbnailQuality()
    {
        var qualitySetting = _settings?.Get("system.thumbnailQuality", "medium") ?? "medium";
        return qualitySetting switch
        {
            "low" => ThumbnailQuality.Low,
            "high" => ThumbnailQuality.High,
            _ => ThumbnailQuality.Medium
        };
    }

    private void OpenRunningPreview(DockItemData item, Point anchorScreen)
    {
        var exe = !string.IsNullOrWhiteSpace(item.TargetPath) ? item.TargetPath : item.ShortcutPath;
        if (string.IsNullOrWhiteSpace(exe))
        {
            return;
        }

        var windows = RunningAppDetector.GetRunningWindows()
            .Where(w => string.Equals(w.ExePath, exe, StringComparison.OrdinalIgnoreCase))
            .ToList();
        DebugLog.Trace("Preview", $"open exe={exe} matched={windows.Count}");
        if (windows.Count == 0)
        {
            return;
        }

        CloseFlyout();
        // 预览窗自 shell-window-tracker 通用化（原 DockThumbWindow）；缩略图质量由本窗从设置读取后传入。
        _flyout = new ThumbnailWindow(anchorScreen, windows, GetPreviewThumbnailQuality());
        // 鼠标离开预览层时请求关闭；关闭延迟内若鼠标回到图标则取消（见 ScheduleDeferClose 的 IsMouseOver 判定）。
        _flyout.PreviewMouseLeft += (_, _) => ScheduleDeferClose();
        _flyout.Show();
        DebugLog.Trace("Preview", $"shown flyout handles={windows.Count}");
    }

    // 预览关闭延迟：鼠标离开图标或预览窗后稍候，若焦点不在图标/预览任一之上才真正关。
    private void ScheduleDeferClose()
    {
        _previewCloseTimer?.Stop();
        _previewCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _previewCloseTimer.Tick += (_, _) =>
        {
            _previewCloseTimer?.Stop();
            _previewCloseTimer = null;
            // 鼠标仍在预览窗上则不关闭（对齐 cairoshell 的 !IsMouseOver 判定）。
            // 图标本身的 hover 会重新触发 ScheduleOpenPreview（已 stop 本关闭计时器），无需在此判断。
            if (_flyout is { IsMouseOver: true })
            {
                return;
            }
            CloseFlyout();
        };
        _previewCloseTimer.Start();
    }

    /// <summary>
    /// 直接激活该应用的最前运行窗口（左键点击运行项时调用）。
    /// EnumWindows 按 Z 序从顶到底返回（MSDN/实测），第一个匹配的可见未最小化窗口即最前窗口，
    /// 避免取到底层窗口导致"点了唤不出来"（explorer 场景 z-order 最底曾是 Progman 桌面宿主，
    /// 已在 GetRunningWindows 排除；LastOrDefault 选最底窗口属注释性错误，实测回归）。
    /// </summary>
    private void ActivateFirstWindow(DockItemData item)
    {
        var exe = !string.IsNullOrWhiteSpace(item.TargetPath) ? item.TargetPath : item.ShortcutPath;
        if (string.IsNullOrWhiteSpace(exe))
        {
            return;
        }

        var matches = RunningAppDetector.GetRunningWindows()
            .Where(w => string.Equals(w.ExePath, exe, StringComparison.OrdinalIgnoreCase))
            .ToList();
        DebugLog.Trace("Activate", $"click exe={exe} matched={matches.Count}");
        if (matches.Count == 0)
        {
            // 窗口已全部关闭（枚举与点击间有时间差）：有真实 exe 时直接启动兜底。
            if (File.Exists(exe))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
                }
                catch
                {
                    // 启动失败静默
                }
            }
            return;
        }

        // EnumWindows 顶到底：First = Z 序最前的可见未最小化窗口；全最小化则取最前（第一个）窗口还原。
        var best = matches.FirstOrDefault(w => IsVisibleAndNotMinimized(w.Hwnd));
        if (best.Hwnd == IntPtr.Zero)
        {
            best = matches[0];
        }

        if (best.Hwnd != IntPtr.Zero)
        {
            // ⚠️ 不得在 MouseLeftButtonUp 处理中同步激活：此时鼠标仍被本线程捕获，
            // SetForegroundWindow 会被 Windows 静默拒绝（实测"matched=1 但窗口没反应"根因）。
            // 延迟到消息队列空闲（capture 已释放）再激活。
            Dispatcher.BeginInvoke(
                () => RunningAppDetector.ActivateWindow(best.Hwnd),
                System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private static bool IsVisibleAndNotMinimized(IntPtr hwnd)
    {
        try
        {
            if (!RunningAppDetector.IsWindowVisible(hwnd))
            {
                return false;
            }

            RunningAppDetector.GetWindowPlacement(hwnd, out var placement);
            // SW_SHOWMINIMIZED = 2
            return placement.showCmd != 2;
        }
        catch
        {
            return false;
        }
    }

    private void CloseFlyout()
    {
        _flyout?.Close();
        _flyout = null;
    }

    /// <summary>
    /// 为 Dock 项统一挂载交互：悬停放大上浮、点击回弹、左键/右键动作。
    /// 使用持久 TransformGroup（Scale + Translate），避免每次 MouseEnter 重建 RenderTransform 导致的动画错位。
    /// </summary>
    private void AttachItemInteractions(
        Grid container,
        Image iconImage,
        Action onClick,
        Action? onRightClick = null,
        Action? onHover = null)
    {
        var scale = new ScaleTransform(1, 1);
        var translate = new TranslateTransform(0, 0);
        iconImage.RenderTransform = new TransformGroup
        {
            Children = { scale, translate }
        };
        iconImage.RenderTransformOrigin = new Point(0.5, 0.5);

        container.Cursor = System.Windows.Input.Cursors.Hand;

        container.MouseEnter += (_, _) =>
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 1.2, TimeSpan.FromMilliseconds(200)));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 1.2, TimeSpan.FromMilliseconds(200)));
            translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -8, TimeSpan.FromMilliseconds(200)));
            onHover?.Invoke();
        };

        container.MouseLeave += (_, _) =>
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.2, 1, TimeSpan.FromMilliseconds(200)));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.2, 1, TimeSpan.FromMilliseconds(200)));
            translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-8, 0, TimeSpan.FromMilliseconds(200)));
        };

        container.MouseLeftButtonUp += (_, _) =>
        {
            // 拖拽重排进行中（_suppressClick）跳过点击启动，避免拖完误触发 LaunchApp。
            if (_suppressClick) return;
            // 点击回弹：缩到 0.9 再弹回，提供明确的按下反馈。
            var bx = new DoubleAnimation(1.2, 0.9, TimeSpan.FromMilliseconds(90)) { AutoReverse = true };
            var by = new DoubleAnimation(1.2, 0.9, TimeSpan.FromMilliseconds(90)) { AutoReverse = true };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, bx);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, by);
            onClick();
        };

        if (onRightClick is not null)
        {
            container.MouseRightButtonUp += (_, e) => onRightClick();
        }
    }

    private async System.Threading.Tasks.Task LoadIconAsync(DockItemData item, Image target)
    {
        try
        {
            var icon = await _dockIconService.GetIconAsync(item);
            if (icon is not null)
            {
                target.Source = icon;
            }
        }
        catch
        {
            // 图标失败不阻断主流程。
        }
    }

    private static ImageSource CreatePlaceholderIcon()
    {
        var visual = new DrawingVisual();
        using var ctx = visual.RenderOpen();
        ctx.DrawRectangle(Brushes.DarkGray, null, new Rect(0, 0, 36, 36));

        var bitmap = new RenderTargetBitmap(36, 36, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        SyncAppBarPosition();
        UpdateEdgeFade();
    }

    private void PositionToBottomCenter()
    {
        // 多显示器策略：all/independent 时在"所有显示器"各放一个 Dock（每屏底部居中）；
        // primary 仅在主屏。当前 Dock 实例只负责所在屏（host 决定在哪些屏启动），
        // 定位统一走 _layout 提供的屏幕底部居中换算，不再硬编码 PrimaryScreen。
        // 当前实例绑定到主屏（多屏多实例由 host 后续扩展）。
        // 坐标参考域（2026-09-02 修复）：⚠️ SystemParameters.PrimaryScreen* 返回的域与窗口 WPF 逻辑域
        // （PerMonitorV2 按窗口所在屏 DPI）不一致时，会把 dock 定位到工作区之外（实测 125% 屏上
        // 窗口被放到物理 1679px，而工作区底只有 1380px → AppBar 协商负高度 W=769 H=-299）。
        // 改为以 GetMonitorInfo 物理工作区为权威源，÷TransformToDevice 换算成 WPF 逻辑坐标。
        var hwnd = _appBarHwndSource?.Handle ?? new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var transform = _appBarHwndSource?.CompositionTarget?.TransformToDevice ?? default;
        var dpiScale = transform.M11 > 0 ? transform.M11 : 1.0;
        Rect screen;
        // 优先用注册前缓存的工作区（不含 dock 自己）——实时 GetMonitorWorkArea 在 dock 注册后
        // 已含自身抬升，会触发"协商→抬升→再定位"循环（dock 被一路抬到屏幕顶，实测回归）。
        var cachedWork = _appBarWorkArea;
        if (cachedWork.Right - cachedWork.Left > 0 && cachedWork.Bottom - cachedWork.Top > 0)
        {
            screen = new Rect(cachedWork.Left / dpiScale, cachedWork.Top / dpiScale,
                (cachedWork.Right - cachedWork.Left) / dpiScale, (cachedWork.Bottom - cachedWork.Top) / dpiScale);
        }
        else if (DockAppBarReservation.GetMonitorWorkArea(hwnd, out var work))
        {
            screen = new Rect(work.Left / dpiScale, work.Top / dpiScale,
                (work.Right - work.Left) / dpiScale, (work.Bottom - work.Top) / dpiScale);
        }
        else
        {
            screen = new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
        }
        // SizeToContent=Width 下 Width 不一定反映真实渲染宽，用 ActualWidth/ActualHeight（带 fallback 到设置值）。
        var dockW = ActualWidth > 0 ? ActualWidth : Width;
        var dockH = ActualHeight > 0 ? ActualHeight : Height;

        // 限宽：内容总宽可能超过屏幕（pinned+running 几十图标 + 倒影 + 名称），让 WPF 把窗口宽限制在屏幕 92% 内，
        // 多出的部分由 ScrollViewer 启用水平滚动条承载，避免 dock 起点算出负值左溢出屏幕。
        // MaxHeight 同样限制到屏幕 50%（倒影+名称不应挤掉桌面大半），超出时由内容自然撑高但保证可视。
        var maxW = Math.Max(200, screen.Width * 0.92);
        var maxH = Math.Max(120, screen.Height * 0.5);
        if (MaxWidth > maxW) MaxWidth = maxW;
        if (MaxHeight > maxH) MaxHeight = maxH;

        // 限宽后再次读取 ActualWidth（Window 会在下一布局 pass 应用 MaxWidth 后重算）
        if (ActualWidth > 0) dockW = Math.Min(ActualWidth, maxW);
        if (ActualHeight > 0) dockH = Math.Min(ActualHeight, maxH);

        var (left, top) = _layout.BottomCenterForScreen(screen, dockW, dockH);
        Left = left;
        Top = top;
        DebugLog.Trace("Dock", $"PositionToBottomCenter: L={Left:F0} T={Top:F0} W={dockW:F0} H={dockH:F0} visible={IsVisible} topmost={Topmost}");
    }
}
