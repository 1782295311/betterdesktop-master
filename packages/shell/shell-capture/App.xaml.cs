using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using BetterDesktop.Shell.Capture.Cli;
using BetterDesktop.Shell.Capture.Core;
using BetterDesktop.Shell.Core.Surface;

namespace BetterDesktop.Shell.Capture;

/// <summary>
/// capture exe 入口（纯代码 Application，无 App.xaml —— 避免 WPF 生成的 Main 与自定义 Main 冲突）。
/// 常驻：全局热键（默认 Win+Shift+B）+ 托盘图标；按需拉起覆盖层/编辑器/贴图/OCR 面板。
/// 单实例：二次启动（含 CLI --capture 与用户再按热键）通过命名事件唤醒首个实例后退出。
/// </summary>
public sealed class App : Application
{
    private const string SingleInstanceMutex = "Local\\BetterDesktop.Capture.SingleInstance";
    private const string TriggerEvent = "Local\\BetterDesktop.Capture.Trigger";

    private Mutex? _mutex;
    private bool _ownsMutex;
    private EventWaitHandle? _trigger;
    private Thread? _triggerListener;
    private volatile bool _running;

    /// <summary>
    /// 一次性模式（<c>--capture-now</c>）：由**常驻热键持有者**（Agent）在按下截图热键时拉起本进程，
    /// 立即进入框选流程，**流程结束即退出** —— 不再常驻（私有内存实测 84MB）、不再自己占热键。
    /// 【2026-09-17 内存预算 · 用户定调"本质是工作不是灯泡"】截图是典型的"用一下就走"的工作。
    /// 不带该参数 = 原行为完全不变（常驻 + 自己注册热键 + 托盘），两条路互不影响。
    /// </summary>
    private const string CaptureNowArg = "--capture-now";

    private bool _oneShot;

    private HotKeyManager? _hotKey;
    private TrayIcon? _tray;
    private CaptureFlow? _flow;
    private ScreenCaptureService? _captureService;

    public static bool IsCliRequested(string[] args) =>
        args.Length > 0 && string.Equals(args[0], "--capture", StringComparison.OrdinalIgnoreCase);

    [STAThread]
    private static int Main(string[] args)
    {
        // CLI 模式（headless）：不拉起 WPF 窗口，结果写剪贴板后退出（exit 0 成功 / 1 失败）。
        if (IsCliRequested(args))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            try
            {
                return CaptureCli.Run(args[1..]);
            }
            finally
            {
                FreeConsole();
            }
        }

        var app = new App();
        return app.Run();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        CaptureLog.Banner();

        // 全局兜底：任何 UI 未处理异常只记日志并保持进程存活（真机多次出现功能窗口
        // 未处理异常导致整个 capture 进程消失、功能"点了没反应"）
        DispatcherUnhandledException += (_, args) =>
        {
            CaptureLog.Error($"UI 未处理异常（已拦截，保持运行）：{args.Exception}");
            CaptureLog.Flush(); // 异步队列在进程被终止时会丢最后几条，崩溃路径必须同步刷盘
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            // 原来只挂了 Dispatcher 那一层：非 UI 线程的未处理异常会让本进程静默消失且不留任何记录。
            CaptureLog.Error($"进程级未处理异常: {args.ExceptionObject}");
            CaptureLog.Flush();
        };

        // ---- 单实例 + 唤醒 ----
        _mutex = new Mutex(true, SingleInstanceMutex, out bool firstInstance);
        _ownsMutex = firstInstance;
        if (!firstInstance)
        {
            // 已有实例在跑：通知它开始一次截图，然后本实例退出。
            try
            {
                using var signal = EventWaitHandle.OpenExisting(TriggerEvent);
                signal.Set();
            }
            catch (Exception ex)
            {
                CaptureLog.Warn($"唤醒主实例失败：{ex.Message}");
            }
            Shutdown();
            return;
        }

