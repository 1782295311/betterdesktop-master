// BetterDesktop.Watchdog — 外部看门狗（C3）
// 常驻监控 Host 进程；Host 消失则拉起。与 Host 内 C1 自重启互补：
// Host 能起来时自己管；Host 启动即崩（C1 没机会注册）时由本看门狗兜底。

using System.Diagnostics;
using System.Threading;

namespace BetterDesktop.Watchdog;

internal static class Program
{
    private const string HostExeName = "BetterDesktop.Host.exe";
    private const int PollIntervalMs = 3000;
    private const int GraceMs = 8000; // Host 正常退出/重启窗口，避免误拉起
    // 防循环：窗口内连续拉起上限（Host 启动即崩时停止刷屏/死循环拉起，窗口过期后自动恢复监控）
    private const int MaxLaunchesInWindow = 5;
    private static readonly TimeSpan LaunchWindow = TimeSpan.FromSeconds(60);
    private static int _launchCount;
    private static DateTime _launchWindowStart = DateTime.UtcNow;

    [STAThread]
    private static void Main()
    {
        // 看门狗自身单实例
        using var self = new Mutex(true, @"Global\BetterDesktop.Watchdog.SingleInstance", out var created);
        if (!created)
        {
            return;
        }

        var hostPath = Path.Combine(AppContext.BaseDirectory, HostExeName);
        var lastSeenAlive = DateTime.UtcNow;

        while (true)
        {
            Thread.Sleep(PollIntervalMs);
            if (IsHostRunning())
            {
                lastSeenAlive = DateTime.UtcNow;
                continue;
            }
            // Host 不在：若在宽限期内（刚自重启/正常退出），不急于拉起
            if ((DateTime.UtcNow - lastSeenAlive).TotalMilliseconds < GraceMs)
            {
                continue;
            }
            // 防循环：窗口内拉起已达上限（疑似 Host 启动即崩），暂停拉起至窗口过期
            var now = DateTime.UtcNow;
            if ((now - _launchWindowStart) > LaunchWindow)
            {
                _launchWindowStart = now;
                _launchCount = 0;
            }
            if (_launchCount >= MaxLaunchesInWindow)
            {
                Console.WriteLine($"[Watchdog] {LaunchWindow.TotalSeconds:0}s 内连续拉起 {MaxLaunchesInWindow} 次仍未存活（疑似 Host 启动即崩），暂停拉起至窗口过期");
                continue;
            }
            _launchCount++;
            TryLaunch(hostPath, ref lastSeenAlive);
        }
    }

    private static bool IsHostRunning()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("BetterDesktop.Host"))
            {
                p.Dispose();
                return true;
            }
        }
        catch
        {
            // 枚举失败当作不在
        }
        return false;
    }

    private static void TryLaunch(string hostPath, ref DateTime lastSeenAlive)
    {
        try
        {
            if (File.Exists(hostPath))
            {
                Process.Start(new ProcessStartInfo(hostPath) { UseShellExecute = true });
                lastSeenAlive = DateTime.UtcNow;
            }
        }
        catch
        {
            // 拉起失败下一轮再试
        }
    }
}
