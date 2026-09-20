using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;

namespace BetterDesktop.Clipboard.Panel;

/// <summary>面板进程静态装配（App 启动 / 单实例转发共用入口）。</summary>
public static class PanelApp
{
    private static PanelMainWindow? _mainWindow;

    public static EntryHost Host
        => (Application.Current as App)?.Host
           ?? throw new InvalidOperationException("面板尚未初始化");

    /// <summary>打开完整面板（侧边栏手柄 / --open 参数共用入口；右侧滑出）。</summary>
    public static void ShowMainWindow()
    {
        var app = Application.Current;
        app.Dispatcher.BeginInvoke(new Action(() =>
        {
            var win = EnsureMainWindow();
            if (win.IsVisible)
            {
                win.HidePanel();
            }
            win.ShowRightAligned();
        }));
    }

    /// <summary>
    /// 确保主窗口可复用：窗口被外部 Close 过（宿主退出/系统消息）后 WPF 禁止再次 Show()
    ///（"关闭窗口后，无法设置可见性"），必须重建实例。见 docs/plans 2026-09-16 面板打不开修复。
    /// </summary>
    private static PanelMainWindow EnsureMainWindow()
    {
        if (_mainWindow is null || !_mainWindow.IsLoaded)
        {
            _mainWindow = new PanelMainWindow(Host, new VibrancyService());
        }
        return _mainWindow;
    }

    /// <summary>侧边栏手柄专用入口（与 ShowMainWindow 同实现，保留语义别名）。</summary>
    public static void ShowMainWindowRight() => ShowMainWindow();

    /// <summary>
    /// 以指定筛选打开面板（引擎全局热键 Ctrl+Shift+P「收藏视图」→ 命名事件 → 本入口）。
    /// <para>
    /// 与 <see cref="ShowMainWindow"/> 的差别：**不做显示/隐藏切换**（幂等）—— 热键的语义是
    /// "让我看到收藏"，连按两次不该把面板关掉。筛选 key 先登记、在 <see cref="PanelMainWindow.ShowRightAligned"/>
    /// 收尾时应用（首显时筛选 chip 才刚由 BuildContent 建出来）。
    /// </para>
    /// </summary>
    public static void ShowMainWindowWithFilter(string filterKey)
    {
        var app = Application.Current;
        app.Dispatcher.BeginInvoke(new Action(() =>
        {
            var win = EnsureMainWindow();
            win.ShowWithFilter(filterKey);
            win.ShowRightAligned();
        }));
    }

    /// <summary>
    /// `Ctrl+Shift+V` / `--open` 的统一入口（引擎热键 → 面板 exe --open → 单实例信号）。
    /// <para>
    /// <b>分流</b>：若按序粘贴会话进行中 → **粘下一条**，而不是弹面板 ——
    /// 否则用户每按一次"粘下一条"都会被弹出的面板打断（面板会抢走前台焦点，
    /// 而 SendPaste 的 Ctrl+V 是打给前台窗口的）。
    /// </para>
    /// </summary>
    public static void OnOpenRequested()
    {
        var app = Application.Current;
        app.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_mainWindow is { } w && w.IsSequentialPasteActive)
            {
                w.PasteNextSequential();
                return;
            }
            ShowMainWindow();
        }));
    }
}

public partial class App : Application
{
    private const string MutexName = "BetterDesktop.Clipboard.Panel.SingleInstance";
    private const string OpenEventName = "BetterDesktop.Clipboard.Panel.OpenPanel";

    /// <summary>
    /// 「收藏视图」专用信号（引擎热键 Ctrl+Shift+P 拉起第二实例时置位）。
    /// <para>
    /// 【2026-09-14】为什么必须与 <see cref="OpenEventName"/> 分开：命名事件**不携带载荷**，
    /// 主实例只能靠事件名区分意图。若共用一条事件，第二次按 P 只会把面板弹出来、筛选不动
    ///（引擎侧对应 `open_panel_with(true)` 追加的 `--favorites` 参数）。
    /// </para>
    /// </summary>
    private const string OpenFavoritesEventName = "BetterDesktop.Clipboard.Panel.OpenFavorites";

    private Mutex? _mutex;
    private EventWaitHandle? _openSignal;
    private EventWaitHandle? _openFavoritesSignal;
    private bool _isPrimary;

    /// <summary>面板进程持有的引擎 IPC 连接（PanelApp.Host 经 Application.Current 取）。</summary>
    public EntryHost? Host { get; private set; }

    /// <summary>
    /// 进程是否正在退出。面板窗口在非退出状态下拦截一切关闭请求（转隐藏）——
    /// 见 <see cref="PanelMainWindow.OnClosing"/>。仅进程退出时允许窗口真正销毁。
    /// </summary>
    public static bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 面板进程把内核诊断日志接到 panel.log：毛玻璃（shell-core `DwmHelper`）等共用组件的
        // DiagnosticLog.Trace 否则在面板里被静默丢弃（2026-09-14 面板改用宿主同一份毛玻璃实现）。
        // 一并交出 flush：崩溃/退出路径需要同步刷盘。
        DiagnosticLog.SetSink((_, message) => PanelLog.Trace(message), PanelLog.Flush);
        PanelLog.Banner();

