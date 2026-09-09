using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;
using BetterDesktop.Kernel.Core;

namespace BetterDesktop.Host;

/// <summary>WPF 应用入口。</summary>
public partial class App : Application
{
    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // M3 命令桥：--menu-cmd <action> <path> → 转发给运行中的首实例后退出
        //（解析顺序：args[0]=--menu-cmd, args[1]=action, args[2]=path 可选）。
        var args = e.Args;

        // 图标恢复哨兵：--icon-restore-sentinel <pid> → 等宿主退出后恢复桌面图标，本进程退出。
        // 必须在单实例互斥之前（运行中的宿主持有 Mutex，哨兵若先抢锁会立即退出失去守护）。
        if (args.Length >= 2 &&
            string.Equals(args[0], "--icon-restore-sentinel", StringComparison.Ordinal) &&
            int.TryParse(args[1], out var hostPid))
        {
            IconRestoreSentinel.Run(hostPid);
            Shutdown(0);
            return;
        }

        // 菜单服务：--menu-service <x> <y> <mode> <path...> → 独立子进程构建原生菜单后退出。
        // 必须在单实例互斥之前（菜单服务是独立短生命周期进程，不抢主实例锁；第三方扩展
        // 在子进程加载，崩溃只崩子进程，不拖垮宿主与 explorer——文件管理器逻辑）。
        if (args.Length >= 4 && string.Equals(args[0], "--menu-service", StringComparison.Ordinal))
        {
            MenuService.Run(args);
            Shutdown(0);
            return;
        }

