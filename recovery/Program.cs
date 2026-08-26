// BetterDesktop.Recovery — 终极兜底恢复程序（D 层保险）
// 当桌面环境 (Host / Watchdog / 任何组件) 彻底失联、连 C1/C3 都来不及或失效时，
// 由本独立程序把用户桌面恢复成原生 Windows 模样：
//   1. 显示被隐藏的原生任务栏 (Shell_TrayWnd / Shell_SecondaryTrayWnd)
//   2. 终止残留的 BetterDesktop 进程 (Host / Watchdog)，清场假死窗口
//   3. (可选 --clean-autostart) 移除 HKCU\Run 自启键，避免开机反复拉起已损坏的环境
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

    // 我们自己的进程名（仅清理这些，绝不碰用户程序）
    private static readonly string[] OurProcessNames = { "BetterDesktop.Host", "BetterDesktop.Watchdog" };

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static readonly string[] OurRunValues = { "BetterDesktop", "BetterDesktop.Watchdog" };

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

    private static void WriteLog(string content)
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var path = Path.Combine(desktop, "BetterDesktop_recovery.log");
            File.AppendAllText(path, content + Environment.NewLine);
        }
        catch
        {
            // 日志写失败不能阻断恢复
        }
    }
}
