// BetterDesktop 启动器 —— CLI 调用封装。
//
// 【为什么一律经 CLI 而不是进程内调用】CLI 是仓库既有的**跨进程契约入口**：
//   · 系统集成（--system-integration status/register/unregister）
//   · 开关（--toggle-key）—— 宿主在跑时它会**管道转发**，由宿主进程内改设置并广播，
//     这样已经开着的菜单栏/Dock/桌面才会**立刻**响应。
// 【为什么必须与 CLI 解耦】启动器要能在"环境半坏"时工作：CLI 不在就降级并留痕，绝不抛。

using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace BetterDesktop.Launcher.Services;

/// <summary>CLI 调用结果。</summary>
/// <param name="ExitCode">进程退出码；负值 = 调用侧失败（-1 未部署/启动失败，-2 超时）。</param>
/// <param name="StdOut">标准输出（未重定向成功时为空串）。</param>
/// <param name="TimedOut">是否因超时被强制收尸。</param>
internal sealed record CliResult(int ExitCode, string StdOut, bool TimedOut);

/// <summary>调用 BetterDesktop.Cli.exe（headless 契约入口）。</summary>
internal static class CliRunner
{
    private const string CliExeName = "BetterDesktop.Cli.exe";

    /// <summary>
    /// 单次调用上限。与托盘 RunCli 的 15s 同量级：CLI 正常都是亚秒级，
    /// 给到 20s 是为了容忍"引擎刚启动/磁盘忙"的慢路径，同时保证**永不无限等待**。
    /// </summary>
    private const int DefaultTimeoutMs = 20000;

    public static CliResult Run(params string[] args) => Run(DefaultTimeoutMs, args);

    public static CliResult Run(int timeoutMs, params string[] args)
    {
        var exe = ComponentPaths.Find(CliExeName);
        if (exe is null)
        {
            LauncherLog.Write($"CLI 未部署（找不到 {CliExeName}）：{string.Join(' ', args)}");
            return new CliResult(-1, string.Empty, false);
        }

        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                WorkingDirectory = Path.GetDirectoryName(exe) ?? ComponentPaths.BaseDir,
                UseShellExecute = false,
                CreateNoWindow = true, // 用户双击启动器时不该闪控制台窗口
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using var process = Process.Start(psi);
            if (process is null)
            {
                return new CliResult(-1, string.Empty, false);
            }

            // 先异步读、再有界等待：直接 ReadToEnd 没有超时（CLI 卡住就会挂住调用线程），
            // 这是托盘 ProcessBridge 已经踩过并修掉的坑（2026-09-18），此处沿用同一写法。
            var readTask = process.StandardOutput.ReadToEndAsync();
            if (!readTask.Wait(timeoutMs))
            {
                Kill(process);
                LauncherLog.Write($"CLI 输出读取超时：{string.Join(' ', args)}");
                return new CliResult(-2, string.Empty, true);
            }

            var output = readTask.Status == TaskStatus.RanToCompletion ? readTask.Result : string.Empty;
            if (!process.WaitForExit(timeoutMs))
            {
                Kill(process);
                LauncherLog.Write($"CLI 超时未退出：{string.Join(' ', args)}");
                return new CliResult(-2, output, true);
            }

            LauncherLog.Write($"CLI {string.Join(' ', args)} → exit={process.ExitCode}");
            return new CliResult(process.ExitCode, output, false);
        }
        catch (Exception ex)
        {
            LauncherLog.Error($"调用 CLI（{string.Join(' ', args)}）", ex);
            return new CliResult(-1, string.Empty, false);
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // 收尸失败不影响结果
        }
    }
}
