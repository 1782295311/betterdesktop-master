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

        // （--menu-service 子进程入口已于 2026-09-10 移除：桌面右键回归自绘 DesktopMenuPopup，
        //   跨进程菜单构建（含 explorer DefView 转发）整体退役，无启动方。）

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

            // 无运行实例：转交 CLI（M3.1 无宿主 headless 执行 / 需宿主动作提示）。
            // 不再拉起宿主静默装配（用户拍板：需宿主完整在线的动作明确提示，不偷偷拉起；
            // headless 动作由 CLI 直执行、干完退出——"不启用程序本体"）。
            Diag("无运行实例，转交 CLI");
            var cliExe = System.IO.Path.Combine(AppContext.BaseDirectory, "BetterDesktop.Cli.exe");
            if (System.IO.File.Exists(cliExe))
            {
                try
                {
                    var psi = new ProcessStartInfo(cliExe)
                    {
                        UseShellExecute = false,
                        WorkingDirectory = AppContext.BaseDirectory,
                    };
                    // C1：参数**逐项**加入，禁止字符串插值拼命令行。
                    // cmdAction/cmdPath 直接来自本进程的命令行（右键扩展/任意进程都能构造），
                    // 插值下 path = `x" --evil "y` 会拼出额外参数 —— 那正是参数注入的经典形态。
                    psi.ArgumentList.Add("--menu-cmd");
                    psi.ArgumentList.Add(cmdAction);
                    psi.ArgumentList.Add(cmdPath);
                    Process.Start(psi);
                    Diag("CLI 已转交（headless 执行或需宿主提示）");
                }
                catch (Exception ex)
                {
                    Diag($"转交 CLI 异常: {ex}");
                }
            }
            else
            {
                Diag("CLI 未部署（同目录无 BetterDesktop.Cli.exe），命令无法送达");
            }
            Shutdown(0);
            return;
        }

        // 静默服务装配：--menu-cmd 无实例时以此参数拉起（--menu-cmd-hosted <action> <path>）。
        // 不显示 splash/主界面，仅装配服务（含命令管道），装配完成后经管道自送执行右键命令——
        // 用户点「剪贴板历史…」等直接看到功能界面，看不到主程序打开。
        if (args.Length >= 2 && string.Equals(args[0], "--menu-cmd-hosted", StringComparison.Ordinal))
        {
            _hostedAction = args[1];
            _hostedPath = args.Length >= 3 ? args[2] : string.Empty;
            // 不 return：继续单实例互斥 + 静默装配（splash 分支按 _hostedAction 跳过）。
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
            // 统一走 Crash：写异常链 + 环境快照并同步刷盘（异步队列在崩溃时可能来不及刷，丢的正是原因）。
            DiagnosticLog.Crash("App", "DispatcherUnhandledException", ea.Exception, terminating: false);
            ea.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, ea) =>
        {
            if (ea.ExceptionObject is Exception ex)
            {
                // 先落盘（含同步刷盘）再重启：自重启会终止当前进程，Crash 是最后的留痕机会。
                DiagnosticLog.Crash("App", "AppDomain.UnhandledException", ex, terminating: ea.IsTerminating);
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

            if (_hostedAction is not null)
            {
                // 静默装配：无 splash、无主界面，仅加载服务；完成后经命令管道自送执行右键命令。
                _ = HostedBootstrapAsync();
                return;
            }

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

    // ---- 静默服务装配（--menu-cmd-hosted） ----

    private string? _hostedAction;
    private string? _hostedPath;

    /// <summary>静默装配：无 splash/主界面，加载服务后经命令管道自送执行右键命令（面板直接出）。</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "Bootstrap.Build 返回宿主根 IContext，生命周期=进程，由宿主退出时统一释放，静默装配不可中途 Dispose")]
    private async Task HostedBootstrapAsync()
    {
        try
        {
            await Bootstrap.Build();
            // 命令管道 server 已在 Build 内就绪：自送命令执行（剪贴板面板等直接弹出）。
            MenuCommandPipe.TrySend(_hostedAction!, _hostedPath ?? string.Empty);
        }
        catch (Exception ex)
        {
            HostWatchdog.RestartFromFatal("Bootstrap.Build(hosted)", ex);
        }
    }
}
