// BetterDesktop.Shell.Core — 消息泵三件套收口（GetMessage/TranslateMessage/DispatchMessage）
// 供 WinEventPump 与未来需要消息泵的组件复用；WM_QUIT 退出语义（GetMessage 返回 0）。

using System;

namespace BetterDesktop.Shell.Core.Native;

/// <summary>
/// 线程消息泵工具。专用泵线程场景（7435）：注册线程必须跑消息循环，
/// OUTOFCONTEXT 回调才能被派发；本类提供标准泵循环与退出投递。
/// </summary>
public static class MessagePump
{
    /// <summary>在当前线程运行消息泵，直到收到 WM_QUIT（GetMessage 返回 0）。</summary>
    public static void Run()
    {
        var msg = new NativeMethods.MSG();
        while (NativeMethods.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
        {
            _ = NativeMethods.TranslateMessage(ref msg);
            _ = NativeMethods.DispatchMessage(ref msg);
        }
    }

    /// <summary>向指定线程投递 WM_QUIT，优雅退出其消息泵。</summary>
    public static void PostQuit(int threadId)
    {
        if (threadId != 0)
        {
            _ = NativeMethods.PostThreadMessage(threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }
    }
}
