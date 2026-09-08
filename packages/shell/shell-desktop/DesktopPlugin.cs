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
//   现改为自实现**幂等**的 NativeMethods.ShowWindow(SW_SHOW/SW_HIDE) 直接作用于 SysListView32，
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
using BetterDesktop.Shell.Core;
using BetterDesktop.Shell.Core.Native;
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

    // WH_MOUSE_LL：原生桌面模式的双击监听（自绘模式由 DesktopWindow 自行处理）
    private IntPtr _mouseHook;
    private NativeMethods.LowLevelMouseProc? _mouseHookProc; // 持有委托引用，防被 GC 回收导致回调失效

    /// <summary>桌面组件开关（设置中心「桌面」分区里的 components.desktop，默认启用）。</summary>
    private bool IsDesktopEnabled => _settings?.Get("components.desktop", true) ?? true;

    public Task LoadAsync(IContext context, CancellationToken cancellationToken = default)
    {
        _context = context;
        _settings = context.Get<ISettingsService>();

        // 系统桌面右键菜单「切换自绘桌面」入口（2026-09-07）：注册到 explorer 桌面空白右键，
        // 宿主未运行时也能从系统菜单开关自绘桌面。幂等注册，每次启动重写同值。
        DesktopSystemMenuRegistrar.EnsureRegistered();
        // 系统文件右键「转换为…」（2026-09-07）：注册到 explorer 文件右键，经命令桥 → 自绘转换菜单。
        DesktopSystemMenuRegistrar.EnsureConvertRegistered();
        DesktopSystemMenuRegistrar.EnsureArchiveRegistered();
        DesktopSystemMenuRegistrar.EnsureUiTogglesRegistered();

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

        // 订阅开关变更 → 即时启停（违规1修复：跨程序集裸 event → IEventBus，Effect 托管生命周期）。
        if (_settings is not null)
        {
            context.Effect(() => context.Events.On<SettingsChangedEventArgs>(
                ShellEvents.SettingsChanged,
                (e, _) =>
                {
                    OnSettingsChanged(e);
                    return Task.CompletedTask;
                }));
        }

        // 退出兜底无条件注册（2026-09-06 修复）：自绘/原生两种模式都可能留下隐藏态
        //（原生模式 iconsHidden=true 也会隐藏 explorer 图标层），退出必须恢复，环境不可破坏。
        // 先反注册再注册：插件可能多次 Load（HMR），幂等。
        RegisterExitRestoreHooks();

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
                SpawnIconRestoreSentinel();
            }
        }

        return Task.CompletedTask;
    }

    public Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        RemoveDesktopDoubleClickHook();
        StopDesktop();
        _browser = null;
        return Task.CompletedTask;
    }

    /// <summary>设置变更：components.desktop 开关即时启停；desktop.iconsHidden 在原生模式落到 explorer 图标层。</summary>
    private void OnSettingsChanged(SettingsChangedEventArgs e)
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
            // 步骤日志：12:47 会话曾冻结在创建/显示窗口阶段无从定位，每步落点。
            DiagnosticLog.Trace("shell.desktop", "启动：创建 DesktopWindow");
            _browser ??= new DesktopBrowser();
            _context.Provide<IDesktopBrowser>(_browser);

            var vibrancy = _context.Get<IVibrancyService>() ?? NullVibrancy.Instance;
            var appearance = _context.Get<IAppearanceService>();
            var convertMenu = _context.Get<BetterDesktop.Shell.Convert.Contracts.IConvertMenuService>();
            var archiveService = _context.Get<BetterDesktop.Shell.Convert.Contracts.IArchiveService>();
            _window = new DesktopWindow(_browser, vibrancy, appearance, _settings, convertMenu, archiveService, _context?.Events);
            _window.Show();
            DiagnosticLog.Trace("shell.desktop", "启动：窗口已显示，隐藏原生图标");

            // 幂等隐藏：NativeMethods.ShowWindow(SW_HIDE) 重复调用无副作用，无翻转语义的时序坑。
            // （仅隐 SysListView32 图标层；DefView 保持可见以承载自绘层。）
            SetNativeIconsVisible(false);
            // 哨兵进程：宿主进程死亡（含强杀/冻结被结束任务等不触发 Exit/ProcessExit 的路径）
            // 即恢复 explorer 图标层——退出恢复的最后一道兜底。
            SpawnIconRestoreSentinel();

            _running = true;
            _context?.Logger.Info($"{Name} 已启动：透明文件显示器已嵌入桌面（壁纸归 explorer），explorer 桌面图标已隐藏（含退出兜底）");
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

        // 退出兜底钩子保持订阅不摘除（恢复在退出时无条件执行，幂等无害；摘除反而会让
        // "运行过自绘桌面后关闭组件再退出"的场景失去兜底）。
        RestoreIcons();
        _window?.Close();
        _window = null;
        _running = false;
        _context?.Logger.Info($"{Name} 已停止：已恢复 explorer 原生桌面");
    }

    /// <summary>Application.Exit：恢复 explorer 桌面图标（幂等）。</summary>
    private void OnExitRestoreIcons(object? sender, EventArgs e)
    {
        DiagnosticLog.Trace("shell.desktop", "Application.Exit → 恢复 explorer 图标层");
        RestoreIcons();
    }

    /// <summary>AppDomain.ProcessExit：恢复 explorer 桌面图标（幂等，防双触发）。</summary>
    private void OnProcessExitRestoreIcons(object? sender, EventArgs e)
    {
        DiagnosticLog.Trace("shell.desktop", "ProcessExit → 恢复 explorer 图标层");
        RestoreIcons();
    }

    /// <summary>注册退出兜底（Application.Exit + AppDomain.ProcessExit，幂等）。</summary>
    private void RegisterExitRestoreHooks()
    {
        try
        {
            Application.Current.Exit -= OnExitRestoreIcons;
            Application.Current.Exit += OnExitRestoreIcons;
            AppDomain.CurrentDomain.ProcessExit -= OnProcessExitRestoreIcons;
            AppDomain.CurrentDomain.ProcessExit += OnProcessExitRestoreIcons;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.desktop", $"退出兜底注册失败：{ex.Message}");
        }
    }

    /// <summary>拉起图标恢复哨兵：宿主 exe 自身以 --icon-restore-sentinel &lt;pid&gt; 运行，
    /// 等待本进程死亡后恢复 explorer 图标层。覆盖 TerminateProcess/强杀/冻结被结束任务等
    /// 不触发任何托管退出事件的死亡路径（2026-09-06 真机实证：会话冻结后被结束任务，
    /// Exit/ProcessExit 均未执行，图标层残留隐藏、桌面右键失效）。</summary>
    private void SpawnIconRestoreSentinel()
    {
        try
        {
            var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exe))
            {
                DiagnosticLog.Trace("shell.desktop", "图标恢复哨兵未拉起：MainModule 为空");
                return;
            }

            var psi = new System.Diagnostics.ProcessStartInfo(
                exe, $"--icon-restore-sentinel {System.Diagnostics.Process.GetCurrentProcess().Id}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            _ = System.Diagnostics.Process.Start(psi);
            DiagnosticLog.Trace("shell.desktop", "图标恢复哨兵已拉起");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.desktop", $"图标恢复哨兵拉起失败：{ex.Message}");
        }
    }

    /// <summary>恢复 explorer 桌面图标为可见（无条件 SW_SHOW，幂等）：壳退出后用户桌面必须
    /// 回到 explorer 默认可见态（环境不可破坏）——无论此前是壳隐藏的还是用户双击隐藏的。
    /// 多个退出钩子先后触发重复调用无副作用。</summary>
    private void RestoreIcons()
    {
        try
        {
            // 图标层整体恢复（DefView + SysListView32）：只恢复 ListView 而 DefView 残留隐藏
            // 会让桌面右键整体失效（真机实证）。
            var listView = FindDesktopListView();
            var defView = FindDesktopDefView();
            if (listView != IntPtr.Zero)
            {
                _ = NativeMethods.ShowWindow(listView, SW_SHOW);
            }
            if (defView != IntPtr.Zero)
            {
                _ = NativeMethods.ShowWindow(defView, SW_SHOW);
            }

            if (listView != IntPtr.Zero || defView != IntPtr.Zero)
            {
                DiagnosticLog.Trace("shell.desktop", $"图标恢复：listView=0x{listView:X} defView=0x{defView:X} SW_SHOW 已执行");
            }
            else
            {
                // 查找失败必须落盘：这是"恢复静默落空"的唯一路径（explorer 桌面结构瞬时变化等）
                DiagnosticLog.Trace("shell.desktop", "图标恢复失败：找不到 DefView/SysListView32（Progman/WorkerW 链）");
            }

            // 【回归修复 2026-09-06】任务栏恢复：explorer 崩溃/壳异常退出后任务栏可能残留隐藏或消失。
            // 1) explorer 未运行则手动拉起（崩溃后未自动重启的场景）；
            // 2) 确保 Shell_TrayWnd / Shell_SecondaryTrayWnd 可见（NativeTaskbarManager 幂等）。
            try
            {
                var explorerProcs = System.Diagnostics.Process.GetProcessesByName("explorer");
                if (explorerProcs.Length == 0)
                {
                    DiagnosticLog.Trace("shell.desktop", "任务栏恢复：explorer 未运行，启动 explorer.exe");
                    System.Diagnostics.Process.Start("explorer.exe");
                }
                else
                {
                    BetterDesktop.Shell.Core.Windowing.NativeTaskbarManager.SetTaskbarVisible(true);
                    DiagnosticLog.Trace("shell.desktop", "任务栏恢复：Shell_TrayWnd SW_SHOW 已执行");
                }
            }
            catch (Exception ex2)
            {
                DiagnosticLog.Trace("shell.desktop", $"任务栏恢复异常：{ex2.Message}");
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.desktop", $"图标恢复异常：{ex.Message}");
        }
    }

    // ======== explorer 原生桌面图标的幂等显隐 ========
    // 窗口链：Shell(Progman) → SHELLDLL_DefView → SysListView32("FolderView")。
    // ⚠️ DefView 不一定挂在 Progman 直下：壁纸引擎（Wallpaper Engine）等会创建 WorkerW
    //    并把 DefView 移过去（DesktopWindow 的挂载查找 FindDesktopHostWindow 同样兼容此变体）。
    //    查找失败曾导致"隐藏成功、恢复失效"——这里两处必须用同一条兼容查找。

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;



    /// <summary>
    /// 定位 explorer 桌面图标 ListView（SysListView32）。
    /// 先查 Progman 直子 DefView；找不到再遍历 WorkerW（壁纸引擎/多显示器变体）。
    /// </summary>
    private static IntPtr FindDesktopListView()
    {
        var shell = NativeMethods.GetShellWindow();
        if (shell == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var defView = NativeMethods.FindWindowEx(shell, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView == IntPtr.Zero)
        {
            // DefView 被移到 WorkerW 下（壁纸引擎等）：遍历同级 WorkerW 找它
            IntPtr worker = IntPtr.Zero;
            do
            {
                worker = NativeMethods.FindWindowEx(shell, worker, "WorkerW", null);
                defView = worker == IntPtr.Zero ? IntPtr.Zero : NativeMethods.FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null);
            }
            while (defView == IntPtr.Zero && worker != IntPtr.Zero);
        }

        return defView == IntPtr.Zero
            ? IntPtr.Zero
            : NativeMethods.FindWindowEx(defView, IntPtr.Zero, "SysListView32", "FolderView");
    }

    /// <summary>explorer 原生桌面图标当前是否可见；窗口找不到按不可见处理（M10，不阻断）。</summary>
    private static bool AreNativeIconsVisible()
    {
        try
        {
            var listView = FindDesktopListView();
            return listView != IntPtr.Zero && NativeMethods.IsWindowVisible(listView);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 幂等设置 explorer 原生桌面图标可见性（ShowWindow 直接作用于 ListView，
    /// 非 toggle 翻转——重复调用无副作用，彻底消除翻转语义的时序坑）。
    /// 只隐 SysListView32：DefView 必须保持可见——它是自绘层的宿主（隐藏会连带
    /// 自绘层不可见，真机实证），且承载自绘层的父链。
    /// </summary>
    private static void SetNativeIconsVisible(bool visible)
    {
        try
        {
            var listView = FindDesktopListView();
            if (listView != IntPtr.Zero)
            {
                _ = NativeMethods.ShowWindow(listView, visible ? SW_SHOW : SW_HIDE);
            }
        }
        catch
        {
            // 显隐失败不阻断（M10）
        }
    }

    /// <summary>定位 SHELLDLL_DefView（兼容 Progman 直子与 WorkerW 变体；FindDesktopListView 的父级）。</summary>
    private static IntPtr FindDesktopDefView()
    {
        var shell = NativeMethods.GetShellWindow();
        if (shell == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var defView = NativeMethods.FindWindowEx(shell, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView != IntPtr.Zero)
        {
            return defView;
        }

        IntPtr worker = IntPtr.Zero;
        do
        {
            worker = NativeMethods.FindWindowEx(shell, worker, "WorkerW", null);
            defView = worker == IntPtr.Zero ? IntPtr.Zero : NativeMethods.FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null);
        }
        while (defView == IntPtr.Zero && worker != IntPtr.Zero);

        return defView;
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



    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public NativeMethods.POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LVHITTESTINFO
    {
        public NativeMethods.POINT pt;
        public uint flags;
        public int iItem;
        public int iSubItem;
    }








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
        _mouseHook = NativeMethods.SetWindowsHookEx(WhMouseLl, _mouseHookProc, NativeMethods.GetModuleHandle(null), 0);
        DiagnosticLog.Trace("shell.desktop", $"原生桌面双击钩子安装：{_mouseHook != IntPtr.Zero}");
    }

    private void RemoveDesktopDoubleClickHook()
    {
        if (_mouseHook == IntPtr.Zero)
        {
            return;
        }

        _ = NativeMethods.UnhookWindowsHookEx(_mouseHook);
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

        return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    /// <summary>屏幕点是否落在原生桌面上（Progman/WorkerW/DefView/SysListView32 任一）。
    /// 命中 SysListView32 时用 LVM_HITTEST 排除"双击在原生图标上"的情况。</summary>
    private static bool IsOverNativeDesktop(NativeMethods.POINT pt)
    {
        try
        {
            var hwnd = NativeMethods.WindowFromPoint(pt);
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            var sb = new StringBuilder(64);
            if (NativeMethods.GetClassName(hwnd, sb, 64) <= 0)
            {
                return false;
            }

            var cls = sb.ToString();
            if (cls == "SysListView32")
            {
                var ht = new LVHITTESTINFO { pt = pt };
                _ = NativeMethods.ScreenToClient(hwnd, ref ht.pt);
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
