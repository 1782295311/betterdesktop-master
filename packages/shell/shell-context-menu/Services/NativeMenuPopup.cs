// BetterDesktop.Shell.ContextMenus — 系统原生右键弹层（M0，2026-09-04 转向计划 §6-M0-3）
//
// 定位：explorer 同款渲染通道——IContextMenu 的 HMENU 直接 TrackPopupMenuEx，
// 不经任何自绘 WPF 栈；第三方 handler 的 owner-draw 子菜单由原生消息转发驱动。
//
// 【套间纪律（P2-D 同源）】COM 创建/查询/菜单模态循环全部在 StaComWorker 常驻 STA 线程
// 执行（Begin 异步派发——模态菜单循环在 STA 线程自泵消息，UI 线程立即返回，绝不同步等待）。
//
// 【消息转发生死线】TrackPopupMenu 的 owner 必须是本类自建的消息窗口，其 WndProc 把
// WM_INITMENUPOPUP(0x0117)/WM_MEASUREITEM(0x011C)/WM_DRAWITEM(0x002B)/WM_MENUSELECT(0x011F)
// 原样转发给 IContextMenu3.HandleMenuMsg2（优先）→ IContextMenu2.HandleMenuMsg——缺它则
// 懒填充子菜单为空、owner-draw 项不可见（与 ShellMenuInterop P0-B 同根因）。
//
// 【格式依据】单选：SHCreateItemFromParsingName → IShellItem.BindToHandler(BHID_SFUIObject)；
// 多选：SHCreateShellItemArrayFromIDLists → IShellItemArray.BindToHandler(BHID_SFUIObject)。
// BHID_SFUIObject = {3981E224-F559-11D3-8E3A-00C04F6837D2}（shobjidl_core.h）。
//
// 【桌面空白】优先转发 WM_CONTEXTMENU 给 explorer 的 SHELLDLL_DefView（真·explorer 背景菜单，
// WorkerW 变体扫描兜底）；转发失败降级为 SHGetDesktopFolder → CreateViewObject(IContextMenu)。
//
// 【降级契约】TryShow* 返回 false（模式非 native / 无路径 / 前置校验失败）→ 调用方回退自研
// 菜单；worker 内部失败记诊断后静默收敛（菜单不弹，宿主无感）。

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.Core.Native;

namespace BetterDesktop.Shell.ContextMenus.Services;

// ── 本文件方法级白话索引（白话 → 方法）──
//   "对选中文件/文件夹弹系统右键菜单"   → TryShowItems（异步入口，失败返 false 让调用方回退）/ ShowItemsSync（子进程同步入口）
//   "桌面空白处弹系统背景菜单"          → TryShowDesktopBackground；实现 ShowBackgroundCore（先转发 explorer DefView，失败降级 SHGetDesktopFolder）
//   "弹菜单主流程（取 IContextMenu→建 HMENU→TrackPopupMenuEx）" → ShowItemsCore
//   "把右键转发给 explorer 的 DefView 窗口" → TryForwardDefView
//   "用户点中某项后执行命令"            → InvokeCommand
//   "owner-draw 菜单的绘制/测量/选择消息（转发 IContextMenu2/3）" → 内部类 MenuOwnerWindow.WndProc
// ────────────────────────────────────

/// <summary>系统原生右键弹层（IContextMenu HMENU → TrackPopupMenuEx；全部 COM 在常驻 STA 线程）。</summary>
public static class NativeMenuPopup
{
    /// <summary>渲染模式设置键（保留兼容旧设置文件；桌面已回归自绘，本键不再参与行为判定）。</summary>
    public const string ModeKey = "context-menu.mode";

    /// <summary>
    /// 是否原生模式。桌面空白/图标右键 2026-09-07 回归自绘后，本组件仍服务开始菜单/文件管理器等
    /// 未申明自绘的表面，恒为 true（custom 与 native 同路，均弹系统原生菜单）——
    /// 【回归修复 2026-09-06 / P2-6】旧语义 custom = 完全不弹菜单（自绘回滚开关的退化残留），
    /// 桌面右键不应存在"无菜单"状态。
    /// </summary>
    public static bool IsNativeMode => true;

