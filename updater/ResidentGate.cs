using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;

namespace BetterDesktop.Updater;

/// <summary>
/// 更新期间的「常驻组件让路」闸门。
/// <para>
/// 【为什么必须有 · 2026-09-18 审计结论】更新器此前**只停 Host/Agent**：既不停看门狗、也不暂停它的守护，
/// 更不停 Tray / DesktopControl。后果分两类：
/// </para>
/// <para>
/// ① **更新必然失败**：看门狗（3s 轮询、8s 宽限）会把刚被优雅停掉的 Agent 用**旧 exe** 拉回来 →
/// <c>WaitHostExit</c> 永远等不到"Host 与 Agent 都退出"，60s 后以"等待退出超时"收场。
/// </para>
/// <para>
/// ② **新旧混跑**：即使替换成功，Tray / Watchdog / DesktopControl 仍以旧二进制运行（占着旧 exe，
/// 改名成 <c>.old-*</c> 后也删不掉）→ 磁盘上"新 Host + 旧 Tray/看门狗/桌面服务"，用户以为升级了其实没有。
/// </para>
/// <para>
/// 做法：先写看门狗豁免标记（与托盘/安装器同一约定，3s 内生效），再停常驻组件——**桌面服务走优雅停止**，
/// 以便它恢复桌面图标与任务栏外观；替换完成后按"更新前是否在跑"逐一恢复，最后撤掉豁免标记。
/// </para>
/// </summary>
internal static class ResidentGate
{
    private const string TrayProcessName = "BetterDesktop.Tray";
    private const string WatchdogProcessName = "BetterDesktop.Watchdog";
    private const string DesktopServiceProcessName = "BetterDesktop.DesktopControl";

    /// <summary>桌面服务命令管道（与 shell-core/DesktopControl/DesktopControlPipe.cs 一致，改一处必须改另一处）。</summary>
    private const string DesktopCmdPipeName = "BetterDesktop.DesktopCmd";

    private const string DesktopStopCommand = "BDDC1|stop|";

    private static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BetterDesktop");

    private static string PauseFlagPath => Path.Combine(DataDir, "watchdog-pause.flag");

    /// <summary>本轮"更新前在跑"的常驻组件集合（用于替换后按原样恢复）。</summary>
    internal sealed class StoppedSet
    {
        public bool Tray;
        public bool Watchdog;
        public bool DesktopService;
    }

    /// <summary>暂停看门狗守护（写豁免标记）。看门狗 3s 轮询内生效；失败不阻断（但要留痕）。</summary>
    public static bool PauseWatchdog(out string message)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(PauseFlagPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " updater");
            message = "已暂停看门狗守护（更新结束会自动恢复）";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            message = $"写看门狗豁免标记失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>恢复看门狗守护（删豁免标记）。幂等；失败只留痕。</summary>
    public static void ResumeWatchdog(out string message)
    {
        try
        {
            if (File.Exists(PauseFlagPath))
            {
                File.Delete(PauseFlagPath);
            }

            message = "已恢复看门狗守护";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            message = $"删除看门狗豁免标记失败（守护仍暂停）：{ex.Message}";
        }
    }

    /// <summary>
    /// 停掉除 Host/Agent 之外的常驻组件（Host/Agent 由 Applier 负责优雅停止）。
    /// 顺序：看门狗 → 托盘 → 桌面服务（优雅）。返回"哪些本来在跑"。
    /// </summary>
    public static StoppedSet StopResidents(out List<string> messages)
    {
        var stopped = new StoppedSet();
        messages = new List<string>();

        // 看门狗先停：它是"把组件拉回来"的那一方，必须最先离场。
        stopped.Watchdog = StopByProcessName(WatchdogProcessName, "看门狗", messages);
        stopped.Tray = StopByProcessName(TrayProcessName, "托盘", messages);

        // 桌面服务：优先优雅停止（它会恢复桌面图标/任务栏外观并释放管道）；
        // 超时才强杀（强杀也有图标恢复哨兵兜底，见 shell-desktop 的 IconRestoreSentinel）。
        stopped.DesktopService = StopDesktopServiceGracefully(messages);

        return stopped;
    }

