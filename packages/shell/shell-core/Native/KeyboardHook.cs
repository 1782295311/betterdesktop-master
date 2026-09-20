// BetterDesktop.Shell.Core — WH_KEYBOARD_LL 低级键盘钩子统一封装
//
// 与 MouseHook 的关键区别：**支持吞键**（回调返回 true 即不传给后续钩子与系统）。
//
// 纪律（同 MouseHook）：
//   - 回调 delegate 字段强引用（防 GC 回收 → 钩子静默失效）；
//   - Start/Stop 幂等成对（7438 退订配对）；
//   - **回调内不得做耗时/阻塞操作** —— 低级钩子有 LowLevelHooksTimeout（默认 300ms），
//     超时会被系统直接摘钩（表现为"功能莫名失效"）。耗时逻辑一律 BeginInvoke 异步派发。
//
// 2026-09-12 新增来源：剪贴板面板"按序粘贴"需要把用户的 Ctrl+V 重定向为"粘下一条"。

using System;
using System.Runtime.InteropServices;

namespace BetterDesktop.Shell.Core.Native;

/// <summary>
/// 全局低级键盘钩子（WH_KEYBOARD_LL）。
/// 回调：<c>(wParam 消息, KBDLLHOOKSTRUCT) → bool</c>，**返回 true 表示吞掉该按键事件**。
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private readonly NativeMethods.LowLevelKeyboardProc _proc; // 字段强引用，防 GC
    private readonly Func<int, NativeMethods.KBDLLHOOKSTRUCT, bool>? _callback;
    private IntPtr _hook;

    public KeyboardHook(Func<int, NativeMethods.KBDLLHOOKSTRUCT, bool>? callback = null)
    {
        _callback = callback;
        _proc = HookProc;
    }

    /// <summary>钩子是否已安装。</summary>
    public bool IsRunning => _hook != IntPtr.Zero;

    /// <summary>安装全局低级键盘钩子（幂等：已安装直接返回 true）。</summary>
    public bool Start()
    {
        if (_hook != IntPtr.Zero)
        {
            return true;
        }
        _hook = NativeMethods.SetWindowsHookExKeyboard(
            NativeMethods.WH_KEYBOARD_LL, _proc, NativeMethods.GetModuleHandle(null), 0);
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

    public void Dispose() => Stop();

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && _callback is not null)
            {
                var info = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                if (_callback(wParam.ToInt32(), info))
                {
                    return new IntPtr(1); // 吞掉：不再传给后续钩子与系统
                }
            }
        }
        catch
        {
            // 回调异常不得冒泡回系统钩子分发（否则可能破坏全局输入链）
        }
        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }
}
