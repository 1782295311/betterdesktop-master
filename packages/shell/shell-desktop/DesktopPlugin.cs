// BetterDesktop.Shell.Desktop — 自绘桌面插件入口
// 装配：DesktopBrowser（Provide 给菜单栏左区/工具条）+ DesktopWindow（透明"文件显示器"，壁纸归 explorer）。
// 启用时隐藏 explorer 原桌面图标（ShellHelper.ToggleDesktopIcons），退出/卸载时还原（用户环境不可破坏）。
//
// 【组件开关：即时生效】
//   设置中心「桌面」分区的 components.desktop 开关**即时生效**：
//   订阅 ISettingsService.Changed，开→StartDesktop()（建窗口+隐藏原生图标），
//   关→StopDesktop()（关窗口+恢复原生图标），无需重启。
//
// 【退出兜底机制】
//   UnloadAsync 只在插件正常卸载时触发；进程被 kill / 崩溃 / 强制退出时不走这里 → explorer 图标残留隐藏。
//   兜底两层：
//     1) 正常退出：Application.Current.Exit 恢复（Application.Shutdown 路径）。
//     2) 进程退出：AppDomain.ProcessExit 恢复（进程被结束时兜底；幂等防双触发）。
//   恢复为无条件 SW_SHOW：壳退出后用户桌面必须回到 explorer 默认可见态（环境不可破坏），
//   多个钩子先后触发重复调用无副作用。
//
// 【双击桌面空白 → 切换图标显隐（desktop.iconsHidden，双模式）】
//   自绘模式：DesktopWindow 窗口级双击 → 只藏自绘网格（原生层仍由本插件恒隐藏，两层零交叉）。
//   原生模式（components.desktop=false）：无自绘窗口可接事件，由本插件的 WH_MOUSE_LL 钩子感知
//   "双击落在原生桌面上"→ 以【实际可见性】为基准切换 desktop.iconsHidden → Changed 幂等落到
//   explorer 图标层。钩子按 WindowFromPoint 类名过滤，自绘模式天然不命中（两层互不打架）。
//
// 【重要：不再使用 ShellHelper.ToggleDesktopIcons（翻转语义）】
//   ManagedShell 的 ToggleDesktopIcons(bool) 实测为**翻转**（每次调用切换显示/隐藏，
//   bool 参数不改变其行为），且其内部窗口查找只认 Progman 直子的 SHELLDLL_DefView——
//   壁纸引擎（Wallpaper Engine）等会把 DefView 移到 WorkerW 下，此时隐藏/恢复都会静默失效
//   （曾导致"关闭自绘桌面后 explorer 图标不恢复"）。
//   现改为自实现**幂等**的 ShowWindow(SW_SHOW/SW_HIDE) 直接作用于 SysListView32，
//   查找链兼容 Progman 直子与 WorkerW 变体；幂等 = 重复调用无副作用，所有时序坑消失。

using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.ContextMenus.Contracts;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.Desktop.Contracts;
using BetterDesktop.Shell.Desktop.Sections;
using BetterDesktop.Shell.Desktop.Services;
using BetterDesktop.Shell.Desktop.Windows;
using BetterDesktop.Shell.Settings.Contracts;

namespace BetterDesktop.Shell.Desktop;

/// <summary>自绘桌面插件（shell.desktop）。</summary>
public sealed class DesktopPlugin : IPlugin
{
    public string Name => "shell.desktop";

    public IReadOnlyList<Type> Inject => Array.Empty<Type>();

    private IContext? _context;
    private ISettingsService? _settings;
    private DesktopBrowser? _browser;
    private DesktopWindow? _window;
    private bool _running;
    private bool _settingsHooked;

    // WH_MOUSE_LL：原生桌面模式的双击监听（自绘模式由 DesktopWindow 自行处理）
    private IntPtr _mouseHook;
    private MouseHookProcDelegate? _mouseHookProc; // 持有委托引用，防被 GC 回收导致回调失效

    /// <summary>桌面组件开关（设置中心「桌面」分区里的 components.desktop，默认启用）。</summary>
    private bool IsDesktopEnabled => _settings?.Get("components.desktop", true) ?? true;

