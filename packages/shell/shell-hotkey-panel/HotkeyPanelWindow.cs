using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.Core.Hotkeys;
using BetterDesktop.Shell.Core.Native;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.Core.Windowing;
using BetterDesktop.Shell.Core.Windows;
using BetterDesktop.Shell.Hotkeys.Contracts;
using BetterDesktop.Shell.Settings.Contracts;

namespace BetterDesktop.Shell.HotkeyPanel;

/// <summary>
/// 热键侧板：宿主常驻透明窗（热键计划 P5 §10 界面分工）。
/// <para>
/// 侧板只做**显示与隐藏管理**（用户裁定"侧板改键反人类"）：只读态 = 完全点击穿透
/// （<c>ClickThroughWindow</c>），实时显示当前可用热键表（冲突/被接管置顶高亮）；
/// 按住右 Alt 并把鼠标指向侧板（<c>RightAltGate</c>，AltGr 防护内建）进入可操作态——
/// 行内隐藏 / 底部"已忽略 N 项 · 管理"恢复，以及"打开热键设置"入口（改键在设置中心"热键"分节）。
/// <para>
/// 【2026-09-16 关键行为】可操作态**不是**"整窗解除穿透"：只有鼠标位于卡片上时才不穿透，
/// 鼠标一移开立即恢复穿透 —— 侧板因此永不遮挡菜单栏下拉/其它浮层（真机复测"用 Alt 操作侧板后
/// 菜单栏点不动"的根因修复；窗口矩形实测 425×962 物理像素、可操作态整窗不穿透时右侧七成高度被吞点击）。
/// </para>
/// </para>
/// <para>数据刷新：500ms DispatcherTimer 轮询注册表纯读接口（Changed 在实现类、跨包订阅需 IEventBus 桥接，
/// 轮询是零跨包事件的等价实现；侧板自身操作后立即刷新不等轮询）。</para>
/// </summary>
public sealed class HotkeyPanelWindow : PopupWindowBase
{
    private const double PanelWidth = 340;
    private const double PanelRightMargin = 8; // 留一点缝（贴死屏幕边缘时视觉上"被切掉"）

    /// <summary>Ctrl 虚拟键码（AltGr 防护判定用）。</summary>
    private const int VkControl = 0x11;

    /// <summary>可操作态空闲多久自动回只读（防"忘了退出"长期挡在屏幕上）。</summary>
    private const double InteractiveIdleSeconds = 15;

    /// <summary>进入可操作态所需的"按住门控键 + 鼠标悬停卡片"持续时间（毫秒）。</summary>
    private const double GateHoldMs = 120;

    private const string PositionXKey = "hotkeys-panel.x";
    private const string PositionYKey = "hotkeys-panel.y";

    private readonly IHotkeyRegistryService _registry;
    private readonly ISettingsService? _settings;
    private readonly ISettingsWindowService? _settingsWindow; // "打开热键设置"入口（缺失则隐藏）
    private readonly HotkeyScopeTracker _tracker;
    private readonly DispatcherTimer _timer;     // 500ms：数据刷新 + 可操作态空闲超时
    private readonly DispatcherTimer _pollTimer; // 50ms：门控轮询（取代 WH_MOUSE_LL / WH_KEYBOARD_LL）
    private readonly HashSet<string> _declared = new(StringComparer.Ordinal);
    private readonly double _listMaxHeight; // 列表可视高度（超出走滚动，见 BuildContent）
    private readonly int _sceneRowLimit;    // 场景区块可显示行数（按窗口高度算，让应用功能键尽量一次看全）

    private StackPanel? _listHost;
    private ScrollViewer? _listScroller;
    private StackPanel? _sceneHost;
    private ForegroundAppWatcher? _sceneWatcher;
    private TextBlock? _footer;
    private TextBlock? _contextLine;
    private TextBlock? _openSettings;
    private TextBlock? _done;
    private TextBlock? _closePanel; // 可操作态"关闭侧板"（右键菜单的保险丝路径）
    private TextBlock? _systemHint;
    private readonly HotkeyPanelState _state = new(); // P2-3：交互状态机（纯模型，窗口只渲染）
    private string? _lastSnapshot; // P1-1：上次刷新快照（无变化不重建）
    private DateTime _lastInteractionUtc = DateTime.UtcNow;
    private bool? _passThrough; // 上次已应用的穿透态（幂等缓存）
    private DateTime? _gateHoldStartUtc; // 门控键按住 + 悬停卡片的起始时刻（持续到 GateHoldMs 才进可操作态）

    // 手动拖动（不用 DragMove）：起始屏幕物理坐标 + 起始窗口 DIP 位置
    private Point? _dragStartScreenPx;
    private Point _dragStartWindowDip;

