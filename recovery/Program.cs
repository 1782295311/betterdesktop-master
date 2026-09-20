// BetterDesktop.Recovery — 终极兜底恢复程序（D 层保险）
// 当桌面环境 (Host / Watchdog / 任何组件) 彻底失联、连 C1/C3 都来不及或失效时，
// 由本独立程序把用户桌面恢复成原生 Windows 模样：
//   1. 显示被隐藏的原生任务栏 (Shell_TrayWnd / Shell_SecondaryTrayWnd)
//   2. 终止残留的 BetterDesktop 进程 (Host / Watchdog)，清场假死窗口
//   3. (可选 --clean-autostart) 移除 HKCU\Run 自启键 + 删除 core 的兜底计划任务，
//      避免开机/每 5 分钟反复拉起已损坏的环境
//   4. 写恢复日志到桌面 BetterDesktop_recovery.log
// 本程序零依赖（不引用任何 BetterDesktop 工程），独立 exe，运行即恢复、结束即退出。

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace BetterDesktop.Recovery;

internal static class Program
{
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    private const string PrimaryTrayClass = "Shell_TrayWnd";
    private const string SecondaryTrayClass = "Shell_SecondaryTrayWnd";

    // 我们自己的进程名（仅清理这些，绝不碰用户程序）。
    // 【2026-09-17 补全】原名单只有 Host/Watchdog，漏了托盘与"占用桌面"的桌面服务、以及会
    // 重新拉起它们的 Agent —— 应急恢复后托盘/Agent 还活着，会把刚清理的组件又拉起来。
    // 刻意不含 Clipboard.Panel / Capture：它们不占用任务栏与桌面图标，且用户可能正在用。
    //
    // 【2026-09-20 S4-4 后的定性 —— 不要把下面两个名字当"残留引用"删掉】
    // 这是**历史清理名单**，不是"当前组件表"：Watchdog / Agent 已随 S4-4 退役、本机不会再产生它们，
    // 但**从旧版升级上来的机器**上，这些进程可能仍在跑。本程序的职责正是清掉这一层残留，
    // 所以这两个名字要留到确认没有旧版机器为止。删掉它们的后果是"旧版残留进程不再被应急恢复清理"
    // —— 一个只有老用户才会碰到、且没人会报的故障。
    // 判据（什么时候该回来删）：安装器不再支持从 S4 之前的老版本升级。
    // 已登记：docs/known-exceptions.md #5。
    private static readonly string[] OurProcessNames =
    {
        "BetterDesktop.Host",
        "BetterDesktop.Watchdog", // 历史值：S4-4 之前存在
        "BetterDesktop.Tray",
        "BetterDesktop.DesktopControl",
        "BetterDesktop.Agent", // 历史值：S4-4 之前存在
    };

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    // 自启值名（与 Kernel.Deployment.AutostartRegistrar 的常量逐字一致，改一处务必改另一处）。
    //
    // `BetterDesktop.Watchdog` 同样是**历史值**（理由见 `OurProcessNames` 上方）：
    // 这里的作用是**清掉旧版留下的 Run 值** —— 删掉它，老用户的机器就会一直带着一个
    // 指向已不存在 exe 的自启项（每次开机静默失败，而用户看不到任何提示）。
    private static readonly string[] OurRunValues = { "BetterDesktop", "BetterDesktop.Watchdog", "BetterDesktop.Tray" };

    // core 的兜底计划任务名（S3-4；与 core/src/task.rs 的 TASK_NAME 逐字一致，改一处务必改另一处）。
    private const string CoreTaskName = "BetterDesktop Core Ensure";

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [STAThread]
    private static int Main(string[] args)
    {
        var cleanAutoStart = args.Any(a => a.Equals("--clean-autostart", StringComparison.OrdinalIgnoreCase));
        var sb = new StringBuilder();
        var stamp = DateTime.Now;
        sb.AppendLine($"[{stamp:yyyy-MM-dd HH:mm:ss}] 开始桌面恢复（BetterDesktop.Recovery）");

        // 单实例：避免双击多次并发
        using var self = new Mutex(true, @"Global\BetterDesktop.Recovery.SingleInstance", out var created);
        if (!created)
        {
            sb.AppendLine("已有恢复程序运行中，退出。");
            WriteLog(sb.ToString());
            return 0;
        }

        // 1. 显示原生任务栏
        try
        {
            var shown = 0;
            EnumWindows((hWnd, _) =>
            {
                var cls = new StringBuilder(256);
                if (GetClassName(hWnd, cls, cls.Capacity) > 0)
                {
                    var name = cls.ToString();
                    if (name == PrimaryTrayClass || name == SecondaryTrayClass)
                    {
                        if (ShowWindow(hWnd, SW_SHOW)) shown++;
                    }
                }
                return true;
            }, IntPtr.Zero);
            sb.AppendLine($"原生任务栏恢复：显示 {shown} 个任务栏窗口。");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"[错误] 恢复任务栏失败：{ex.Message}");
        }

