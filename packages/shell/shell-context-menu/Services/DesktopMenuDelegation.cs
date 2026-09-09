// BetterDesktop.Shell.ContextMenus — 桌面图标右键：自绘桌面识别对象 → 系统右键菜单（2026-09-06 方向切换）
//
// 【用户拍板方向 2026-09-06】"我们自己识别在自绘桌面操作的对象，然后把需求发给系统右键菜单，
// 而不是让系统自己来"：
//   自绘桌面（DesktopIconsControl）右键时已完整知道对象（路径集合 + 多选集）——无需让 explorer
//   去 hit-test 它的隐藏网格。故：图标菜单 = NativeMenuPopup.TryShowItems（本进程 IContextMenu，
//   SHBindToParent + GetUIObjectOf + TrackPopupMenuEx，explorer 同源聚合、handler 由系统渲染执行）。
//   跨进程委托（LVM_FINDITEMW 同名匹配 → 跨进程选中 → LVM_GETITEMRECT 坐标 → WM_CONTEXTMENU 转发）
//   整体退役——其脆弱性（依赖 explorer 窗口结构、环境重建后 Progman 残留空壳 DefView、
//   SysListView32 时有时无）正是"图标右键无菜单"的历史根因（用户实测：委托失败×N，静默无菜单）。
//
// 【保留】空白路径仍用 explorer DefView 转发（目标：桌面空白 = explorer 原生背景菜单）：
//   - FindVerifiedDefView()：空白转发与命中检查共用的 DefView 定位（验证含 SysListView32，防空壳）；
//   - HitTestDefViewItem()：转发前检查光标是否命中 explorer 隐藏图标（P2-8）——命中则不转发，
//     由 NativeMenuPopup 降级本进程桌面背景菜单（防弹成条目菜单）。
//
// 【回调】onCompleted 在菜单关闭后（TrackPopupMenuEx 同步返回）由 NativeMenuPopup 的 STA worker
// 回调，调用方（DesktopIconsControl）借此触发浏览器刷新。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.Core.Native;

namespace BetterDesktop.Shell.ContextMenus.Services;

// ── 本文件方法级白话索引（白话 → 方法）──
//   "桌面图标右键弹菜单（跨进程选中图标再委托系统菜单）" → TryShowForItems → TryShowItemsCrossProcess
//   "清空 explorer 桌面图标的选中态"     → ClearDesktopSelection（底层 DeselectAll/SelectItem/FindItemByName/GetItemRect 均为跨进程 LVM 操作）
//   "判断屏幕坐标是否真的命中 explorer 桌面图标（防空白处误弹）" → HitTestDefViewItem
//   "找到可信的 SHELLDLL_DefView 窗口（防空壳窗口）" → FindVerifiedDefView
// ────────────────────────────────────

/// <summary>桌面图标右键委托项（path + 显示标签）。</summary>
public sealed record DesktopIconRef(string Path, string? Label);

/// <summary>桌面图标右键：自绘桌面识别对象 → 本进程系统右键菜单（异步；多选整集；结束回调刷新）。</summary>
public static class DesktopMenuDelegation
{
    private const uint LVM_FIRST = 0x1000;
    private const uint LVM_HITTEST = LVM_FIRST + 18;
    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessVmWrite = 0x0020;
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint PageReadWrite = 0x04;
    private const uint MemRelease = 0x8000;

    /// <summary>壁纸遮挡回调：菜单显示期间自绘层临时渲染壁纸（不透明）盖住 explorer 图标透出；
    /// 由 shell-desktop 的 DesktopWindow 在构造时注册。主进程跨进程委托在 STA worker 线程调用，
    /// 回调内部自行 Dispatcher.Invoke 切回 UI 线程。</summary>
    public static Action<bool>? SetWallpaperOcclusion;