        // 全局异常兜底：独立面板进程不允许静默退出——任何未处理异常都落盘（panel.log）后决定是否继续
        DispatcherUnhandledException += (_, args) =>
        {
            // Crash 写异常链 + 环境快照并同步刷盘（异步队列在崩溃时可能来不及刷，丢的正是原因）。
            DiagnosticLog.Crash("panel", "DispatcherUnhandledException", args.Exception, terminating: false);
            args.Handled = true; // UI 线程异常兜住，进程保持存活（异常上下文可能已损坏，重试即可）
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            DiagnosticLog.Crash(
                "panel",
                "AppDomain.UnhandledException",
                args.ExceptionObject as Exception,
                terminating: args.IsTerminating);
        };

        // --favorites = 引擎热键 Ctrl+Shift+P「收藏视图」（与 --open 叠加传入）
        var wantsFavorites = Array.Exists(e.Args, a => a.Equals("--favorites", StringComparison.OrdinalIgnoreCase));

        _mutex = new Mutex(initiallyOwned: true, MutexName, out _isPrimary);
        if (!_isPrimary)
        {
            // 已有面板进程：转发 --open 语义（命名事件通知主实例拉面板）后退出。
            // 【2026-09-14】收藏视图走**独立事件** —— 命名事件不带载荷，主实例只能靠事件名区分意图。
            try
            {
                using var signal = new EventWaitHandle(
                    false, EventResetMode.AutoReset,
                    wantsFavorites ? OpenFavoritesEventName : OpenEventName);
                signal.Set();
            }
            catch (Exception ex)
            {
                PanelLog.Trace($"单实例转发失败: {ex.Message}");
            }
            Shutdown();
            return;
        }

        // 单实例：注册信号监听（后续宿主/用户再启动一次即拉面板）
        try
        {
            _openSignal = new EventWaitHandle(false, EventResetMode.AutoReset, OpenEventName);
            StartSignalWatcher(_openSignal, () =>
            {
                // 【2026-09-12】不直接 ShowMainWindow —— 交给 OnOpenRequested 分流：
                // 按序粘贴会话进行中时该信号代表"粘下一条"（Ctrl+Shift+V），
                // 此时弹面板会把焦点从用户的目标窗口抢走，正好破坏刚粘上去的内容。
                Dispatcher.BeginInvoke(new Action(PanelApp.OnOpenRequested));
            });

            _openFavoritesSignal = new EventWaitHandle(false, EventResetMode.AutoReset, OpenFavoritesEventName);
            StartSignalWatcher(_openFavoritesSignal, () =>
            {
                // 「收藏视图」：打开面板并切到「收藏」筛选（chip key = pinned，见 BuildFilters）
                Dispatcher.BeginInvoke(new Action(() => PanelApp.ShowMainWindowWithFilter("pinned")));
            });
        }
        catch (Exception ex)
        {
            PanelLog.Trace($"单实例信号监听失败: {ex.Message}");
        }

        // ① 主题引导：读宿主 settings.json → 推 App.Resources 令牌（随后所有窗口/控件 DynamicResource 生效）
        // 实现在 shell-core/Surface/EntryTheme（与截图入口面共用同一份推导与同一套设计刻度）
        PanelTheme.Load();
        EntryTheme.ApplyToAppResources();
        // 细滚动条（隐式样式）：历史列表的默认 WPF 滚动条同样是"带箭头的系统样式"，
        // 与深色面板割裂（截图入口面先修，此处共用同一份实现）。
        SlimScrollBar.Install(this);
        PanelLog.Trace($"主题引导完成（mode={EntryTheme.ThemeMode}, loaded={EntryTheme.Loaded}, entryStyle={PanelTheme.ExtensionConfig().EntryStyle}）");

        // ② 入口装配（entry-style 判定）+ IPC 连接
        Host = EntryHost.Create();
        Host.ConnectAndProbe();

        // ③ 引擎探活：无引擎进程则拉起（连接由 IPC 重连循环兜底）
        EnsureEngineRunning();

        // ④ 前台入口：--open 参数或默认常驻（侧边栏手柄）
        var openRequested = Array.Exists(e.Args, a => a.Equals("--open", StringComparison.OrdinalIgnoreCase));
        Host.EnsureFrontEntry();
        if (openRequested)
        {
            if (wantsFavorites)
            {
                PanelApp.ShowMainWindowWithFilter("pinned");
            }
            else
            {
                PanelApp.ShowMainWindow();
            }
        }
    }

    /// <summary>后台监听一个命名事件（收到即执行回调）。线程 IsBackground：进程退出自然结束。</summary>
    private void StartSignalWatcher(EventWaitHandle handle, Action onSignal)
    {
        var watcher = new Thread(() =>
        {
            while (handle.WaitOne())
            {
                onSignal();
            }
        })
        {
            IsBackground = true
        };
        watcher.Start();
    }

    private static void EnsureEngineRunning()
    {
        // 【2026-09-20 收敛】原先这里本地枚举同名进程判活 + 自己定位 exe + 自己拉起。
        // 现在只发一句"请 core 确保引擎在跑"：定位/判活/gate/重复拉起防护全在 core，
        // 面板不再持有 exe 路径与进程操作。失败仍会留痕（PanelLog.Trace 由 EngineProber 转发）。
        if (!EngineProber.EnsureEngine())
        {
            PanelLog.Trace("请 core 启动引擎未成功——面板仍可启动，引擎由宿主/检索入口按需拉起");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        IsExiting = true;
        try { Host?.Dispose(); } catch { /* 退出清理 */ }
        try { _mutex?.Dispose(); } catch { /* 退出清理 */ }
        try { _openSignal?.Dispose(); } catch { /* 退出清理 */ }
        try { _openFavoritesSignal?.Dispose(); } catch { /* 退出清理 */ }
        base.OnExit(e);
    }
}
