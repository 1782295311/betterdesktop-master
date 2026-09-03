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
//   4) explorer 原生图标的隐藏/恢复由 DesktopPlugin 负责（ToggleDesktopIcons）。
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
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.ContextMenus.Contracts;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.Desktop.Contracts;
using BetterDesktop.Shell.Desktop.Controls;
using BetterDesktop.Shell.Settings.Contracts;

namespace BetterDesktop.Shell.Desktop.Windows;

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
    private const int SmCxvirtualscreen = 78;
    private const int SmCyvirtualscreen = 79;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    private const int SmCyscreen = 1; // SM_CYSCREEN：主屏物理像素高度

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    private readonly IDesktopBrowser _browser;
    private readonly ISettingsService? _settings;
    private DesktopIconsControl? _icons;
    private bool _embedded;

    public DesktopWindow(
        IDesktopBrowser browser,
        IVibrancyService vibrancy,
        IAppearanceService? appearance,
        ISettingsService? settings = null,
        IMenuService? menus = null,
        IFileClassifier? classifier = null)
        : base(appearance, vibrancy)
    {
        _browser = browser;
        _settings = settings;

        Title = "BetterDesktop.Desktop";
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowState = WindowState.Normal; // 禁止 Maximized：手动铺 VirtualScreen（防 ABN_FULLSCREENAPP）

        // 透明文件显示器：背景 #01000000（alpha=1 近透明，透出 explorer 壁纸）。
        // 不自绘壁纸（生死线 #1）——壁纸由 explorer 原生桌面渲染，本窗口只画图标层。
        // 基类 ShellWindow 构造已默认设该背景；此处显式重申并注明不变量，防止后续误改。
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));

        // 图标网格：瀑布列（先填列后换列），避开顶部菜单栏条带 + 底部 dock/原生任务栏
        _icons = new DesktopIconsControl(_browser, _settings, menus, classifier);
        UpdateIconsReserve();
        ApplyIconsHidden();

        // 双击空白处 → 切换隐藏桌面图标。两条触发路径按布局模式互补：
        //   自动排列：WrapPanel 空白不吞事件，冒泡到窗口层（图标 cell 的按下已 Handled，不会误触发）；
        //   自由布局：空白按下被框选逻辑 Handled，由 DesktopIconsControl.BlankAreaDoubleClick 上报。
        _icons.BlankAreaDoubleClick += (_, _) => ToggleIconsHidden();
        MouseLeftButtonDown += OnWindowBlankDoubleClick;

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
        //   右键已由 WPF 层（MouseRightButtonUp → IMenuService）接管并弹统一菜单，但 WPF 不吞
        //   WM_CONTEXTMENU——DefWindowProc 会把未处理的 WM_CONTEXTMENU 转发给父窗口
        //   （explorer 桌面）→ 原生右键菜单与统一菜单**同时弹出并存**（截图实证）。
        //   故在 hwnd hook 一律吞掉 WM_CONTEXTMENU（覆盖整棵子窗口树的转发链）。
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero && HwndSource.FromHwnd(hwnd) is { } src)
            {
                src.AddHook(DesktopWndProc);
            }
        };

        // 设置变化（dock 尺寸滑块/组件开关）→ 重算底部避让，避免图标被底栏瞬时遮挡
        if (_settings is not null)
        {
            _settings.Changed += OnSettingsChanged;
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
            var tray = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null);
            if (tray != IntPtr.Zero && IsWindowVisible(tray) && GetWindowRect(tray, out RECT rc))
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

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
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
            ApplyIconsHidden();
            // 图标类设置（格尺寸/字号/.lnk 隐藏/菜单与拖放开关）需重建网格才生效；
            // iconsHidden 只切网格可见性，无需整网格重建
            if (isDesktop && e.Key != "desktop.iconsHidden")
            {
                _icons?.Rebuild();
            }
        }));
    }

    /// <summary>切换「隐藏桌面图标」（desktop.iconsHidden，持久化）——仅自绘模式路径。
    /// ⚠️ 只动自绘网格这一层：explorer 原生图标由 DesktopPlugin 幂等管理（恒隐藏 + 退出兜底恢复），
    /// 两条线各自独立、绝不交叉操作——若在这里再去 Show/Hide 原生 SysListView32，
    /// 任何状态漂移都会造成两层图标同时可见/互相错位（"打架"根源）。
    /// 原生桌面模式（components.desktop=false）的另一条路径在 DesktopPlugin 的 WH_MOUSE_LL 钩子，
    /// 两者共享同一意图键但各管各的层，靠模式与类名过滤天然互斥。
    /// 窗口本体保持可见可交互：藏的只是图标网格，恢复通道（再次双击）永远在本窗口上；
    /// 若 Hide() 整个窗口，第二次双击会落进 explorer DefView，自绘层就再也收不回来了。</summary>
    private void ToggleIconsHidden()
    {
        var hidden = _settings?.Get("desktop.iconsHidden", false) ?? false;
        DiagnosticLog.Trace("shell.desktop", $"双击桌面空白：desktop.iconsHidden {hidden} → {!hidden}");
        _settings?.Set("desktop.iconsHidden", !hidden);
    }

    /// <summary>应用桌面图标网格可见性（desktop.iconsHidden；启动与设置变更两条入口共用）。</summary>
    private void ApplyIconsHidden()
    {
        if (_icons is null)
        {
            return;
        }

        var hidden = _settings?.Get("desktop.iconsHidden", false) ?? false;
        _icons.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>窗口层空白双击（自动排列路径：WrapPanel 空白不吞事件冒泡到此）。
    /// 图标上的双击（打开文件）在 cell 层已标记 Handled，不会到达这里。</summary>
    private void OnWindowBlankDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2)
        {
            ToggleIconsHidden();
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
            _settings.Changed -= OnSettingsChanged;
        }

        base.OnClosed(e);
    }

    // ======== 嵌入 explorer 桌面（cairoshell DesktopManager.ConfigureDesktop 同款） ========

    /// <inheritdoc />
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 默认：嵌入模式（挂 SHELLDLL_DefView，cairoshell 正解——见 FindDesktopHostWindow）。
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
            _ = DwmSetWindowAttribute(hwnd, DwmwaExcludedFromPeek, ref attr, sizeof(int));
        }
        catch
        {
            // 老系统无该属性：忽略
        }
    }

    private const int DwmwaExcludedFromPeek = 12;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

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
        _ = SetWindowPos(hwnd, insertAfter, x, y, w, h, SwpNoActivate);
    }

    /// <summary>
    /// 嵌入 explorer 桌面：WS_CHILD + SetParent 到"含 SHELLDLL_DefView 的窗口"。
    /// 兼容壁纸软件（WorkerW 形态）；失败返回 false（调用方降级）。幂等：重复调用无害。
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

            // WPF 窗口 → 子窗口样式（对齐 cairoshell ConfigureDesktop）
            int style = GetWindowLong(hwnd, GwlStyle);
            _ = SetWindowLong(hwnd, GwlStyle, (style | WsChild) & ~WsOverlapped);
            _ = SetParent(hwnd, host);

            // ★ 右键/命中生死线：SetParent 后必须把窗口提到宿主子窗口栈顶（HWND_TOP）——
            //   否则压在 DefView 的 SysListView32（explorer 原生图标层）之下，鼠标点击全被
            //   explorer 截走：表现为"自绘桌面右键唤不出菜单、空白处弹系统右键菜单"。
            //   FillVirtualScreen 同时完成提层 + 相对父客户区铺满。
            FillVirtualScreen(HwndTop);
            DiagnosticLog.Trace("shell.desktop", $"已嵌入桌面宿主 host=0x{host:X} 并提层 HWND_TOP（覆盖原生图标层命中）");
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

    /// <summary>校验桌面窗口仍在桌面宿主下且可见；失联则重新嵌入。</summary>
    private void VerifyEmbedding()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
            {
                return; // hwnd 尚未创建/已失效（后者不应发生：DestroyWindow 不能跨进程）
            }

            var host = FindDesktopHostWindow();
            if (host == IntPtr.Zero)
            {
                return; // explorer 桌面暂不可用（如正在重启）：下轮再试
            }

            if (GetParent(hwnd) != host || !IsWindowVisible(hwnd))
            {
                _embedded = TryEmbedDesktop(); // 失联 → 重新嵌入（幂等）
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
    /// 挂 DefView 之下 = 与原生图标同层：在壁纸引擎 DComp 壁纸（Progman 直接子级）之上，
    /// 原生图标隐藏后由本窗口原位接管。
    /// </summary>
    private static IntPtr FindDesktopHostWindow()
    {
        var progman = GetShellWindow();
        if (progman == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        // 默认形态：DefView 直接在 Progman 下 → 挂载点 = DefView
        var defView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView != IntPtr.Zero)
        {
            return defView;
        }

        // WorkerW 变体（壁纸软件切换过桌面结构）：DefView 在某顶层 WorkerW 下 → 挂载点仍是 DefView
        IntPtr worker = IntPtr.Zero;
        IntPtr dv = IntPtr.Zero;
        do
        {
            worker = FindWindowEx(IntPtr.Zero, worker, "WorkerW", null);
            dv = worker != IntPtr.Zero ? FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null) : IntPtr.Zero;
        }
        while (dv == IntPtr.Zero && worker != IntPtr.Zero);

        return dv != IntPtr.Zero ? dv : progman; // 兜底 Progman（罕见）
    }

    /// <summary>
    /// 吞 WM_CONTEXTMENU：右键菜单统一经 IMenuService（见构造函数注释）。
    /// 不吞则 DefWindowProc 转发给父窗口（explorer 桌面）→ 原生右键菜单与统一菜单并存。
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
            _ = SetWindowPos(hwnd, (IntPtr)HwndBottom, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate);
        }
    }
}