    /// <summary>
    /// 对一组桌面图标弹系统右键菜单（异步，不阻塞 UI 线程）：
    /// 自绘桌面已识别对象（路径集合）→ 直接交给 NativeMenuPopup 本进程构造系统菜单；
    /// 菜单关闭后回调 onCompleted（STA worker 线程）。
    /// </summary>
    public static void TryShowForItems(IReadOnlyList<DesktopIconRef> items, System.Windows.Point physicalPx, Action? onCompleted = null)
    {
        if (items is not { Count: > 0 })
        {
            return;
        }
        var paths = items.Select(i => i.Path).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (paths.Count == 0)
        {
            onCompleted?.Invoke();
            return;
        }
        var pos = physicalPx;
        // 【回归修复 2026-09-06】跨进程委托 explorer 弹菜单（唯一安全路径）：第三方 shell 扩展
        // 在 explorer 进程加载，不进本进程。本进程 IContextMenu（GetUIObjectOf 加载第三方扩展）
        // 已实锤致进程崩溃（日志：IShellFolder RCW ok 后中断 28s，哨兵检测宿主退出）——**禁止回退**。
        // 跨进程委托失败时直接返回（不弹菜单），避免崩溃。后续可加自建基础菜单作为安全回退。
        if (TryShowItemsCrossProcess(paths, pos))
        {
            onCompleted?.Invoke();
            return;
        }
        DiagnosticLog.Trace("shell.contextmenu", "跨进程委托失败 → 不弹菜单（禁止回退本进程 IContextMenu，防崩溃）");
        onCompleted?.Invoke();
    }

    // ===== 跨进程委托：让 explorer 自己弹图标右键菜单（身份定位=按文件名找同名项，非坐标转移） =====

    private const uint LvmFinditemW = LVM_FIRST + 83;
    private const uint LvmSetitemstate = LVM_FIRST + 43;
    private const uint LvmGetitemrect = LVM_FIRST + 14;
    private const uint WmContextmenu = 0x007B;
    private const uint LvisSelected = 0x0002;
    private const uint LvisFocused = 0x0001;
    private const uint LvfiString = 0x0002;

