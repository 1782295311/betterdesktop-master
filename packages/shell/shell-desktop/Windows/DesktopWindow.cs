// BetterDesktop.Shell.Desktop — 自绘桌面窗口（透明"文件显示器"，嵌入 explorer 桌面）
//
// 【方案对齐 cairoshell（TECH-KNOWLEDGE/63-桌面渲染/desktop-progman-embed.md 生死线 #1）】
//   1) 桌面窗口本质 = 透明"文件显示器"：非 shell 模式**不画壁纸**（背景 #01000000 alpha=1 近透明），
//      壁纸永远由 explorer 原生桌面（Progman）渲染。自画壁纸是错误方案（重复渲染、换壁纸不同步、
//      丢失壁纸引擎兼容）——本窗口不读注册表、不自绘壁纸。
//   2) 嵌入桌面窗口树——WS_CHILD + SetParent 到 **SHELLDLL_DefView（原生图标视图窗口）**，
//      与 ManagedShell GetLowestDesktopChildHwnd 反编译实现一致（挂 DefView 而非 Progman！
//      挂 Progman 直接子级会被壁纸引擎 DComp 层压住不上屏）。失联看门狗 3 秒自动重挂。
//   3) 禁止 Maximized——手动 SetWindowPos 铺 VirtualScreen（高度 -1 防 ABN_FULLSCREENAPP）。
//   4) explorer 原生图标的隐藏/恢复由 DesktopPlugin 负责（SetNativeIconsVisible：自绘=ShowWindow、原生=SetParent 摘除法）。
//
// 【分层透明（ULW）上屏验证】
//   历史上曾因"分层窗口跨进程挂 Progman 后 PrintWindow 有图但屏幕不上屏"而禁用透明并自画壁纸。
//   该结论针对 **Progman 挂载**（红线 #9 实证：壁纸引擎 DComp 层压住 Progman 直接子级）；
//   本窗口挂载点已是 **DefView**（红线 #6/#9 正解），分层透明不再有该合成坑。
//   真机验收必须用 CopyFromScreen（PrintWindow 有图 ≠ 屏幕可见）。
//
// 【ShellWindow 基类适配】
//   UseSkinBackground=false —— 皮肤背景会顶掉透明（壁纸消失）；
//   ApplyWindowMaterial 置空 —— 透明文件显示器无需 DWM blur（套 blur 会糊掉壁纸）；
//   AllowsTransparencyDefault=true（基类默认，不 override）—— 透出壁纸必需；
//   DefaultTopmost=false —— 桌面不置顶；DefaultResizeMode=NoResize —— 不可拖拽。

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.ContextMenus.Contracts;
using BetterDesktop.Shell.ContextMenus.Services;
using BetterDesktop.Shell.Core;
using BetterDesktop.Shell.Core.Native;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.Desktop.Contracts;
using BetterDesktop.Shell.Desktop.Controls;
using BetterDesktop.Shell.Settings.Contracts;

namespace BetterDesktop.Shell.Desktop.Windows;

// ── 本文件方法级白话索引（桌面宿主窗口，白话 → 方法）──
//   "把自绘窗口嵌入 explorer 桌面"     → TryEmbedDesktop（找宿主 FindDesktopHostWindow/DesktopHostWindow.IsHostClass、填虚拟屏 FillVirtualScreen、排挤出 Peek ExcludeFromPeek）
//   "嵌入态校验/看门狗（防脱钩）"      → VerifyEmbedding / StartEmbedWatchdog
//   "隐藏/恢复 explorer 原生图标"      → ToggleIconsHidden / ApplyIconsHidden / UpdateIconsReserve
//   "壁纸遮挡（弹菜单时遮壁纸操作）"   → SetWallpaperOcclusionImpl
//   "窗口消息处理（吞 WM_CONTEXTMENU 防双菜单）" → DesktopWndProc
//   "窗口置底"                        → SendToBottom；桌面空白双击 OnWindowBlankDoubleClick
//   图标画布本体在 Controls/DesktopIconsControl.cs；右键路由见 shell-context-menu。
// ────────────────────────────────────

/// <summary>自绘桌面窗口（透明图标显示层，嵌入 explorer 桌面；壁纸由系统渲染）。</summary>
internal sealed class DesktopWindow : ShellWindow
{
    /// <summary>菜单栏条带高度（逻辑像素，图标网格避开此区域）。</summary>
    private const double MenuBarSafeTop = 24;

