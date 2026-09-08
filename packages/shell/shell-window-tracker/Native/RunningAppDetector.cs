using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using BetterDesktop.Shell.Core.Native;

namespace BetterDesktop.Shell.WindowTracker.Native;

/// <summary>
/// 单个可见顶层窗口快照。
/// </summary>
public readonly record struct RunningWindow(IntPtr Hwnd, uint ProcessId, string ExePath, string Title);

/// <summary>
/// 窗口 placement（WINDOWPLACEMENT 子集，仅含激活所需的 showCmd）。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct WindowPlacement
{
    public int Length;
    public int Flags;
    public int showCmd;
    public int PtMinPositionX;
    public int PtMinPositionY;
    public int PtMaxPositionX;
    public int PtMaxPositionY;
    public int RcNormalPositionLeft;
    public int RcNormalPositionTop;
    public int RcNormalPositionRight;
    public int RcNormalPositionBottom;
}

/// <summary>
/// 运行中应用窗口检测器（从 shell-dock 原样迁入，命名空间改为 WindowTracker.Native）。
/// 过滤规则：
/// - IsWindowVisible（USER32 可见性）
/// - 无 WS_EX_TOOLWINDOW（工具窗口）
/// - DWM 未标记为 cloaked（折叠隐藏的窗口）
/// - 有窗口标题
/// - 能获取进程路径（Process.MainModule，兜底 QueryFullProcessImageName）
/// - 非自身 shell 进程
///
/// 注：本类目前仍被 shell-dock 的 UI 层（DockWindow / DockThumbWindow）直接调用，
/// 故关键成员暂为 public；待 Step 5/7 将 Dock 改写为走 IWindowTrackerService 后可重新收敛为 internal。
/// </summary>
public static class RunningAppDetector
{
    // 自身 shell 进程的可执行路径，用于排除自身窗口
    internal static readonly string? SelfExePath = NormalizePath(GetCurrentProcessPath());

    // Win32 常量
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080;
    private const int DwmwaCloaked = 14;
    private const int SwRestore = 9;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeGetWindowPlacement(IntPtr hWnd, out WindowPlacement lpwndpl);

    /// <summary>
    /// 获取窗口 placement（含显示状态 showCmd），用于判断最小化。
    /// </summary>
    public static void GetWindowPlacement(IntPtr hWnd, out WindowPlacement placement)
    {
        placement = new WindowPlacement
        {
            Length = Marshal.SizeOf<WindowPlacement>()
        };
        try
        {
            _ = NativeGetWindowPlacement(hWnd, out placement);
        }
        catch
        {
            placement.showCmd = 0;
        }
    }


    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out uint pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>
    /// 枚举当前可见的运行中应用窗口。
    /// 同进程的多窗口各自返回；调用方按 ExePath 去重。
    /// </summary>
    public static IReadOnlyList<RunningWindow> GetRunningWindows()
    {
        var result = new List<RunningWindow>();

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            try
            {
                // 必须有 WS_VISIBLE
                if (!NativeMethods.IsWindowVisible(hwnd))
                {
                    return true;
                }

                // 不能有 WS_EX_TOOLWINDOW（工具窗口不显示在任务栏）
                if ((NativeMethods.GetWindowLongPtr(hwnd, GwlExStyle).ToInt64() & WsExToolWindow) != 0)
                {
                    return true;
                }

                // 不能被 DWM 标记为折叠隐藏
                if (IsCloaked(hwnd))
                {
                    return true;
                }

                // 必须有窗口标题
                var title = GetWindowText(hwnd);
                if (string.IsNullOrWhiteSpace(title))
                {
                    return true;
                }

                // 必须获取到进程 ID
                if (NativeMethods.GetWindowThreadProcessId(hwnd, out var pid) == 0 || pid == 0)
                {
                    return true;
                }

                // 必须获取到进程路径
                var exePath = GetProcessPath(pid);
                if (string.IsNullOrWhiteSpace(exePath))
                {
                    return true;
                }

                // 排除自身 shell 进程
                if (string.Equals(exePath, SelfExePath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                // 排除 shell 桌面宿主窗口（Progman/WorkerW）：属于 explorer.exe 但不是"运行中的应用"。
                // 不排除的话它们会并入 explorer 的运行图标，且因 z-order 最底、常驻可见，
                // 点击激活会选中桌面本身 → 看起来"点了资源管理器没反应"（实测回归）。
                var className = new StringBuilder(64);
                if (NativeMethods.GetClassName(hwnd, className, 64) > 0)
                {
                    var cn = className.ToString();
                    if (cn == "Progman" || cn == "WorkerW")
                    {
                        return true;
                    }
                }

                // 排除"幽灵窗口"：有标题、可见、未最小化，但矩形极小（< 64×48）。
                // 这类窗口是应用的隐藏宿主/通信窗口，激活它毫无反应——
                // 一旦它被当成"运行中的应用"，点击永远唤不出真正的窗口
                // （表现为"dock 里看得到、点它没反应"）。最小化的窗口不参与此过滤：
                // 最小化矩形本就小，且它们正是要被唤出来的目标。
                if (IsGhostWindow(hwnd))
                {
                    return true;
                }

                result.Add(new RunningWindow(hwnd, pid, exePath, title));
            }
            catch
            {
                // 单窗口异常不影响整体枚举
            }

            return true;
        }, IntPtr.Zero);

        return result;
    }