    /// <summary>图标右键：跨进程委托 explorer 弹图标菜单（唯一可行路径——本机实锤：
    /// GetUIObjectOf 在非 explorer 进程加载第三方扩展（360/夸克/百度网盘等）系统级崩溃，
    /// 连最小独立程序/手动 vtable 调用都崩；只有 explorer 自己的 listView 窗口过程 hit-test
    /// 能弹正确图标菜单）。流程：SelectItem 选中 → 自绘层壁纸遮挡（盖住 SW_SHOW 透出的
    /// explorer 图标）→ SW_SHOW → WM_CONTEXTMENU（lParam=图标中心）→ 菜单关闭后
    /// SW_HIDE + DeselectAll + 解除遮挡。</summary>
    private static bool TryShowItemsCrossProcess(IReadOnlyList<string> paths, System.Windows.Point physicalPx)
    {
        IntPtr hProc = IntPtr.Zero;
        try
        {
            var defView = FindVerifiedDefView();
            if (defView == IntPtr.Zero)
            {
                DiagnosticLog.Trace("shell.contextmenu", "跨进程委托：找不到验证 DefView");
                return false;
            }
            var hList = NativeMethods.FindWindowExW(defView, IntPtr.Zero, "SysListView32", null);
            if (hList == IntPtr.Zero)
            {
                DiagnosticLog.Trace("shell.contextmenu", $"跨进程委托：找不到 SysListView32，defView=0x{defView:X}");
                return false;
            }
            _ = NativeMethods.GetWindowThreadProcessId(hList, out var pid);
            hProc = NativeMethods.OpenProcess(ProcessVmOperation | ProcessVmRead | ProcessVmWrite, false, pid);
            if (hProc == IntPtr.Zero)
            {
                DiagnosticLog.Trace("shell.contextmenu", "跨进程委托：OpenProcess 失败");
                return false;
            }

            // explorer 桌面默认隐藏 .lnk 扩展名，SysListView32 显示名是 "VS Code" 而非 "VS Code.lnk"。
            // LVM_FINDITEMW(LVFI_STRING) 是前缀匹配：去掉 .lnk 后查找兼容"隐藏/显示扩展名"两种设置。
            var firstFile = System.IO.Path.GetFileName(paths[0]);
            var findName = firstFile.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
                ? firstFile.Substring(0, firstFile.Length - 4)
                : firstFile;
            var itemIndex = FindItemByName(hList, hProc, findName);
            if (itemIndex < 0)
            {
                itemIndex = FindItemByName(hList, hProc, firstFile);
                if (itemIndex < 0)
                {
                    DiagnosticLog.Trace("shell.contextmenu", $"跨进程委托：LVM_FINDITEMW 未找到 findName={findName} orig={firstFile}");
                    return false;
                }
            }

            // 选中目标（mask=LVIF_STATE；LVITEM 结构偏移已修正 state@8/stateMask@12/80B，
            // 旧偏移 state@12 会写坏 pszText 指针位 → explorer 访问违例崩溃——跨进程第一版"又崩溃了"根因）
            SelectItem(hList, hProc, itemIndex);

            // explorer 布局中该图标中心坐标（屏幕坐标）→ listView hit-test 命中该图标
            var rect = GetItemRect(hList, hProc, itemIndex);
            if (rect.Right <= rect.Left || rect.Bottom <= rect.Top)
            {
                DiagnosticLog.Trace("shell.contextmenu", "跨进程委托：LVM_GETITEMRECT 无效");
                return false;
            }
            var pt = new NativeMethods.POINT
            {
                X = rect.Left + (rect.Right - rect.Left) / 2,
                Y = rect.Top + (rect.Bottom - rect.Top) / 2
            };
            ClientToScreen(hList, ref pt);
            var lParam = unchecked((IntPtr)((pt.Y & 0xFFFF) << 16 | (pt.X & 0xFFFF)));

            // ★ 壁纸遮挡（2026-09-07）：SW_SHOW 后 explorer 原生图标层透过自绘层透明背景透出
            // （用户实测"显示菜单时桌面变成 explorer 图标"）。菜单显示期间自绘层临时渲染壁纸
            // （不透明）盖住 explorer 图标；菜单关闭后恢复透明。用户看到的桌面始终是壁纸+自绘图标。
            SetWallpaperOcclusion?.Invoke(true);
            try
            {
                _ = NativeMethods.ShowWindow(hList, SwShow);
                try
                {
                    // SendMessageW 同步阻塞到菜单关闭（explorer 模态菜单循环）
                    _ = NativeMethods.SendMessage(hList, WmContextmenu, hList, lParam);
                }
                finally
                {
                    _ = NativeMethods.ShowWindow(hList, SwHide);
                    DeselectAll(hList, hProc);
                }
            }
            finally
            {
                SetWallpaperOcclusion?.Invoke(false);
            }
            DiagnosticLog.Trace("shell.contextmenu", $"跨进程委托：选中 item={itemIndex} → WM_CONTEXTMENU→listView(临时显示+壁纸遮挡) pos=({pt.X},{pt.Y}) 已隐藏+清除选中");
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.contextmenu", $"跨进程委托异常: {ex.Message}");
            return false;
        }
        finally
        {
            if (hProc != IntPtr.Zero)
            {
                _ = NativeMethods.CloseHandle(hProc);
            }
        }
    }

    /// <summary>跨进程清除 explorer 桌面 listView 的全部选中：空白右键前置调用，
    /// 确保 DefView 收到 WM_CONTEXTMENU 时弹背景菜单而非图标菜单（菜单类型由选中状态决定）。</summary>
    public static void ClearDesktopSelection()
    {
        try
        {
            var defView = FindVerifiedDefView();
            if (defView == IntPtr.Zero) return;
            var hList = NativeMethods.FindWindowExW(defView, IntPtr.Zero, "SysListView32", null);
            if (hList == IntPtr.Zero) return;
            _ = NativeMethods.GetWindowThreadProcessId(hList, out var pid);
            var hProc = NativeMethods.OpenProcess(ProcessVmOperation | ProcessVmRead | ProcessVmWrite, false, pid);
            if (hProc == IntPtr.Zero) return;
            try
            {
                DeselectAll(hList, hProc);
            }
            finally
            {
                _ = NativeMethods.CloseHandle(hProc);
            }
        }
        catch
        {
            // 清除失败静默：空白右键仍按原路径处理
        }
    }

