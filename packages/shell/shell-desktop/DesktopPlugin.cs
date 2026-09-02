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
//   恢复函数按 _iconsHidden 标记幂等：无论多个钩子先后触发，只恢复一次。
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
    private bool _iconsHidden;
    private bool _running;
    private bool _settingsHooked;

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

        if (IsDesktopEnabled)
        {
            StartDesktop();
        }
        else
        {
            context.Logger.Info($"{Name} 已跳过：components.desktop=false（使用 explorer 原生桌面）");
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

        StopDesktop();
        _browser = null;
        return Task.CompletedTask;
    }

    /// <summary>设置变更：components.desktop 开关即时启停；其余键交窗口自处理。</summary>
    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Key != "components.desktop")
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(ApplyEnabledState));
            return;
        }

        ApplyEnabledState();
    }

    private void ApplyEnabledState()
    {
        if (IsDesktopEnabled)
        {
            StartDesktop();
        }
        else
        {
            StopDesktop();
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
            _iconsHidden = true;
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

    /// <summary>恢复 explorer 桌面图标；按 _iconsHidden 标记只恢复一次（多钩子先后触发不重复）。</summary>
    private void RestoreIcons()
    {
        if (!_iconsHidden)
        {
            return;
        }

        _iconsHidden = false;
        try
        {
            // 幂等恢复：ShowWindow(SW_SHOW)，重复调用无副作用。
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
}

/// <summary>Vibrancy 服务缺失时的空壳兜底（窗口不变毛玻璃，但不崩溃，M10）。</summary>
internal sealed class NullVibrancy : IVibrancyService
{
    public static NullVibrancy Instance { get; } = new();

    private NullVibrancy() { }

    public void Apply(IntPtr hwnd, VibrancyStyle style, bool roundCorners, bool smallRadius) { }

    public void Disable(IntPtr hwnd) { }
}
