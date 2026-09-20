namespace BetterDesktop.Tray;

/// <summary>
/// BetterDesktop 常驻系统托盘入口（独立组件，对齐"草特码工具"形态：主程序=UI 壳，托盘=常驻控制面）。
///
/// 为什么必须独立成进程（不是主程序里的一个 NotifyIcon）：
///   · 主程序退出/崩溃/被结束任务后，托盘仍要在 —— 它是用户找回功能与状态的唯一入口；
///   · 零包引用（只用 BCL+WinForms），主程序损坏时它照常工作，可用来跑应急恢复与更新；
///   · 与主程序无共享生命周期：托盘退出不影响主程序，主程序退出不影响托盘。
///
/// 参数：无参 = 常驻；--silent = 自启场景（不弹提示，目前同无参）。
/// </summary>
internal static class Program
{
    /// <summary>单实例互斥（Local\ 前缀：每个登录会话一个托盘，不跨会话互相顶掉）。</summary>
    private const string MutexName = @"Local\BetterDesktop.Tray.SingleInstance";

    [STAThread]
    private static int Main(string[] args)
    {
        // 自检模式（CI/排障用）：不建托盘、**不抢互斥**——允许在托盘已常驻时排障
        //（把自检放在互斥之后会被"已有实例"挡掉，实测踩过）。
        if (Array.Exists(args, a => string.Equals(a, "--selftest", StringComparison.Ordinal)))
        {
            TrayLog.Write($"托盘自检 pid={Environment.ProcessId}");
            return SelfTest();
        }

        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            TrayLog.Write("已有托盘实例在运行，本次启动退出");
            return 0;
        }

        TrayLog.Banner();
        TrayLog.Write($"托盘启动 pid={Environment.ProcessId} 参数=[{string.Join(" ", args)}]");

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // 常驻组件绝不允许因为一个未处理异常就消失：UI 线程异常记日志继续，
        // 进程级异常也先记日志（不自杀），由用户从托盘菜单走"应急恢复"。
        // 崩溃路径一律同步刷盘：异步队列在进程终止时会把最后几条（往往就是原因）丢掉。
        Application.ThreadException += (_, e) =>
        {
            TrayLog.Error("UI 线程异常", e.Exception);
            TrayLog.Flush();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                TrayLog.Error("进程级异常", ex);
            }

            TrayLog.Flush();
        };

        using var context = new TrayApplicationContext();
        Application.Run(context);

        TrayLog.Write("托盘已退出");
        return 0;
    }

    /// <summary>自检：组件就位情况 + 设置可读性 + CLI 命令契约连通性。全部写托盘日志并返回退出码（0=健康）。</summary>
    private static int SelfTest()
    {
        TrayLog.Write("=== 自检开始 ===");
        var healthy = true;

        void Check(string what, bool ok, string detail)
        {
            TrayLog.Write($"  [{(ok ? "OK" : "!!")}] {what}：{detail}");
            if (!ok)
            {
                healthy = false;
            }
        }

        Check("部署目录", Directory.Exists(AppPaths.BaseDir), AppPaths.BaseDir);
        Check("主程序 Host", File.Exists(AppPaths.HostExe), AppPaths.HostExe);
        Check("命令契约 CLI", File.Exists(AppPaths.CliExe), AppPaths.CliExe);
        Check("更新器 Updater", File.Exists(AppPaths.UpdaterExe), File.Exists(AppPaths.UpdaterExe) ? "已部署" : "未部署（更新功能未落地）");
        Check("应急恢复 Recovery", File.Exists(AppPaths.RecoveryExe), File.Exists(AppPaths.RecoveryExe) ? "已部署" : "未部署");
        // 【S4-4】此处原有「看门狗 Watchdog」与「常驻服务 Agent」的就位检查 —— 两者已退役，
        // 改为由 core 承担（core 自带部署自检，见 core/src/main.rs 的启动日志）。

        TrayLog.Write($"  主程序运行中：{ProcessBridge.IsHostRunning()}");
        foreach (var spec in new[]
                 {
                     "components.desktop", "desktop.iconsHidden", "components.menubar",
                     "components.dock", "components.wintaskbar",
                 })
        {
            TrayLog.Write($"  设置 {spec} = {SettingsBridge.GetBool(spec, true)}");
        }

        TrayLog.Write($"  开机自启：{AutoStart.IsEnabled()}");

        // 命令契约连通性：只在宿主管道就绪时实测——否则 CLI 对"需宿主动作"会弹原生对话框并阻塞约 10s
        //（实测踩过），自检不该给用户弹窗。
        var pipeUp = PipeProbe.IsMenuPipeUp();
        TrayLog.Write($"  宿主管道（BetterDesktop.HostCmd）：{(pipeUp ? "就绪" : "未就绪（宿主未运行）")}");
        if (pipeUp)
        {
            var rc = ProcessBridge.RunCli("--menu-cmd", "open-settings");
            Check("命令契约实测", rc == 0, $"open-settings 返回 {rc}");
        }
        else
        {
            Check("命令契约实测", File.Exists(AppPaths.CliExe), "宿主未运行，跳过实测（CLI 就位即可）");
        }

        TrayLog.Write($"=== 自检结束：{(healthy ? "健康" : "存在问题")} ===");
        return healthy ? 0 : 1;
    }
}