    /// <summary>跨进程 LVM_SETITEMSTATE 清除全部选中（wParam=-1=所有项，state=0）。
    /// LVITEMW x64 布局：mask@0, iItem@4, state@8, stateMask@12, pszText@16... 分配 80B。
    /// ⚠️ 旧代码 state@12/stateMask@16 是错的——会把 0x3 写进 pszText 指针位，explorer 读取时
    /// 访问违例崩溃（跨进程第一版"又崩溃了"的根因）。</summary>
    private static void DeselectAll(IntPtr hList, IntPtr hProc)
    {
        var remote = VirtualAllocEx(hProc, IntPtr.Zero, (UIntPtr)80, MemCommit | MemReserve, PageReadWrite);
        if (remote == IntPtr.Zero) return;
        try
        {
            var buf = new byte[80];
            WriteU32(buf, 0, 0x8);           // mask = LVIF_STATE
            WriteU32(buf, 8, 0);             // state = 0（清除）
            WriteU32(buf, 12, LvisSelected | LvisFocused);  // stateMask = SELECTED|FOCUSED
            _ = WriteProcessMemory(hProc, remote, buf, (UIntPtr)80, out _);
            _ = NativeMethods.SendMessage(hList, LvmSetitemstate, new IntPtr(-1), remote);
        }
        finally
        {
            _ = VirtualFreeEx(hProc, remote, 0, MemRelease);
        }
    }

    /// <summary>跨进程 LVM_SETITEMSTATE 选中指定项（mask=LVIF_STATE，state/stateMask=SELECTED|FOCUSED）。
    /// LVITEMW x64 布局：mask@0, iItem@4, state@8, stateMask@12（旧代码写 @12/@16 会污染 pszText）。</summary>
    private static void SelectItem(IntPtr hList, IntPtr hProc, int itemIndex)
    {
        var remote = VirtualAllocEx(hProc, IntPtr.Zero, (UIntPtr)80, MemCommit | MemReserve, PageReadWrite);
        if (remote == IntPtr.Zero) return;
        try
        {
            var buf = new byte[80];
            WriteU32(buf, 0, 0x8);           // mask = LVIF_STATE
            WriteU32(buf, 8, LvisSelected | LvisFocused);   // state
            WriteU32(buf, 12, LvisSelected | LvisFocused);  // stateMask
            _ = WriteProcessMemory(hProc, remote, buf, (UIntPtr)80, out _);
            _ = NativeMethods.SendMessage(hList, LvmSetitemstate, (IntPtr)itemIndex, remote);
        }
        finally
        {
            _ = VirtualFreeEx(hProc, remote, 0, MemRelease);
        }
    }

    private static int FindItemByName(IntPtr hList, IntPtr hProc, string fileName)
    {
        // LVFINDINFOW x64: flags(4)+pad(4)+psz(8)+lParam(8)+pt(8)+vkDirection(4)+pad(4) = 40B
        var remote = VirtualAllocEx(hProc, IntPtr.Zero, (UIntPtr)40, MemCommit | MemReserve, PageReadWrite);
        if (remote == IntPtr.Zero) return -1;
        try
        {
            // 文件名需要写入 explorer 进程内存（LVFINDINFO.psz 指向远程字符串）
            var strBytes = System.Text.Encoding.Unicode.GetBytes(fileName + " ");
            var remoteStr = VirtualAllocEx(hProc, IntPtr.Zero, (UIntPtr)strBytes.Length, MemCommit | MemReserve, PageReadWrite);
            if (remoteStr == IntPtr.Zero) return -1;
            try
            {
                _ = WriteProcessMemory(hProc, remoteStr, strBytes, (UIntPtr)strBytes.Length, out _);
                var buf = new byte[40];
                WriteU32(buf, 0, LvfiString);
                WritePtr(buf, 8, remoteStr);
                _ = WriteProcessMemory(hProc, remote, buf, (UIntPtr)40, out _);
                var ret = NativeMethods.SendMessage(hList, LvmFinditemW, IntPtr.Zero, remote);
                return ret.ToInt32();
            }
            finally
            {
                _ = VirtualFreeEx(hProc, remoteStr, 0, MemRelease);
            }
        }
        finally
        {
            _ = VirtualFreeEx(hProc, remote, 0, MemRelease);
        }
    }