    public Task LoadAsync(IContext context, CancellationToken cancellationToken = default)
    {
        _context = context;
        _settings = context.Get<ISettingsService>();

        // 设置分区（侧栏「桌面」）：无论桌面开关如何都注册，保证用户能在设置里重新开启。
        try
        {
            var registry = context.Get<ISettingsSectionRegistry>();
            if (registry is null)
            {
                DiagnosticLog.Trace("shell.desktop", "设置分区注册跳过：ISettingsSectionRegistry 未注册");
            }
            else
            {
                registry.Register(new DesktopSection());
                DiagnosticLog.Trace("shell.desktop", $"已注册设置分区「桌面」（当前分区数={registry.Sections.Count}）");
            }
        }
        catch (Exception ex)
        {
            // 分区注册失败不阻断（M10）
            DiagnosticLog.Trace("shell.desktop", $"设置分区注册失败：{ex.Message}");
        }

        // 订阅开关变更 → 即时启停（用户点击「启用自绘桌面」无需重启即生效）。
        if (_settings is not null && !_settingsHooked)
        {
            _settings.Changed += OnSettingsChanged;
            _settingsHooked = true;
        }

        // 双击桌面空白切换图标显隐（desktop.iconsHidden）：
        //   自绘模式 → DesktopWindow 窗口级双击；原生模式 → 本钩子感知（窗口不存在，无别的感知手段）。
        //   钩子常驻、按类名过滤，两种模式互不打架（详见文件头）。
        InstallDesktopDoubleClickHook();

        if (IsDesktopEnabled)
        {
            StartDesktop();
        }
        else
        {
            context.Logger.Info($"{Name} 已跳过：components.desktop=false（使用 explorer 原生桌面）");

            // 原生模式 + 上次会话隐藏了图标 → 开机即恢复用户意图
            //（自绘模式下 StartDesktop 隐藏原生图标 + DesktopWindow 按此键应用网格可见性，无需这里）
            if (_settings?.Get("desktop.iconsHidden", false) ?? false)
            {
                SetNativeIconsVisible(false);
            }
        }

        return Task.CompletedTask;
    }

    public Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        if (_settings is not null && _settingsHooked)
        {
            _settings.Changed -= OnSettingsChanged;
            _settingsHooked = false;
        }