    // ===== 对外 API（UI 线程调用；返回 false = 调用方回退自研菜单） =====

    /// <summary>对一组文件系统路径弹系统原生菜单。physicalPx = 屏幕物理像素坐标（调用方 PointToScreen）。</summary>
    public static bool TryShowItems(IReadOnlyList<string> paths, System.Windows.Point physicalPx, bool extendedVerbs = false, Action? onCompleted = null)
    {
        if (!IsNativeMode || paths is not { Count: > 0 })
        {
            return false;
        }
        var captured = paths;
        var pos = physicalPx;
        StaComWorker.Begin(() =>
        {
            try
            {
                DiagnosticLog.Trace("shell.contextmenu", $"原生弹层 items={captured.Count} first={captured[0]} shift={extendedVerbs}");
                ShowItemsCore(captured, pos, extendedVerbs);
            }
            finally
            {
                // 【方向切换 2026-09-06】菜单已关闭（TrackPopupMenuEx 同步返回）→ 通知调用方刷新。
                onCompleted?.Invoke();
            }
        });
        return true;
    }

    /// <summary>桌面空白处弹原生背景菜单（优先转发 explorer DefView，失败降级桌面 IContextMenu）。</summary>
    public static bool TryShowDesktopBackground(System.Windows.Point physicalPx)
    {
        if (!IsNativeMode)
        {
            return false;
        }
        var pos = physicalPx;
        StaComWorker.Begin(() => ShowBackgroundCore(pos));
        return true;
    }

    // ===== 菜单服务同步入口（独立子进程调用；文件管理器逻辑：本进程 IContextMenu） =====

    /// <summary>菜单服务：同步弹图标菜单（当前线程执行，子进程内直接调用，不经 StaComWorker）。</summary>
    public static void ShowItemsSync(IReadOnlyList<string> paths, System.Windows.Point physicalPx, bool extendedVerbs = false)
    {
        ShowItemsCore(paths, physicalPx, extendedVerbs);
    }