    private static NativeRect GetItemRect(IntPtr hList, IntPtr hProc, int itemIndex)
    {
        // RECT = 16B；wParam = item index, lParam = pointer to RECT
        var remote = VirtualAllocEx(hProc, IntPtr.Zero, (UIntPtr)16, MemCommit | MemReserve, PageReadWrite);
        if (remote == IntPtr.Zero) return default;
        try
        {
            var buf = new byte[16];
            _ = WriteProcessMemory(hProc, remote, buf, (UIntPtr)16, out _);
            _ = NativeMethods.SendMessage(hList, LvmGetitemrect, (IntPtr)itemIndex, remote);
            var outBuf = new byte[16];
            if (!ReadProcessMemory(hProc, remote, outBuf, (UIntPtr)16, out _)) return default;
            return new NativeRect
            {
                Left = BitConverter.ToInt32(outBuf, 0),
                Top = BitConverter.ToInt32(outBuf, 4),
                Right = BitConverter.ToInt32(outBuf, 8),
                Bottom = BitConverter.ToInt32(outBuf, 12)
            };
        }
        finally
        {
            _ = VirtualFreeEx(hProc, remote, 0, MemRelease);
        }
    }

    private static void WritePtr(byte[] b, int o, IntPtr v)
    {
        var ptr = v.ToInt64();
        b[o] = (byte)ptr; b[o + 1] = (byte)(ptr >> 8); b[o + 2] = (byte)(ptr >> 16); b[o + 3] = (byte)(ptr >> 24);
        b[o + 4] = (byte)(ptr >> 32); b[o + 5] = (byte)(ptr >> 40); b[o + 6] = (byte)(ptr >> 48); b[o + 7] = (byte)(ptr >> 56);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref NativeMethods.POINT lpPoint);

    private const int SwShow = 5;
    private const int SwHide = 0;

