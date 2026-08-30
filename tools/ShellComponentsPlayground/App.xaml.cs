using System.Windows;
using System.Windows.Threading;

namespace BetterDesktop.Tools.ShellComponentsPlayground;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    // 任何 UI 线程未捕获异常不再静默走 WER 崩溃：写日志 + 弹可读错误框，便于定位。
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var ex = e.Exception;
        var msg = $"[{System.DateTime.Now:HH:mm:ss}] 未处理异常：{ex.GetType().Name}\n{ex.Message}\n\n{ex.StackTrace}";

        try
        {
            var path = System.IO.Path.Combine(System.AppContext.BaseDirectory, "playground-crash.log");
            System.IO.File.AppendAllText(path, msg + System.Environment.NewLine + new string('-', 60) + System.Environment.NewLine);
        }
        catch { /* 日志写入失败不影响弹窗与诊断 */ }

        MessageBox.Show(msg, "ShellComponentsPlayground 运行时错误", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
