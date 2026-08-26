using System;
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
            Bootstrap.Build();
        }
        catch (Exception ex)
        {
            HostWatchdog.RestartFromFatal("Bootstrap.Build", ex);
        }
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        HostWatchdog.Release();
        base.OnExit(e);
    }
}
