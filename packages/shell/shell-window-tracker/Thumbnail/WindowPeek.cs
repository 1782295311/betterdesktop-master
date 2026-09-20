using System;
using System.Runtime.InteropServices;
using BetterDesktop.Shell.Core.Native;
using BetterDesktop.Shell.WindowTracker;
using BetterDesktop.Shell.WindowTracker.Native;

namespace BetterDesktop.Shell.WindowTracker.Thumbnail;

/// <summary>
/// 悬停缩略图的「窗口临时浮现」（Windows Aero Peek 语义）：
/// 通过 DWM 合成器层的 <c>DwmActivateLivePreview</c> 让目标窗口全彩浮现、其余窗口透明化，
/// 但不激活、不抢焦点、不改变真实 Z 序；鼠标移开后预览关闭、窗口还原。
///
/// 【方案依据（2026-09-11，cairoshell/ManagedShell 标准做法）】旧实现用 SetWindowPos 改 Z 序抬窗，
/// 对**高完整性窗口**（管理员运行的应用，IL12288 &gt; 宿主 IL8192）被 UIPI 拒绝（err=5）——
/// 用户实测"管理员窗口 Peek 失效"的根因。cairoshell 底座 ManagedShell 的
/// <c>WindowHelper.PeekWindow</c> 就是裸调 <c>DwmActivateLivePreview(enable, target, calling, AeroPeekType.Window)</c>：
/// 预览动作发生在 DWM 合成器层，**天然免疫 UIPI**（explorer 中完整性对管理员窗口做任务栏预览正靠它），
/// 无需提权。新版 cairoshell 用等价未文档化 API <c>DwmActivatePeek</c>，同为 DWM 层。
///
/// 【红线 1】绝不用 HWND_TOPMOST / SetWindowPos 抬窗：既有 UIPI 缺口 + 抬完须精确还原 Z 序，
/// DWM 预览不触碰 Z 序，无还原负担（本文件已整体移除 Z 序快照/锚点机制，见计划
/// 2026-09-11-host-elevation-dock-peek.md）。
///
/// 【红线 2】Begin/End 必须成对：预览开启后，MouseLeave / 浮层关闭（Closed）/ 宿主销毁（Dispose）
/// 任一路径都要调用 End/Cancel 关闭预览，否则窗口停留在"假预览"状态。
///
/// 【红线 3】最小化窗口同样要 peek（用户明确要求）：
/// - 显示：`NativeMethods.ShowWindowAsync(SW_SHOWNOACTIVATE=4)`——`ShowWindow(SW_RESTORE=9)` 会激活窗口抢焦点，禁用；
///   最小化窗口先以不激活方式显示，DWM 预览才能浮现它。
/// - 收回：`SetWindowPlacement(原 placement)`——`ShowWindow(SW_MINIMIZE)` 会激活"下一个"窗口偷走焦点，禁用。
/// - 点选激活前必须 `Cancel()`：否则 End 会先把它收回最小化、ActivateWindow 再还原，闪一下。
/// </summary>
public sealed class WindowPeek : IDisposable
{
    // 显示最小化窗口但**不激活**（SW_RESTORE=9 会激活窗口抢焦点，禁用）。
    private const int SwShowNoActivate = 4;

    // AeroPeekType.Window = 3（ManagedShell 0.0.344 反射值：Default=0 / Desktop=1 / Window=3，勿臆改）。
    private const int AeroPeekTypeWindow = 3;

    private IntPtr _target;
    // 抬起前的最小化 placement：**可空**——读不到就绝不改写目标窗口
    // （null = 本次 peek 没有"可还原的最小化状态"，End 不做任何写回）。
    private WindowPlacement? _minimizedPlacement;

    /// <summary>当前被临时预览的窗口句柄（未 peek 时为 Zero）。</summary>
    public IntPtr Target => _target;

    /// <summary>是否有窗口正处于临时预览态。</summary>
    public bool IsActive => _target != IntPtr.Zero;