        RemoveDesktopDoubleClickHook();
        StopDesktop();
        _browser = null;
        return Task.CompletedTask;
    }

    /// <summary>设置变更：components.desktop 开关即时启停；desktop.iconsHidden 在原生模式落到 explorer 图标层。</summary>
    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Key == "components.desktop")
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(ApplyEnabledState));
                return;
            }

            ApplyEnabledState();
            return;
        }

        if (e.Key == "desktop.iconsHidden" && !_running)
        {
            // 原生桌面模式：用户意图直接作用于 explorer 图标层（幂等 ShowWindow）。
            // 自绘模式由 DesktopWindow 自行应用（藏的是自绘网格），这里不插手——两层零交叉。
            var hidden = _settings?.Get("desktop.iconsHidden", false) ?? false;
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(() => SetNativeIconsVisible(!hidden)));
                return;
            }

            SetNativeIconsVisible(!hidden);
        }
    }

    private void ApplyEnabledState()
    {
        if (IsDesktopEnabled)
        {
            StartDesktop();
            return;
        }

        StopDesktop();
        // 运行时切回原生桌面：若用户此前隐藏了图标，把意图落到原生层。
        // （正常退出路径不走这里——退出必须无条件恢复 explorer 默认可见，环境不可破坏。）
        if (_settings?.Get("desktop.iconsHidden", false) ?? false)
        {
            SetNativeIconsVisible(false);
        }
    }

    /// <summary>启动自绘桌面：创建桌面窗口 + 隐藏 explorer 原生图标 + 注册退出兜底。</summary>
    private void StartDesktop()
    {
        if (_running || _context is null)
        {
            return;
        }

        try
        {
            _browser ??= new DesktopBrowser();
            _context.Provide<IDesktopBrowser>(_browser);

            var vibrancy = _context.Get<IVibrancyService>() ?? NullVibrancy.Instance;
            var appearance = _context.Get<IAppearanceService>();
            var menus = _context.Get<IMenuService>();
            var classifier = _context.Get<IFileClassifier>();
            _window = new DesktopWindow(_browser, vibrancy, appearance, _settings, menus, classifier);
            _window.Show();

            // 幂等隐藏：ShowWindow(SW_HIDE) 重复调用无副作用，无翻转语义的时序坑。
            SetNativeIconsVisible(false);

            // 兜底：正常退出与进程退出都恢复（幂等）。先反注册再注册，防重复订阅。
            Application.Current.Exit -= OnExitRestoreIcons;
            Application.Current.Exit += OnExitRestoreIcons;
            AppDomain.CurrentDomain.ProcessExit -= OnProcessExitRestoreIcons;
            AppDomain.CurrentDomain.ProcessExit += OnProcessExitRestoreIcons;

            _running = true;
            _context.Logger.Info($"{Name} 已启动：透明文件显示器已嵌入桌面（壁纸归 explorer），explorer 桌面图标已隐藏（含退出兜底）");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.desktop", $"启动自绘桌面失败：{ex.Message}");
        }
    }

    /// <summary>停止自绘桌面：关闭窗口 + 恢复 explorer 原生图标 + 移除退出兜底。</summary>
    private void StopDesktop()
    {
        if (!_running)
        {
            return;
        }

        try
        {
            Application.Current.Exit -= OnExitRestoreIcons;
            AppDomain.CurrentDomain.ProcessExit -= OnProcessExitRestoreIcons;
        }
        catch
        {
            // 移除钩子失败不阻断（M10）
        }

        RestoreIcons();
        _window?.Close();
        _window = null;
        _running = false;
        _context?.Logger.Info($"{Name} 已停止：已恢复 explorer 原生桌面");
    }

    /// <summary>Application.Exit：恢复 explorer 桌面图标（幂等）。</summary>
    private void OnExitRestoreIcons(object? sender, EventArgs e) => RestoreIcons();

    /// <summary>AppDomain.ProcessExit：恢复 explorer 桌面图标（幂等，防双触发）。</summary>
    private void OnProcessExitRestoreIcons(object? sender, EventArgs e) => RestoreIcons();

    /// <summary>恢复 explorer 桌面图标为可见（无条件 SW_SHOW，幂等）：壳退出后用户桌面必须
    /// 回到 explorer 默认可见态（环境不可破坏）——无论此前是壳隐藏的还是用户双击隐藏的。
    /// 多个退出钩子先后触发重复调用无副作用。</summary>
    private void RestoreIcons()
    {
        try
        {
            SetNativeIconsVisible(true);
        }
        catch
        {
            // 还原失败不阻断（M10）
        }
    }

    // ======== explorer 原生桌面图标的幂等显隐 ========
    // 窗口链：Shell(Progman) → SHELLDLL_DefView → SysListView32("FolderView")。
    // ⚠️ DefView 不一定挂在 Progman 直下：壁纸引擎（Wallpaper Engine）等会创建 WorkerW
    //    并把 DefView 移过去（DesktopWindow 的挂载查找 FindDesktopHostWindow 同样兼容此变体）。
    //    查找失败曾导致"隐藏成功、恢复失效"——这里两处必须用同一条兼容查找。

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>
    /// 定位 explorer 桌面图标 ListView（SysListView32）。
    /// 先查 Progman 直子 DefView；找不到再遍历 WorkerW（壁纸引擎/多显示器变体）。
    /// </summary>
    private static IntPtr FindDesktopListView()
    {
        var shell = GetShellWindow();
        if (shell == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var defView = FindWindowEx(shell, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView == IntPtr.Zero)
        {
            // DefView 被移到 WorkerW 下（壁纸引擎等）：遍历同级 WorkerW 找它
            IntPtr worker = IntPtr.Zero;
            do
            {
                worker = FindWindowEx(shell, worker, "WorkerW", null);
                defView = worker == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null);
            }
            while (defView == IntPtr.Zero && worker != IntPtr.Zero);
        }

        return defView == IntPtr.Zero
            ? IntPtr.Zero
            : FindWindowEx(defView, IntPtr.Zero, "SysListView32", "FolderView");
    }

    /// <summary>explorer 原生桌面图标当前是否可见；窗口找不到按不可见处理（M10，不阻断）。</summary>
    private static bool AreNativeIconsVisible()
    {
        try
        {
            var listView = FindDesktopListView();
            return listView != IntPtr.Zero && IsWindowVisible(listView);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 幂等设置 explorer 原生桌面图标可见性（ShowWindow 直接作用于 ListView，
    /// 非 toggle 翻转——重复调用无副作用，彻底消除翻转语义的时序坑）。
    /// </summary>
    private static void SetNativeIconsVisible(bool visible)
    {
        try
        {
            var listView = FindDesktopListView();
            if (listView != IntPtr.Zero)
            {
                _ = ShowWindow(listView, visible ? SW_SHOW : SW_HIDE);
            }
        }
        catch
        {
            // 显隐失败不阻断（M10）
        }
    }

    // ======== 原生桌面模式的双击切换（WH_MOUSE_LL） ========
    // 自绘桌面关闭时没有自绘窗口可接双击，只能用低级鼠标钩子感知"双击落在原生桌面上"。
    // 判定链（每步都是快路径，不命中立即放行）：
    //   1) WM_LBUTTONDBLCLK；
    //   2) WindowFromPoint 的窗口类 ∈ {Progman, WorkerW, SHELLDLL_DefView, SysListView32}
    //      —— 自绘模式下桌面被 DesktopWindow 覆盖（类名 HwndWrapper[...]），天然不命中，
    //      两种模式互不打架；dock/菜单栏/开始菜单等普通窗口同样不命中；
    //   3) 命中 SysListView32 时再做 LVM_HITTEST：双击在原生图标上（打开文件语义）不切换。
    // 切换以【实际可见性】为基准写 desktop.iconsHidden（而非持久化值）：用户用 explorer
    // 自带的「查看 > 显示桌面图标」改过状态时自动对齐，避免状态漂移导致一次"空切换"。

    private const int WhMouseLl = 14;
    private const int WmLbuttondblclk = 0x0203;
    private const int LvmHittest = 0x1012;   // LVM_FIRST + 0x12
    private const uint LvhtOnitem = 0x000E;  // ONITEMICON|ONITEMLABEL|ONITEMSTATEICON

    private delegate IntPtr MouseHookProcDelegate(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LVHITTESTINFO
    {
        public POINT pt;
        public uint flags;
        public int iItem;
        public int iSubItem;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, MouseHookProcDelegate lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT p);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hWnd, ref POINT p);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref LVHITTESTINFO lParam);

    private void InstallDesktopDoubleClickHook()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(InstallDesktopDoubleClickHook));
            return;
        }

        if (_mouseHook != IntPtr.Zero)
        {
            return;
        }

        // LL 钩子装在 UI 线程（有消息泵即工作）；回调内只做快判定，切换经 Dispatcher 落设置
        _mouseHookProc = OnMouseHookProc;
        _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseHookProc, GetModuleHandle(null), 0);
        DiagnosticLog.Trace("shell.desktop", $"原生桌面双击钩子安装：{_mouseHook != IntPtr.Zero}");
    }

    private void RemoveDesktopDoubleClickHook()
    {
        if (_mouseHook == IntPtr.Zero)
        {
            return;
        }

        _ = UnhookWindowsHookEx(_mouseHook);
        _mouseHook = IntPtr.Zero;
        _mouseHookProc = null;
    }

    private IntPtr OnMouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 &&
                wParam.ToInt64() == WmLbuttondblclk &&
                !IsDesktopEnabled && // 双保险：自绘模式由 DesktopWindow 处理（类名过滤已排除，这里再拦一道）
                lParam != IntPtr.Zero &&
                Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT)) is MSLLHOOKSTRUCT info &&
                IsOverNativeDesktop(info.pt))
            {
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher is null || dispatcher.CheckAccess())
                {
                    ToggleNativeIconsByDoubleClick();
                }
                else
                {
                    dispatcher.BeginInvoke(new Action(ToggleNativeIconsByDoubleClick));
                }
            }
        }
        catch
        {
            // 钩子回调绝不允许抛异常（挂掉会拖垮全局鼠标输入）
        }

        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    /// <summary>屏幕点是否落在原生桌面上（Progman/WorkerW/DefView/SysListView32 任一）。
    /// 命中 SysListView32 时用 LVM_HITTEST 排除"双击在原生图标上"的情况。</summary>
    private static bool IsOverNativeDesktop(POINT pt)
    {
        try
        {
            var hwnd = WindowFromPoint(pt);
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            var sb = new StringBuilder(64);
            if (GetClassName(hwnd, sb, 64) <= 0)
            {
                return false;
            }

            var cls = sb.ToString();
            if (cls == "SysListView32")
            {
                var ht = new LVHITTESTINFO { pt = pt };
                _ = ScreenToClient(hwnd, ref ht.pt);
                _ = SendMessage(hwnd, LvmHittest, IntPtr.Zero, ref ht);
                return (ht.flags & LvhtOnitem) == 0;
            }

            return cls is "SHELLDLL_DefView" or "Progman" or "WorkerW";
        }
        catch
        {
            return false;
        }
    }

    /// <summary>原生桌面空白双击 → 切换 desktop.iconsHidden（以实际可见性为基准，见块注释）。</summary>
    private void ToggleNativeIconsByDoubleClick()
    {
        try
        {
            var nowVisible = AreNativeIconsVisible();
            DiagnosticLog.Trace("shell.desktop", $"原生桌面双击：图标当前{(nowVisible ? "可见" : "隐藏")} → 切换");
            _settings?.Set("desktop.iconsHidden", nowVisible); // 可见→隐藏(true)；隐藏→显示(false)
        }
        catch
        {
            // 切换失败不阻断（M10）
        }
    }
}

/// <summary>Vibrancy 服务缺失时的空壳兜底（窗口不变毛玻璃，但不崩溃，M10）。</summary>
internal sealed class NullVibrancy : IVibrancyService
{
    public static NullVibrancy Instance { get; } = new();

    private NullVibrancy() { }

    public void Apply(IntPtr hwnd, VibrancyStyle style, bool roundCorners, bool smallRadius) { }

    public void Disable(IntPtr hwnd) { }
}
