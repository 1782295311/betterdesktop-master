// BetterDesktop.Shell.Status — 前台窗口切换的 WinEvent 消息泵（输入法事件驱动的后端线程）
// 背景：SetWinEventHook 用 WINEVENT_OUTOFCONTEXT 时，钩子回调必须送达"安装钩子那个线程"的消息队列，
// 因此需要一条带消息泵的专用后台线程。输入法切换（切输入法 / 按 Shift 中英切换）都伴随前台窗口或焦点变化，
// 挂 EVENT_SYSTEM_FOREGROUND 即可在切换发生时即时刷新 IME 状态，替代"至少等 1s 轮询"。
// 该泵只做一件事：前台窗口变化时触发回调；不承载任何业务逻辑。
// 【7435 收口】P/Invoke 声明已统一收口到 shell-core/Native（NativeMethods + MessagePump），本类仅保留泵的业务语义。

using System;
using System.Threading;
using BetterDesktop.Shell.Core.Native;

namespace BetterDesktop.Shell.Status.Services;

/// <summary>
/// 一条专用后台线程 + 消息泵，挂全局 EVENT_SYSTEM_FOREGROUND 钩子。
/// 前台窗口每次变化时调用 <paramref name="onForegroundChanged"/>（在泵线程上下文）。
/// </summary>
internal sealed class WinEventForegroundPump : IDisposable
{
    private readonly Action _onForegroundChanged;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly NativeMethods.WinEventProc _hookProc; // 保持委托存活，防止被 GC 回收导致钩子失效
    private readonly object _gate = new();

    private Thread? _thread;
    private int _threadId;
    private IntPtr _hook = IntPtr.Zero;

    public WinEventForegroundPump(Action onForegroundChanged)
    {
        _onForegroundChanged = onForegroundChanged;
        _hookProc = OnWinEvent;
    }

    /// <summary>启动后台泵线程并等待钩子安装完成（带超时，安装失败不阻塞）。</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_thread is not null) return;
            _ready.Reset();
            _thread = new Thread(ThreadProc) { IsBackground = true, Name = "ImeForegroundHook" };
            _thread.Start();
        }
        _ready.Wait(TimeSpan.FromSeconds(1)); // 等钩子装好或失败，避免事件前就去 PollNow
    }

    /// <summary>通过向泵线程投递 WM_QUIT 优雅退出（确保钩子在泵上下文内干净卸载）。</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (_thread is null) return;
            int tid = _threadId;
            if (tid != 0)
            {
                MessagePump.PostQuit(tid);
            }
            _thread.Join(2000);
            _thread = null;
        }
    }

    public void Dispose() => Stop();

    private void ThreadProc()
    {
        _threadId = NativeMethods.GetCurrentThreadId();
        _hook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _hookProc, 0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
        _ready.Set();

        try
        {
            // 消息泵：WINEVENT_OUTOFCONTEXT 的回调靠本线程打消息队列才能被派发。
            // GetMessage 返回值：>0 有消息、0 收到 WM_QUIT、-1 出错。
            MessagePump.Run();
        }
        finally
        {
            if (_hook != IntPtr.Zero)
            {
                _ = NativeMethods.UnhookWinEvent(_hook);
                _hook = IntPtr.Zero;
            }
            _threadId = 0;
        }
    }

    private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        try
        {
            _onForegroundChanged?.Invoke();
        }
        catch
        {
            // 回调里拉取失败不影响泵本身继续工作
        }
    }
}
