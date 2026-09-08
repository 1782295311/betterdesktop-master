using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BetterDesktop.Shell.Core.Native;
using BetterDesktop.Shell.WindowTracker;
using BetterDesktop.Shell.WindowTracker.Native;

namespace BetterDesktop.Shell.WindowTracker.Thumbnail;

/// <summary>Z 序快照条目：顶层窗口句柄 + 是否处于置顶层（WS_EX_TOPMOST）。</summary>
public readonly record struct ZOrderEntry(IntPtr Handle, bool IsTopmost);

/// <summary>
/// 纯裁决结果：能否 peek + 还原时插到哪个窗口之后（<see cref="IntPtr.Zero"/> = HWND_TOP，即普通层最前）。
/// </summary>
public readonly record struct PeekPlan(bool CanPeek, IntPtr InsertAfterOnRestore);

/// <summary>
/// 悬停缩略图的「窗口临时置顶」（Windows Aero Peek 语义）：
/// 把目标窗口抬到<b>普通窗口层最前</b>（HWND_TOP）但**不激活、不抢焦点**，
/// 鼠标移开后按抬起前记录的 Z 序锚点精确插回原位。
///
/// 【红线 1】绝不用 HWND_TOPMOST 抬窗：dock / 浮层 / 系统任务栏都在置顶层，
/// 一旦把目标抬进置顶层，浮层会被它自己的预览目标盖住（且还原时必须清 TOPMOST 样式）。
/// 抬到普通层最前即可——置顶层恒在普通层之上，天然压住被抬起的窗口。
///
/// 【红线 2】还原锚点必须是「抬起前紧邻其上的非置顶窗口」：Z 序分两层（置顶层 / 普通层），
/// 拿置顶窗口当 hwndInsertAfter 会把目标带进置顶层（分层规则优先于插入位置）。
/// 找不到非置顶锚点 → 回落 HWND_TOP（宁可高一位，不可插到置顶层）。
///
/// 【红线 3】只改 Z 序不激活：SWP_NOACTIVATE，否则预览目标会抢走前台焦点。
///
/// 【红线 4（实测主因）】非前台进程抬窗必须先 `AttachThreadInput` 绑到前台线程再 `SetWindowPos`，抬完立刻解绑：
/// 否则系统只把窗口放在「前台窗口**之后**」，前台窗口仍压在它上面 = 用户看到的就是"根本没置顶"。
/// 与前台锁同源、解法也同源（本包 RunningAppDetector.ActivateWindow 同款已验证范式）。
///
/// 【红线 5】最小化窗口同样要 peek（用户明确要求）：
/// - 显示：`NativeMethods.ShowWindowAsync(SW_SHOWNOACTIVATE=4)`——`ShowWindow(SW_RESTORE=9)` 会激活窗口抢焦点，禁用；
///   且最小化窗口的 Z 序操作无意义，必须先让它显示出来。
/// - 收回：`SetWindowPlacement(原 placement)`——`ShowWindow(SW_MINIMIZE)` 会激活"下一个"窗口偷走焦点，禁用。
/// - 点选激活前必须 `Cancel()`：否则 End 会先把它收回最小化、ActivateWindow 再还原，闪一下。
/// </summary>
public sealed class WindowPeek : IDisposable
{
    private const int GwlExStyle = -20;
    private const long WsExTopmost = 0x00000008;

    // 显示最小化窗口但**不激活**（SW_RESTORE=9 会激活窗口抢焦点，禁用）。
    private const int SwShowNoActivate = 4;

    // 置顶层入口/出口：仅当 HWND_TOP 被前台限制挡住时作为回退手段（End 必须成对退出）。
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNotTopmost = new(-2);

    // SetWindowPos 标志：不动尺寸/位置、不激活、不动所有者 Z 序、不发 WM_WINDOWPOSCHANGING。
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const uint SwpNoSendChanging = 0x0400;
    private const uint ZOrderOnlyFlags =
        SwpNoSize | SwpNoMove | SwpNoActivate | SwpNoOwnerZOrder | SwpNoSendChanging;