        if (args.Length >= 2 && string.Equals(args[0], "--menu-cmd", StringComparison.Ordinal))
        {
            var cmdAction = args[1];
            var cmdPath = args.Length >= 3 ? args[2] : string.Empty;
            // 命令进程 sink 未注入（宿主完整启动才 SetSink），Trace 不可见；写独立诊断文件供右键功能排查。
            var diagFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bdt-menu-cmd.log");
            void Diag(string msg)
            {
                try { System.IO.File.AppendAllText(diagFile, $"[{DateTime.Now:HH:mm:ss}] {msg}\r\n"); } catch { }
            }

            Diag($"进入 --menu-cmd action={cmdAction} path={cmdPath} exe={Environment.ProcessPath}");
            if (MenuCommandPipe.TrySend(cmdAction, cmdPath))
            {
                Diag("转发成功（已有实例）");
                Shutdown(0);
                return;
            }

            // 无运行实例：宿主已退出时右键命令仍需可用——拉起宿主进程，等待命令管道就绪后重发命令。
            // 宿主启动完成（含插件加载）才起命令管道；循环重发直到成功或超时（上限 ~12s）。
            // 注意：此处必须 return，不再"继续正常启动"——否则会与拉起的宿主抢单实例锁重复启动。
            Diag("无运行实例，拉起宿主并等待转发");
            var hostExe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(hostExe))
            {
                try
                {
                    var psi = new ProcessStartInfo(hostExe)
                    {
                        UseShellExecute = false,
                        WorkingDirectory = AppContext.BaseDirectory,
                    };
                    using var proc = Process.Start(psi);
                    Diag($"宿主进程已拉起 pid={proc?.Id ?? -1}");
                    for (var i = 0; i < 48; i++)
                    {
                        Thread.Sleep(250);
                        if (proc is not null && proc.HasExited)
                        {
                            Diag($"宿主已退出（拉起失败?）exit={proc.ExitCode}");
                            break; // 宿主拉起失败/立即退出，命令无法送达
                        }
                        if (MenuCommandPipe.TrySend(cmdAction, cmdPath))
                        {
                            Diag($"转发成功（第 {i + 1} 次尝试）");
                            Shutdown(0);
                            return;
                        }
                    }
                    Diag("等待宿主就绪超时，命令未送达");
                }
                catch (Exception ex)
                {
                    Diag($"拉起宿主异常: {ex}");
                }
            }
            else
            {
                Diag("无法解析宿主路径");
            }
            Shutdown(0);
            return;
        }

        // 自绘桌面开关（2026-09-07 系统右键菜单入口）：--toggle-desktop → 有实例经命令桥热切，
        // 无实例直写 settings.json 并按需拉起宿主。必须在单实例互斥之前（开关是短生命周期命令进程，
        // 不抢主实例锁——与 --menu-cmd 同款设计）。
        if (args.Length >= 1 && string.Equals(args[0], "--toggle-desktop", StringComparison.Ordinal))
        {
            if (MenuCommandPipe.TrySend("toggle-desktop", string.Empty))
            {
                Shutdown(0);
                return;
            }
            DesktopToggleCommand.ToggleAndMaybeLaunch();
            Shutdown(0);
            return;
        }

        // 自绘UI开关（2026-09-07 系统右键「自绘桌面 ▸」入口）：--toggle-key <icons|menubar|dock>
        // → 有实例经命令桥热切，无实例直写 settings.json（下次宿主启动生效）。
        // 必须在单实例互斥之前（短生命周期命令进程，不抢主实例锁——与 --toggle-desktop 同款设计）。
        if (args.Length >= 2 && string.Equals(args[0], "--toggle-key", StringComparison.Ordinal))
        {
            if (MenuCommandPipe.TrySend("toggle-key", args[1]))
            {
                Shutdown(0);
                return;
            }
            ToggleKeyCommand.Run(args[1]);
            Shutdown(0);
            return;
        }

        // 单实例互斥：已有实例在运行则直接退出（含 watchdog 拉起与手动启动并存的双实例）。
        if (!HostWatchdog.TryAcquireSingleInstance())
        {
            Shutdown(0);
            return;
        }

        // UI 线程异步异常：Handled=true 表示已处理，仅记日志继续运行，不自杀式重启
        // （当初"Handled=true 仍 RestartFromFatal"是把可恢复异常放大为致命重启的错误设计）。
        // 只有进程级致命异常（AppDomain.UnhandledException）才走 C1 自重启。
        DispatcherUnhandledException += (_, ea) =>
        {
            DiagnosticLog.Trace("App", $"DispatcherUnhandledException: {ea.Exception.GetType().Name}: {ea.Exception.Message}");
            DiagnosticLog.Trace("App", "DispatcherUnhandledException STACK: " + (ea.Exception.StackTrace ?? "(null)"));
            if (ea.Exception.InnerException is { } ie)
            {
                DiagnosticLog.Trace("App", $"DispatcherUnhandledException INNER: {ie.GetType().Name}: {ie.Message}");
            }
            ea.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, ea) =>
        {
            if (ea.ExceptionObject is Exception ex)
            {
                DiagnosticLog.Trace("App", $"AppDomain.UnhandledException: {ex.GetType().Name}: {ex.Message}");
                // 致命异常：直接自重启干净进程（Environment.Exit 终止当前进程，无 Handled 可设）
                HostWatchdog.RestartFromFatal("AppDomain.UnhandledException", ex);
            }
        };

        try
        {
            // 初始化弹窗:先播放品牌动画,动画播完(或点击跳过/失败兜底)后再装配主程序。
            // 必须提前改为显式关闭模式:splash 是首个且唯一窗口,若保持默认
            // OnLastWindowClose,它一关闭 WPF 就会退出进程,Closed 里的 Bootstrap 将失去上下文。
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var splash = new Views.SplashWindow();
            splash.Closed += async (_, _) =>
            {
                try
                {
                    await Bootstrap.Build();
                }
                catch (Exception ex)
                {
                    HostWatchdog.RestartFromFatal("Bootstrap.Build", ex);
                }
            };
            splash.Show();
        }
        catch (Exception ex)
        {
            HostWatchdog.RestartFromFatal("App.OnStartup.Splash", ex);
        }
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        HostWatchdog.Release();
        base.OnExit(e);
    }
}
