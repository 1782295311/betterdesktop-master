// BetterDesktop.Shell.Core — WH_MOUSE_LL 低级鼠标钩子统一封装
// 收口自 MenuBarPopupWindow 内联声明（SetWindowsHookEx/UnhookWindowsHookEx/CallNextHookEx/GetModuleHandle）。
// 纪律：回调 delegate 字段强引用（防 GC 回收 → 钩子静默失效）；Stop/Dispose 成对 Unhook（7438 退订配对）。
// 注意：WH_MOUSE_LL 回调在安装钩子的线程上下文执行（调用 Start 的线程），耗时逻辑不得在回调内同步阻塞。

using System;

namespace BetterDesktop.Shell.Core.Native;

/// <summary>
/// 全局低级鼠标钩子（WH_MOUSE_LL）。Start 在调用线程安装，回调同样在线程上下文执行。
/// 回调参数：(nCode, wParam, lParam)，其中 wParam 为鼠标消息（如 WM_LBUTTONDOWN），
/// lParam 为 MSLLHOOKSTRUCT 指针，由调用方按需 Marshal 解析。钩子链由本类统一 CallNext。
/// </summary>
public sealed class MouseHook : IDisposable
{
    private readonly NativeMethods.LowLevelMouseProc _proc; // 字段强引用，防 GC
    private readonly Action<int, IntPtr, IntPtr>? _callback; // (nCode, wParam, lParam)
    private IntPtr _hook;

    public MouseHook(Action<int, IntPtr, IntPtr>? callback = null)
    {
        _callback = callback;
        _proc = HookProc;
    }

    /// <summary>安装全局低级鼠标钩子（幂等：已安装直接返回 true）。</summary>
    public bool Start()
    {
        if (_hook != IntPtr.Zero) return true;
        _hook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_MOUSE_LL, _proc, NativeMethods.GetModuleHandle(null), 0);
        return _hook != IntPtr.Zero;
    }

    /// <summary>卸载钩子（幂等）。</summary>
    public void Stop()
    {
        if (_hook != IntPtr.Zero)
        {
            _ = NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    /// <summary>把事件交给钩子链中的下一个钩子（必须调用，否则系统行为异常）。</summary>
    public IntPtr CallNext(int nCode, IntPtr wParam, IntPtr lParam)
        => NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);

    public void Dispose() => Stop();

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                _callback?.Invoke(nCode, wParam, lParam);
            }
        }
        catch
        {
            // 回调异常不得冒泡回系统钩子分发（否则可能影响全局输入链）
        }
        return CallNext(nCode, wParam, lParam);
    }
}