        // 2. 终止残留的 BetterDesktop 进程（清场假死/残留窗口）
        var killed = 0;
        foreach (var name in OurProcessNames)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    p.Kill();
                    p.WaitForExit(3000);
                    killed++;
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"[警告] 终止 {name} (pid={p.Id}) 失败：{ex.Message}");
                }
                finally
                {
                    p.Dispose();
                }
            }
        }
        sb.AppendLine($"残留进程清理：终止 {killed} 个 BetterDesktop 进程。");

        // 3. 可选：清理开机自启键
        if (cleanAutoStart)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
                if (key is not null)
                {
                    foreach (var v in OurRunValues)
                    {
                        if (key.GetValue(v) is not null)
                        {
                            key.DeleteValue(v, throwOnMissingValue: false);
                            sb.AppendLine($"已移除自启键：{v}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"[警告] 清理自启键失败：{ex.Message}");
            }

            // 计划任务也必须一起清，**顺序在进程清理之后**（第 2 步已经杀掉了 core）。
            // 留着它的后果很具体：那条兜底每 5 分钟触发一次，用户刚恢复出来的干净桌面
            // 会在 5 分钟内被重新拉起的 core 污染 —— 而本程序存在的全部意义就是"恢复成原生模样"。
            sb.AppendLine(DeleteCoreTask());
        }
        else
        {
            sb.AppendLine("未清理自启键（如需移除请加 --clean-autostart）。");
        }

        sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 桌面恢复完成。");
        WriteLog(sb.ToString());

        // 若以控制台方式运行（开发者调试），回显结果
        if (Environment.GetCommandLineArgs().Any(a => a.Equals("--verbose", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine(sb.ToString());
        }
        return 0;
    }

    /// <summary>
    /// 删除 core 的兜底计划任务（S3-4）。幂等："任务不存在"不算失败。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 本程序**零依赖**（不引用任何 BetterDesktop 工程），所以这里只能按名字调 <c>schtasks</c> ——
    /// 但"按名字删"不生成任何任务定义，因此**不构成第二份实现**：任务定义 XML 只有
    /// <c>core/src/task.rs</c> 一份。那个名字由 <c>scripts/verify-system-integration.ps1</c>
    /// 在门禁里四处交叉核对。
    /// </para>
    /// <para>
    /// 超时 15 秒：schtasks 正常在 1 秒内回话，但恢复程序是**人已经在着急时**才跑的工具，
    /// 绝不能因为一个卡住的子进程而自己变成新的卡住点。
    /// </para>
    /// </remarks>
    private static string DeleteCoreTask()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            // 用 ArgumentList 而不是拼一个命令行字符串：任务名含空格，手拼引号是这个套路里
            // 最经典的出错点（拼错的后果是删了别的任务，或压根没删而报成功）。
            psi.ArgumentList.Add("/delete");
            psi.ArgumentList.Add("/tn");
            psi.ArgumentList.Add(CoreTaskName);
            psi.ArgumentList.Add("/f");

            using var process = Process.Start(psi);
            if (process is null)
            {
                return $"[警告] 无法启动 schtasks.exe，计划任务 '{CoreTaskName}' 未清理。";
            }

            process.WaitForExit(15000);
            return process.ExitCode == 0
                ? $"已移除计划任务：{CoreTaskName}"
                : $"计划任务 '{CoreTaskName}' 不存在或移除失败（schtasks 退出码 {process.ExitCode}）。";
        }
        catch (Exception ex)
        {
            return $"[警告] 清理计划任务失败：{ex.Message}";
        }
    }

    private static void WriteLog(string content)
    {
        // 统一日志根（%LOCALAPPDATA%\BetterDesktop\logs），不再往桌面写；
        // 本工具是短命进程，写完立即刷盘，保证"用过就有记录"。
        RecoveryLog.Info(content);
        RecoveryLog.Flush();
    }
}
