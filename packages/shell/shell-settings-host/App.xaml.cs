// BetterDesktop.Settings — 「设置中心」独立进程（2026-09-17 用户定调：托盘=中转站，功能按需启动）
//
// 【为什么必须独立】设置中心是**管理常驻功能**的控制台 —— 恰恰在主程序（Host）关掉时最需要它。
// 旧链路：托盘「打开设置中心」→ CLI `open-settings` → 命名管道 → **宿主** → 宿主进程内开窗
//（`Bootstrap.cs` `case "open-settings"`）→ 即"为了看一眼设置，先把整个壳（≈125MB + 菜单栏/Dock/桌面层）
// 拉起来"。本进程把这条链改成：托盘直接拉起本 exe，**不依赖宿主、不建任何壳面窗口**，关窗即退（内存立刻归还）。
//
// 【怎么做到"不加载插件也能显示分区"】设置分区原本由各插件在 LoadAsync 里
// `registry.Register(new XxxSection(...))` 贡献 —— 不加载插件就几乎空白。这里改成**分区目录**（SectionCatalog）：
// **引用**（≠ 加载）各插件程序集，直接 new 出各自的 `ISettingsSection`。三项特殊依赖按用户 2026-09-17 的裁决处理：
//   · 需要活体回调的（菜单栏 / 灵动岛）→ 传空回调 = **只持久化，下次该功能启动时生效**（用户原话：
//     "记录设置对于关闭功能的最后修改，等下次功能启动后再应用修改"）；
//   · 需要活体服务但已做 null 兜底的（任务栏外观 / 开始菜单 / Dock 固定项）→ 分区显示占位，不崩；
//   · 硬依赖 `IHotkeyRegistryService` 的「热键」分区 → 该服务只有内核 hotkeys 插件提供、无静态桥，
//     故独立进程**暂不显示热键分区**（M3 热键面板独立后随注册表一起可用）；宿主内打开的设置窗仍带热键分区。
//
// 【主题】与宿主同一份外观字典（shell-core/Surface/ShellTheme.xaml）+ 同一套令牌推导
//（AppearanceService.Initialize()），所以视觉一致，不复制样式。

using System;
using System.Threading;
using System.Windows;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.Settings;
using BetterDesktop.Shell.Settings.Contracts;
using BetterDesktop.Shell.Settings.Services;

namespace BetterDesktop.Shell.SettingsHost;

public partial class App : Application
{
    private const string MutexName = "BetterDesktop.Settings.SingleInstance";
    private const string OpenEventName = "BetterDesktop.Settings.Open";

    private Mutex? _mutex;
    private EventWaitHandle? _openSignal;
    private SettingsService? _settings;
    private IContext? _context;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Diagnosis.Install();

        // 单实例：重复打开 = 把已有窗口带到前台（与剪贴板面板同款；命名事件不带载荷，故只区分"打开"）。
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var isPrimary);
        if (!isPrimary)
        {
            try
            {
                using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, OpenEventName);
                signal.Set();
            }
            catch (Exception ex)
            {
                Log($"单实例转发失败: {ex.Message}");
            }

            Shutdown();
            return;
        }

        try
        {
            _openSignal = new EventWaitHandle(false, EventResetMode.AutoReset, OpenEventName);
            StartSignalWatcher(_openSignal, () => Dispatcher.BeginInvoke(new Action(BringToFront)));
        }
        catch (Exception ex)
        {
            Log($"单实例信号监听失败: {ex.Message}");
        }

        BuildAndShow();
    }

    /// <summary>装配最小依赖 + 分区目录 + 打开设置窗（任何一步失败都要留痕，不留"点了没反应"）。</summary>
    private void BuildAndShow()
    {
        try
        {
            // ① 最小内核上下文：只为拿到事件总线（外观变更要通知已开的窗口重绘）。不装配任何插件。
            _context = new CordisContext(logSink: (level, message) => Log($"[{level}] {message}"));

            // ② 设置服务：context 可空（headless）——这里仍传 context，使外观变更事件能广播给本进程窗口。
            //    落盘走跨进程 mutex + 读-合并-写，与宿主/其它进程并发写入安全。
            _settings = new SettingsService(_context);

            // ③ 外观引导：把令牌推进 Application.Resources（分区的 DynamicResource 才有值）。
            var appearance = new AppearanceService(_settings, _context);
            appearance.Initialize();

            // ④ 分区目录（引用插件程序集、不加载插件）
            var registry = new SettingsSectionRegistry();
            SectionCatalog.RegisterAll(registry, Log);

            // ⑤ 毛玻璃（可选）：拿不到就用 Null 实现（窗口照常显示，只是不套材质）。
            IVibrancyService vibrancy = new VibrancyService();

            var window = new SettingsWindow(registry, _settings, appearance, vibrancy, _context.Events);
            window.Closed += (_, _) =>
            {
                Log("设置窗已关闭 → 进程退出（内存归还）");
                Shutdown();
            };

            MainWindow = window;
            window.Show();
            Log($"设置窗已显示：分区 {registry.Sections.Count} 个");
        }
        catch (Exception ex)
        {
            Log($"装配失败：{ex}");
            Shutdown();
        }
    }

    private void BringToFront()
    {
        try
        {
            if (MainWindow is { } window)
            {
                if (!window.IsVisible)
                {
                    window.Show();
                }

                if (window.WindowState == WindowState.Minimized)
                {
                    window.WindowState = WindowState.Normal;
                }

                window.Activate();
            }
        }
        catch (Exception ex)
        {
            Log($"前置窗口失败: {ex.Message}");
        }
    }

    private void StartSignalWatcher(EventWaitHandle handle, Action onSignal)
    {
        var watcher = new Thread(() =>
        {
            while (handle.WaitOne())
            {
                try
                {
                    onSignal();
                }
                catch (Exception ex)
                {
                    Log($"信号处理失败: {ex.Message}");
                }
            }
        })
        {
            IsBackground = true
        };
        watcher.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _settings?.Dispose(); // flush（跨进程落盘）
            (_context as IDisposable)?.Dispose();
            _openSignal?.Dispose();
            _mutex?.Dispose();
        }
        catch (Exception ex)
        {
            Log($"退出收尾异常: {ex.Message}");
        }

        base.OnExit(e);
    }

    private static void Log(string message) => SettingsHostLog.Trace(message);

    /// <summary>诊断钩子：全局异常一律落盘（本进程不该"静默什么都不发生"）。</summary>
    private static class Diagnosis
    {
        public static void Install()
        {
            SettingsHostLog.Install();
            Current.DispatcherUnhandledException += (_, args) =>
            {
                Log($"Dispatcher 未处理异常: {args.Exception}");
                args.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                Log($"AppDomain 未处理异常: {args.ExceptionObject}");
        }
    }
}