    /// <summary>
    /// 获取当前可见运行中的可执行路径集合（已过滤自身 shell 进程）。
    /// </summary>
    public static HashSet<string> GetRunningExecutablePaths()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var window in GetRunningWindows())
        {
            result.Add(window.ExePath);
        }

        return result;
    }

    /// <summary>
    /// 把指定窗口激活到前台（还原最小化 + 置前 + 抢前台焦点）。
    /// ⚠️ 裸 SetForegroundWindow 在本进程非前台时会被 Windows 前台锁静默拒绝（点击 dock 图标后
    /// 前台是目标应用或桌面，不是本进程）——必须先 AttachThreadInput 把输入队列绑到前台线程
    /// 再置前（微软经典解法，cairoshell C1 WindowOperations 同款已验证范式）。
    /// </summary>
    /// <returns>是否成功把目标置为前台窗口。</returns>
    public static bool ActivateWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            // ⚠️ 必须用**同步**的 ShowWindow，不能用 ShowWindowAsync：
            // Async 版只往目标线程投递消息就立刻返回，窗口此刻**仍处于最小化状态**，
            // 紧随其后的 SetForegroundWindow 对最小化窗口必然失败（"唤不出来"的经典竞态）。
            // 同步 ShowWindow 保证窗口状态在返回前已更新。
            NativeMethods.ShowWindow(hwnd, SwRestore);

            var foreThread = NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out _);
            var targetThread = NativeMethods.GetWindowThreadProcessId(hwnd, out _);
            var thisThread = (uint)NativeMethods.GetCurrentThreadId();
            var bound = foreThread != 0 && foreThread != thisThread;
            var boundTarget = bound && targetThread != 0 && targetThread != foreThread;

            if (bound)
            {
                _ = NativeMethods.AttachThreadInput(thisThread, foreThread, true);
            }

            if (boundTarget)
            {
                _ = NativeMethods.AttachThreadInput(targetThread, foreThread, true);
            }

            try
            {
                BringWindowToTop(hwnd);
                if (!SetForegroundWindow(hwnd))
                {
                    // 前台锁兜底：ALT 键抖动为本线程解锁前台权限后再试（经典技巧，无害）。
                    NativeMethods.keybd_event(VkMenu, 0, 0, UIntPtr.Zero);
                    NativeMethods.keybd_event(VkMenu, 0, KeyEventFKeyUp, UIntPtr.Zero);
                    var retried = SetForegroundWindow(hwnd);
                    DebugLog.Trace("Activate", $"SetForegroundWindow retry={retried} hwnd={hwnd:X} foreThread={foreThread} targetThread={targetThread}");
                    return retried;
                }

                DebugLog.Trace("Activate", $"SetForegroundWindow ok hwnd={hwnd:X} foreThread={foreThread} targetThread={targetThread}");
                return true;
            }
            finally
            {
                if (boundTarget)
                {
                    _ = NativeMethods.AttachThreadInput(targetThread, foreThread, false);
                }

                if (bound)
                {
                    _ = NativeMethods.AttachThreadInput(thisThread, foreThread, false);
                }
            }
        }
        catch
        {
            // 激活失败不影响 Dock
            return false;
        }
    }

    // 前台设置失败后，延迟多久校验"是否真的唤出来了"。
    private const int ActivateVerifyDelayMs = 320;

    /// <summary>
    /// 激活窗口；**明确失败时回退为「重新启动该 exe」**。
    ///
    /// 【为什么需要回退 · UIPI】任务管理器（Taskmgr.exe）在管理员账户下会自动提权到**高完整性级别**，
    /// 而本 shell 以中等完整性运行。UIPI（用户界面特权隔离）会让低完整性进程对高完整性窗口的
    /// `ShowWindowAsync` / `SetForegroundWindow` **静默失败**——现象就是"dock 运行区看得到缩略图，
    /// 点它却唤不出来"（实测：任务管理器最小化后必现）。
    ///
    /// 回退把激活权交还给应用自己：Taskmgr 是单例，再次 ShellExecute 时由它自己把已有实例唤到前台，
    /// 这一步发生在它的进程/完整性级别内，不受 UIPI 限制。
    ///
    /// 【避免误伤】只有"前台设置明确失败 **且** 延迟校验后目标仍是最小化"才回退；
    /// 前台失败但窗口其实已显示（只是没抢到焦点）时不会多开窗口。
    /// </summary>
    public static void ActivateWindowOrRelaunch(IntPtr hwnd, string? exePath)
    {
        if (ActivateWindow(hwnd))
        {
            return;
        }

        if (hwnd == IntPtr.Zero || string.IsNullOrWhiteSpace(exePath))
        {
            return;
        }

        var path = exePath;
        _ = Task.Delay(ActivateVerifyDelayMs).ContinueWith(_ =>
        {
            try
            {
                if (!NativeMethods.IsWindowVisible(hwnd) || !NativeMethods.IsIconic(hwnd))
                {
                    return; // 已经显示出来了（只是没抢到焦点），不重启
                }

                DebugLog.Trace("Activate", $"relaunch fallback exe={path} hwnd=0x{(long)hwnd:X}");
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch
            {
                // 回退失败静默：原始激活已尽力。
            }
        }, TaskScheduler.Default);
    }

    private const byte VkMenu = 0xA4;       // VK_MENU (ALT)
    private const uint KeyEventFKeyUp = 0x0002;

    /// <summary>
    /// 请求关闭窗口——等价于点标题栏的 ✕：投递 `WM_CLOSE`，**由应用自己决定**是否提示保存/拒绝关闭。
    /// ⚠️ 不强制结束进程：强杀不可逆（未保存数据直接丢失），那是任务管理器该干的事，shell 只做"优雅关闭"。
    /// 用 PostMessage（异步）而非 SendMessage：无响应的应用不会把调用方挂死。
    /// </summary>
    /// <returns>消息是否成功投递（true ≠ 窗口已关闭）。</returns>
    public static bool CloseWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return NativeMethods.PostMessage(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            return false;
        }
    }

    private const uint WmClose = 0x0010;

    /// <summary>
    /// 激活指定可执行路径对应的第一个窗口。
    /// </summary>
    public static void ActivateFirstWindowOf(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return;
        }

        foreach (var window in GetRunningWindows())
        {
            if (string.Equals(window.ExePath, exePath, StringComparison.OrdinalIgnoreCase))
            {
                ActivateWindow(window.Hwnd);
                return;
            }
        }
    }

    // 幽灵窗口判定阈值（保守）：宽**且**高都小于此尺寸的"可见未最小化"顶层窗口，
    // 才判定为隐藏宿主/通信窗口。阈值取小值 + AND 条件，宁可漏放也不误伤真实小窗口。
    private const int MinRealWindowSize = 48;

    /// <summary>
    /// 是否为"幽灵窗口"：**可见且未最小化**，但矩形小到不可能是一个应用主窗口。
    /// 最小化的窗口一律不算（最小化矩形本就小，且是合法的激活目标）。
    /// </summary>
    private static bool IsGhostWindow(IntPtr hwnd)
    {
        try
        {
            if (NativeMethods.IsIconic(hwnd))
            {
                return false;
            }

            if (!NativeMethods.GetWindowRect(hwnd, out var rect))
            {
                return false;
            }

            return (rect.Right - rect.Left) < MinRealWindowSize &&
                   (rect.Bottom - rect.Top) < MinRealWindowSize;
        }
        catch
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private static bool IsCloaked(IntPtr hWnd)
    {
        // DwmGetWindowAttribute(DwmwaCloaked) 返回 S_OK (0) 且 cloaked != 0 时表示窗口被折叠隐藏
        // 注意：DwmGetWindowAttribute 可能对某些窗口失败（返回非0），此时不认为是被 cloak
        return DwmGetWindowAttribute(hWnd, DwmwaCloaked, out var cloaked, sizeof(uint)) == 0 && cloaked != 0;
    }

    internal static string GetWindowText(IntPtr hwnd)
    {
        var length = NativeMethods.GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(length + 1);
        _ = NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>
    /// 获取进程路径。先用 Process.MainModule（标准方式），失败时用 QueryFullProcessImageName（低权限方式）。
    /// </summary>
    private static string? GetProcessPath(uint processId)
    {
        string? path = null;

        // 方式 1：Process.MainModule（标准方式，适用于大部分进程）
        try
        {
            using var process = Process.GetProcessById((int)processId);
            path = process.MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(path))
            {
                return NormalizePath(path);
            }
        }
        catch
        {
            // Process.MainModule 失败（高完整性进程等），尝试方式 2
        }

        // 方式 2：QueryFullProcessImageName（只需 PROCESS_QUERY_LIMITED_INFORMATION）
        try
        {
            var handle = NativeMethods.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
            if (handle != IntPtr.Zero)
            {
                try
                {
                    var sb = new StringBuilder(1024);
                    var size = sb.Capacity;
                    // QueryFullProcessImageNameW 返回 true 表示成功
                    if (QueryFullProcessImageNameW(handle, 0, sb, ref size) && size > 0)
                    {
                        path = sb.ToString();
                    }
                }
                finally
                {
                    NativeMethods.CloseHandle(handle);
                }
            }
        }
        catch
        {
            // 忽略
        }

        return NormalizePath(path);
    }

    /// <summary>
    /// 获取当前进程路径。
    /// </summary>
    private static string? GetCurrentProcessPath()
    {
        // 优先用 Environment.ProcessPath（.NET Core 3.0+，最可靠）
        var path = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(path))
        {
            return NormalizePath(path);
        }

        // 兜底：Process.GetCurrentProcess().MainModule
        try
        {
            using var process = Process.GetCurrentProcess();
            return NormalizePath(process.MainModule?.FileName);
        }
        catch
        {
            return null;
        }
    }

    private static string? NormalizePath(string? path)
    {
        return string.IsNullOrWhiteSpace(path) ? null : path.Trim();
    }
}
