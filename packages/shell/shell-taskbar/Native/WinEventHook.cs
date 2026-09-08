using System;
using System.Runtime.InteropServices;

namespace BetterDesktop.Shell.Taskbar.Native;

/// <summary>
/// 封装 SetWinEventHook，用于监听窗口创建/销毁/前台切换/重排/位置变化，
/// 触发任务栏外观刷新（原样搬运 TTB 的事件订阅集合）。
/// 仅在当前进程上下文（WINEVENT_OUTOFCONTEXT）回调，回调经 ThreadPool 派发。
/// </summary>
public sealed class WinEventHook : IDisposable
{
    private delegate void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    private const uint EVENT_OBJECT_CREATE = 0x8000;
    private const uint EVENT_OBJECT_DESTROY = 0x8001;
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_OBJECT_REORDER = 0x8008;
    private const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    private readonly WinEventProc _proc;
    private IntPtr _hook;
    private readonly Action<uint, IntPtr> _callback;
    private bool _disposed;

    public WinEventHook(uint eventMin, uint eventMax, Action<uint, IntPtr> callback)
    {
        _callback = callback;
        _proc = HookProc;
        _hook = SetWinEventHook(eventMin, eventMax, IntPtr.Zero, Marshal.GetFunctionPointerForDelegate(_proc),
            0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
    }

    private void HookProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (idObject != 0 || idChild != 0) return; // OBJID_WINDOW / CHILDID_SELF
        try { _callback(eventType, hwnd); } catch { /* 回调异常不得冒泡到 WinEvent 系统 */ }
    }

    public static WinEventHook Create(uint eventMin, uint eventMax, Action<uint, IntPtr> callback)
        => new(eventMin, eventMax, callback);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
        GC.SuppressFinalize(this);
    }

    ~WinEventHook() => Dispose();

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        IntPtr pfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);
}