    /// <summary>菜单服务：同步弹桌面背景菜单（本进程 CreateViewObject，不转发 DefView、不碰 explorer）。</summary>
    public static void ShowBackgroundSync(System.Windows.Point physicalPx)
    {
        var x = (int)Math.Round(physicalPx.X);
        var y = (int)Math.Round(physicalPx.Y);
        var owner = new MenuOwnerWindow();
        IntPtr hmenu = IntPtr.Zero;
        try
        {
            var hrFolder = SHGetDesktopFolder(out var folder);
            if (hrFolder != 0 || folder is null)
            {
                DiagnosticLog.Trace("shell.contextmenu", $"菜单服务 SHGetDesktopFolder 失败 hr=0x{hrFolder:X8}");
                return;
            }

            var iidCm = IidIContextMenu;
            var hrCm = folder.CreateViewObject(owner.Hwnd, ref iidCm, out var ppv);
            if (hrCm != 0 || ppv == IntPtr.Zero)
            {
                DiagnosticLog.Trace("shell.contextmenu", $"菜单服务 CreateViewObject 失败 hr=0x{hrCm:X8}");
                return;
            }

            var cm = (IContextMenu)Marshal.GetObjectForIUnknown(ppv);
            _ = Marshal.Release(ppv);
            owner.SetTarget(cm);

            hmenu = NativeMethods.CreatePopupMenu();
            var hr = cm.QueryContextMenu(hmenu, 0, CmdFirst, CmdLast, CmfNormal);
            if (hr < 0 || NativeMethods.GetMenuItemCount(hmenu) <= 0)
            {
                return;
            }

            var cmd = TrackPopupMenuEx(hmenu, TpmReturnCmd | TpmRightButton | TpmNoNotify, x, y, owner.Hwnd, IntPtr.Zero);
            if (cmd >= CmdFirst && cmd <= CmdLast)
            {
                InvokeCommand(cm, owner.Hwnd, cmd, physicalPx);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.contextmenu", $"菜单服务背景菜单失败: {ex.Message}");
        }
        finally
        {
            if (hmenu != IntPtr.Zero)
            {
                _ = NativeMethods.DestroyMenu(hmenu);
            }
            owner.Dispose();
        }
    }

    // ===== STA worker 侧实现 =====

    private static void ShowItemsCore(IReadOnlyList<string> paths, System.Windows.Point physicalPx, bool extendedVerbs)
    {
        var owner = new MenuOwnerWindow();
        var pidls = new List<IntPtr>();
        IntPtr hmenu = IntPtr.Zero;
        try
        {
            foreach (var p in paths)
            {
                if (string.IsNullOrWhiteSpace(p) || !PathExists(p))
                {
                    continue;
                }
                var pidl = NativeMethods.ILCreateFromPath(p);
                if (pidl != IntPtr.Zero)
                {
                    pidls.Add(pidl);
                }
            }
            if (pidls.Count == 0)
            {
                DiagnosticLog.Trace("shell.contextmenu", "原生弹层放弃：无有效路径");
                return;
            }
            DiagnosticLog.Trace("shell.contextmenu", $"弹层探针 pidl ok n={pidls.Count}");

            var cm = BindContextMenu(paths, pidls, owner.Hwnd);
            if (cm is null)
            {
                DiagnosticLog.Trace("shell.contextmenu", "弹层探针 bind=null");
                return;
            }

            owner.SetTarget(cm);
            hmenu = NativeMethods.CreatePopupMenu();
            var flags = CmfNormal | (extendedVerbs ? CmfExtendedVerbs : 0u);
            DiagnosticLog.Trace("shell.contextmenu", "弹层探针 QueryContextMenu begin");
            var hr = cm.QueryContextMenu(hmenu, 0, CmdFirst, CmdLast, flags);
            DiagnosticLog.Trace("shell.contextmenu", $"弹层探针 QueryContextMenu end hr=0x{hr:X8} n={NativeMethods.GetMenuItemCount(hmenu)}");
            if (hr < 0 || NativeMethods.GetMenuItemCount(hmenu) <= 0)
            {
                DiagnosticLog.Trace("shell.contextmenu", $"原生弹层空菜单 hr=0x{hr:X8}");
                return;
            }

            DiagnosticLog.Trace("shell.contextmenu", "弹层探针 Track begin");
            var cmd = TrackPopupMenuEx(
                hmenu, TpmReturnCmd | TpmRightButton | TpmNoNotify,
                (int)Math.Round(physicalPx.X), (int)Math.Round(physicalPx.Y), owner.Hwnd, IntPtr.Zero);
            DiagnosticLog.Trace("shell.contextmenu", $"原生菜单 Track 返回 cmd={cmd}");

            if (cmd >= CmdFirst && cmd <= CmdLast)
            {
                InvokeCommand(cm, owner.Hwnd, cmd, physicalPx);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.contextmenu", $"原生弹层失败: {ex.Message}");
        }
        finally
        {
            if (hmenu != IntPtr.Zero)
            {
                _ = NativeMethods.DestroyMenu(hmenu);
            }
            foreach (var pidl in pidls)
            {
                NativeMethods.ILFree(pidl);
            }
            owner.Dispose();
        }
    }

    /// <summary>
    /// 绑定 IContextMenu：经典权威通道（explorer 前时代标准路径，文件系统项必中）——
    /// 【真机实证 2026-09-05】BindToHandler(BHID_SFUIObject) 在本机全类型返回 0x800401E5
    /// （MK_E_UNAVAILABLE，GUID 无误仍不可用）→ 弃用 BindToHandler 通道，改
    /// SHBindToParent 拿父 IShellFolder + 相对子 pidl → GetUIObjectOf(IContextMenu)。
    /// 多选：全部取首路径父目录分组（桌面/开始菜单多选恒同目录；跨目录多选取第一组并记诊断）。
    /// </summary>
    private static IContextMenu? BindContextMenu(IReadOnlyList<string> paths, List<IntPtr> fullPidls, IntPtr ownerHwnd)
    {
        try
        {
            var firstParent = Path.GetDirectoryName(paths[0]);
            var childPidls = new List<IntPtr>();
            IntPtr? folderPtr = null;
            foreach (var p in paths)
            {
                if (firstParent is not null && !string.Equals(Path.GetDirectoryName(p), firstParent, StringComparison.OrdinalIgnoreCase))
                {
                    continue; // 跨目录多选：只取首组（M0 边界，桌面多选恒同目录）
                }
                var full = NativeMethods.ILCreateFromPath(p);
                if (full == IntPtr.Zero)
                {
                    continue;
                }
                var iidFolder = IidShellFolder;
                var hrBind = SHBindToParent(full, ref iidFolder, out var folder, out var child);
                if (hrBind != 0 || folder == IntPtr.Zero)
                {
                    DiagnosticLog.Trace("shell.contextmenu", $"SHBindToParent 失败 hr=0x{hrBind:X8}: {p}");
                    continue;
                }
                if (folderPtr is null)
                {
                    folderPtr = folder; // 首个父 IShellFolder 保活到 GetUIObjectOf 后释放
                }
                else
                {
                    _ = Marshal.Release(folder); // 同目录重复拿到的 psf 立即释放
                }
                childPidls.Add(child); // SHBindToParent 的子 pidl 指向 full 内部，随 full 一起释放
                fullPidls.Add(full);
            }

            if (folderPtr is null || childPidls.Count == 0)
            {
                DiagnosticLog.Trace("shell.contextmenu", "BindContextMenu：无有效条目");
                return null;
            }

            var psf = (IShellFolder)Marshal.GetObjectForIUnknown(folderPtr.Value);
            DiagnosticLog.Trace("shell.contextmenu", "弹层探针 IShellFolder RCW ok");
            try
            {
                var riid = IidIContextMenu;
                var hr = psf.GetUIObjectOf(ownerHwnd, (uint)childPidls.Count, childPidls.ToArray(), ref riid, IntPtr.Zero, out var ppv);
                DiagnosticLog.Trace("shell.contextmenu", $"弹层探针 GetUIObjectOf hr=0x{hr:X8} ppv=0x{ppv.ToInt64():X}");
                if (hr != 0 || ppv == IntPtr.Zero)
                {
                    DiagnosticLog.Trace("shell.contextmenu", $"GetUIObjectOf(IContextMenu) 失败 hr=0x{hr:X8}");
                    return null;
                }
                try
                {
                    return (IContextMenu)Marshal.GetObjectForIUnknown(ppv);
                }
                finally
                {
                    _ = Marshal.Release(ppv);
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(psf);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.contextmenu", $"绑定 IContextMenu 失败: {ex.Message}");
            return null;
        }
    }

    private static void ShowBackgroundCore(System.Windows.Point physicalPx)
    {
        var x = (int)Math.Round(physicalPx.X);
        var y = (int)Math.Round(physicalPx.Y);

        // 【回归修复 2026-09-07】空白右键 = DefView 转发（explorer 原生背景菜单，已验证可靠）。
        // 实测：DefView 收到 WM_CONTEXTMENU 不看坐标、不看选中，一律弹背景菜单——空白右键
        // 走 DefView 转发即弹正确背景菜单。菜单服务子进程 CreateViewObject 已实证 hr=0x1
        // 失败（本机系统级），不再走子进程。
        DesktopMenuDelegation.ClearDesktopSelection();
        if (TryForwardDefView(x, y))
        {
            DiagnosticLog.Trace("shell.contextmenu", $"桌面空白 → DefView 转发 ({x},{y})");
            return;
        }
        DiagnosticLog.Trace("shell.contextmenu", $"桌面空白 → DefView 转发失败，不弹菜单 ({x},{y})");

        // 降级：桌面文件夹 IContextMenu（CreateViewObject 空选择 = 背景菜单）
        var owner = new MenuOwnerWindow();
        IntPtr hmenu = IntPtr.Zero;
        try
        {
            var hrFolder = SHGetDesktopFolder(out var folder);
            if (hrFolder != 0 || folder is null)
            {
                DiagnosticLog.Trace("shell.contextmenu", $"SHGetDesktopFolder 失败 hr=0x{hrFolder:X8}");
                return;
            }

            var iidCm = IidIContextMenu;
            var hrCm = folder.CreateViewObject(owner.Hwnd, ref iidCm, out var ppv);
            if (hrCm != 0 || ppv == IntPtr.Zero)
            {
                DiagnosticLog.Trace("shell.contextmenu", $"桌面 CreateViewObject 失败 hr=0x{hrCm:X8}");
                return;
            }

            var cm = (IContextMenu)Marshal.GetObjectForIUnknown(ppv);
            _ = Marshal.Release(ppv);
            owner.SetTarget(cm);

            hmenu = NativeMethods.CreatePopupMenu();
            var hr = cm.QueryContextMenu(hmenu, 0, CmdFirst, CmdLast, CmfNormal);
            if (hr < 0 || NativeMethods.GetMenuItemCount(hmenu) <= 0)
            {
                return;
            }

            var cmd = TrackPopupMenuEx(hmenu, TpmReturnCmd | TpmRightButton | TpmNoNotify, x, y, owner.Hwnd, IntPtr.Zero);
            if (cmd >= CmdFirst && cmd <= CmdLast)
            {
                InvokeCommand(cm, owner.Hwnd, cmd, physicalPx);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.contextmenu", $"桌面原生背景菜单失败: {ex.Message}");
        }
        finally
        {
            if (hmenu != IntPtr.Zero)
            {
                _ = NativeMethods.DestroyMenu(hmenu);
            }
            owner.Dispose();
        }
    }

    /// <summary>转发 WM_CONTEXTMENU 给 explorer 桌面的 SHELLDLL_DefView（兼容 WorkerW 变体）。</summary>
    private static bool TryForwardDefView(int x, int y)
    {
        try
        {
            // 【回归修复 2026-09-06 / P2-9】共享验证版查找：空壳 DefView（无 SysListView32）
            // 转发必无菜单——壁纸引擎重建桌面层后 Progman 直下可能残留空壳，真身移到顶层 WorkerW。
            var defView = DesktopMenuDelegation.FindVerifiedDefView();
            if (defView == IntPtr.Zero)
            {
                return false;
            }

            var lParam = unchecked((IntPtr)((y & 0xFFFF) << 16 | (x & 0xFFFF)));
            _ = NativeMethods.SendMessage(defView, (uint)WmContextMenu, defView, lParam);
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.contextmenu", $"DefView 转发失败: {ex.Message}");
            return false;
        }
    }

    private static bool PathExists(string path)
        => File.Exists(path) || Directory.Exists(path);

    /// <summary>TPM_RETURNCMD 语义：lpVerb 低字 = 菜单 id − idCmdFirst（MSDN CMINVOKECOMMANDINFO 契约）。</summary>
    private static void InvokeCommand(IContextMenu cm, IntPtr hwnd, int cmd, System.Windows.Point physicalPx)
    {
        var verb = new IntPtr(cmd - CmdFirst);
        var info = new CMINVOKECOMMANDINFOEX
        {
            cbSize = (uint)Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
            fMask = CmicMaskPtInvoke,
            hwnd = hwnd,
            lpVerb = verb,
            nShow = SwShowNormal,
            ptInvoke = new POINT { X = (int)Math.Round(physicalPx.X), Y = (int)Math.Round(physicalPx.Y) },
        };
        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<CMINVOKECOMMANDINFOEX>());
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            var hr = cm.InvokeCommand(ptr);
            if (hr != 0)
            {
                DiagnosticLog.Trace("shell.contextmenu", $"原生 InvokeCommand hr=0x{hr:X8} cmd={cmd}");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    // ===== 消息窗口（TrackPopupMenu owner + owner-draw/懒填充消息转发） =====

    /// <summary>
    /// TrackPopupMenu owner：不可见顶层窗口 + 四消息转发。
    /// 【真机实证 2026-09-05】HWND_MESSAGE 消息专用窗口不能承载 TrackPopupMenuEx——菜单静默不弹
    /// （模态循环立即返回 0，无错误码）。必须用【不可见顶层窗口】（WS_POPUP，从不 Show）作 owner。
    /// </summary>
    private sealed class MenuOwnerWindow : IDisposable
    {
        private const int GwlpWndProc = -4;
        private const uint WsPopup = 0x80000000;
        private const uint WsExToolWindow = 0x00000080;
        private const uint WsExNoActivate = 0x08000000;

        private readonly WndProcDelegate _wndProc;
        private IntPtr _hwnd;
        private IContextMenu2? _cm2;
        private IContextMenu3? _cm3;
        // 【审查修复 2026-09-05】HandleMenuMsg2 的 plResult 是 LRESULT*（[in,out]，owner-draw/懒填充
        // handler 会写它）：传 IntPtr.Zero 时第三方 handler 写 *plResult = 写空指针 = AV
        //（本次真机崩溃时间线：原生弹层弹出后、Track 返回前，崩在模态循环转发消息阶段）。
        private readonly IntPtr _plResult = Marshal.AllocHGlobal(sizeof(long));
        private bool _plResultFreed;

        public MenuOwnerWindow()
        {
            _wndProc = WndProc;
            _hwnd = NativeMethods.CreateWindowExW(WsExToolWindow | WsExNoActivate, "STATIC", "BetterDesktop.NativeMenu",
                WsPopup, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (_hwnd != IntPtr.Zero)
            {
                _ = SetWindowLongPtrW(_hwnd, GwlpWndProc, Marshal.GetFunctionPointerForDelegate(_wndProc));
            }
            else
            {
                DiagnosticLog.Trace("shell.contextmenu", "原生弹层 owner 窗口创建失败");
            }
        }

        public IntPtr Hwnd => _hwnd;

        /// <summary>Track 前设置转发目标（RCW 由调用方 local 保活，这里只存引用）。</summary>
        public void SetTarget(IContextMenu cm)
        {
            _cm2 = cm as IContextMenu2;
            _cm3 = cm as IContextMenu3;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case WmInitMenuPopup:   // 懒填充：进子菜单前 handler 才填项
                case WmMeasureItem:     // owner-draw 尺寸
                case WmDrawItem:        // owner-draw 绘制
                case WmMenuSelect:      // 状态提示（部分 handler 依赖）
                    DiagnosticLog.Trace("shell.contextmenu", $"弹层探针 owner 转发 msg=0x{msg:X4}");
                    if (_cm3 is not null)
                    {
                        _ = _cm3.HandleMenuMsg2((uint)msg, wParam, lParam, _plResult);
                        return IntPtr.Zero;
                    }
                    _cm2?.HandleMenuMsg((uint)msg, wParam, lParam);
                    return IntPtr.Zero;
                default:
                    return DefWindowProcW(hwnd, msg, wParam, lParam);
            }
        }

        public void Dispose()
        {
            _cm2 = null;
            _cm3 = null;
            if (_plResult != IntPtr.Zero && !_plResultFreed)
            {
                _plResultFreed = true;
                Marshal.FreeHGlobal(_plResult);
            }
            if (_hwnd != IntPtr.Zero)
            {
                _ = NativeMethods.DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }
        }
    }

    // ===== 常量与 P/Invoke =====

    private const uint CmfNormal = 0;
    private const uint CmfExtendedVerbs = 0x100;
    private const int CmdFirst = 1;
    private const int CmdLast = 0x7FFF;
    private const uint TpmReturnCmd = 0x0100;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmNoNotify = 0x0080;
    private const uint CmicMaskPtInvoke = 0x20000000;
    private const int SwShowNormal = 1;
    private const int WmInitMenuPopup = 0x0117;
    private const int WmMeasureItem = 0x011C;
    private const int WmDrawItem = 0x002B;
    private const int WmMenuSelect = 0x011F;
    private const int WmContextMenu = 0x007B;

    private static readonly Guid IidShellFolder = new("000214E6-0000-0000-C000-000000000046");
    private static readonly Guid IidIContextMenu = new("000214E4-0000-0000-C000-000000000046");
    // 【崩溃根因 2026-09-05 实锤】原值 000214E8 是 IShellExtInit 的 IID：
    // GetUIObjectOf(000214E8) 返回聚合对象的 IShellExtInit → 同错误 GUID 强转"成功" →
    // QueryContextMenu 跳到 vtable 槽3=Initialize(pidlFolder,...) 把 HMENU 当 PIDL 解引用 → AV。
    // 正确值 IContextMenu = 000214E4（IContextMenu2=000214F4、IContextMenu3=BCFCE0A0 已核对无误）。

    [DllImport("shell32.dll")]
    private static extern int SHGetDesktopFolder(out IShellFolder ppshf);

    [DllImport("shell32.dll")]
    private static extern int SHBindToParent(IntPtr pidl, ref Guid riid, out IntPtr ppv, out IntPtr ppidlLast);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr hmenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);




    private delegate IntPtr WndProcDelegate(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hwnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CMINVOKECOMMANDINFOEX
    {
        public uint cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        public IntPtr lpParameters;
        public IntPtr lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr lpTitle;
        public IntPtr lpVerbW;
        public IntPtr lpParametersW;
        public IntPtr lpDirectoryW;
        public IntPtr lpTitleW;
        public POINT ptInvoke;
        // 【审查修复 2026-09-05】原声明漏掉 lpDirectoryW（shobjidl_core.h 契约）→ 整体 96 字节，
        // 少真实结构（104）8 字节且 lpTitleW/ptInvoke 全部错位：handler 读 lpTitleW 得到 ptInvoke
        // 的坐标位型被当指针解引用 = AV。cbSize 报 96 但字段错位比 cbSize 校验更致命。
    }

    // ===== COM 接口（vtable 顺序经 SDK 头文件核实；只声明到所调用的最深方法） =====

    [ComImport]
    [Guid("000214E4-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig]
        int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);

        [PreserveSig]
        int InvokeCommand(IntPtr pici);

        [PreserveSig]
        int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pwReserved, [Out] StringBuilder pszName, uint cchMax);

        [PreserveSig]
        int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
    }

    [ComImport]
    [Guid("000214F4-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu2
    {
        [PreserveSig]
        int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);

        [PreserveSig]
        int InvokeCommand(IntPtr pici);

        [PreserveSig]
        int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pwReserved, [Out] StringBuilder pszName, uint cchMax);

        [PreserveSig]
        int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
    }

    [ComImport]
    [Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3
    {
        [PreserveSig]
        int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);

        [PreserveSig]
        int InvokeCommand(IntPtr pici);

        [PreserveSig]
        int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pwReserved, [Out] StringBuilder pszName, uint cchMax);

        [PreserveSig]
        int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);

        [PreserveSig]
        int HandleMenuMsg2(uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr plResult);
    }

    [ComImport]
    [Guid("000214E6-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig]
        int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string displayName,
            out uint pchEaten, out IntPtr ppidl, out uint pdwAttributes);

        [PreserveSig]
        int EnumObjects(IntPtr hwnd, int grfFlags, out IntPtr enumIdList);

        [PreserveSig]
        int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);

        [PreserveSig]
        int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);

        [PreserveSig]
        int CreateViewObject(IntPtr hwnd, ref Guid riid, out IntPtr ppv);

        [PreserveSig]
        int GetAttributesOf(uint cidl, [In] IntPtr[] apidl, out uint rgfInOut);

        [PreserveSig]
        int GetUIObjectOf(IntPtr hwndOwner, uint cidl, [In] IntPtr[] apidl, ref Guid riid, IntPtr rgfReserved, out IntPtr ppv);
    }
}
