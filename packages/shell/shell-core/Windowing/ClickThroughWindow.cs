using System;
using BetterDesktop.Shell.Core.Native;

namespace BetterDesktop.Shell.Core.Windowing;

/// <summary>
/// 点击穿透两态窗口原语（全仓首个"点击穿透"机制，登记 docs/MECHANISMS.md）。
/// <para>
/// 只读态 = 完全穿透（<c>WS_EX_TRANSPARENT</c>：鼠标点击/滚动落到下方应用）+
/// <c>WS_EX_NOACTIVATE</c>（常驻不抢焦点，见 <see cref="WindowStyleHelper.MakeFloatingNoActivate"/>）；
/// 可操作态 = 去掉穿透位；<c>WS_EX_NOACTIVATE</c> 是否保留由调用方决定。
/// </para>
/// <para>
/// 【为什么 keepNoActivate 要有】可操作态分两类：含键盘输入的行内控件（录键/搜索框）必须
/// **去掉** NOACTIVATE，否则"点击后仍不获得焦点、键盘输入落不进 TextBox"（602 纪律）；
/// 而纯鼠标操作的常驻浮层（热键侧板的隐藏/恢复按钮）应当**保留** NOACTIVATE ——
/// 点一下就激活窗口会把用户正在用的应用顶到后台。默认 false 保持 602 纪律，侧板显式传 true。
/// </para>
/// <para>
/// 用法：窗口创建后 <c>SetClickThrough(hWnd, true)</c> 进入常驻只读态；门控键 + 点击
/// （<see cref="Hotkeys.RightAltGate"/>）切到可操作态时 <c>SetClickThrough(hWnd, false, keepNoActivate)</c>。
/// </para>
/// </summary>
public static class ClickThroughWindow
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    /// <summary>
    /// 切换点击穿透（幂等）。穿透态附带 NOACTIVATE（不抢焦点）；
    /// 可操作态去穿透位，<paramref name="keepNoActivate"/> = true 时保留 NOACTIVATE（可点但永不激活）。
    /// </summary>
    public static void SetClickThrough(IntPtr hWnd, bool clickThrough, bool keepNoActivate = false)
    {
        if (hWnd == IntPtr.Zero)
        {
            return;
        }

        var style = NativeMethods.GetWindowLong(hWnd, GWL_EXSTYLE);
        if (clickThrough)
        {
            style |= WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;
        }
        else
        {
            style &= ~WS_EX_TRANSPARENT;
            if (!keepNoActivate)
            {
                style &= ~WS_EX_NOACTIVATE;
            }
        }

        _ = NativeMethods.SetWindowLong(hWnd, GWL_EXSTYLE, style);
    }

    /// <summary>当前是否处于穿透态。</summary>
    public static bool IsClickThrough(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero)
        {
            return false;
        }

        return (NativeMethods.GetWindowLong(hWnd, GWL_EXSTYLE) & WS_EX_TRANSPARENT) != 0;
    }
}
