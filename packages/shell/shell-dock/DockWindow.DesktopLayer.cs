// BetterDesktop.Shell.Dock — dock 的「桌面层实例」（计划里的**正本**）
//
// 【要解决的问题】DwmActivateLivePreview（v2 原生 peek，cairoshell 标准做法）会把**除目标外所有
//   顶层窗口**透明化 —— 与 Z 序无关（所以"沉底/不置顶/EXCLUDED_FROM_PEEK"都挡不住它）。
//   桌面图标能存活，是因为它是 SHELLDLL_DefView 下的 SysListView32 **子窗口**，不是顶层窗口。
//   → 把 dock 挂成桌面子窗口，就能在 peek 期间保持不被变暗。
//
// 【代价（必须知道）】子窗口**盖不住任何顶层窗口**，所以这一份实例不能承担"平时沉底 + 靠近/贴底
//   热区临时置顶唤出"的职责。故它与现行顶层 dock 并存：
//     · 平时：顶层实例（副本）= 现行交互，完全不变
//     · peek 期间：这份桌面层实例（正本）接住显示与操作
//   交接协议、振荡风险与验收：docs/plans/2026-09-14-dock-desktop-layer-peek-v2.md §3 / §6。
//
// 【先例与红线】shell-desktop/Windows/DesktopWindow.cs：
//   · 必须挂 **DefView + 提 HWND_TOP** 才保得住交互（否则被 SysListView32 抢走点击）；
//   · 「沉 WorkerW 层」真机已否决 —— 自绘层收不到任何鼠标输入；
//   · 壁纸引擎/DWM 会重建桌面层把外来子窗口挤出 → 必须带**失联看门狗**。
//
// 【本步范围（刻意最小）】只落"正本"本身 + 开关 + 看门狗，**先不接 peek、不做交接**。
//   目的是让用户真机只验两件事：① 桌面可见时能否点击；② peek 期间它是否真的活着。
//   这两关不过，后面的交接与切 v2 都不值得写。
//
// 【开关】BETTERDESKTOP_DOCK_DESKTOP_LAYER=1（默认关 → 行为与现状完全一致，零风险回退）。
//   诊断日志：%LOCALAPPDATA%\BetterDesktop\logs\dock-desktop-layer.log

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using BetterDesktop.Shell.Core.Native;

namespace BetterDesktop.Shell.Dock;

/// <summary>
/// dock 桌面层实例（正本）的挂载 / 失联重挂 / 日志。
/// 独立静态类而非 partial：不侵入 <see cref="DockWindow"/> 的成员（接线只加一行）。
/// </summary>
internal static class DockDesktopLayer
{
    private const int GwlStyle = -16;
    private const int WsChild = 0x40000000;
    private const int WsPopup = unchecked((int)0x80000000);
    private const uint SwpNoActivate = 0x0010;
    private const int SwShow = 5;

    /// <summary>HWND_TOP = 0：宿主内部提到最上层（不越过其他顶层窗口，这是子窗口的固有限制）。</summary>
    private static readonly IntPtr HwndTop = IntPtr.Zero;

    private static DispatcherTimer? _watchdog;

    /// <summary>开关（默认关）。关着时本文件的所有代码都不产生任何行为。</summary>
    internal static bool Enabled => string.Equals(
        Environment.GetEnvironmentVariable("BETTERDESKTOP_DOCK_DESKTOP_LAYER"),
        "1",
        StringComparison.Ordinal);

    /// <summary>接线入口：由 <c>DockWindow.OnSourceInitialized</c> 调用（开关关时立即返回）。</summary>
    internal static void ApplyIfEnabled(Window dock)
    {
        if (!Enabled || dock is null)
        {
            return;
        }

        TryEmbed(dock);
    }

