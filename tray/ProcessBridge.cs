using System.Diagnostics;
using System.IO.Pipes;

namespace BetterDesktop.Tray;

/// <summary>
/// 进程编排：探测/启动主程序、调用 CLI 命令契约、拉起更新器与应急恢复。
/// 所有方法都不抛异常（失败返回 false / 退出码），保证托盘常驻不因外部组件缺失而崩。
///
/// 看门狗豁免标记（2026-09-16）：停止 Host/Agent 时写标记，看门狗（watchdog/Program.cs）
/// 见标记不自动拉起；启动时删标记恢复守护。约定目录 %LOCALAPPDATA%\BetterDesktop\。
/// </summary>
internal static class ProcessBridge
{
    public const string HostProcessName = "BetterDesktop.Host";

    public static bool IsRunning(string processName)
    {
        try
        {
            var ps = Process.GetProcessesByName(processName);
            try
            {
                return ps.Length > 0;
            }
            finally
            {
                foreach (var p in ps)
                {
                    p.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            TrayLog.Write($"进程探测失败（{processName}）: {ex.Message}");
            return false;
        }
    }

    public static bool IsHostRunning() => IsRunning(HostProcessName);

    /// <summary>启动主程序（无参 = 完整外壳，带 splash）。已运行时不重复启动。</summary>
    public static bool StartHost()
    {
        if (IsHostRunning())
        {
            return true;
        }

        try
        {
            if (!File.Exists(AppPaths.HostExe))
            {
                TrayLog.Write("启动主程序失败：找不到 " + AppPaths.HostExe);
                return false;
            }

            using var p = Process.Start(new ProcessStartInfo(AppPaths.HostExe)
            {
                WorkingDirectory = AppPaths.BaseDir,
                UseShellExecute = false,
            });
            TrayLog.Write("已启动主程序");
            SetStopFlag("host-stopped.flag", false); // 看门狗恢复守护壳
            return p is not null;
        }
        catch (Exception ex)
        {
            TrayLog.Error("启动主程序", ex);
            return false;
        }
    }

    /// <summary>结束主程序（托盘"重启主程序"用）。写豁免标记，看门狗不再自动拉起。</summary>
    public static bool StopHost()
    {
        var ok = StopProcess(HostProcessName, "主程序");
        if (ok)
        {
            SetStopFlag("host-stopped.flag", true);
        }
        return ok;
    }

    /// <summary>强制结束指定进程（按进程名，全部实例）。</summary>
    public static bool StopProcess(string processName, string what)
    {
        try
        {
            var ps = Process.GetProcessesByName(processName);
            var killed = false;
            foreach (var p in ps)
            {
                try
                {
                    p.Kill();
                    killed = true;
                }
                finally
                {
                    p.Dispose();
                }
            }

            if (killed)
            {
                TrayLog.Write($"已强制结束{what}（{processName}）");
            }

            return killed;
        }
        catch (Exception ex)
        {
            TrayLog.Error($"结束{what}", ex);
            return false;
        }
    }

    // ---- 常驻能力宿主（Agent）：已随 S4-4 删除 ----
    //
    // 这里原为 IsAgentRunning / StartAgent / StopAgent（含 `agent-stopped.flag` 留痕）。
    // Agent 退役后职责迁 core：截图热键（core/src/hotkeys.rs）、右键扩展自愈（core/src/shellmenu.rs）、
    // 桌面服务监护（core/src/supervisor.rs）—— 因此"优雅停止 + 留痕"这一整套语义也随之消失
    //（core 用的是组件级 `stopFlag`，见 core/components.json 与 supervisor 的决策优先级）。

    // ---- 桌面服务（自绘桌面 / 桌面控制，不依赖主程序）----

    public const string DesktopServiceProcessName = "BetterDesktop.DesktopControl";

    /// <summary>
    /// 桌面服务是否在运行。判据 = **命令管道可达**，不是进程名。
    /// <para>
    /// 【2026-09-17 审计修正】「桌面控制菜单」是短命进程、与常驻服务同名（都是
    /// BetterDesktop.DesktopControl.exe），按进程名判定会让"正在弹菜单"被误判成"服务在运行"：
    /// 用户点「启动桌面服务」被置灰（点了没反应）、点「停止桌面服务」把菜单进程一起杀掉。
    /// 服务模式独有的特征是它监听的命名管道（DesktopControlPipe.PipeName）。
    /// </para>
    /// </summary>
    public static bool IsDesktopServiceRunning() => PipeProbe.IsDesktopServicePipeUp();

    /// <summary>启动桌面服务（自绘桌面 + 桌面控制菜单 + 原生双击钩子；单实例互斥由服务自己保证）。</summary>
    public static bool StartDesktopService()
    {
        if (IsDesktopServiceRunning())
        {
            return true;
        }

        var ok = StartDetached(AppPaths.DesktopServiceExe);
        if (ok)
        {
            SetStopFlag("desktop-stopped.flag", false); // 用户显式启动：清"已停止"标记
        }
        return ok;
    }

    /// <summary>
    /// 停止桌面服务：先请求**优雅停止**（管道 stop → 服务恢复 explorer 图标层、还原任务栏外观），
    /// 超时才强杀——强杀也有图标恢复哨兵兜底（DesktopPlugin 派生 `--icon-restore-sentinel`）。
    /// 停止成功后写豁免标记，托盘（及将来的看门狗）不再自动拉起。
    /// </summary>
    public static bool StopDesktopService()
    {
        if (!IsDesktopServiceRunning())
        {
            return false;
        }

        var rc = SendDesktopServiceCommand("stop");
        for (var i = 0; i < 20; i++)
        {
            if (!IsDesktopServiceRunning())
            {
                TrayLog.Write($"桌面服务已优雅停止（管道返回 {rc}）");
                SetStopFlag("desktop-stopped.flag", true);
                return true;
            }

            Thread.Sleep(500);
        }

        TrayLog.Write("桌面服务优雅停止超时，改为强制结束");
        var ok = StopProcess(DesktopServiceProcessName, "桌面服务");
        if (ok)
        {
            SetStopFlag("desktop-stopped.flag", true);
        }
        return ok;
    }

    /// <summary>
    /// 桌面服务命令通道：协议与 packages/shell/shell-core/DesktopControl/DesktopControlPipe.cs 一致
    /// （单行 `BDDC1|&lt;action&gt;|&lt;path&gt;`）。托盘零包引用，故此处只复刻 **stop** 这一个动作；
    /// 其余动作（弹菜单 / 翻转键）一律走 CLI 契约，避免两处实现漂移。
    /// </summary>
    private static int SendDesktopServiceCommand(string action)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", "BetterDesktop.DesktopCmd", PipeDirection.Out);
            client.Connect(1500);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine($"BDDC1|{action}|");
            return 0;
        }
        catch (Exception ex)
        {
            TrayLog.Write($"桌面服务通道不可达（服务未运行?）: {ex.Message}");
            return -1;
        }
    }