    private IntPtr _target;
    private IntPtr _restoreAnchor;
    // 抬起前是否处于最小化：End 时须原样收回最小化（SetWindowPlacement，不激活）。
    private bool _restoreMinimized;
    private WindowPlacement _minimizedPlacement;
    // 是否走了「置顶层」回退：End 时必须用 HWND_NOTOPMOST 退出，否则第三方窗口被永久置顶。
    private bool _usedTopmost;

    /// <summary>当前被临时抬起的窗口句柄（未 peek 时为 Zero）。</summary>
    public IntPtr Target => _target;

    /// <summary>是否有窗口正处于临时置顶态。</summary>
    public bool IsActive => _target != IntPtr.Zero;

    /// <summary>
    /// 把窗口临时抬到普通层最前（**最小化窗口同样适用**：先以「不激活」方式显示出来，移开后原样收回最小化）。
    /// 已 peek 同一句柄则无副作用返回 true；目标不可 peek（不可见/本身已置顶/属于本进程/不在 Z 序快照）
    /// 时返回 false 且不改动任何窗口，原因写入调试日志（桌面 BetterDesktop_debug.log，tag=Peek）。
    /// </summary>
    public bool Begin(IntPtr hwnd)
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

        // 切换目标：先还原上一个，避免两个窗口同时被抬起。
        End();

        var skip = GetSkipReason(hwnd);
        if (skip is not null)
        {
            DebugLog.Trace("Peek", $"skip hwnd=0x{(long)hwnd:X} reason={skip}");
            return false;
        }

        var zOrder = SnapshotZOrder();
        var plan = Plan(zOrder, hwnd);
        if (!plan.CanPeek)
        {
            DebugLog.Trace("Peek", $"skip hwnd=0x{(long)hwnd:X} reason=not-in-zorder");
            return false;
        }

        // 最小化窗口：先「不激活地」显示出来再抬 Z 序。
        // ⚠️ 必须用 SW_SHOWNOACTIVATE(4)——ShowWindow(SW_RESTORE=9) 会激活窗口抢焦点，与 peek 语义相悖；
        // 且最小化窗口的 Z 序操作毫无意义，必须先让它显示。
        var minimized = NativeMethods.IsIconic(hwnd);
        if (minimized)
        {
            RunningAppDetector.GetWindowPlacement(hwnd, out var placement);
            _minimizedPlacement = placement;
            _restoreMinimized = true;
            NativeMethods.ShowWindowAsync(hwnd, SwShowNoActivate);
        }