    /// <summary>按更新前的状态恢复常驻组件（只恢复"本来在跑"的）。</summary>
    public static void RestartResidents(StoppedSet stopped, string target, out List<string> messages)
    {
        messages = new List<string>();

        if (stopped.Tray)
        {
            StartFromTarget(target, "BetterDesktop.Tray.exe", "托盘", messages);
        }

        if (stopped.DesktopService)
        {
            StartFromTarget(target, "BetterDesktop.DesktopControl.exe", "桌面服务", messages);
        }

        // 看门狗**最后**起：它一起来就会开始守护（此时其它组件都已就位，不会误判"缺失"）。
        // 注意必须在撤掉豁免标记之后再起，否则它会立刻按旧的暂停态继续待命（无害但不直观）。
        if (stopped.Watchdog)
        {
            StartFromTarget(target, "BetterDesktop.Watchdog.exe", "看门狗", messages);
        }
    }

    private static bool StopByProcessName(string processName, string what, List<string> messages)
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(processName);
        }
        catch (Exception ex)
        {
            messages.Add($"{what}：枚举进程失败（跳过）{ex.Message}");
            return false;
        }

        if (processes.Length == 0)
        {
            return false;
        }

        var killed = false;
        foreach (var process in processes)
        {
            try
            {
                process.Kill();
                _ = process.WaitForExit(5000);
                killed = true;
            }
            catch (Exception ex)
            {
                messages.Add($"{what}：结束进程失败 {ex.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }

        if (killed)
        {
            messages.Add($"已停止{what}（{processName}），替换后按原状态恢复");
        }

        return killed;
    }

    /// <summary>优雅停止桌面服务（命令管道）。服务不在时直接返回 false。</summary>
    private static bool StopDesktopServiceGracefully(List<string> messages)
    {
        if (!IsDesktopServicePipeUp())
        {
            return false;
        }

        try
        {
            using var client = new NamedPipeClientStream(".", DesktopCmdPipeName, PipeDirection.Out);
            client.Connect(1500);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(DesktopStopCommand);
        }
        catch (Exception ex)
        {
            messages.Add($"桌面服务优雅停止通道不可达（将继续轮询其退出）：{ex.Message}");
        }

        for (var i = 0; i < 12; i++)
        {
            if (!IsDesktopServicePipeUp())
            {
                messages.Add("已优雅停止桌面服务（桌面图标/任务栏外观已由它自行还原）");
                return true;
            }

            Thread.Sleep(500);
        }

        // 超时：强杀兜底（图标恢复还有 DesktopPlugin 的哨兵进程）。
        messages.Add("桌面服务优雅停止超时，改为强制结束");
        var killed = StopByProcessName(DesktopServiceProcessName, "桌面服务", messages);
        return killed || true; // 本来在管道在 = 本来在跑，无论怎么停都要恢复
    }

    private static void StartFromTarget(string target, string exeName, string what, List<string> messages)
    {
        var exe = Path.Combine(target, exeName);
        if (!File.Exists(exe))
        {
            messages.Add($"{what}未部署（找不到 {exe}），跳过恢复");
            return;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(exe)
            {
                WorkingDirectory = target,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            messages.Add(process is null ? $"{what}恢复失败：进程未创建" : $"已恢复{what}");
        }
        catch (Exception ex)
        {
            messages.Add($"{what}恢复失败：{ex.Message}");
        }
    }

    /// <summary>桌面服务管道是否存在（= 常驻服务模式在跑；同名短命"菜单"进程不算）。</summary>
    private static bool IsDesktopServicePipeUp()
    {
        try
        {
            foreach (var name in Directory.GetFiles(@"\\.\pipe\"))
            {
                if (string.Equals(Path.GetFileName(name), DesktopCmdPipeName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 探测失败按"不在"处理：不影响更新主流程。
            _ = ex.Message;
        }

        return false;
    }
}