    // ---- 看门狗与「暂停守护」：已随 S4-4 删除 ----
    //
    // 这里原为 IsWatchdogRunning / StartWatchdog / StopWatchdog / IsGuardPaused / SetGuardPaused，
    // 以及只被后者使用的 IsStopFlagPresent。
    //   · 看门狗已退役：监护职责迁 core 的 supervisor（含 stopFlag / 管道判活 / 退避熔断）；
    //   · 「暂停监护」改由 **core 的托盘菜单**落地：写 `user-pause.flag`，而 core 读它
    //     **加上**更新器写的 `watchdog-pause.flag`（任一存在即暂停，来源分别记日志）。

    // ---- 看门狗豁免标记（与 core 约定同一目录与文件名）----

    private static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BetterDesktop");

    private static void SetStopFlag(string fileName, bool stop)
    {
        try
        {
            var path = Path.Combine(DataDir, fileName);
            if (stop)
            {
                File.WriteAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                TrayLog.Write($"已写看门狗豁免标记：{fileName}（对应进程不会被自动拉起）");
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            TrayLog.Error("写看门狗豁免标记", ex);
        }
    }

    /// <summary>
    /// 调用 CLI 命令契约（--menu-cmd / --toggle-key / --toggle-desktop 等）。
    /// CLI 自行路由「宿主在 → 管道转发」「宿主不在 → 直写设置/headless」，托盘不需要知道细节。
    /// 返回退出码：0 成功；6 = 动作需要宿主在线；负值 = 调用侧失败（见 TrayLog）。
    /// </summary>
    public static int RunCli(params string[] args)
    {
        try
        {
            if (!File.Exists(AppPaths.CliExe))
            {
                TrayLog.Write("CLI 未部署（同目录无 BetterDesktop.Cli.exe）：" + string.Join(" ", args));
                return -1;
            }

            var psi = new ProcessStartInfo(AppPaths.CliExe)
            {
                WorkingDirectory = AppPaths.BaseDir,
                UseShellExecute = false,
                CreateNoWindow = true, // 避免托盘操作时闪控制台窗口
            };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using var p = Process.Start(psi);
            if (p is null)
            {
                return -1;
            }

            if (!p.WaitForExit(15000))
            {
                TrayLog.Write("CLI 超时未退出: " + string.Join(" ", args));
                TryKill(p);
                return -2;
            }

            return p.ExitCode;
        }
        catch (Exception ex)
        {
            TrayLog.Error("CLI 调用", ex);
            return -1;
        }
    }

    /// <summary>
    /// 调 CLI 并**拿回标准输出**（系统集成状态查询用：状态是要展示给用户的文本）。
    /// RunCli 只关心退出码、不重定向输出，故单开一个入口；同样带 15s 超时，绝不无限等待
    /// （常驻组件的铁律：任何外部调用都可能不存在/卡住）。
    /// </summary>
    public static (int Code, string StdOut) RunCliCapture(params string[] args)
    {
        try
        {
            if (!File.Exists(AppPaths.CliExe))
            {
                TrayLog.Write("CLI 未部署（同目录无 BetterDesktop.Cli.exe）：" + string.Join(" ", args));
                return (-1, string.Empty);
            }

            var psi = new ProcessStartInfo(AppPaths.CliExe)
            {
                WorkingDirectory = AppPaths.BaseDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using var p = Process.Start(psi);
            if (p is null)
            {
                return (-1, string.Empty);
            }

            // 【2026-09-18】绝不能直接 ReadToEnd：它**没有超时**，CLI 若卡住（例如无宿主时它自己弹了
            // 模态框、或在等宿主），会把**调用它的 UI 线程**永久挂住（旧注释"不会死锁"与实现不符）。
            // 改为"异步读 + 有界等待 + 超时收尸"。
            var readTask = p.StandardOutput.ReadToEndAsync();
            if (!readTask.Wait(15000))
            {
                TrayLog.Write("CLI 输出读取超时: " + string.Join(" ", args));
                TryKill(p);
                return (-3, string.Empty);
            }

            var output = readTask.Result;
            if (!p.WaitForExit(15000))
            {
                TrayLog.Write("CLI 超时未退出: " + string.Join(" ", args));
                TryKill(p);
                return (-2, output);
            }

            return (p.ExitCode, output);
        }
        catch (Exception ex)
        {
            TrayLog.Error("CLI 调用（取输出）", ex);
            return (-1, string.Empty);
        }
    }

    /// <summary>
    /// 运行一个组件并等它结束（用于更新器：需要它的退出码 + 它写出的状态文件）。
    /// CreateNoWindow：更新器是控制台程序，从托盘拉起时不应闪出黑框。
    /// </summary>
    public static int RunTool(string exe, int timeoutMs, params string[] args)
    {
        try
        {
            if (!File.Exists(exe))
            {
                TrayLog.Write("组件未部署：" + exe);
                return -1;
            }

            var psi = new ProcessStartInfo(exe)
            {
                WorkingDirectory = AppPaths.BaseDir,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using var p = Process.Start(psi);
            if (p is null)
            {
                return -1;
            }

            if (!p.WaitForExit(timeoutMs))
            {
                TrayLog.Write($"组件超时未退出（{timeoutMs}ms）：{exe}");
                TryKill(p);
                return -2;
            }

            return p.ExitCode;
        }
        catch (Exception ex)
        {
            TrayLog.Error("运行组件 " + exe, ex);
            return -1;
        }
    }

    /// <summary>独立拉起一个组件（不等待其退出）。</summary>
    public static bool StartDetached(string exe, params string[] args)
    {
        try
        {
            if (!File.Exists(exe))
            {
                TrayLog.Write("组件未部署：" + exe);
                return false;
            }

            var psi = new ProcessStartInfo(exe)
            {
                WorkingDirectory = AppPaths.BaseDir,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using var p = Process.Start(psi);
            return p is not null;
        }
        catch (Exception ex)
        {
            TrayLog.Error("启动组件 " + exe, ex);
            return false;
        }
    }

    /// <summary>
    /// 以独立进程跑 PowerShell 脚本（卸载用）：脚本第一步就是停掉托盘自身，
    /// 所以**绝不能同步等待**（等于先自锁再自杀）。创建后立即返回。
    /// </summary>
    public static bool StartPowerShellScript(string scriptPath)
    {
        try
        {
            if (!File.Exists(scriptPath))
            {
                TrayLog.Write("脚本未部署：" + scriptPath);
                return false;
            }

            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? AppPaths.BaseDir,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(scriptPath);

            using var p = Process.Start(psi);
            return p is not null;
        }
        catch (Exception ex)
        {
            TrayLog.Error("启动脚本 " + scriptPath, ex);
            return false;
        }
    }

    /// <summary>
    /// 超时后收尸：只 Dispose 不 Kill 会留下**仍在运行的孤儿子进程**
    /// （反复点"打开剪贴板历史/检查更新"会堆积多个 CLI/更新器）。
    /// 杀整棵进程树（子进程可能又拉了自己的子进程）；失败只记日志，绝不抛。
    /// </summary>
    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch (Exception ex)
        {
            TrayLog.Write($"结束超时子进程失败: {ex.Message}");
        }
    }

    /// <summary>用资源管理器打开目录/文件。</summary>
    public static void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            TrayLog.Error("打开路径 " + path, ex);
        }
    }
}