        _target = hwnd;
        _restoreAnchor = plan.InsertAfterOnRestore;
        // NOACTIVATE：只抬 Z 序，不抢焦点。抬完自校验，必要时退到置顶层最底部（见 RaiseToTop）。
        var mode = RaiseToTop(hwnd, zOrder);
        DebugLog.Trace("Peek", $"begin hwnd=0x{(long)hwnd:X} anchor=0x{(long)_restoreAnchor:X} wasMinimized={(minimized ? 1 : 0)} mode={mode}");
        return true;
    }

    /// <summary>
    /// 把窗口插回抬起前的位置；若抬起前是最小化的，再原样收回最小化（同样不激活）。
    /// 幂等：重复调用无副作用。目标窗口已销毁 → 无需还原；锚点窗口已销毁 → 回落 HWND_TOP。
    /// </summary>
    public void End()
    {
        var target = _target;
        var anchor = _restoreAnchor;
        var restoreMinimized = _restoreMinimized;
        var placement = _minimizedPlacement;
        var usedTopmost = _usedTopmost;
        _target = IntPtr.Zero;
        _restoreAnchor = IntPtr.Zero;
        _restoreMinimized = false;
        _minimizedPlacement = default;
        _usedTopmost = false;

        if (target == IntPtr.Zero || !NativeMethods.IsWindow(target))
        {
            return;
        }

        if (usedTopmost)
        {
            // 回退路径用到了置顶层：必须先退出（否则第三方窗口被永久置顶），
            // HWND_NOTOPMOST 会把它落到普通层最前，随后再插回原锚点之下。
            MoveZOrder(target, HwndNotTopmost);
        }

        var insertAfter = anchor != IntPtr.Zero && NativeMethods.IsWindow(anchor) ? anchor : IntPtr.Zero;
        // 先还原 Z 序，再收回最小化：窗口还在普通层里时插锚点才准确。
        NativeMethods.SetWindowPos(target, insertAfter, 0, 0, 0, 0, ZOrderOnlyFlags);
        if (restoreMinimized)
        {
            // ⚠️ 用 SetWindowPlacement 而非 ShowWindow(SW_MINIMIZE)——后者会激活"下一个"窗口，
            // 把焦点从用户当前操作处抢走。SetWindowPlacement 只改显示状态，不动激活。
            SetWindowPlacement(target, ref placement);
        }

        DebugLog.Trace("Peek", $"end hwnd=0x{(long)target:X} insertAfter=0x{(long)insertAfter:X} reMinimized={(restoreMinimized ? 1 : 0)}");
    }

    /// <summary>
    /// 放弃还原（点选缩略图要真正激活窗口时调用）：
    /// 清掉状态但**不执行任何还原动作**，让激活流程接管——
    /// 否则会先被 End() 收回最小化、再被 ActivateWindow 还原，出现"闪一下"的抖动。
    /// </summary>
    public void Cancel()
    {
        _target = IntPtr.Zero;
        _restoreAnchor = IntPtr.Zero;
        _restoreMinimized = false;
        _minimizedPlacement = default;
        _usedTopmost = false;
    }

    /// <summary>释放即还原（宿主窗口/浮层销毁时的兜底路径）。</summary>
    public void Dispose() => End();

    /// <summary>
    /// 抬到普通层最前（HWND_TOP）；抬完**自校验**，没抬动则回退到「置顶层最底部」。
    ///
    /// ⚠️ 前台限制：**非前台进程调 NativeMethods.SetWindowPos(HWND_TOP) 时，系统可能只把窗口放在
    /// 「前台窗口之后」**，前台窗口仍压在上面 = 看起来完全没抬。
    /// 对策一：抬之前先 AttachThreadInput 绑到前台线程取得前台权限（与本包
    /// RunningAppDetector.ActivateWindow 同款已验证范式），抬完立刻解绑。
    /// 对策二（回退）：显式用 HWND_TOPMOST 进入置顶层（该操作不受前台锁限制），
    /// 再插到「抬窗前置顶层最底部窗口」之下——结果仍在 dock / 系统任务栏 **之下**，
    /// 但一定压住所有普通窗口（包括前台窗口）。End 时用 HWND_NOTOPMOST 退出置顶层。
    /// </summary>
    /// <returns>实际采用的模式：normal（普通层最前）/ topmost（置顶层最底）。</returns>
    private string RaiseToTop(IntPtr hwnd, List<ZOrderEntry> zOrder)
    {
        MoveZOrder(hwnd, IntPtr.Zero /* HWND_TOP */);
        if (IsTopOfNormalBand(hwnd))
        {
            _usedTopmost = false;
            return "normal";
        }

        // 没抬动 → 进置顶层，再插到原置顶层最底部之下（仍被 dock/任务栏压住，但压住所有普通窗口）。
        MoveZOrder(hwnd, HwndTopmost);
        var lastTopmost = IntPtr.Zero;
        foreach (var entry in zOrder)
        {
            if (entry.IsTopmost)
            {
                lastTopmost = entry.Handle;
            }
        }

        if (lastTopmost != IntPtr.Zero && lastTopmost != hwnd)
        {
            MoveZOrder(hwnd, lastTopmost);
        }

        _usedTopmost = true;
        return "topmost";
    }

    /// <summary>目标窗口是否已是「普通层最前」（Z 序快照里第一个非置顶窗口）。</summary>
    private static bool IsTopOfNormalBand(IntPtr hwnd)
    {
        foreach (var entry in SnapshotZOrder())
        {
            if (entry.IsTopmost)
            {
                continue;
            }

            return entry.Handle == hwnd;
        }

        return false;
    }

    /// <summary>
    /// 改 Z 序，必要时先 AttachThreadInput 绑到前台线程取得前台权限（抬完立刻解绑）。
    /// 只在必要时绑定：前台窗口存在且其线程不是本线程。
    /// </summary>
    private static void MoveZOrder(IntPtr hwnd, IntPtr insertAfter)
    {
        var fore = NativeMethods.GetForegroundWindow();
        var foreThread = fore != IntPtr.Zero ? NativeMethods.GetWindowThreadProcessId(fore, out _) : 0u;
        var thisThread = (uint)NativeMethods.GetCurrentThreadId();
        var bound = foreThread != 0 && foreThread != thisThread;

        if (bound)
        {
            _ = NativeMethods.AttachThreadInput(thisThread, foreThread, true);
        }

        try
        {
            var ok = NativeMethods.SetWindowPos(hwnd, insertAfter, 0, 0, 0, 0, ZOrderOnlyFlags);
            if (!ok)
            {
                DebugLog.Trace("Peek", $"SetWindowPos failed hwnd=0x{(long)hwnd:X} insertAfter=0x{(long)insertAfter:X} err={Marshal.GetLastWin32Error()}");
            }
        }
        finally
        {
            if (bound)
            {
                _ = NativeMethods.AttachThreadInput(thisThread, foreThread, false);
            }
        }
    }

    /// <summary>
    /// 纯函数：按 Z 序快照裁决「能否 peek + 还原锚点」。不触碰 Win32，可单测。
    /// 快照顺序必须是 EnumWindows 的自顶向底顺序。
    /// </summary>
    /// <returns>
    /// 目标不在快照中、或目标本身已在置顶层 → <c>default</c>（CanPeek=false）；
    /// 否则 CanPeek=true，锚点 = 其上方最近的<b>非置顶</b>窗口；上方无非置顶窗口则 Zero（HWND_TOP）。
    /// </returns>
    public static PeekPlan Plan(IReadOnlyList<ZOrderEntry> zOrder, IntPtr target)
    {
        if (target == IntPtr.Zero || zOrder is null)
        {
            return default;
        }

        var index = -1;
        for (var i = 0; i < zOrder.Count; i++)
        {
            if (zOrder[i].Handle == target)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            // 窗口不在可见顶层窗口序列中（已销毁/被过滤）：不动它。
            return default;
        }

        if (zOrder[index].IsTopmost)
        {
            // 本身就在置顶层（如第三方悬浮窗）：已经在最上，无需也不应重排。
            return default;
        }

        for (var i = index - 1; i >= 0; i--)
        {
            if (!zOrder[i].IsTopmost)
            {
                return new PeekPlan(true, zOrder[i].Handle);
            }
        }

        // 上方全是置顶窗口 → 目标原本就是普通层最前，还原时回到 HWND_TOP。
        return new PeekPlan(true, IntPtr.Zero);
    }

    /// <summary>
    /// Z 序快照（自顶向底，仅可见顶层窗口），并记录每个窗口是否在置顶层。
    /// </summary>
    public static List<ZOrderEntry> SnapshotZOrder()
    {
        var list = new List<ZOrderEntry>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            try
            {
                if (NativeMethods.IsWindowVisible(hwnd))
                {
                    list.Add(new ZOrderEntry(hwnd, IsTopmostWindow(hwnd)));
                }
            }
            catch
            {
                // 单窗口探测失败不阻断整体快照。
            }

            return true;
        }, IntPtr.Zero);

        return list;
    }

    /// <summary>
    /// 不可 peek 的原因；可 peek 返回 null。
    /// 注意：**最小化不是拒绝理由**（IsWindowVisible 对最小化窗口恒为 true，
    /// 最小化窗口也照样留在 EnumWindows 快照里，可以正常取 Z 序锚点）。
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

    private static bool IsTopmostWindow(IntPtr hwnd)
    {
        return (NativeMethods.GetWindowLongPtr(hwnd, GwlExStyle).ToInt64() & WsExTopmost) != 0;
    }

    /// <summary>以「不激活」方式显示窗口（含从最小化还原）。</summary>
    /// <summary>只改显示状态（含收回最小化），**不激活**任何窗口。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPlacement(IntPtr hWnd, ref WindowPlacement lpwndpl);

}
