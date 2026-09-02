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
using System.Windows.Interop;
using System.Windows.Media;
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

    /// <summary>Dock 名称行高度（对齐 DockLayoutService.Measure 的 labelHeight）。</summary>
    private const double DockLabelHeight = 24;

    private const int HwndBottom = 1; // HWND_BOTTOM
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
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

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

        // 根 Border：满足基类 ChromeBorder 约定（DEBUG 断言强制，未设置会 FailFast）。
        // 附带收益：字号缩放（ApplyFontScale）与主题前景传导经此 Border 生效。
        var root = new Border { Background = Brushes.Transparent, Child = _icons };
        Content = root;
        ChromeBorder = root;

        // 设置变化（dock 尺寸滑块/组件开关）→ 重算底部避让，避免图标被底栏瞬时遮挡
        if (_settings is not null)
        {
            _settings.Changed += OnSettingsChanged;
        }
    }

    /// <summary>
    /// 图标网格避让：顶部菜单栏（desktop.reserveMenuBar）+ 底部 dock/原生任务栏
    /// （desktop.reserveDock / desktop.reserveTaskbar，桌面分区独立开关）。
    /// </summary>
    private void UpdateIconsReserve()
    {
        if (_icons is null)
        {
            return;
        }

        double bottomReserve = 0;

        // Dock 高度估算：contentHeight = iconSize + labelHeight + bottomMargin*2，
        // 距底 bottomMargin，故占用 = iconSize + labelHeight + bottomMargin*3（对齐 DockLayoutService.Measure）。
        // ⚠️ 必须**同时**满足「桌面侧开关 desktop.reserveDock」与「Dock 组件真的启用 components.dock」：
        //    否则 Dock 关闭时仍预留约 98 DIP，桌面下方会空出一大块（用户反馈的布局 bug）。
        var dockReserveOn = _settings?.Get("desktop.reserveDock", true) ?? true;
        var dockEnabled = _settings?.Get("components.dock", true) ?? true;
        if (dockReserveOn && dockEnabled)
        {
            var iconSize = _settings?.Get("dock.iconSize", 44d) ?? 44d;
            var showLabel = _settings?.Get("dock.showLabel", true) ?? true;
            var bottomMargin = _settings?.Get("dock.bottomMargin", 10d) ?? 10d;
            var dockHeight = iconSize + (showLabel ? DockLabelHeight : 0) + bottomMargin * 3;
            bottomReserve = Math.Max(bottomReserve, dockHeight);
        }

        // 原生任务栏高度：占主屏底边（WorkArea 差值）。
        // 同样需满足桌面侧开关 + 任务栏组件真的显示（components.wintaskbar；隐藏时 WorkArea 差值本就≈0，
        // 此处再加一道判断，避免任务栏被隐藏后仍按缓存的工作区数值预留）。
        var taskbarReserveOn = _settings?.Get("desktop.reserveTaskbar", true) ?? true;
        var taskbarShown = _settings?.Get("components.wintaskbar", true) ?? true;
        if (taskbarReserveOn && taskbarShown)
        {
            var taskbarHeight = SystemParameters.PrimaryScreenHeight - SystemParameters.WorkArea.Height;
            bottomReserve = Math.Max(bottomReserve, taskbarHeight);
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
            // 图标类设置（格尺寸/字号/.lnk 隐藏/菜单与拖放开关）需重建网格才生效
            if (isDesktop)
            {
                _icons?.Rebuild();
            }
        }));
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
    private void FillVirtualScreen()
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
        _ = SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, SwpNoZorder | SwpNoActivate);
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
                return false;
            }

            var host = FindDesktopHostWindow();
            if (host == IntPtr.Zero)
            {
                return false;
            }

            // WPF 窗口 → 子窗口样式（对齐 cairoshell ConfigureDesktop）
            int style = GetWindowLong(hwnd, GwlStyle);
            _ = SetWindowLong(hwnd, GwlStyle, (style | WsChild) & ~WsOverlapped);
            _ = SetParent(hwnd, host);

            FillVirtualScreen(); // 挂载后坐标相对父客户区
            return true;
        }
        catch
        {
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
