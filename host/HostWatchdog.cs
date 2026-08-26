// BetterDesktop.Host — HostWatchdog 宿主进程内自重启（C1）
// 致命异常 → DumpCrash → 拉起新实例 → 旧进程退出。防自杀循环 + 单实例互斥。

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace BetterDesktop.Host;

/// <summary>
/// 宿主级稳定性：捕获致命异常后自重启干净进程（C1）。
/// 与 C3 外部看门狗 exe 互补——Host 能跑起来时自己管；Host 启动即崩时由看门狗拉起。
/// </summary>
public static class HostWatchdog
{
    private const string MutexName = @"Global\BetterDesktop.Host.SingleInstance";
    private const string RestartCountFile = "BetterDesktop_restart.count";
    private const int MaxRapidRestarts = 5;
    private static readonly TimeSpan RapidWindow = TimeSpan.FromSeconds(60);

    private static Mutex? _mutex;

    /// <summary>尝试获取单实例锁；返回 false 表示已有实例在运行（应退出）。</summary>
    public static bool TryAcquireSingleInstance()
    {
        _mutex = new Mutex(true, MutexName, out var createdNew);
        return createdNew;
    }

    /// <summary>致命异常时调用：落盘 → 拉起新进程 → 退出当前进程。</summary>
    public static void RestartFromFatal(string source, Exception ex)
    {
        DumpCrash(source, ex);
        if (ShouldAbandonRestart())
        {
            // 自杀循环：连续过快重启，放弃自重启，保留崩溃日志让用户介入
            DumpCrash("HostWatchdog", new InvalidOperationException(
                $"连续 {MaxRapidRestarts} 次过快重启，停止自重启以避免刷屏。请查看 BetterDesktop_crash.log"));
            return;
        }
        try
        {
            var exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(exe))
            {
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            }
        }
        catch (Exception startEx)
        {
            DumpCrash("HostWatchdog.Start", startEx);
        }
        // 退出旧进程（不抛，让调用方 Shutdown/Exit）
        Environment.Exit(1);
    }

    /// <summary>进程正常退出时释放单实例锁。</summary>
    public static void Release()
    {
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        _mutex = null;
    }

    private static bool ShouldAbandonRestart()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop), RestartCountFile);
            var now = DateTime.UtcNow;
            var (count, last) = ReadCount(path);
            if ((now - last) > RapidWindow)
            {
                count = 0;
            }
            count++;
            WriteCount(path, count, now);
            return count > MaxRapidRestarts;
        }
        catch
        {
            // 计数失败不阻断重启
            return false;
        }
    }

    private static (int Count, DateTime Last) ReadCount(string path)
    {
        if (!File.Exists(path))
        {
            return (0, DateTime.MinValue);
        }
        var text = File.ReadAllText(path);
        var parts = text.Split(',');
        if (parts.Length == 2 &&
            int.TryParse(parts[0], out var c) &&
            DateTime.TryParse(parts[1], out var l))
        {
            return (c, l);
        }
        return (0, DateTime.MinValue);
    }

    private static void WriteCount(string path, int count, DateTime last)
    {
        File.WriteAllText(path, $"{count},{last:o}");
    }

    private static void DumpCrash(string source, Exception ex)
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var p = Path.Combine(desktop, "BetterDesktop_crash.log");
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 来源: {source}");
            sb.AppendLine($"异常: {ex.GetType().FullName}: {ex.Message}");
            sb.AppendLine("堆栈:");
            sb.AppendLine(ex.StackTrace);
            if (ex.InnerException is not null)
            {
                sb.AppendLine("内部异常:");
                sb.AppendLine($"  {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}");
                sb.AppendLine(ex.InnerException.StackTrace);
            }
            sb.AppendLine(new string('=', 60));
            File.AppendAllText(p, sb.ToString());
        }
        catch
        {
            // 落盘失败也不能再抛
        }
    }
}
