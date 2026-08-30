// BetterDesktop.Shell.Status — 前台窗口切换的 WinEvent 消息泵（输入法事件驱动的后端线程）
// 背景：SetWinEventHook 用 WINEVENT_OUTOFCONTEXT 时，钩子回调必须送达"安装钩子那个线程"的消息队列，
// 因此需要一条带消息泵的专用后台线程。输入法切换（切输入法 / 按 Shift 中英切换）都伴随前台窗口或焦点变化，
// 挂 EVENT_SYSTEM_FOREGROUND 即可在切换发生时即时刷新 IME 状态，替代"至少等 1s 轮询"。
// 该泵只做一件事：前台窗口变化时触发回调；不承载任何业务逻辑。

using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace BetterDesktop.Shell.Status.Services;

/// <summary>
/// 一条专用后台线程 + 消息泵，挂全局 EVENT_SYSTEM_FOREGROUND 钩子。
/// 前台窗口每次变化时调用 <paramref name="onForegroundChanged"/>（在泵线程上下文）。
/// </summary>
internal sealed class WinEventForegroundPump : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const int WM_QUIT = 0x0012;

    private readonly Action _onForegroundChanged;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly WinEventProc _hookProc; // 保持委托存活，防止被 GC 回收导致钩子失效
    private readonly object _gate = new();

    private Thread? _thread;
    private int _threadId;
    private IntPtr _hook = IntPtr.Zero;

    private delegate void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

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
                _ = PostThreadMessage(tid, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            }
            _thread.Join(2000);
            _thread = null;
        }
    }

    public void Dispose() => Stop();

    private void ThreadProc()
    {
        _threadId = GetCurrentThreadId();
        _hook = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _hookProc, 0, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        _ready.Set();

        try
        {
            // 消息泵：WINEVENT_OUTOFCONTEXT 的回调靠本线程打消息队列才能被派发。
            // GetMessage 返回值：>0 有消息、0 收到 WM_QUIT、-1 出错。
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == WM_QUIT) break;
                _ = TranslateMessage(ref msg);
                _ = DispatchMessage(ref msg);
            }
        }
        finally
        {
            if (_hook != IntPtr.Zero)
            {
                _ = UnhookWinEvent(_hook);
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

    // ---------------- P/Invoke ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventProc pfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("kernel32.dll")]
    private static extern int GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(int idThread, uint Msg, IntPtr wParam, IntPtr lParam);
}