    private static void TryEmbed(Window dock)
    {
        try
        {
            var hwnd = new WindowInteropHelper(dock).Handle;
            var host = DesktopHostWindow.FindDefView();
            if (hwnd == IntPtr.Zero || host == IntPtr.Zero)
            {
                Log($"embed skipped: hwnd=0x{hwnd:X} host=0x{host:X}");
                return;
            }

            // 位置：屏幕坐标 → 宿主客户区坐标。
            // 不假设"DefView 原点 = 屏幕原点"（多显示器时虚拟屏原点可能为负），故用两个 GetWindowRect 相减。
            _ = GetWindowRect(hwnd, out var dockRect);
            _ = GetWindowRect(host, out var hostRect);
            int x = dockRect.Left - hostRect.Left;
            int y = dockRect.Top - hostRect.Top;
            int w = dockRect.Right - dockRect.Left;
            int h = dockRect.Bottom - dockRect.Top;

            // WPF 无边框窗口是 WS_POPUP：改成 WS_CHILD 才能挂进宿主（对齐 DesktopWindow 的做法）。
            int style = GetWindowLongW(hwnd, GwlStyle);
            _ = SetWindowLongW(hwnd, GwlStyle, (style | WsChild) & ~WsPopup);
            _ = SetParent(hwnd, host);

            // 提 HWND_TOP：不提的话会压在 DefView 的 SysListView32（原生图标层）之下，
            // 鼠标点击全被 explorer 截走 —— 自绘桌面当年的血案，dock 同理。
            _ = SetWindowPos(hwnd, HwndTop, x, y, w, h, SwpNoActivate);

            // 跨进程 SetParent 后 WS_VISIBLE 可能丢了 → 显式 SW_SHOW（DesktopWindow 同款修复）。
            _ = ShowWindow(hwnd, SwShow);

            Log($"embedded host=0x{host:X}({DesktopHostWindow.ClassOf(host)}) rect={x},{y} {w}x{h}");
            StartWatchdog(dock);
        }
        catch (Exception ex)
        {
            // 挂不上一律静默回退（dock 本体不受影响，仍是顶层可用状态）
            Log($"embed failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 失联看门狗：壁纸引擎换壁纸 / DWM 活动会重建桌面层，把外来子窗口挤出（DesktopWindow 实机实证）。
    /// 判定同 DesktopWindow：**按类名接受任意桌面宿主**（DefView 优先、Progman 兜底），
    /// 避免查找抖动导致"每 3 秒重挂一次"的振荡。
    /// </summary>
    private static void StartWatchdog(Window dock)
    {
        if (_watchdog is not null)
        {
            return;
        }

        _watchdog = new DispatcherTimer(DispatcherPriority.Background, dock.Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(3),
        };
        _watchdog.Tick += (_, _) =>
        {
            try
            {
                var hwnd = new WindowInteropHelper(dock).Handle;
                if (hwnd == IntPtr.Zero)
                {
                    return;
                }

                var parent = GetParent(hwnd);
                if (!DesktopHostWindow.IsHostClass(parent))
                {
                    Log($"parent lost (0x{parent:X} {DesktopHostWindow.ClassOf(parent)}) → re-embed");
                    TryEmbed(dock);
                }
            }
            catch (Exception ex)
            {
                Log($"watchdog failed: {ex.GetType().Name}: {ex.Message}");
            }
        };
        _watchdog.Start();
    }

    /// <summary>桌面宿主（DefView）查找：收口 shell-core <c>DesktopHostWindow.FindDefView</c>。</summary>
    private static IntPtr FindDesktopHost() => DesktopHostWindow.FindDefView();

    private static string? _logPath;

    /// <summary>
    /// 诊断日志（自足实现，不依赖 shell-core 的日志类型）。
    /// 为什么必须有：本步的真机结论（能否点击 / peek 期间是否存活）只能靠日志与肉眼，两者都要留痕。
    /// </summary>
    private static void Log(string message)
    {
        Debug.WriteLine($"[dock-desktop-layer] {message}");
        try
        {
            _logPath ??= BuildLogPath();
            if (_logPath is null)
            {
                return;
            }

            File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}\r\n");
        }
        catch
        {
            // 诊断失败不影响 dock
        }
    }

    private static string? BuildLogPath()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BetterDesktop", "logs");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "dock-desktop-layer.log");
        }
        catch
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- P/Invoke
    // 窗口查找/判类已收口 shell-core（`DesktopHostWindow.FindDefView/IsHostClass/ClassOf`）；
    // 这里只留本文件特有、shell-core 没有的入口。

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    // 注意：GetWindowLongW/SetWindowLongW 是 32 位入口，但样式位就在低 32 位内，x64 上同样正确
    //（GetWindowLongPtrW 只在需要读写指针时才是必需的）。
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLongW(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