    public HotkeyPanelWindow(
        IVibrancyService vibrancy,
        IAppearanceService? appearance,
        IHotkeyRegistryService registry,
        ISettingsService? settings,
        ISettingsWindowService? settingsWindow,
        HotkeyScopeTracker tracker)
        : base(vibrancy, appearance)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _settings = settings;
        _settingsWindow = settingsWindow;
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));

        Width = PanelWidth;

        // 【2026-09-16 P0 根因】窗口高度**贴合内容**，不再固定"屏高 85%"。
        // 真机实测（GetWindowRect）：旧实现窗口恒为 340×950 逻辑 / 425×1190 物理，而卡片内容常常
        // 只有一半高度 —— 只读态穿透所以看不出来，**可操作态不穿透，多出来的空白区会静默吃掉鼠标点击**：
        // 落在右侧区域的菜单栏下拉面板、其它浮层全部点不动，用户复测表述为"用 Alt 操作侧板后菜单栏用不了"。
        // 修法：SizeToContent 让窗口矩形 == 卡片矩形（空白区根本不属于窗口，点击直接落到下方）。
        SizeToContent = SizeToContent.Height;

        // 列表/场景的可见行上限改用**屏高**推导（此前由 Height 反推 → 与 SizeToContent 循环依赖）。
        var screenBudget = SystemParameters.WorkArea.Height * 0.6;
        _listMaxHeight = Math.Max(160, screenBudget);
        // 场景区块可用行数：留出标题 / 当前应用行 / 底部提示的高度，约每行 19px（11px 字 + 间距）。
        _sceneRowLimit = Math.Max(12, (int)(screenBudget / 19));
        // 保住卡片形态：内容再多也不超过屏高六成 + 固定块（超出部分由 ScrollViewer 承载）。
        MaxHeight = SystemParameters.WorkArea.Height * 0.85;
        Background = Brushes.Transparent;
        AllowsTransparency = true;

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        // P1-1：交互/管理中停表——整表重建会替换操作按钮（按下与抬起之间被换掉会丢点击）；
        // 退出交互时 Refresh 已显式触发。空闲超时判定必须放在停表判断**之前**（交互态本来就停表）。
        _timer.Tick += (_, _) =>
        {
            if (_state.IsInteractive
                && (DateTime.UtcNow - _lastInteractionUtc).TotalSeconds > InteractiveIdleSeconds)
            {
                // 【2026-09-16 行为修正】旧语义"松开右 Alt 立即回只读"使可操作态根本点不到按钮
                //（得一直按住右 Alt 才敢点，一松手按钮就没了）。改为：进入后保持，退出靠
                // "「完成」按钮 / 离开卡片后空闲 15 秒"两条路——穿透已随鼠标位置自动恢复，不依赖退出。
                SetReadOnly();
                return;
            }

            if (!_state.CanTimerRefresh)
            {
                return;
            }

            Refresh();
        };

        // 【2026-09-16 P0 根因修复：彻底移除全局钩子，改 50ms 轮询】
        // 用户复测"右 Alt 操作侧板后菜单栏无法触及"，两次修复（钩子异步化、禁 DragMove、Deactivated
        // 退出、位置钳制）都未根治 —— 真因是**低级钩子本身**：WH_MOUSE_LL / WH_KEYBOARD_LL 必须由
        // 安装线程（= UI 线程）处理回调，UI 线程只要忙于重建列表/布局，回调就无法及时返回，
        // Windows 会挂起**全局鼠标输入**直至超时（LowLevelHooksTimeout），甚至静默摘除钩子 ——
        // 表现就是"鼠标点不动任何东西、菜单栏无法触及"。
        // 结论：常驻浮层**不得**依赖低级钩子。改用轮询（GetAsyncKeyState + GetCursorPos）：
        // 纯"拉"模式，永不阻塞系统输入，也永不失效。开销 50ms 一次，可忽略。
        _pollTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };
        _pollTimer.Tick += (_, _) => PollGate();
    }

    /// <summary>
    /// 门控轮询（取代全局钩子）：
    /// ① 右 Alt 按住 + 左键点击落在卡片上 → 进可操作态；
    /// ② 可操作态下**鼠标位于卡片上才解除穿透**，鼠标一离开立即恢复穿透。
    /// <para>
    /// 【为什么是"悬停即生效"·2026-09-16 真机根因】此前"可操作态整窗不穿透"，而侧板窗口实测
    /// 425×962 物理像素（右侧整列七成高）——鼠标点向落在该矩形内的菜单栏下拉面板/其它浮层时，
    /// 点击被侧板静默吃掉，用户复测表述为"用 Alt 操作侧板后菜单栏点不动"。
    /// 穿透位只影响**点击**、不影响鼠标移动：因此"鼠标几何上位于卡片内就关穿透"既能点按钮，
    /// 又保证鼠标不在卡片上时侧板对全屏零交互影响（50ms 轮询 → 切换延迟 &lt;50ms，早于真人按下）。
    /// </para>
    /// </summary>
    private void PollGate()
    {
        if (!NativeMethods.GetCursorPos(out var pt))
        {
            return;
        }

        var onCard = CursorOnCard(pt);

        if (_state.IsInteractive)
        {
            SetPassThrough(!onCard); // 悬停即解除穿透；离开即恢复（永不遮挡菜单栏/其它浮层）
            if (onCard)
            {
                _lastInteractionUtc = DateTime.UtcNow; // 在卡片上操作：重置空闲计时
            }
            return;
        }

        bool altDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_RMENU) & NativeMethods.KeyStateDown) != 0;
        bool ctrlDown = (NativeMethods.GetAsyncKeyState(VkControl) & NativeMethods.KeyStateDown) != 0;
        if (!RightAltGate.ShouldSwitchToInteractive(altDown, ctrlDown, onCard, NativeMethods.VK_RMENU))
        {
            _gateHoldStartUtc = null;
            return;
        }

        // 【为什么是"持续按住"而不是"点击"】点击是瞬时事件：50ms 轮询下"逐帧比较 down 状态"会漏掉快击；
        // 而 GetAsyncKeyState 的低位标记在宿主内会被其它轮询消耗（真机两次复现都进不去可操作态）。
        // 改为检测**持续状态**（按住右 Alt + 鼠标悬在卡片上 ≥ GateHoldMs）→ 无漏检；仍然足够刻意
        //（打 @{} 的 AltGr、Alt+Tab 都不可能在侧板上悬停这么久）。
        _gateHoldStartUtc ??= DateTime.UtcNow;
        if ((DateTime.UtcNow - _gateHoldStartUtc.Value).TotalMilliseconds < GateHoldMs)
        {
            return;
        }

        _gateHoldStartUtc = null;
        if (_state.EnterInteractive())
        {
            EnterInteractive();
        }
    }

    /// <summary>进入可操作态：解除穿透 + 刷新（**不激活窗口**——不抢前台、不动菜单栏/输入法状态）。</summary>
    private void EnterInteractive()
    {
        _lastInteractionUtc = DateTime.UtcNow;
        SetPassThrough(false);
        Refresh();
    }

    /// <summary>鼠标（屏幕物理坐标）是否落在侧板卡片上。</summary>
    private bool CursorOnCard(NativeMethods.POINT pt)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            return false;
        }

        // 【不要做 DPI 换算】GetCursorPos 与 GetWindowRect 在本进程（DPI-aware）返回**同一物理坐标空间**。
        // 早期版本照搬低级钩子时代的"pt 可能是逻辑坐标"兜底，额外判了 pt×scale —— 125% 缩放下会把卡片
        // 左侧 400 多像素宽的区域(1708..2135)误判为"在卡片上"，于是鼠标移开也不恢复穿透（真机实测复现）。
        return pt.X >= rect.Left && pt.X <= rect.Right
            && pt.Y >= rect.Top && pt.Y <= rect.Bottom;
    }

    // ── 主题令牌取色（UI 纪律：界面颜色一律走 Application.Resources 令牌，禁止硬编码；
    //    令牌全集见 host/App.xaml，取色入口见 shell-core/Surface/ThemeBrushes） ──

    private static Brush ThemeForeground => ThemeBrushes.Get("ThemeForeground");
    private static Brush ThemeMuted => ThemeBrushes.Get("ThemeMutedForeground");
    private static Brush ThemeAccent => ThemeBrushes.Get("SkinAccentFromSkin");
    private static Brush ThemeSubtle => ThemeBrushes.Get("BorderStrokeSubtle");
    private static Brush ThemePanel => ThemeBrushes.Get("SettingsCardBackground");
    private static Brush ThemeFaint => ThemeBrushes.Tint("ThemeForeground", 0.5);
    private static Brush ThemeDanger => new SolidColorBrush(ThemeBrushes.DangerColor);
    private static Brush ThemeWarning => new SolidColorBrush(ThemeBrushes.WarningColor);

    protected override bool AutoHideOnOutsideClick => false; // 常驻：点侧板外不收起

    /// <summary>用户要求"窗口属性全透明"：不用主题半透明面板底（无深色框、无描边），文字悬浮桌面。</summary>
    protected override Brush? PanelBackgroundOverride => Brushes.Transparent;

    /// <summary>全透明浮层：不应用 DWM 毛玻璃材质（否则深色渐变底），窗口纯透明。</summary>
    protected override bool UseWindowMaterial => false;

    protected override FrameworkElement BuildContent()
    {
        // 极轻玻璃卡片底 + 1px 描边：纯透明时文字直接压在壁纸上，浅色壁纸下对比度不达 4.5:1（无障碍红线）。
        // 用令牌给的 5.5% 白底 + 8% 描边即可读，同时保留"通透"观感（窗口本身仍是透明分层窗口）。
        var root = new Border
        {
            Background = ThemePanel,
            BorderBrush = ThemeSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 10, 14, 10),
        };

        // 子树内所有按钮统一为胶囊 + hover/pressed 反馈（隐式样式，见 HotkeyPanelVisuals）
        root.Resources.Add(typeof(Button), HotkeyPanelVisuals.PillButtonStyle);

        // 【2026-09-17 用户需求】右键菜单：给侧板一个"启动 / 关闭"的快捷入口。
        // 只读态整块穿透（右键会落到下方应用）→ 本菜单只在**可操作态**可达，与行内「隐藏」同一前提。
        root.ContextMenu = BuildPanelContextMenu();

        var panel = new StackPanel
        {
            // 有底之后不再需要"描边式"重阴影，只留极轻一层（苹果式 whisper-light 阴影）
            Effect = new DropShadowEffect
            {
                BlurRadius = 2,
                ShadowDepth = 0,
                Color = Colors.Black,
                Opacity = 0.28,
            },
        };
        root.Child = panel;

        // 标题行 + 门控提示（标题区兼作可操作态的拖动手柄：位置会记忆，避免挡住用户视线）
        var title = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Cursor = Cursors.SizeAll,
            ToolTip = "可操作态下拖这里移动侧板（位置会记住）",
        };
        title.Children.Add(new TextBlock
        {
            Text = "热键",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeForeground,
        });
        title.Children.Add(new TextBlock
        {
            Text = "  按住右 Alt 指向此处可管理",
            FontSize = 10,
            Foreground = ThemeMuted,
            VerticalAlignment = VerticalAlignment.Center,
        });
        // 【2026-09-16 P0 修复】**手动拖动，不用 DragMove**：DragMove 对 WS_EX_NOACTIVATE 的
        // 分层窗口会进入系统移动循环 + SetCapture；若循环中途样式被改（本窗口正好会切样式），
        // 异常退出会让**鼠标捕获残留在侧板上** → 之后所有点击（菜单栏/桌面/应用）都被定向到侧板，
        // 真机表现就是"菜单栏点不动"。手动拖动全程可控、可兜底释放捕获。
        title.MouseLeftButtonDown += (_, e) =>
        {
            if (!_state.IsInteractive)
            {
                return; // 只读态是穿透的，收不到鼠标
            }
            _dragStartScreenPx = PointToScreen(e.GetPosition(this));
            _dragStartWindowDip = new Point(Left, Top);
            _ = title.CaptureMouse();
            e.Handled = true;
        };
        title.MouseMove += (_, e) =>
        {
            if (_dragStartScreenPx is null || e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }
            var nowPx = PointToScreen(e.GetPosition(this)); // 物理像素（WPF PointToScreen 恒物理）
            var dpi = VisualTreeHelper.GetDpi(this);
            var scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
            var scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;
            Left = _dragStartWindowDip.X + (nowPx.X - _dragStartScreenPx.Value.X) / scaleX;
            Top = _dragStartWindowDip.Y + (nowPx.Y - _dragStartScreenPx.Value.Y) / scaleY;
            ClampToWorkArea(); // 不许拖到菜单栏/任务栏的工作区之外（否则侧板会盖住它们吃点击）
        };
        title.MouseLeftButtonUp += (_, _) =>
        {
            title.ReleaseMouseCapture();
            _dragStartScreenPx = null;
            SavePosition();
        };
        title.LostMouseCapture += (_, _) =>
        {
            _dragStartScreenPx = null; // 系统夺走捕获时也要收敛状态（否则下次 MouseMove 会跳位）
        };
        panel.Children.Add(title);

        _contextLine = new TextBlock
        {
            Text = "当前无界面上下文，仅全局键可用",
            FontSize = 10,
            Foreground = ThemeMuted,
            Margin = new Thickness(0, 6, 0, 0),
        };
        panel.Children.Add(_contextLine);

        // ── 场景区块（置顶）──
        // 用户需求："打开 Blender 后显示的功能按键，这些需要优先显示"（按场景把当前应用的功能键排前）。
        // 前台应用命中「应用热键目录」时，先列该应用的功能键，再列通用热键；未收录则不占位置。
        _sceneHost = new StackPanel { Visibility = Visibility.Collapsed };
        panel.Children.Add(_sceneHost);

        _listHost = new StackPanel();
        // 列表放 ScrollViewer：热键条数（自家 + 冲突 + 管理视图）会超出窗口高度。
        // 只读态是点击穿透的，滚轮会落到下方应用；**可操作态**才可滚动查看全部（用户预期）。
        _listScroller = new ScrollViewer
        {
            Content = _listHost,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = _listMaxHeight,
        };
        panel.Children.Add(_listScroller);

        _footer = new TextBlock
        {
            Text = string.Empty,
            FontSize = 10,
            Foreground = ThemeMuted,
            Margin = new Thickness(0, 6, 0, 0),
            Cursor = Cursors.Hand,
        };
        // P0-3：底部"已忽略 N 项 · 管理"入口 → 内联展开隐藏项 + 恢复（绝不静默丢失）
        _footer.MouseLeftButtonDown += (_, _) =>
        {
            if (_footer.Visibility != Visibility.Visible)
            {
                return;
            }

            _state.ToggleManage();
            Refresh();
        };
        panel.Children.Add(_footer);

        // 改键入口（改键在设置中心"热键"分节，侧板只做显示与隐藏管理）：
        // 可操作态显示"打开热键设置 →"；settings 窗口服务缺失（M10 降级）则隐藏。
        _openSettings = new TextBlock
        {
            Text = "改键请打开热键设置 →",
            FontSize = 10,
            Foreground = ThemeAccent,
            Margin = new Thickness(0, 8, 0, 0),
            Cursor = Cursors.Hand,
            Visibility = Visibility.Collapsed,
            ToolTip = "在设置窗口的「热键」分节修改热键（正常窗口，键盘输入可用）",
        };
        _openSettings.MouseLeftButtonDown += (_, _) => _settingsWindow?.ShowSection("热键");
        panel.Children.Add(_openSettings);

        // 可操作态"完成"出口（点面板外/空闲 30 秒同样会退出）
        _done = new TextBlock
        {
            Text = "完成",
            FontSize = 10,
            Foreground = ThemeMuted,
            Margin = new Thickness(0, 6, 0, 0),
            Cursor = Cursors.Hand,
            Visibility = Visibility.Collapsed,
        };
        _done.MouseLeftButtonDown += (_, _) => SetReadOnly();
        panel.Children.Add(_done);

        // 【2026-09-17】关闭侧板（可操作态可见）：右键菜单万一不可达时的**保险丝**——
        // 侧板是本机常驻浮层，用户必须有一条不依赖菜单、不依赖热键的关闭路径。
        _closePanel = new TextBlock
        {
            Text = "关闭侧板（Ctrl+Alt+H 可再打开）",
            FontSize = 10,
            Foreground = ThemeFaint,
            Margin = new Thickness(0, 6, 0, 0),
            Cursor = Cursors.Hand,
            Visibility = Visibility.Collapsed,
            ToolTip = "收起侧板（不再常驻）；随时按热键或用右键菜单里的「显示热键侧板」再打开",
        };
        _closePanel.MouseLeftButtonDown += (_, _) => ClosePanel();
        panel.Children.Add(_closePanel);

        // 系统热键已从侧板移出（20+ 行会淹没真正可管理的项）→ 指向设置中心全量表
        _systemHint = new TextBlock
        {
            Text = "系统热键（Win+E 等）见「设置中心 › 热键」",
            FontSize = 10,
            Foreground = ThemeFaint,
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        panel.Children.Add(_systemHint);

        return root;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        RestorePosition();

        SetPassThrough(true); // 只读态常驻穿透
        _pollTimer.Start(); // 门控轮询（取代全局钩子；见构造函数注释）
        _timer.Start();
        SyncDeclaredEntries();
        Refresh();

        // SizeToContent 的高度要等首次布局才算出来 → 布局完成后再按真实高度钳制一次（否则可能压到屏幕下缘）；
        // 之后内容增减导致窗口尺寸变化时同样要保持在工作区内。
        SizeChanged += (_, _) => ClampToWorkArea();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ClampToWorkArea));

        // 场景感知：监听前台应用变化（500ms 轮询，不引入钩子），命中目录即置顶显示其功能键
        _sceneWatcher = new ForegroundAppWatcher();
        _sceneWatcher.Changed += name => Dispatcher.InvokeAsync(() => RenderScene(name));
        _sceneWatcher.Start();
    }

    /// <summary>
    /// 场景区块渲染：当前前台应用命中「应用热键目录」时，把它的功能键**排在最上面**
    /// （用户需求："打开 Blender 后显示的功能按键，这些需要优先显示"）。
    /// <para>只读态是穿透的、滚轮落到下方应用，所以这里只放前 <see cref="SceneRowLimit"/> 行；
    /// 完整清单在设置中心「应用热键」卡里看。</para>
    /// </summary>
    private void RenderScene(string? processName)
    {
        if (_sceneHost is null)
        {
            return;
        }

        _sceneHost.Children.Clear();
        var profile = AppHotkeyCatalog.Match(processName);
        if (profile is null)
        {
            _sceneHost.Visibility = Visibility.Collapsed;
            ApplySceneLayout(hasScene: false);
            return;
        }

        _sceneHost.Visibility = Visibility.Visible;
        ApplySceneLayout(hasScene: true);
        _sceneHost.Children.Add(new TextBlock
        {
            Text = $"▶ {profile.App}（当前应用 · {profile.Count} 个功能键）",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeAccent,
            Margin = new Thickness(0, 2, 0, 2),
            TextWrapping = TextWrapping.Wrap,
        });

        int shown = 0;
        foreach (var group in profile.Groups)
        {
            if (shown >= _sceneRowLimit)
            {
                break;
            }

            _sceneHost.Children.Add(new TextBlock
            {
                Text = group.Title,
                FontSize = 10,
                Foreground = ThemeMuted,
                Margin = new Thickness(0, 6, 0, 1),
            });

            foreach (var item in group.Items)
            {
                if (shown >= _sceneRowLimit)
                {
                    break;
                }
                _sceneHost.Children.Add(SceneRow(item));
                shown++;
            }
        }

        if (profile.Count > shown)
        {
            _sceneHost.Children.Add(new TextBlock
            {
                Text = $"…另有 {profile.Count - shown} 项，见设置中心「热键 › 应用热键」",
                FontSize = 10,
                Foreground = ThemeMuted,
                Margin = new Thickness(0, 4, 0, 0),
            });
        }
        else if (!_state.IsInteractive)
        {
            _sceneHost.Children.Add(new TextBlock
            {
                Text = "通用热键已暂时收起（按住右 Alt 指向侧板即可查看并可滚动）",
                FontSize = 10,
                Foreground = ThemeMuted,
                Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        // 与下方通用热键表分隔
        _sceneHost.Children.Add(new Border
        {
            Height = 1,
            Background = ThemeSubtle,
            Margin = new Thickness(0, 8, 0, 4),
        });
    }

    /// <summary>
    /// 场景区块与通用热键表的取舍：**只读态滚不动**（穿透），所以命中场景时把通用表暂时收起，
    /// 把侧板高度整块让给"当前应用的功能键"（用户诉求：场景键优先，且要能看全）；
    /// **可操作态**恢复通用表（那里可以滚动）。
    /// </summary>
    private void ApplySceneLayout(bool hasScene)
    {
        if (_listScroller is not null)
        {
            _listScroller.Visibility = hasScene && !_state.IsInteractive
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
    }

    private static FrameworkElement SceneRow(AppHotkey item)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        line.Children.Add(new TextBlock
        {
            Text = item.Chord,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeForeground,
            Width = 150,
        });
        line.Children.Add(new TextBlock
        {
            Text = item.Description,
            FontSize = 11,
            Foreground = ThemeForeground,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = item.Description,
        });
        return line;
    }

    /// <summary>位置：优先用上次拖动记忆的位置，否则主屏右侧中部；并钳制在工作区内（防换分辨率后跑出屏）。</summary>
    private void RestorePosition()
    {
        var wa = SystemParameters.WorkArea;
        double left = wa.Right - Width - PanelRightMargin;
        // SizeToContent 下此刻 Height 尚未确定（NaN）→ 先用估计值落位，布局完成后再按真实高度钳制一次。
        var estimated = double.IsNaN(Height) || Height <= 0
            ? Math.Min(600, wa.Height * 0.5)
            : Height;
        double top = wa.Top + (wa.Height - estimated) / 2;

        var sx = _settings?.Get(PositionXKey, string.Empty);
        var sy = _settings?.Get(PositionYKey, string.Empty);
        if (double.TryParse(sx, NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            && double.TryParse(sy, NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
        {
            left = x;
            top = y;
        }

        Left = left;
        Top = top;
        ClampToWorkArea();
    }

    /// <summary>记忆拖动后的位置（跨会话保留，避免每次开机又挡住同一处）。</summary>
    private void SavePosition()
    {
        _settings?.Set(PositionXKey, Left.ToString("0", CultureInfo.InvariantCulture));
        _settings?.Set(PositionYKey, Top.ToString("0", CultureInfo.InvariantCulture));
    }

    /// <summary>插件 Load 时在窗口创建后调用：声明全部既有热键。</summary>
    public void LoadDeclarations()
    {
        SyncDeclaredEntries();
        Refresh();
    }

    // ---- 侧板显隐（2026-09-17 用户需求：启动 / 关闭这个面板；三条入口同源，见 HotkeyPanelSettings） ----

    /// <summary>
    /// 收起侧板。对外入口：右键菜单 / 底部文字入口 / 全局热键 / 设置中心勾选框——都先落
    /// <see cref="HotkeyPanelSettings"/> 的显隐意图，本方法只负责把窗口收起来（幂等）。
    /// </summary>
    public void HidePanel()
    {
        _pollTimer.Stop(); // 不在屏幕上就不再 50ms 轮询门控（省一个常驻定时器）
        _timer.Stop();
        HidePopup();       // 基类：退作用域 + 摘外点钩子 + Hide（本身幂等）
        DiagnosticLog.Trace("hotkeys-panel", "侧板已关闭（可用切换热键或设置中心「热键」再打开）");
    }

    /// <summary>重新显示侧板（恢复记忆位置，并回到只读穿透态）。幂等。</summary>
    public void ShowPanel()
    {
        _state.ExitToReadOnly(); // 若关闭时正处可操作态，重开先回只读（否则会带着"不穿透"复活）
        RestorePosition();
        ShowAt(new Point(Left, Top));
        SetPassThrough(true);
        _pollTimer.Start();
        _timer.Start();
        Refresh();
        DiagnosticLog.Trace("hotkeys-panel", "侧板已显示");
    }

    /// <summary>关闭侧板：先落显隐意图（持久化、跨重启保持），再直接收起（不等事件回环；插件侧应用是幂等的）。</summary>
    private void ClosePanel()
    {
        HotkeyPanelSettings.SetEnabled(_settings, false);
        HidePanel();
    }

    /// <summary>
    /// 侧板右键菜单（**只在可操作态可达**：只读态整块点击穿透，右键会落到下方应用）：
    /// ①「显示热键侧板」勾选项 = 显隐开关本体（勾 = 正在显示，取消勾选 → 关闭），右侧显示当前切换热键；
    /// ②「打开热键设置」= 改键 / 停用入口（与底部"改键请打开热键设置"同一入口）。
    /// </summary>
    private ContextMenu BuildPanelContextMenu()
    {
        var menu = new ContextMenu();
        menu.SetResourceReference(Control.BackgroundProperty, "PopupBackground");
        menu.SetResourceReference(Control.BorderBrushProperty, "PopupBorder");
        menu.SetResourceReference(Control.ForegroundProperty, "ThemeForeground");

        var toggle = new MenuItem
        {
            Header = "显示热键侧板",
            IsCheckable = true,
            IsChecked = true, // 菜单只在侧板可见时弹得出来 → 恒为勾选态
            InputGestureText = CurrentToggleChord(),
        };
        toggle.Click += (_, _) => ClosePanel();
        menu.Items.Add(toggle);

        if (_settingsWindow is not null)
        {
            menu.Items.Add(new Separator());
            var open = new MenuItem { Header = "打开热键设置" };
            open.Click += (_, _) => _settingsWindow.ShowSection("热键");
            menu.Items.Add(open);
        }

        return menu;
    }

    /// <summary>当前切换热键键位（用户改过键就显示改后的；注册表里没有该条时回默认）。</summary>
    private string CurrentToggleChord()
        => _registry.GetAll()
               .FirstOrDefault(v => string.Equals(v.Binding.Id, HotkeyPanelSettings.ToggleHotkeyId, StringComparison.Ordinal))
               ?.Binding.Chord.Spec
           ?? HotkeyPanelSettings.ToggleHotkeyDefault;

    private void SyncDeclaredEntries()
    {
        // 声明清单是"当前应声明"的目标；与已声明集合做差集/交集
        var target = HotkeyDeclarations.Build(_settings)
            .ToDictionary(b => b.Id, StringComparer.Ordinal);

        foreach (var id in _declared.ToList())
        {
            if (!target.ContainsKey(id))
            {
                _registry.Unregister(id); // 配置被清空（paste-back）→ 注销声明
                _declared.Remove(id);
            }
        }

        foreach (var (id, binding) in target)
        {
            if (!_declared.Contains(id))
            {
                var r = _registry.Declare(binding);
                if (r.Ok)
                {
                    _declared.Add(id);
                }
            }
            else
            {
                // P1-2：声明条目已存在但键位变化（设置中心改配置键后重启）→ Rebind 同步。
                // Rebind 对声明条目不落 chord 持久化（配置键为唯一真相源），仅更新内存 + 清残留覆盖。
                var current = _registry.GetAll().FirstOrDefault(v => v.Binding.Id == id);
                if (current is not null
                    && !string.Equals(current.Binding.Chord.Spec, binding.Chord.Spec, StringComparison.Ordinal))
                {
                    _registry.Rebind(id, binding.Chord);
                }
            }
        }
    }

    private void Refresh()
    {
        if (_listHost is null || _footer is null || _contextLine is null)
        {
            return;
        }

        // 同一绑定可能同时 OsConflict + Shadowed（同 Id 两条冲突）→ GroupBy 防 ToDictionary 抛
        var conflicts = _registry.GetConflicts()
            .GroupBy(c => c.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var all = _registry.GetAll();
        var hidden = HotkeyListModel.HiddenCount(all);

        // 管理浮层（P0-3）：已忽略项列表 + 恢复入口，绝不静默丢失
        List<HotkeyRow> rows;
        if (_state.Mode == HotkeyPanelMode.Manage)
        {
            if (hidden == 0)
            {
                _state.LeaveManage(); // 已全部恢复，自动退出管理
            }
            else
            {
                rows = HotkeyListModel.BuildManageRows(all, conflicts);
                RebuildList(rows, manage: true);
                _footer.Text = $"已忽略 {hidden} 项 · 点击行右端恢复";
                _footer.Visibility = Visibility.Visible;
                return;
            }
        }

        // P0-1：只读态按上下文过滤（GetActive），可操作态列出全部条目（含非活跃作用域，灰显+徽标）——
        // 否则"已接线的管理能力"会被过滤逻辑吃掉。
        // 系统热键：只读态显示常用子集（用户要"按得动、有功能"的热键——Windows 全局热键正合）；
        // 可操作态不列出（系统热键不可管理：改键/隐藏无意义，避免行数把 400px 侧板撑爆）。
        if (_state.ShowsAllRows)
        {
            // 可操作态 = **全量**（自家 + 冲突 + 全部系统热键）：高度自适应 + ScrollViewer 滚动，
            // 用户要求"显示范围有限就用滚动看更多"——只读态是穿透的滚不动，所以全量放这里。
            rows = HotkeyListModel.BuildAllRows(_registry.GetAll(), conflicts, _tracker.Current);
            _contextLine.Text = "可操作态：滚轮查看全部；鼠标移开即穿透；「完成」或空闲 15 秒退出";
        }
        else
        {
            // 只读态：自家热键（含冲突项）+ 系统热键常用子集——穿透态滚不动，只放最常用的一批。
            rows = HotkeyListModel.BuildRows(SidebarRows(_registry.GetActive()), conflicts);
            var hasSurface = _tracker.Current.Any(s => s.StartsWith("Surface.", StringComparison.Ordinal));
            _contextLine.Text = hasSurface
                ? string.Empty
                : "当前无界面上下文，仅全局键可用";
        }

        // 改键入口（设置中心"热键"分节）：仅可操作态且服务可用时显示
        if (_openSettings is not null)
        {
            _openSettings.Visibility = _settingsWindow is not null && _state.IsInteractive
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        if (_done is not null)
        {
            _done.Visibility = _state.IsInteractive ? Visibility.Visible : Visibility.Collapsed;
        }

        if (_closePanel is not null)
        {
            _closePanel.Visibility = _state.IsInteractive ? Visibility.Visible : Visibility.Collapsed;
        }

        if (_systemHint is not null)
        {
            _systemHint.Visibility = _state.IsInteractive ? Visibility.Collapsed : Visibility.Visible;
        }

        // 场景区块与通用表的布局随「只读 / 可操作」切换（可操作态恢复通用表，因为那里能滚动）
        ApplySceneLayout(_sceneHost?.Visibility == Visibility.Visible);

        var footerText = hidden > 0 ? $"已忽略 {hidden} 项 · 管理" : "全部热键正常";
        var contextText = _contextLine.Text;

        // P1-1：快照比对——数据/状态无变化时不重建（防无谓整表重建抖动）
        var snapshot = string.Join("\n", rows.Select(r =>
            r.Id + "|" + r.ChordText + "|" + r.Conflict + "|" + r.InactiveScope + "|" + r.Enabled + "|" + r.OwnerAlive))
            + "\n" + footerText + "\n" + contextText
            + "\nMode:" + _state.Mode
            + "\nOpenSettings:" + (_openSettings?.Visibility ?? Visibility.Collapsed);
        if (_lastSnapshot == snapshot)
        {
            return;
        }

        _lastSnapshot = snapshot;
        RebuildList(rows);
        _footer.Text = footerText;
        _footer.Visibility = hidden > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>侧板**只读态**可见行过滤：自家热键全列 + 系统热键只取常用子集（穿透态滚不动，
    /// 全量留给可操作态）。</summary>
    private static IReadOnlyList<HotkeyView> SidebarRows(IReadOnlyList<HotkeyView> all)
        => all.Where(v => !HotkeyDeclarations.IsSystem(v.Binding.Id)
            || HotkeyDeclarations.SystemSidebarIds.Contains(v.Binding.Id)).ToList();

    private void RebuildList(List<HotkeyRow> rows, bool manage = false)
    {
        if (_listHost is null)
        {
            return;
        }

        _listHost.Children.Clear();
        if (rows.Count == 0)
        {
            _listHost.Children.Add(new TextBlock
            {
                Text = manage ? "（没有已忽略的热键）" : "（当前无可用热键）",
                FontSize = 11,
                Foreground = ThemeFaint,
                Margin = new Thickness(0, 4, 0, 0),
            });
            return;
        }

        foreach (var row in rows)
        {
            _listHost.Children.Add(manage ? BuildManageRow(row) : BuildRow(row));
        }
    }

    /// <summary>管理浮层行（P0-3）：描述 + 恢复按钮（隐藏项可找回，绝不静默丢失）。</summary>
    private FrameworkElement BuildManageRow(HotkeyRow row)
    {
        var host = new StackPanel { Margin = new Thickness(0, 5, 0, 0) };

        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(new TextBlock
        {
            Text = row.ChordText,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeMuted,
            Width = 150,
        });
        line.Children.Add(new TextBlock
        {
            Text = row.Description,
            FontSize = 11,
            Foreground = ThemeMuted,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        host.Children.Add(line);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(150, 2, 0, 0),
        };
        actions.Children.Add(ActionButton("恢复显示", () =>
        {
            _registry.SetVisible(row.Id, true);
            Refresh();
        }));
        host.Children.Add(actions);

        return host;
    }

    private FrameworkElement BuildRow(HotkeyRow row)
    {
        var host = new StackPanel { Margin = new Thickness(0, 5, 0, 0) };

        // 非活跃作用域条目：整体灰显 + 作用域徽标（P0-1 可操作态全量列表）
        var dimmed = row.InactiveScope;
        var dimBrush = ThemeFaint;

        var line = new StackPanel { Orientation = Orientation.Horizontal };
        var chord = new TextBlock
        {
            Text = row.ChordText,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = row.Conflict ? ThemeDanger : dimmed ? dimBrush : ThemeForeground,
            Width = 150,
        };
        line.Children.Add(chord);

        var desc = new TextBlock
        {
            Text = row.Description,
            FontSize = 11,
            Foreground = dimmed ? dimBrush : ThemeForeground,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = row.Description, // 窄侧板必然截断长描述 → 悬停看全文（保整齐不丢信息）
        };
        line.Children.Add(desc);

        if (row.ScopeBadge is not null)
        {
            line.Children.Add(new TextBlock
            {
                Text = "· " + row.ScopeBadge + " 未开",
                FontSize = 10,
                Foreground = dimBrush,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "该键仅在对应界面打开时生效，当前未打开（可在此改键/停用，生效于下次打开）",
            });
        }

        if (row.Owner.Length > 0)
        {
            line.Children.Add(new TextBlock
            {
                Text = row.Owner == HotkeyDeclarations.SystemOwner ? " 系统" : " " + row.Owner,
                FontSize = 10,
                Foreground = dimBrush,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        // P1-4：owner 进程未运行（如 capture/engine 没起来）→ 行上标"未运行"（活性真相，不假装可用）
        if (!row.OwnerAlive)
        {
            line.Children.Add(new TextBlock
            {
                Text = " · 未运行",
                FontSize = 10,
                Foreground = ThemeWarning,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "该热键由外部进程注册，当前进程未运行——键此刻不会生效",
            });
        }

        host.Children.Add(line);

        if (row.Conflict && !string.IsNullOrWhiteSpace(row.ConflictReason))
        {
            host.Children.Add(new TextBlock
            {
                Text = row.ConflictReason,
                FontSize = 10,
                Foreground = ThemeDanger,
                Margin = new Thickness(150, 0, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        // 可操作态：操作按钮行（隐藏；改键已移出侧板 → 设置中心"热键"分节）
        if (_state.IsInteractive)
        {
            host.Children.Add(BuildActions(row));
        }

        return host;
    }

    private UIElement BuildActions(HotkeyRow row)
    {
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(150, 2, 0, 0),
        };

        // 侧板只做显示与隐藏管理：唯一行内操作是"隐藏"（可恢复，见底部"已忽略 N 项 · 管理"）；
        // 改键/停用/启用移出侧板（用户裁定"侧板改键反人类"）→ 设置中心"热键"分节。
        actions.Children.Add(ActionButton("隐藏", () => { _registry.SetVisible(row.Id, false); Refresh(); }));

        return actions;
    }

    private static Button ActionButton(string text, Action onClick)
    {
        var b = new Button
        {
            Content = text,
            FontSize = 10,
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(0, 0, 6, 0),
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            Foreground = ThemeForeground,
            BorderBrush = ThemeSubtle,
            BorderThickness = new Thickness(1), // 描边给出"可点击"的视觉暗示（纯文字按钮难以识别）
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    // ---- 两态切换 ----

    /// <summary>
    /// 设置点击穿透（幂等）。两态都**保留 NOACTIVATE**：侧板永不激活 →
    /// 不抢前台、不切换输入法、不动菜单栏状态。
    /// </summary>
    private void SetPassThrough(bool passThrough)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || _passThrough == passThrough)
        {
            return; // 句柄未就绪时**不写缓存**，否则句柄创建后会因"值没变"而被跳过、穿透从未真正应用
        }

        _passThrough = passThrough;
        ClickThroughWindow.SetClickThrough(hwnd, passThrough, keepNoActivate: true);
    }

    private void SetReadOnly()
    {
        _state.ExitToReadOnly();
        ReleaseOwnCapture();
        SetPassThrough(true);
        // 不在"用户点击那一刻"同步重建整棵列表（这一击往往正是落向菜单栏/其它浮层的点击，
        // UI 线程被抢会让系统看起来"点不动"）→ 置空快照，交给下个 tick（≤500ms）惰性重绘。
        _lastSnapshot = null;
    }

    /// <summary>
    /// 只释放**本窗口自己的**鼠标捕获。
    /// <para>【为什么不是 <c>Mouse.Capture(null)</c>】那是**线程级**粗暴释放——宿主菜单栏等其它窗口
    /// 若正用捕获跟踪一次点击序列（按下 → 抬起判定），会被一并打断，表现就是"点了菜单栏没反应"。
    /// 这里仅在自己（或自己的可视子树）确实持有捕获时释放。</para>
    /// </summary>
    private void ReleaseOwnCapture()
    {
        _dragStartScreenPx = null;
        if (Mouse.Captured is DependencyObject captured
            && (ReferenceEquals(captured, this) || IsAncestorOf(captured)))
        {
            Mouse.Capture(null);
        }
    }

    /// <summary>把窗口钳制进工作区（<see cref="SystemParameters.WorkArea"/> 已排除菜单栏/任务栏等 AppBar）。</summary>
    private void ClampToWorkArea()
    {
        var wa = SystemParameters.WorkArea;
        Left = Math.Clamp(Left, wa.Left, Math.Max(wa.Left, wa.Right - Width));
        Top = Math.Clamp(Top, wa.Top, Math.Max(wa.Top, wa.Bottom - Height));
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _pollTimer.Stop();
        _sceneWatcher?.Dispose();
        base.OnClosed(e);
    }
}
