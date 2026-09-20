using System;
using BetterDesktop.Shell.Core.Native;

namespace BetterDesktop.Shell.Core.Windows;

/// <summary>
/// 把指定窗口**可靠地**激活到前台（供"粘贴回原窗口"等场景使用）。
///
/// <para>
/// 【为什么不能裸调 SetForegroundWindow · 2026-09-13】Windows 有前台锁：**只有当前台进程**调用
/// <c>SetForegroundWindow</c> 才会被接受，否则静默失败并返回 false。而本进程（面板）在收起过程中
/// 会失去前台，于是"收起面板 → 归还前台 → 注入 Ctrl+V"这条链里，归还那一步可能被拒 →
/// Ctrl+V 打到面板自己身上，表现为**偶发**的内容丢失（按序粘贴"第一条凭空消失"的根因）。
/// </para>
/// <para>
/// 解法是微软经典范式（本项目 <c>shell-window-tracker/RunningAppDetector.ActivateWindow</c> 已用同款）：
/// 先把本线程与"当前前台线程"、"目标线程"的输入队列**绑定**（<c>AttachThreadInput</c>）——
/// 绑定期间本线程享有前台线程的输入状态，于是置前被接受；仍失败再用 ALT 键抖动解锁一次前台权限
/// （无害的经典技巧）。结束后必须解绑，否则两条输入队列会一直粘连。
/// </para>
/// <para>
/// **与 <c>RunningAppDetector.ActivateWindow</c> 的分工**：同范式、关注点不同 —— 那份含
/// "UIPI 高完整性窗口回退重启""延迟校验是否真的唤出"等 **dock 专属**逻辑；本类只做"把窗口置前"一件事。
/// 未合并是为了不动已验收的 dock 激活逻辑（后续可收敛为一份）。
/// </para>
/// </summary>
public static class WindowActivator
{
    private const int SwRestore = 9;
    private const byte VkMenu = 0x12; // ALT
    private const uint KeyEventFKeyUp = 0x0002;

    /// <summary>
    /// 尝试把 <paramref name="hwnd"/> 激活为前台窗口。
    /// </summary>
    /// <returns>true = 该窗口此刻已是前台（含"本来就是"的情况）。</returns>
    public static bool Activate(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
        {
            return false;
        }

        try
        {
            var foreground = NativeMethods.GetForegroundWindow();
            if (foreground == hwnd)
            {
                return true;
            }

            // ⚠️ 必须用**同步** ShowWindow：Async 版只投递消息就返回，窗口此刻仍是最小化状态，
            // 紧随其后的 SetForegroundWindow 对最小化窗口必然失败（"唤不出来"的经典竞态）。
            _ = NativeMethods.ShowWindow(hwnd, SwRestore);

            var foregroundThread = NativeMethods.GetWindowThreadProcessId(foreground, out _);
            var targetThread = NativeMethods.GetWindowThreadProcessId(hwnd, out _);
            var thisThread = (uint)NativeMethods.GetCurrentThreadId();

            var bindSelf = foregroundThread != 0 && foregroundThread != thisThread;
            var bindTarget = bindSelf && targetThread != 0 && targetThread != foregroundThread;

            if (bindSelf)
            {
                _ = NativeMethods.AttachThreadInput(thisThread, foregroundThread, true);
            }
            if (bindTarget)
            {
                _ = NativeMethods.AttachThreadInput(targetThread, foregroundThread, true);
            }

            try
            {
                _ = NativeMethods.BringWindowToTop(hwnd);
                if (NativeMethods.SetForegroundWindow(hwnd))
                {
                    return true;
                }

                // 前台锁兜底：ALT 键抖动为本线程解锁前台权限后再试一次（经典技巧，无副作用）。
                NativeMethods.keybd_event(VkMenu, 0, 0, UIntPtr.Zero);
                NativeMethods.keybd_event(VkMenu, 0, KeyEventFKeyUp, UIntPtr.Zero);
                return NativeMethods.SetForegroundWindow(hwnd);
            }
            finally
            {
                if (bindTarget)
                {
                    _ = NativeMethods.AttachThreadInput(targetThread, foregroundThread, false);
                }
                if (bindSelf)
                {
                    _ = NativeMethods.AttachThreadInput(thisThread, foregroundThread, false);
                }
            }
        }
        catch
        {
            return false; // 激活失败不得影响主流程（调用方按"失败"处理）
        }
    }
}
