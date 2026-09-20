// BetterDesktop.Shell.Core — 桌面宿主窗口（explorer 的 Progman / SHELLDLL_DefView）判定与查找
//
// 【2026-09-14 收口 · 判类】同一段判类逻辑此前有两份：shell-desktop/DesktopWindow 与
// shell-dock/DockWindow.DesktopLayer（后者还自带一份 user32 GetClassNameW 声明）。
// 判据完全一致（类名 ∈ {SHELLDLL_DefView, Progman}），收口到这里。
//
// 【2026-09-14 收口 · 查找】"找 DefView / 找桌面图标 ListView"此前有 5 份拷贝
// （desktop/DesktopPlugin ×2、desktop/Windows/DesktopWindow、dock/DockWindow.DesktopLayer、
// host/IconRestoreSentinel ×2），其中 **3 份的 WorkerW 回退分支是错的**：
// 它们以 Progman 为父去 FindWindowEx("WorkerW")，而 WorkerW 是**顶层窗口**（agent 侧真机实测：
// 15 个顶层 WorkerW 的父窗口全为 0），壁纸引擎/自绘桌面接管后 DefView 被迁到顶层 WorkerW 下，
// 于是这些回退永远找不到 —— 表现为"图标能隐藏、恢复失效"这条只在该分支出现的静默故障。
// 这里统一采用顶层遍历（= agent/DesktopIconsCapability 那份已验证的写法）。

using System;
using System.Text;

namespace BetterDesktop.Shell.Core.Native;

/// <summary>explorer 桌面宿主窗口的识别、查找与类名读取（嵌入自绘桌面层 / 原生图标显隐时用）。</summary>
public static class DesktopHostWindow
{
    /// <summary>
    /// 是否为桌面宿主窗口类：<c>SHELLDLL_DefView</c>（挂图标层）或 <c>Progman</c>
    /// （Win11 24H2+ 及部分环境把 DefView 直接作为 Progman 子窗）。
    /// </summary>
    public static bool IsHostClass(IntPtr hwnd)
    {
        string cls = ClassOf(hwnd);
        return cls is "SHELLDLL_DefView" or "Progman";
    }

    /// <summary>读取窗口类名；失败返回空串（诊断日志用）。</summary>
    public static string ClassOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(64);
        return NativeMethods.GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : string.Empty;
    }

    /// <summary>
    /// 定位 SHELLDLL_DefView（原生图标视图窗口）：先查 Progman 直子，再**从桌面根做顶层遍历**找
    /// 承载它的 WorkerW（壁纸引擎/自绘桌面接管后的变体）。找不到返回 <see cref="IntPtr.Zero"/>。
    /// <para>⚠️ 顶层遍历不可改为“以 Progman 为父找 WorkerW”——见文件头，那是有真机记录的静默故障。</para>
    /// </summary>
    public static IntPtr FindDefView()
    {
        var shell = NativeMethods.GetShellWindow();
        if (shell == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        // 默认形态：DefView 直接挂在 Progman 下
        var defView = NativeMethods.FindWindowEx(shell, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView != IntPtr.Zero)
        {
            return defView;
        }

        // 变体：WorkerW 是顶层窗口，DefView 被迁到某个顶层 WorkerW 之下
        IntPtr worker = IntPtr.Zero;
        do
        {
            worker = NativeMethods.FindWindowEx(IntPtr.Zero, worker, "WorkerW", null);
            if (worker == IntPtr.Zero)
            {
                break;
            }

            defView = NativeMethods.FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null);
        }
        while (defView == IntPtr.Zero);

        return defView;
    }

    /// <summary>
    /// 自绘窗口的**嵌入挂载点**：DefView 本身（与原生图标同层，HWND_TOP 提层后可交互），
    /// 查不到时兜底 Progman（罕见）。都不存在返回 <see cref="IntPtr.Zero"/>。
    /// </summary>
    public static IntPtr FindMountPoint()
    {
        var defView = FindDefView();
        return defView != IntPtr.Zero ? defView : NativeMethods.GetShellWindow();
    }

    /// <summary>
    /// 桌面图标 ListView：DefView 下的 <c>SysListView32</c>（窗口名 "FolderView"）。
    /// 查不到返回 <see cref="IntPtr.Zero"/>（原生图标显隐/选中判定的入口）。
    /// </summary>
    public static IntPtr FindIconListView()
    {
        var defView = FindDefView();
        return defView == IntPtr.Zero
            ? IntPtr.Zero
            : NativeMethods.FindWindowEx(defView, IntPtr.Zero, "SysListView32", "FolderView");
    }
}
