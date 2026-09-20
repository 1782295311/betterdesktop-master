// BetterDesktop 启动器 —— 应用入口。
//
// 【纪律】本进程是**一次性**的：拉起组件 + 应用用户偏好后立即退出，绝不常驻、不注册自启
//（常驻那一份是托盘，自启值名 BetterDesktop.Tray）。这里只做两件事：单实例互斥、开窗。

using System.Threading;
using System.Windows;

namespace BetterDesktop.Launcher;

/// <summary>启动器应用（见 App.xaml 的样式资源与工程文件头的设计说明）。与应用内其余类型一致保持 internal。</summary>
internal partial class App : Application
{
    /// <summary>
    /// 单实例互斥。
    /// <para>
    /// 刻意用 <c>Local\</c> 前缀（与会话绑定，与 Host 的 <c>Global\</c> 不同）：
    /// 启动器是"用户双击"的前台程序，多用户/多会话各开一个是正常的，
    /// 而 Host 必须全机唯一（它要唯一接管菜单栏/dock），两者语义不同，互斥前缀也不该照抄。
    /// </para>
    /// </summary>
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(true, @"Local\BetterDesktop.Launcher.SingleInstance", out var created);
        if (!created)
        {
            // 已在跑就别开第二个：两个窗口同时改开关会互相覆盖（用户看不出发生了什么）。
            MessageBox.Show(
                "BetterDesktop 启动器已经在运行。",
                "BetterDesktop",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        Services.LauncherLog.Write($"启动器启动（版本 {typeof(App).Assembly.GetName().Version}）");
        new LauncherWindow().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        _singleInstance = null;
        base.OnExit(e);
    }
}