        // ---- 主题引导（必须先于任何窗口创建）----
        // 读宿主 settings.json → 推 App.Resources 令牌；覆盖层/编辑器/OCR 面板/贴图四类窗口
        // 全部据此取色。实现在 shell-core/Surface/EntryTheme（与剪贴板面板共用同一份推导与刻度）。
        // 读失败不回退"硬编码深色"而是走令牌默认值（与宿主同款），并留一条诊断。
        EntryTheme.OnDiagnostic = CaptureLog.Warn;
        EntryTheme.Load();
        EntryTheme.ApplyToAppResources();
        // 细滚动条（隐式样式）：默认 WPF 滚动条是"上下箭头 + 方头浅灰轨道"的系统样式，
        // 与深色面板严重割裂（OCR 结果列表尤其明显）。必须在主题令牌之后安装（模板引用令牌）。
        SlimScrollBar.Install(this);
        CaptureLog.Info($"主题引导完成（mode={EntryTheme.ThemeMode}, loaded={EntryTheme.Loaded}）");

        _running = true;
        _trigger = new EventWaitHandle(false, EventResetMode.AutoReset, TriggerEvent);
        _triggerListener = new Thread(ListenForTrigger) { IsBackground = true, Name = "capture-trigger" };
        _triggerListener.Start();

        _captureService = new ScreenCaptureService();
        _flow = new CaptureFlow(_captureService, CompleteFlow);

        // ---- 热键（默认 Win+Shift+B；可被 settings.json 覆盖/停用，见 HotKeyManager）----
        try
        {
            _hotKey = new HotKeyManager(OnHotKey);
            _hotKey.Register();
        }
        catch (Exception ex)
        {
            CaptureLog.Warn($"热键注册失败（{ex.Message}），仅托盘可用");
        }

        // ---- 托盘 ----
        try
        {
            _tray = new TrayIcon(
                onCapture: () => Dispatcher.InvokeAsync(StartCapture),
                onExit: () => Dispatcher.InvokeAsync(Shutdown));
        }
        catch (Exception ex)
        {
            CaptureLog.Warn($"托盘创建失败（{ex.Message}）");
        }

        // 启动清扫：崩溃残留的 24h 前临时文件
        int cleaned = TempFileManager.CleanupStartup();
        if (cleaned > 0)
        {
            CaptureLog.Info($"启动清扫删除 {cleaned} 个过期临时文件");
        }

        _oneShot = Array.Exists(e.Args, a => string.Equals(a, CaptureNowArg, StringComparison.OrdinalIgnoreCase));
        if (_oneShot)
        {
            CaptureLog.Info("--capture-now：一次性模式（截完即退，内存归还）");
            Dispatcher.BeginInvoke(StartCapture, DispatcherPriority.Background);
        }

        CaptureLog.Info("capture exe 就绪（Win+Shift+B 或托盘图标开始截图）");
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }

    private void ListenForTrigger()
    {
        while (_running && _trigger is not null)
        {
            try
            {
                _trigger.WaitOne();
                if (_running)
                {
                    Dispatcher.BeginInvoke(StartCapture, DispatcherPriority.Background);
                }
            }
            catch (Exception ex)
            {
                CaptureLog.Warn($"唤醒监听异常：{ex.Message}");
                Thread.Sleep(500);
            }
        }
    }

    private void OnHotKey() => Dispatcher.BeginInvoke(StartCapture, DispatcherPriority.Background);

    private void StartCapture()
    {
        if (_flow is null || _flow.IsActive)
        {
            return; // 正在截图：忽略重入（含重复热键）
        }
        _flow.Start();
    }

    /// <summary>会话结束回调（写剪贴板成功/失败/取消）——托盘静默反馈，不抢前台（红线 4）。</summary>
    private void CompleteFlow(string? message)
    {
        if (!string.IsNullOrEmpty(message))
        {
            _tray?.ShowBalloon("截图", message, 1500);
        }

        // 一次性模式：截完即退 —— 进程消失、内存归还；下次按热键由常驻持有者再拉起一个干净实例。
        if (_oneShot)
        {
            CaptureLog.Info("一次性截屏结束，退出进程（按需模式）");
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _running = false;
        _trigger?.Set();
        _hotKey?.Dispose();
        _tray?.Dispose();
        _captureService?.Dispose();
        _trigger?.Dispose();
        if (_mutex is not null && _ownsMutex)
        {
            // 仅首个实例拥有 mutex（非首实例二次启动走 Shutdown 不持有，ReleaseMutex 会崩）
            _mutex.ReleaseMutex();
        }
        _mutex?.Dispose();
        base.OnExit(e);
    }

    // ---------------- 控制台附着（WinExe 的 stdout 不可见；CLI 模式附着父控制台） ----------------
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();

    private const int ATTACH_PARENT_PROCESS = -1;
}