    /// <summary>
    /// 让目标窗口以 Aero Peek 实时预览浮现（**最小化窗口同样适用**：先以「不激活」方式显示出来，
    /// 移开后原样收回最小化）。已 peek 同一句柄则无副作用返回 true；目标不可 peek（无效句柄/
    /// 不可见/本身已置顶/属于本进程）或 DWM 调用失败时返回 false 且不改动任何窗口，
    /// 原因写入调试日志（桌面 BetterDesktop_debug.log，tag=Peek）。
    /// </summary>
    /// <param name="hwnd">被预览的窗口句柄。</param>
    /// <param name="callingHwnd">调用者窗口句柄（本 dock 预览浮层自身），语义同任务栏句柄。</param>
    public bool Begin(IntPtr hwnd, IntPtr callingHwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
        {
            DebugLog.Trace("Peek", $"skip hwnd=0x{(long)hwnd:X} reason=invalid-handle");
            return false;
        }

        if (_target == hwnd)
        {
            return true;
        }

        // 切换目标：先关闭上一个预览，避免两个窗口同时处于预览态。
        End();

        var skip = GetSkipReason(hwnd);
        if (skip is not null)
        {
            DebugLog.Trace("Peek", $"skip hwnd=0x{(long)hwnd:X} reason={skip}");
            return false;
        }

        // 最小化窗口：先「不激活地」显示出来再开预览。
        // ⚠️ 必须用 SW_SHOWNOACTIVATE(4)——ShowWindow(SW_RESTORE=9) 会激活窗口抢焦点，与 peek 语义相悖；
        // 且最小化窗口对 DWM 预览无意义，必须先让它显示出来。
        var minimized = NativeMethods.IsIconic(hwnd);
        if (minimized)
        {
            // 【2026-09-18 安全闸门】读不到 placement 就**不显示**：一旦先显示再无法原样收回，
            // 用户的窗口会被留在"被显示但未预览"的异常态——那是我们弄坏了别人的窗口。
            // 宁可这一次不 peek（用户只是少看一张预览），也绝不冒"改坏他人窗口几何"的风险。
            if (!RunningAppDetector.TryGetWindowPlacement(hwnd, out var placement))
            {
                DebugLog.Trace("Peek",
                    $"skip hwnd=0x{(long)hwnd:X} reason=placement-unreadable（无法保证原样收回最小化，已放弃本次 peek）");
                return false;
            }

            _minimizedPlacement = placement;
            NativeMethods.ShowWindowAsync(hwnd, SwShowNoActivate);
        }

        _target = hwnd;
        var hr = NativeMethods.DwmActivateLivePreview(1, hwnd, callingHwnd, AeroPeekTypeWindow, IntPtr.Zero);
        if (hr != 0)
        {
            // DWM 预览失败（如 DWM 关闭/远程会话）：回滚状态，让调用方走失败抑制。
            _target = IntPtr.Zero;
            var rollback = _minimizedPlacement;
            _minimizedPlacement = null;
            // ⚠️ 补收回：失败前若已把最小化窗口 ShowWindowAsync 显示出来，必须原样收回最小化
            // （否则窗口停留在"被显示但未预览"的异常态，表现为尺寸/状态错乱）。
            // rollback 为 null = 本次没显示过（非最小化，或读失败已提前返回）：什么都不做。
            if (rollback is { } restore && NativeMethods.IsWindow(hwnd))
            {
                SetWindowPlacement(hwnd, ref restore);
            }
            DebugLog.Trace("Peek", $"begin failed hwnd=0x{(long)hwnd:X} calling=0x{(long)callingHwnd:X} hr=0x{(uint)hr:X8}");
            return false;
        }

        DebugLog.Trace("Peek", $"begin hwnd=0x{(long)hwnd:X} calling=0x{(long)callingHwnd:X} wasMinimized={(minimized ? 1 : 0)}");
        return true;
    }