    /// <summary>光标屏幕坐标是否命中 explorer 桌面 SysListView32 的任意图标项（空白右键转发前检查，P2-8）。</summary>
    public static bool HitTestDefViewItem(int screenX, int screenY)
    {
        try
        {
            var defView = FindVerifiedDefView();
            if (defView == IntPtr.Zero)
            {
                return false;
            }
            var hList = NativeMethods.FindWindowExW(defView, IntPtr.Zero, "SysListView32", null);
            if (hList == IntPtr.Zero)
            {
                return false;
            }
            _ = NativeMethods.GetWindowThreadProcessId(hList, out var pid);
            var hProc = NativeMethods.OpenProcess(ProcessVmOperation | ProcessVmRead | ProcessVmWrite, false, pid);
            if (hProc == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                var pt = new NativeMethods.POINT { X = screenX, Y = screenY };
                if (!NativeMethods.ScreenToClient(hList, ref pt))
                {
                    return false;
                }

                // 远程 LVHITTESTINFO{POINT pt; uint flags; int iItem; int iSubItem} = 20B
                //（跨进程列表消息必须远程内存）
                var remote = VirtualAllocEx(hProc, IntPtr.Zero, (UIntPtr)20, MemCommit | MemReserve, PageReadWrite);
                if (remote == IntPtr.Zero)
                {
                    return false;
                }
                try
                {
                    var buf = new byte[20];
                    WriteU32(buf, 0, (uint)pt.X);
                    WriteU32(buf, 4, (uint)pt.Y);
                    _ = WriteProcessMemory(hProc, remote, buf, (UIntPtr)20, out _);
                    _ = NativeMethods.SendMessage(hList, LVM_HITTEST, IntPtr.Zero, remote);
                    var outBuf = new byte[20];
                    if (!ReadProcessMemory(hProc, remote, outBuf, (UIntPtr)20, out _))
                    {
                        return false;
                    }
                    return BitConverter.ToInt32(outBuf, 12) >= 0;
                }
                finally
                {
                    _ = VirtualFreeEx(hProc, remote, 0, MemRelease);
                }
            }
            finally
            {
                _ = NativeMethods.CloseHandle(hProc);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.contextmenu", $"命中检查异常: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 定位含桌面图标的 SHELLDLL_DefView——【回归修复 2026-09-06 / P2-9】必须验证其内含
    /// SysListView32("FolderView") 才返回：壁纸引擎/DWM 活动会重建桌面层，Progman 直下可能
    /// 残留空壳 DefView（真身被移到顶层 WorkerW 下），拿到空壳 → 转发/委托找不到 SysListView32 →
    /// 静默无菜单（用户实测：委托失败×8，退出时 FindDesktopListView 同口径却能找到）。
    /// </summary>
    public static IntPtr FindVerifiedDefView()
    {
        var progman = NativeMethods.FindWindowW("Progman", null);
        if (progman != IntPtr.Zero)
        {
            var direct = NativeMethods.FindWindowExW(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (direct != IntPtr.Zero && NativeMethods.FindWindowExW(direct, IntPtr.Zero, "SysListView32", null) != IntPtr.Zero)
            {
                return direct;
            }
        }

        // WorkerW 是顶层窗口（非 Progman 子级）：顶层枚举找含 DefView 的 WorkerW，
        // 同样逐验含 SysListView32（壁纸引擎把 DefView 移到顶层 WorkerW 下的变体）。
        for (var worker = NativeMethods.FindWindowExW(IntPtr.Zero, IntPtr.Zero, "WorkerW", null);
             worker != IntPtr.Zero;
             worker = NativeMethods.FindWindowExW(IntPtr.Zero, worker, "WorkerW", null))
        {
            var defView = NativeMethods.FindWindowExW(worker, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView != IntPtr.Zero && NativeMethods.FindWindowExW(defView, IntPtr.Zero, "SysListView32", null) != IntPtr.Zero)
            {
                return defView;
            }
        }

        // 【回归修复 2026-09-06】回退：SysListView32 被隐藏后 FindWindowExW 可能枚举不到（
        // 自绘桌面启动后原生图标层 SW_HIDE），但 DefView 本身仍在且可接收 WM_CONTEXTMENU。
        // 找不到含 listView 的 DefView 时，返回任意 DefView（Progman 直子或 WorkerW 子）。
        // 空白右键转发只需 DefView；图标委托会再单独找 listView，找不到则自然失败不崩溃。
        var fallback = NativeMethods.FindWindowExW(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (fallback != IntPtr.Zero)
        {
            DiagnosticLog.Trace("shell.contextmenu", "FindVerifiedDefView：回退 Progman 直子 DefView（无 SysListView32 验证）");
            return fallback;
        }
        for (var worker = NativeMethods.FindWindowExW(IntPtr.Zero, IntPtr.Zero, "WorkerW", null);
             worker != IntPtr.Zero;
             worker = NativeMethods.FindWindowExW(IntPtr.Zero, worker, "WorkerW", null))
        {
            var dv = NativeMethods.FindWindowExW(worker, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (dv != IntPtr.Zero)
            {
                DiagnosticLog.Trace("shell.contextmenu", "FindVerifiedDefView：回退 WorkerW 子 DefView（无 SysListView32 验证）");
                return dv;
            }
        }
        return IntPtr.Zero;
    }

    private static void WriteU32(byte[] b, int o, uint v)
    {
        b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24);
    }

    // ===== Win32 =====


    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr hProc, IntPtr addr, UIntPtr size, uint type, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(IntPtr hProc, IntPtr addr, uint size, uint type);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr hProc, IntPtr baseAddr, byte[] buffer, UIntPtr size, out IntPtr written);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr hProc, IntPtr baseAddr, byte[] buffer, UIntPtr size, out IntPtr read);

}
