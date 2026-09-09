// BetterDesktop.Shell.Core — 窗口事件泵（WinEventHook OUTOFCONTEXT 统一实现，7435 变体 A）
//
// 收口三份实现：
//   shell-status/Services/WinEventForegroundPump.cs（变体 A，正确范例）
//   shell-taskbar/Native/WinEventHook.cs（原变体 B，已按本泵纪律修正后并入）
//   shell-window-tracker/Native/WinEventHook.cs（原变体 C，无专用泵线程 → 回调依赖调用方消息泵）
//
// 【生死线（7435）】
//   1. OUTOFCONTEXT 回调必须送达"注册钩子的线程"的消息队列 → 专用 STA 泵线程，注册在泵线程内执行；
//   2. 回调 delegate 必须字段强引用（_proc）→ 防 GC 回收导致钩子静默失效；
//   3. UnhookWinEvent 在泵线程内执行（泵退出时 finally 全量卸载）；
//   4. 只监听需要的事件区间（调用方按需传 min/max）。
// 【线程模型】回调在泵线程上下文执行；业务回调耗时逻辑必须回抛业务线程（9.4 风险）。

using System;
using System.Collections.Generic;
using System.Threading;

namespace BetterDesktop.Shell.Core.Native;

/// <summary>
/// 单泵多订阅的全局窗口事件钩子：一条专用 STA 泵线程承载多个事件区间订阅。
/// <see cref="Subscribe"/> 在泵线程内注册并同步等待句柄；<see cref="Dispose"/> 卸载全部钩子并退出泵线程。
/// </summary>
public sealed class WinEventPump : IDisposable
{
    /// <summary>泵唤醒消息（WM_APP + 1，用于处理注册/卸载请求队列）。</summary>
    private const uint WmPumpWake = 0x8001;

    private readonly object _gate = new();
    private readonly NativeMethods.WinEventProc _proc; // 字段强引用，7435 生死线 2
    private readonly Thread _pumpThread;
    private readonly Dictionary<IntPtr, Action<uint, IntPtr>> _hooks = new();
    private readonly Queue<Action> _work = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private int _threadId;
    private volatile bool _disposed;

    public WinEventPump()
    {
        _proc = OnWinEvent;
        _pumpThread = new Thread(PumpLoop) { IsBackground = true, Name = "ShellCore.WinEventPump" };
        _pumpThread.SetApartmentState(ApartmentState.STA);
        _pumpThread.Start();
        _ready.Wait(TimeSpan.FromSeconds(2)); // 等泵线程就绪（threadId 可用）
    }

    /// <summary>
    /// 注册一个事件区间订阅（在泵线程内 SetWinEventHook）。返回钩子句柄；失败返回 <see cref="IntPtr.Zero"/>。
    /// 回调参数：(eventType, hwnd)。幂等：同一回调重复订阅不同区间各自独立。
    /// </summary>
    public IntPtr Subscribe(uint eventMin, uint eventMax, Action<uint, IntPtr> callback)
    {
        if (_disposed || callback is null) return IntPtr.Zero;
        var result = IntPtr.Zero;
        using var done = new ManualResetEventSlim(false);
        Enqueue(() =>
        {
            var h = NativeMethods.SetWinEventHook(
                eventMin, eventMax, IntPtr.Zero, _proc, 0, 0,
                NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
            if (h != IntPtr.Zero)
            {
                _hooks[h] = callback;
            }
            result = h;
            done.Set();
        });
        done.Wait(TimeSpan.FromSeconds(2));
        return result;
    }

    /// <summary>卸载指定钩子（在泵线程内 UnhookWinEvent，7435 生死线 3；同步等待完成）。
    /// 返回是否确实移除了一个已注册钩子（未注册/未知句柄返回 false）。</summary>
    public bool Unsubscribe(IntPtr hook)
    {
        if (hook == IntPtr.Zero || _disposed) return false;
        var removed = false;
        using var done = new ManualResetEventSlim(false);
        Enqueue(() =>
        {
            if (_hooks.Remove(hook))
            {
                _ = NativeMethods.UnhookWinEvent(hook);
                removed = true;
            }
            done.Set();
        });
        done.Wait(TimeSpan.FromSeconds(2));
        return removed;
    }

    /// <summary>已注册钩子数（诊断用）。</summary>
    public int SubscriberCount
    {
        get { lock (_gate) return _hooks.Count; }
    }

    private void Enqueue(Action action)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _work.Enqueue(action);
            if (_threadId != 0)
            {
                _ = NativeMethods.PostThreadMessage(_threadId, WmPumpWake, IntPtr.Zero, IntPtr.Zero);
            }
        }
    }

    private void PumpLoop()
    {
        _threadId = NativeMethods.GetCurrentThreadId();
        _ready.Set();
        try
        {
            var msg = new NativeMethods.MSG();
            // GetMessage：>0 有消息、0 收到 WM_QUIT、-1 出错（退出并清理）
            while (NativeMethods.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == NativeMethods.WM_QUIT) break;
                if (msg.message == WmPumpWake)
                {
                    DrainWork();
                    continue;
                }
                _ = NativeMethods.TranslateMessage(ref msg);
                _ = NativeMethods.DispatchMessage(ref msg);
            }
        }
        finally
        {
            // 泵退出：全量卸载（与注册同线程语义），避免钩子残留（7438 退订配对）。
            foreach (var h in _hooks.Keys)
            {
                _ = NativeMethods.UnhookWinEvent(h);
            }
            _hooks.Clear();
            lock (_gate) _threadId = 0;
        }
    }

    private void DrainWork()
    {
        while (true)
        {
            Action? action;
            lock (_gate)
            {
                if (_work.Count == 0) return;
                action = _work.Dequeue();
            }
            try
            {
                action();
            }
            catch
            {
                // 单个注册/卸载失败不影响泵继续（M10）
            }
        }
    }

    private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        try
        {
            if (_hooks.TryGetValue(hWinEventHook, out var callback))
            {
                callback(eventType, hwnd);
            }
        }
        catch
        {
            // 回调异常绝不冒泡：泵线程抛异常会杀死消息泵 → 后续事件全丢（7435 日志纪律）
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate)
        {
            if (_threadId != 0)
            {
                _ = NativeMethods.PostThreadMessage(_threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            }
        }
        if (_pumpThread.IsAlive)
        {
            _pumpThread.Join(2000);
        }
    }
}