    /// <summary>
    /// 关闭实时预览；若预览前是最小化的，再原样收回最小化（同样不激活）。
    /// 幂等：重复调用无副作用。目标窗口已销毁 → 无需任何动作。
    /// </summary>
    public void End()
    {
        var target = _target;
        var placement = _minimizedPlacement; // null = 本次没有"可还原的最小化状态"，End 不做任何写回
        _target = IntPtr.Zero;
        _minimizedPlacement = null;

        if (target == IntPtr.Zero || !NativeMethods.IsWindow(target))
        {
            return;
        }

        var hr = NativeMethods.DwmActivateLivePreview(0, IntPtr.Zero, IntPtr.Zero, AeroPeekTypeWindow, IntPtr.Zero);
        if (placement is { } restore)
        {
            // ⚠️ 用 SetWindowPlacement 而非 ShowWindow(SW_MINIMIZE)——后者会激活"下一个"窗口，
            // 把焦点从用户当前操作处抢走。SetWindowPlacement 只改显示状态，不动激活。
            // 【2026-09-18】只在"确实读到过有效 placement"时才写回：这里写的是**别人的窗口**，
            // 用零值写回会把目标窗口隐藏（showCmd=0）并把还原矩形归零（重开变成很小一块）。
            SetWindowPlacement(target, ref restore);
        }

        DebugLog.Trace("Peek", $"end hwnd=0x{(long)target:X} hr=0x{(uint)hr:X8} reMinimized={(placement is null ? 0 : 1)}");
    }

    /// <summary>
    /// 放弃还原（点选缩略图要真正激活窗口时调用）：
    /// 关闭预览并清掉状态但**不执行"收回最小化"动作**，让激活流程接管——
    /// 否则会先被 End() 收回最小化、再被 ActivateWindow 还原，出现"闪一下"的抖动。
    /// </summary>
    public void Cancel()
    {
        var target = _target;
        _target = IntPtr.Zero;
        _minimizedPlacement = null;

        if (target == IntPtr.Zero || !NativeMethods.IsWindow(target))
        {
            return;
        }

        var hr = NativeMethods.DwmActivateLivePreview(0, IntPtr.Zero, IntPtr.Zero, AeroPeekTypeWindow, IntPtr.Zero);
        DebugLog.Trace("Peek", $"cancel hwnd=0x{(long)target:X} hr=0x{(uint)hr:X8}");
    }

    /// <summary>释放即关闭预览（宿主窗口/浮层销毁时的兜底路径）。</summary>
    public void Dispose() => End();

    /// <summary>
    /// 不可 peek 的原因；可 peek 返回 null。
    /// 注意：**最小化不是拒绝理由**（最小化窗口照样可以被 DWM 预览浮现）。
    /// </summary>
    private static string? GetSkipReason(IntPtr hwnd)
    {
        try
        {
            if (!NativeMethods.IsWindowVisible(hwnd))
            {
                return "hidden";
            }

            if (IsTopmostWindow(hwnd))
            {
                // 本身就在置顶层（如第三方悬浮窗）：已恒在最上，DWM 预览无意义。
                return "already-topmost";
            }

            var selfPid = (uint)Environment.ProcessId;
            if (NativeMethods.GetWindowThreadProcessId(hwnd, out var pid) != 0 && pid == selfPid)
            {
                return "own-process";
            }

            return null;
        }
        catch
        {
            return "probe-failed";
        }
    }

    private const int GwlExStyle = -20;
    private const long WsExTopmost = 0x00000008;

    private static bool IsTopmostWindow(IntPtr hwnd)
    {
        return (NativeMethods.GetWindowLongPtr(hwnd, GwlExStyle).ToInt64() & WsExTopmost) != 0;
    }

    /// <summary>只改显示状态（含收回最小化），**不激活**任何窗口。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPlacement(IntPtr hWnd, ref WindowPlacement lpwndpl);

}
