using System;
using System.Diagnostics;
using System.Windows.Threading;
using BetterDesktop.Shell.Core.Native;

namespace BetterDesktop.Shell.HotkeyPanel;

/// <summary>
/// 前台应用探测（场景感知热键的输入）：低频轮询前台窗口 → 进程名，变化时通知。
/// <para>
/// 【为什么是轮询不是 WinEvent 钩子】本模块已经因为"低级钩子挂在 UI 线程、UI 一忙就拖累系统输入"
/// 吃过一次大亏（见计划 §21）。前台应用探测用 500ms 轮询（<c>GetForegroundWindow</c> 极廉价），
/// 与前台的 50ms 门控轮询同源——**整个侧板不装任何全局钩子**。
/// </para>
/// </summary>
internal sealed class ForegroundAppWatcher : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly Func<string?> _probe;
    private string? _current;

    /// <param name="probe">进程名探测器（可注入以便单测；默认读真实前台窗口）。</param>
    public ForegroundAppWatcher(Func<string?>? probe = null)
    {
        _probe = probe ?? ProbeForegroundProcessName;
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _timer.Tick += (_, _) => Poll();
    }

    /// <summary>当前前台应用进程名（无 / 取不到为 null）。</summary>
    public string? CurrentProcessName => _current;

    /// <summary>前台应用变化（仅在实际变化时触发一次）。</summary>
    public event Action<string?>? Changed;

    public void Start()
    {
        Poll();
        _timer.Start();
    }

    /// <inheritdoc />
    public void Dispose() => _timer.Stop();

    private void Poll()
    {
        var name = _probe();
        if (string.Equals(name, _current, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _current = name;
        Changed?.Invoke(name);
    }

    /// <summary>前台窗口所属进程名（拿不到返回 null，绝不抛）。</summary>
    internal static string? ProbeForegroundProcessName()
    {
        try
        {
            var hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                return null;
            }

            _ = NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0)
            {
                return null;
            }

            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (Exception)
        {
            // 系统进程 / 权限不足 / 进程刚退出：按"未知"处理（场景区块隐藏，不影响其它功能）
            return null;
        }
    }
}
