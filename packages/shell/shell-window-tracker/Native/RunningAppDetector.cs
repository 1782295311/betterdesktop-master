using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

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

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

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

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out uint pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

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

        EnumWindows((hwnd, _) =>
        {
            try
            {
                // 必须有 WS_VISIBLE
                if (!IsWindowVisible(hwnd))
                {
                    return true;
                }

                // 不能有 WS_EX_TOOLWINDOW（工具窗口不显示在任务栏）
                if ((GetWindowLongPtr(hwnd, GwlExStyle).ToInt64() & WsExToolWindow) != 0)
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
                if (GetWindowThreadProcessId(hwnd, out var pid) == 0 || pid == 0)
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
                if (GetClassName(hwnd, className, 64) > 0)
                {
                    var cn = className.ToString();
                    if (cn == "Progman" || cn == "WorkerW")
                    {
                        return true;
                    }
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
    public static void ActivateWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            ShowWindowAsync(hwnd, SwRestore);

            var foreThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            var targetThread = GetWindowThreadProcessId(hwnd, out _);
            var thisThread = GetCurrentThreadId();
            var bound = foreThread != 0 && foreThread != thisThread;
            var boundTarget = bound && targetThread != 0 && targetThread != foreThread;

            if (bound)
            {
                _ = AttachThreadInput(thisThread, foreThread, true);
            }

            if (boundTarget)
            {
                _ = AttachThreadInput(targetThread, foreThread, true);
            }

            try
            {
                BringWindowToTop(hwnd);
                if (!SetForegroundWindow(hwnd))
                {
                    // 前台锁兜底：ALT 键抖动为本线程解锁前台权限后再试（经典技巧，无害）。
                    keybd_event(VkMenu, 0, 0, IntPtr.Zero);
                    keybd_event(VkMenu, 0, KeyEventFKeyUp, IntPtr.Zero);
                    var retried = SetForegroundWindow(hwnd);
                    DebugLog.Trace("Activate", $"SetForegroundWindow retry={retried} hwnd={hwnd:X} foreThread={foreThread} targetThread={targetThread}");
                }
                else
                {
                    DebugLog.Trace("Activate", $"SetForegroundWindow ok hwnd={hwnd:X} foreThread={foreThread} targetThread={targetThread}");
                }
            }
            finally
            {
                if (boundTarget)
                {
                    _ = AttachThreadInput(targetThread, foreThread, false);
                }

                if (bound)
                {
                    _ = AttachThreadInput(thisThread, foreThread, false);
                }
            }
        }
        catch
        {
            // 激活失败不影响 Dock
        }
    }

    private const byte VkMenu = 0xA4;       // VK_MENU (ALT)
    private const uint KeyEventFKeyUp = 0x0002;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, IntPtr dwExtraInfo);

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

    private static bool IsCloaked(IntPtr hWnd)
    {
        // DwmGetWindowAttribute(DwmwaCloaked) 返回 S_OK (0) 且 cloaked != 0 时表示窗口被折叠隐藏
        // 注意：DwmGetWindowAttribute 可能对某些窗口失败（返回非0），此时不认为是被 cloak
        return DwmGetWindowAttribute(hWnd, DwmwaCloaked, out var cloaked, sizeof(uint)) == 0 && cloaked != 0;
    }

    internal static string GetWindowText(IntPtr hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(length + 1);
        _ = GetWindowText(hwnd, sb, sb.Capacity);
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
            var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
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
                    CloseHandle(handle);
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