    private const int HwndBottom = 1; // HWND_BOTTOM
    private static readonly IntPtr HwndTop = IntPtr.Zero; // HWND_TOP
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoZorder = 0x0004;

    private const int GwlStyle = -16;
    private const int WsChild = 0x40000000;
    private const int WsOverlapped = 0x00000000;

    private const int SmXvirtualscreen = 76;
    private const int SmYvirtualscreen = 77;

    private const uint SpiGetdeskwallpaper = 0x0073;
    private Brush? _transparentBackground;
    private const int SmCxvirtualscreen = 78;
    private const int SmCyvirtualscreen = 79;

    [DllImport("user32.dll")]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);




    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);



    private const int SmCyscreen = 1; // SM_CYSCREEN：主屏物理像素高度

    private readonly IDesktopBrowser _browser;
    private readonly ISettingsService? _settings;
    private readonly BetterDesktop.Shell.Convert.Contracts.IConvertMenuService? _convertMenu;
    private readonly BetterDesktop.Shell.Convert.Contracts.IArchiveService? _archive;
    private readonly Func<BetterDesktop.Shell.Clipboard.Contracts.IClipboardService?>? _clipboardFactory;
    private readonly IEventBus? _events;
    private IDisposable? _settingsSub;
    private DesktopIconsControl? _icons;
    private bool _embedded;

    public DesktopWindow(
        IDesktopBrowser browser,
        IVibrancyService vibrancy,
        IAppearanceService? appearance,
        ISettingsService? settings = null,
        BetterDesktop.Shell.Convert.Contracts.IConvertMenuService? convertMenu = null,
        BetterDesktop.Shell.Convert.Contracts.IArchiveService? archive = null,
        IEventBus? events = null,
        Func<BetterDesktop.Shell.Clipboard.Contracts.IClipboardService?>? clipboardFactory = null)
        : base(appearance, vibrancy)
    {
        _browser = browser;
        _settings = settings;
        _convertMenu = convertMenu;
        _archive = archive;
        _clipboardFactory = clipboardFactory;
        _events = events;
        Events = events;

        Title = "BetterDesktop.Desktop";
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowState = WindowState.Normal; // 禁止 Maximized：手动铺 VirtualScreen（防 ABN_FULLSCREENAPP）

        // 透明文件显示器：背景 #01000000（alpha=1 近透明，透出 explorer 壁纸）。
        // 不自绘壁纸（生死线 #1）——壁纸由 explorer 原生桌面渲染，本窗口只画图标层。
        // 基类 ShellWindow 构造已默认设该背景；此处显式重申并注明不变量，防止后续误改。
        // _transparentBackground 供菜单期间的壁纸遮挡恢复用（恢复=回透明）。
        _transparentBackground = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        Background = _transparentBackground;

        // 图标网格：瀑布列（先填列后换列），避开顶部菜单栏条带 + 底部 dock/原生任务栏
        _icons = new DesktopIconsControl(_browser, _settings, _convertMenu, _archive, _events, _clipboardFactory);
        UpdateIconsReserve();
        ApplyIconsHidden();

        // 双击空白处 → 切换隐藏桌面图标。两条触发路径按布局模式互补：
        //   自动排列：WrapPanel 空白不吞事件，冒泡到窗口层（图标 cell 的按下已 Handled，不会误触发）；
        //   自由布局：空白按下被框选逻辑 Handled，由 DesktopIconsControl.BlankAreaDoubleClick 上报。
        // 【2026-09-11 二次修复 · "双击太敏感"】判定依据由「事件源是否属于图标 cell」改为
        //   「落点几何是否命中图标格」：cell 宽 = CellWidth-6，列间天然留 6px 空隙，格子边缘也有
        //   padding——原事件源判定在这些位置上判成"空白"，于是用户**双击图标启动应用**被当成
        //   双击桌面空白 → 图标全被隐藏（用户实测"太敏感"）。
        //   三条触发路径（自由布局画布 / 自动排列冒泡 / Preview 隧道）现在统一过同一个闸门。
        _icons.BlankAreaDoubleClick += (_, p) => TryToggleOnBlankDoubleClick("自由布局空白", p);
        MouseLeftButtonDown += OnWindowBlankDoubleClick;
        // 【2026-09-11 修复】双击切换统一走窗口层 Preview 隧道（隧道先于 ScrollViewer/Canvas 处理，
        // 任何位置空白都收得到——自由布局 canvas 只有图标区大小 420x1064，canvas 之外桌面空白
        // 左键会被 ScrollViewer 吞掉冒泡，此前判定范围太小；隐藏态 Content=null 后内部链全断）。
        // 显示态：落点在图标上**既不切换也不 mark Handled**（Handled 会让 cell 收不到事件，
        // "双击图标=打开文件"会一起失效）；隐藏态：Content=null 无 cell，任何位置双击都恢复。
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            // 【2026-09-11】精确 ==2：仅双击触发。三击（ClickCount=3）曾触发二次切换 = 图标闪回。
            if (e.ClickCount != 2) return;
            if (!TryToggleOnBlankDoubleClick("Preview隧道", e.GetPosition(_icons)))
            {
                return; // 落点在图标上：放行给 cell
            }
            e.Handled = true;
        };
        // 【2026-09-07 诊断】窗口层左键按下
        MouseLeftButtonDown += (_, e) =>
            DiagnosticLog.Trace("shell.desktop",
                $"窗口层 左键按下: pos={e.GetPosition(this)}");

        // 根 Border：满足基类 ChromeBorder 约定（DEBUG 断言强制，未设置会 FailFast）。
        // 附带收益：字号缩放（ApplyFontScale）与主题前景传导经此 Border 生效。
        var root = new Border { Background = Brushes.Transparent, Child = _icons };
        Content = root;
        ChromeBorder = root;

        // 右键诊断埋点（tunneling 首站）：确认右键消息到达自绘窗口（未到=被 explorer 层截走）。
        PreviewMouseRightButtonUp += (_, e) =>
            DiagnosticLog.Trace("shell.desktop",
                $"窗口层右键 up pos={e.GetPosition(this)} source={e.OriginalSource.GetType().Name}");


        // ★ 原生菜单抑制生死线（2026-09-02 截图实证）：本窗口 WS_CHILD 嵌入 explorer 桌面。
        //   右键已由 WPF 层接管并弹菜单（桌面图标/空白 → DesktopIconsControl.ShowMenu 统一自绘
        //   DesktopMenuPopup；跨进程 DefView 转发已 2026-09-10 移除），但 WPF 不吞 WM_CONTEXTMENU——
        //   DefWindowProc 会把未处理的 WM_CONTEXTMENU 转发给父窗口（explorer 桌面）→
        //   原生右键菜单与我们的菜单**同时弹出并存**（截图实证）。
        //   故在 hwnd hook 一律吞掉 WM_CONTEXTMENU（覆盖整棵子窗口树的转发链）。
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero && HwndSource.FromHwnd(hwnd) is { } src)
            {
                src.AddHook(DesktopWndProc);
            }
        };

        // 设置变化（dock 尺寸滑块/组件开关）→ 重算底部避让（违规1修复：裸 event → IEventBus）
        if (_settings is not null && _events is not null)
        {
            _settingsSub = _events.On<SettingsChangedEventArgs>(
                ShellEvents.SettingsChanged,
                (e, _) =>
                {
                    OnSettingsChanged(e);
                    return Task.CompletedTask;
                });
            Closed += (_, _) => _settingsSub?.Dispose();
        }
    }

    /// <summary>
    /// 图标网格避让：顶部菜单栏（desktop.reserveMenuBar）+ 底部原生任务栏
    /// （desktop.reserveTaskbar，Shell_TrayWnd 实际可见高度）。dock 为浮动条，不占用桌面基准。
    /// </summary>
    private void UpdateIconsReserve()
    {
        if (_icons is null)
        {
            return;
        }

        double bottomReserve = 0;

        // 底部基准 = 原生任务栏（Shell_TrayWnd）的实际可见高度——它是唯一【恒久】占底部的部件。
        // dock 是浮动条（空闲/全屏自动隐藏、用到时才出现），【不参与】抬高桌面基准：
        // 此前按 dock 高度估算（≈98 DIP，大于 dock 实际 footprint 且恒久生效），是
        // "桌面基准被抬高"的根因（用户 2026-09-02 定稿：让出高度以任务栏为基准，而非 dock）。
        // dock 显示时浮在图标之上（平时非置顶，不挡窗口层操作），隐藏时零占用。
        // 原生任务栏被 components.wintaskbar=false 隐藏时 IsWindowVisible=false → 基准自动归 0。
        var taskbarReserveOn = _settings?.Get("desktop.reserveTaskbar", true) ?? true;
        if (taskbarReserveOn)
        {
            var tray = NativeMethods.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null);
            if (tray != IntPtr.Zero && NativeMethods.IsWindowVisible(tray) && NativeMethods.GetWindowRect(tray, out NativeMethods.RECT rc))
            {
                var trayPhysical = rc.Bottom - rc.Top;
                if (trayPhysical > 0)
                {
                    // GetWindowRect 是物理像素，图标 Margin 是 DIP：
                    // dpiScale = 主屏物理高(SM_CYSCREEN) / SystemParameters.PrimaryScreenHeight(DIP)。
                    var screenPhysical = GetSystemMetrics(SmCyscreen);
                    var dpiScale = screenPhysical / SystemParameters.PrimaryScreenHeight;
                    if (dpiScale > 0)
                    {
                        bottomReserve = Math.Max(bottomReserve, trayPhysical / dpiScale);
                    }
                }
            }
        }

        var topReserve = (_settings?.Get("desktop.reserveMenuBar", true) ?? true)
            ? MenuBarSafeTop + 13
            : 13;

        _icons.Margin = new Thickness(7, topReserve, 0, bottomReserve);
    }

    private void OnSettingsChanged(SettingsChangedEventArgs e)
    {
        // 拖动重排落位时会写 desktop.iconPositions，但拖动过程已直接更新了 UI，
        // 此处再 Rebuild 只会造成闪烁与拖动态丢失，故跳过该键。
        if (e.Key == "desktop.iconPositions")
        {
            return;
        }

        var isDockLayout = e.Key.StartsWith("dock.", StringComparison.Ordinal) ||
                           e.Key is "components.dock" or "components.wintaskbar";
        var isDesktop = e.Key.StartsWith("desktop.", StringComparison.Ordinal);
        if (!isDockLayout && !isDesktop)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            UpdateIconsReserve();
            // 【回归修复 2026-09-06】iconsHidden 键走 ApplyIconsHidden（内部按需 Rebuild/清空）；
            // 其他 desktop 键直接 Rebuild——避免 ApplyIconsHidden 无条件 Rebuild 与后续 Rebuild
            // 叠加，导致拖动/设置变更时频繁重建（抽搐）。
            if (e.Key == "desktop.iconsHidden")
            {
                ApplyIconsHidden();
            }
            else if (isDesktop)
            {
                _icons?.Rebuild();
            }
        }));
    }

    /// <summary>切换「隐藏桌面图标」（desktop.iconsHidden，持久化）——仅自绘模式路径。
    /// ⚠️ 只动自绘网格这一层：explorer 原生图标层由 DesktopPlugin 幂等管理（iconsHidden 驱动 + 退出兜底恢复），
    /// 两条线各自独立、绝不交叉操作——若在这里再去 Show/Hide 原生 SysListView32，
    /// 任何状态漂移都会造成两层图标同时可见/互相错位（"打架"根源）。
    /// 原生桌面模式（components.desktop=false）的另一条路径在 DesktopPlugin 的 WH_MOUSE_LL 钩子，
    /// 两者共享同一意图键但各管各的层，靠模式与类名过滤天然互斥。
    /// 窗口本体保持可见可交互：藏的只是图标网格，恢复通道（再次双击）永远在本窗口上；
    /// 若 Hide() 整个窗口，第二次双击会落进 explorer DefView，自绘层就再也收不回来了。</summary>
    private void ToggleIconsHidden(string origin)
    {
        // 2026-09-10 开关：desktop.doubleClickHideIcons=false 时双击空白不切换（默认 true 保持原行为）。
        if (!(_settings?.Get("desktop.doubleClickHideIcons", true) ?? true))
        {
            return;
        }

        var hidden = _settings?.Get("desktop.iconsHidden", false) ?? false;
        // 【2026-09-11】日志补来源：原实现无论哪条路径都打"双击桌面空白"，导致"点图标被误判"
        // 这类问题在日志里完全无法自证（今天排查时就被这条误导过）。
        DiagnosticLog.Trace("shell.desktop",
            $"双击切换图标显隐（来源={origin}）：desktop.iconsHidden {hidden} → {!hidden}");
        _settings?.Set("desktop.iconsHidden", !hidden);
    }

    /// <summary>应用桌面图标网格可见性（desktop.iconsHidden；启动与设置变更两条入口共用）。
    /// 自绘模式下原生图标层由本插件恒隐藏，网格即桌面图标 UI：hidden → 只藏网格，
    /// 窗口本体保持可见可交互（恢复通道=再次双击）。</summary>
    private void ApplyIconsHidden()
    {
        if (_icons is null)
        {
            return;
        }

        var hidden = _settings?.Get("desktop.iconsHidden", false) ?? false;
        // 【回归修复 2026-09-06】隐藏图标 ≠ 禁用右键：Collapsed 吞事件导致隐藏后空白右键无菜单。
        // 隐藏 → 直接清空 Content（保留控件交互：空白右键=背景菜单、双击=恢复通道）；
        // 显示 → Rebuild 重建网格。只在 iconsHidden 变更时被调用（见 OnSettingsChanged），
        // 避免与其他设置变更的 Rebuild 叠加导致频繁重建（抽搐）。
        _icons.Visibility = Visibility.Visible;
        if (hidden)
        {
            _icons.Content = null;
        }
        else
        {
            _icons.Rebuild();
        }
    }

    /// <summary>落点（DesktopIconsControl 坐标系）是否在图标上。
    /// 【2026-09-11 二次修复】改为**几何判定**（按每个 cell 的实际矩形，含容差），
    /// 原实现按事件源是否属于某个 cell 判定，在"格边缘/列间 6px 空隙"上会漏判成空白。
    /// 隐藏态（网格 Content=null）恒为 false = 空白（任何双击都是恢复通道）。</summary>
    private bool IsOverIcon(Point pInIcons)
    {
        if (_settings?.Get("desktop.iconsHidden", false) ?? false)
        {
            return false;
        }

        return _icons is not null && _icons.IsPointOverIcon(pInIcons);
    }

    /// <summary>统一的"双击空白"闸门：仅当**落点确实在空白处**才切换，返回是否已切换。
    /// 三条触发路径（自由布局画布 / 自动排列冒泡 / Preview 隧道）全部经此判定，
    /// 任何一条路径单独漏判都会重现"双击图标启动应用 → 图标全被隐藏"。</summary>
    private bool TryToggleOnBlankDoubleClick(string origin, Point pInIcons)
    {
        if (IsOverIcon(pInIcons))
        {
            // 这条日志是"双击太敏感"类问题的自证依据：能看到「点图标 → 已放行」
            DiagnosticLog.Trace("shell.desktop",
                $"双击落在图标上：不切换（来源={origin}，落点={pInIcons.X:F0},{pInIcons.Y:F0}）");
            return false;
        }

        ToggleIconsHidden(origin);
        return true;
    }

    /// <summary>窗口层空白双击（自动排列路径：WrapPanel 空白不吞事件冒泡到此）。
    /// 图标上的双击（打开文件）在 cell 层已标记 Handled，不会到达这里；此处仍过同一闸门（双保险）。</summary>
    private void OnWindowBlankDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // 【2026-09-11】精确 ==2：三击（ClickCount=3）不重复切换（曾导致图标闪回）。
        if (e.ClickCount == 2)
        {
            _ = TryToggleOnBlankDoubleClick("窗口冒泡", e.GetPosition(_icons));
        }
    }

    // ======== ShellWindow 基类行为重写（见文件头说明） ========

    /// <inheritdoc />
    protected override bool DefaultTopmost => false;

    /// <inheritdoc />
    protected override ResizeMode DefaultResizeMode => ResizeMode.NoResize;

    /// <inheritdoc />
    protected override bool UseSkinBackground => false;

    /// <summary>透明文件显示器无需 DWM blur（套 blur 会糊掉壁纸）。</summary>
    protected override void ApplyWindowMaterial()
    {
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        if (_settings is not null)
        {
        }

        base.OnClosed(e);
    }

    // ======== 嵌入 explorer 桌面（cairoshell DesktopManager.ConfigureDesktop 同款） ========

    /// <inheritdoc />
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 默认：嵌入模式（挂 SHELLDLL_DefView，cairoshell 正解——见 FindDesktopHostWindow）。
        // 【2026-09-06 方案 A（沉壁纸 WorkerW）真机否决已回退：自绘层收不到鼠标输入，
        //   自由挪动/拖拽失效——自绘桌面必须保持全交互。】
        // BETTERDESKTOP_DESKTOP_TOPLEVEL=1 可强制顶层 HWND_BOTTOM 模式（cairoshell 降级分支）。
        if (Environment.GetEnvironmentVariable("BETTERDESKTOP_DESKTOP_TOPLEVEL") == "1")
        {
            _embedded = false;
            SendToBottom();
        }
        else
        {
            _embedded = TryEmbedDesktop();
            if (!_embedded)
            {
                SendToBottom(); // 挂载失败降级：顶层 + 失焦回底
            }
            StartEmbedWatchdog(); // 失联自动重挂（壁纸引擎重建桌面结构等场景）
        }

        ExcludeFromPeek(); // 对齐 cairo HideWindowFromTasks：Peek/Win+Tab 不显示桌面窗口
    }

    /// <summary>DWM Peek 排除（cairo HideWindowFromTasks 的 DWM 部分；TOOLWINDOW 已由 ShowInTaskbar=false 提供）。</summary>
    private void ExcludeFromPeek()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            int attr = 1;
            _ = NativeMethods.DwmSetWindowAttribute(hwnd, DwmwaExcludedFromPeek, ref attr, sizeof(int));
        }
        catch
        {
            // 老系统无该属性：忽略
        }
    }

    private const int DwmwaExcludedFromPeek = 12;

    /// <summary>窗口铺满虚拟屏（高度 -1 防 ABN_FULLSCREENAPP，cairoshell setSize 同款）。</summary>
    private void FillVirtualScreen(IntPtr insertAfter)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        int x = GetSystemMetrics(SmXvirtualscreen);
        int y = GetSystemMetrics(SmYvirtualscreen);
        int w = GetSystemMetrics(SmCxvirtualscreen);
        int h = GetSystemMetrics(SmCyvirtualscreen) - 1;
        _ = NativeMethods.SetWindowPos(hwnd, insertAfter, x, y, w, h, SwpNoActivate);
    }

    /// <summary>
    /// 嵌入 explorer 桌面：WS_CHILD + SetParent 到「含 SHELLDLL_DefView 的窗口」（cairoshell
    /// DesktopManager.ConfigureDesktop 同款）——自绘层与原生图标同层且提层 HWND_TOP，
    /// 保留自绘网格的全部交互（自由挪动/拖拽/右键委托）。兼容壁纸软件（WorkerW 变体）。
    /// 失败返回 false（调用方降级）。幂等：重复调用无害。
    /// 【2026-09-06 方案 A（沉壁纸 WorkerW 层）真机否决：自绘层收不到任何鼠标输入，
    /// 自由挪动/拖拽全部失效——已回退，保留显示与可见性修复。】
    /// </summary>
    private bool TryEmbedDesktop()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
            {
                DiagnosticLog.Trace("shell.desktop", "嵌入失败：hwnd 为空");
                return false;
            }

            var host = FindDesktopHostWindow();
            if (host == IntPtr.Zero)
            {
                DiagnosticLog.Trace("shell.desktop", "嵌入失败：未找到桌面宿主（Progman/DefView）");
                return false;
            }

            // WPF 窗口 → 子窗口样式（对齐 cairoshell ConfigureDesktop）。
            // 步骤日志：跨进程 SetParent 存在输入队列同步（冻结曾在 12:47 会话停在嵌入前的
            // 某一步无从定位），每步落点便于下次直接读日志定位。
            DiagnosticLog.Trace("shell.desktop", $"嵌入：host=0x{host:X} SetWindowLong/SetParent 中");
            int style = NativeMethods.GetWindowLong(hwnd, GwlStyle);
            _ = NativeMethods.SetWindowLong(hwnd, GwlStyle, (style | WsChild) & ~WsOverlapped);
            _ = SetParent(hwnd, host);

            // ★ 右键/命中生死线：SetParent 后必须把窗口提到宿主子窗口栈顶（HWND_TOP）——
            //   否则压在 DefView 的 SysListView32（explorer 原生图标层）之下，鼠标点击全被
            //   explorer 截走，自绘网格收不到任何输入（自由挪动/拖拽失效）。
            //   FillVirtualScreen 同时完成提层 + 相对父客户区铺满。
            FillVirtualScreen(HwndTop);

            // ★ 显示生死线（2026-09-06 真机实证）：嵌入发生在 OnSourceInitialized（WPF 尚未
            //   完成可见性设置），且样式改写/SetParent 后 WS_VISIBLE 可能仍为 0 → 自绘桌面
            //   整窗不渲染。必须显式 SW_SHOW。
            if (!NativeMethods.IsWindowVisible(hwnd))
            {
                _ = NativeMethods.ShowWindow(hwnd, SwShow);
                DiagnosticLog.Trace("shell.desktop", "嵌入：窗口 WS_VISIBLE=0 → 补 NativeMethods.ShowWindow(SW_SHOW)");
            }

            // ★ 宿主可见性兜底：IsWindowVisible 按祖先链与运算，宿主 DefView 自身若被隐藏
            //   （真机实测 style 无 WS_VISIBLE），本窗口无论怎么 SW_SHOW 都不可见。发现即恢复。
            if (!NativeMethods.IsWindowVisible(host))
            {
                _ = NativeMethods.ShowWindow(host, SwShow);
                DiagnosticLog.Trace("shell.desktop", $"嵌入：宿主 DefView 0x{host:X} 处于隐藏态 → 补 NativeMethods.ShowWindow(SW_SHOW)");
            }

            DiagnosticLog.Trace("shell.desktop", $"已嵌入桌面宿主 host=0x{host:X} 并提层 HWND_TOP（自绘层可交互，原生图标层被本插件隐藏）");
            return true;
        }
        catch (Exception ex)
        {
            // 嵌入失败原因必须落盘（此前静默导致"降级顶层 HWND_BOTTOM 被 explorer 盖住"无从排查）
            DiagnosticLog.Trace("shell.desktop", $"嵌入异常（降级顶层）：{ex.Message}");
            return false;
        }
    }

    // ======== 嵌入看门狗：壁纸引擎/DWM 活动会重建 Progman 结构，把外来子窗口挤出 ========
    // 实机实证：启动时已挂 Progman，数分钟后失联（Wallpaper Engine 换壁纸重建桌面层）——
    // 窗口带着 WS_CHILD 样式变孤儿 → 渲染异常不可见（用户看不到图标但离屏渲染正常）。
    // cairoshell 用 WindowManager 的 DwmChanged/TaskbarCreated 事件重建桌面窗口应对；
    // 本仓库无该服务，用轻量轮询等价实现：发现失联立即重新嵌入。

    private const int WatchdogSeconds = 3;
    private System.Windows.Threading.DispatcherTimer? _watchdog;

    /// <summary>启动失联看门狗（轮询校验父子关系，失联即重挂）。</summary>
    private void StartEmbedWatchdog()
    {
        if (_watchdog is not null)
        {
            return;
        }

        _watchdog = new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromSeconds(WatchdogSeconds),
            System.Windows.Threading.DispatcherPriority.Background,
            (_, _) => VerifyEmbedding(),
            Dispatcher);
    }


    private const int SwShow = 5;

    private const uint GW_HWNDPREV = 3;

    /// <summary>校验桌面窗口仍在桌面宿主下且可见；失联则重新嵌入。</summary>
    private void VerifyEmbedding()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
            {
                return; // hwnd 尚未创建/已失效（后者不应发生：DestroyWindow 不能跨进程）
            }

            var host = FindDesktopHostWindow();
            if (host == IntPtr.Zero)
            {
                return; // explorer 桌面暂不可用（如正在重启）：下轮再试
            }

            var parent = GetParent(hwnd);
            // 【振荡修复 2026-09-06】不再与 FindDesktopHostWindow 的瞬时结果做相等比较：
            //   壁纸引擎 churn 期间 DefView 查找会在「Progman 直子/WorkerW 子/Progman 兜底」间
            //   抖动，旧逻辑 parent(DefView)≠host(Progman兜底) 每轮误判失联 → 每 3s 重挂一次
            //   （日志实证 12:30-12:31 连续 22 次）。改为按类名接受任何桌面宿主（DefView 优先
            //   挂载点、Progman 兜底），父窗口失效/非宿主才重挂。
            // 【重挂≠显示 2026-09-06 真机实证】父窗口正常但 WS_VISIBLE=0 时重挂永远修不好
            //   可见位（SetParent 不改可见性），只会制造高频跨进程 SetParent churn（冻结嫌疑）
            //   ——这种情形只补 NativeMethods.ShowWindow(SW_SHOW)。
            if (!NativeMethods.IsWindow(parent) || !DesktopHostWindow.IsHostClass(parent))
            {
                DiagnosticLog.Trace("shell.desktop",
                    $"嵌入看门狗：重挂（parent=0x{parent:X} isWindow={NativeMethods.IsWindow(parent)} visible={NativeMethods.IsWindowVisible(hwnd)}）");
                _embedded = TryEmbedDesktop(); // 真失联 → 重新嵌入（幂等）
            }
            else if (!NativeMethods.IsWindowVisible(parent))
            {
                // 宿主自身被隐藏（真机实证 DefView style 无 WS_VISIBLE）：恢复宿主，
                // 否则本窗口作为其子窗口在祖先链与运算下永不可见。
                _ = NativeMethods.ShowWindow(parent, SwShow);
                DiagnosticLog.Trace("shell.desktop", $"嵌入看门狗：宿主 DefView 0x{parent:X} 隐藏 → 补 NativeMethods.ShowWindow(SW_SHOW)");
            }
            else if (!NativeMethods.IsWindowVisible(hwnd))
            {
                _ = NativeMethods.ShowWindow(hwnd, SwShow); // 父子关系正常、只是不可见 → 补显示
                DiagnosticLog.Trace("shell.desktop", "嵌入看门狗：父子关系正常但不可见 → 补 NativeMethods.ShowWindow(SW_SHOW)");
            }

            // 【Z 序守卫 2026-09-07】explorer 重建 ListView/桌面层（壁纸引擎切换、explorer 刷新）
            // 会把本窗口从宿主子窗口栈顶挤下去：父关系/可见性都正常，但自绘层被原生层盖住 →
            // 鼠标命中全被截走（历史"桌面图标消失/无法交互"的反复成因）。按类名校验可见父链，
            // 若自绘窗口不是父窗口的最后一个子（Z 序被压）→ 重新提到 HWND_TOP。
            try
            {
                var prev = NativeMethods.GetWindow(hwnd, GW_HWNDPREV);
                if (prev != IntPtr.Zero)
                {
                    _ = NativeMethods.SetWindowPos(hwnd, HwndTop, 0, 0, 0, 0,
                        SwpNoMove | SwpNoSize | SwpNoActivate);
                    DiagnosticLog.Trace("shell.desktop",
                        $"嵌入看门狗：Z 序被压（prev=0x{prev.ToInt64():X}）→ 重新提层 HWND_TOP");
                }
            }
            catch
            {
                // Z 序守卫不允许抛异常（M10）
            }

            // 【回归修复 2026-09-06】任务栏恢复兜底：右键菜单等操作可能致 explorer 崩溃，
            // 崩溃后任务栏消失，Windows 不一定自动重启 explorer。看门狗每 3s 检测一次，
            // explorer 未运行则立即拉起（任务栏随 explorer 启动自动恢复）。
            try
            {
                if (System.Diagnostics.Process.GetProcessesByName("explorer").Length == 0)
                {
                    DiagnosticLog.Trace("shell.desktop", "嵌入看门狗：explorer 未运行 → 启动 explorer.exe 恢复任务栏");
                    System.Diagnostics.Process.Start("explorer.exe");
                }
            }
            catch
            {
                // 启动 explorer 失败静默（M10），下轮重试
            }
        }
        catch
        {
            // 看门狗自身不允许抛异常（M10）
        }
    }

    /// <summary>
    /// 找桌面挂载点：**SHELLDLL_DefView（原生图标视图窗口）本身**——对齐 cairoshell/
    /// ManagedShell <c>WindowHelper.GetLowestDesktopChildHwnd</c> 反编译实现（不是 Progman！）。
    /// 挂 DefView 之下 = 与原生图标同层且 HWND_TOP 提层后可交互（自由挪动/拖拽/右键委托）。
    /// <para>查找已收口 shell-core（<c>DesktopHostWindow.FindMountPoint</c>：DefView → 兜底 Progman，
    /// 含顶层 WorkerW 变体）；此处只保留"挂载点选 DefView 而不是 Progman"这一策略。</para>
    /// </summary>
    private static IntPtr FindDesktopHostWindow() => DesktopHostWindow.FindMountPoint();

    /// <summary>
    /// 吞 WM_CONTEXTMENU：自绘桌面右键统一走本进程自绘菜单路径（图标/空白 → DesktopIconsControl.ShowMenu
    /// → DesktopMenuPopup；跨进程 DefView 转发已于 2026-09-10 移除）。
    /// 不吞则 DefWindowProc 转发给父窗口（explorer 桌面）→ 原生右键菜单与本进程菜单并存。
    /// </summary>
    private IntPtr DesktopWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_CONTEXTMENU = 0x007B;
        if (msg == WM_CONTEXTMENU)
        {
            handled = true;
        }
        return IntPtr.Zero;
    }

    // ======== 降级路径：顶层窗口时失焦回底 ========

    /// <inheritdoc />
    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (!_embedded)
        {
            SendToBottom(); // 子窗口模式无需管理 Z 序（天然最低）
        }
    }

    private void SendToBottom()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            _ = NativeMethods.SetWindowPos(hwnd, (IntPtr)HwndBottom, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate);
        }
    }
}
