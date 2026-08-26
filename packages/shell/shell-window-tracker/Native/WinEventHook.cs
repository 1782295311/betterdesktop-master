using System;
using System.Runtime.InteropServices;

namespace BetterDesktop.Shell.WindowTracker.Native;

/// <summary>
/// 基于 SetWinEventHook 的窗口事件钩子（out-of-context），监听窗口创建/销毁/前台/标题变化。
/// 回调委托以字段方式持有，避免被 GC 回收导致钩子失效。
/// </summary>
internal sealed class WinEventHook : IDisposable
{
    // 仅关注顶层窗口自身的对象/子项事件
    private const int ObjidWindow = 0;
    private const int ChildidSelf = 0;

    private const uint EventObjectCreate = 0x8000;
    private const uint EventObjectDestroy = 0x8001;
    private const uint EventObjectNameChange = 0x800C;
    private const uint EventSystemForeground = 0x0003; // EVENT_SYSTEM_FOREGROUND，属 SYSTEM 区间

    // WINEVENT_OUTOFCONTEXT (0x0000) | WINEVENT_SKIPOWNPROCESS (0x0002)
    private const uint WineventFlags = 0x0002;

    private delegate void WinEventProc(
        IntPtr hookHandle,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr hmodWinEventProc,
        WinEventProc pfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    private readonly WinEventProc _callback;
    private IntPtr _hookObject;   // EVENT_OBJECT_CREATE .. EVENT_OBJECT_NAMECHANGE (0x8000..0x800C)
    private IntPtr _hookForeground; // EVENT_SYSTEM_FOREGROUND (0x0003)，属 SYSTEM 区间，需单独注册

    public WinEventHook()
    {
        _callback = OnWinEvent;
        // EVENT_OBJECT_* 区间（创建/销毁/标题变化）
        _hookObject = SetWinEventHook(
            EventObjectCreate,
            EventObjectNameChange,
            IntPtr.Zero,
            _callback,
            0,
            0,
            WineventFlags);
        // EVENT_SYSTEM_FOREGROUND 不在 OBJECT 区间内（0x0003 < 0x8000），必须单独注册
        _hookForeground = SetWinEventHook(
            EventSystemForeground,
            EventSystemForeground,
            IntPtr.Zero,
            _callback,
            0,
            0,
            WineventFlags);
    }

    /// <summary>窗口集合变化（创建/销毁/标题变化）时触发。</summary>
    public event Action? WindowsChanged;

    /// <summary>前台窗口变化时触发（传入前台窗口句柄）。</summary>
    public event Action<IntPtr>? ForegroundChanged;

    private void OnWinEvent(
        IntPtr hookHandle,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (idObject != ObjidWindow || idChild != ChildidSelf)
        {
            return;
        }

        try
        {
            if (eventType == EventSystemForeground)
            {
                ForegroundChanged?.Invoke(hwnd);
            }
            else
            {
                WindowsChanged?.Invoke();
            }
        }
        catch
        {
            // 事件回调异常绝不能冒泡（M10）
        }
    }

    public void Dispose()
    {
        Unhook(ref _hookObject);
        Unhook(ref _hookForeground);
        GC.SuppressFinalize(this);
    }

    private static void Unhook(ref IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            UnhookWinEvent(handle);
        }
        catch
        {
            // 忽略
        }

        handle = IntPtr.Zero;
    }
}
