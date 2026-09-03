// BetterDesktop.Shell.ContextMenus — ShellEx COM 透传（M2 计划 §3.2）
// ContextMenuHandlers\<CLSID> → CoCreateInstance → IShellExtInit.Initialize(CF_HDROP) →
// IContextMenu.QueryContextMenu → HMENU 树解析 → 点击 InvokeCommand（GCS_VERBW）。
// 【风险明示】COM handler 在进程内运行，SEH 级崩溃无法托管隔离——host 有 watchdog/recovery 兜底；
//           out-of-proc broker 登记为 beyond。每个 handler 全程 try/catch（托管层异常不外泄）。
// 【性能】Query+Initialize 结果按 (clsid, 路径集) 缓存 60s；首次未命中由贡献者后台预热（秒开纪律）。

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using BetterDesktop.Shell.ContextMenus.Contracts;

namespace BetterDesktop.Shell.ContextMenus.Services;

/// <summary>COM 透传条目（树）。</summary>
public sealed record ShellVerbItem(
    string Text,
    bool IsSeparator,
    bool IsSubMenu,
    List<ShellVerbItem> Children,
    Action? Invoke);

internal static class ShellMenuInterop
{
    private const uint CmfNormal = 0;
    private const uint GcsVerbW = 4;
    private const uint CmdFirst = 1;
    private const uint CmdLast = 0x7FFF;

    // ===== COM 接口定义（shell 标准） =====

    [ComImport]
    [Guid("000214E8-0000-0000-C000-000000000046")]
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
    [Guid("000214E4-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellExtInit
    {
        [PreserveSig]
        int Initialize(IntPtr pidlFolder, IntPtr pDataObj, IntPtr hKeyProgId);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CMINVOKECOMMANDINFOEX
    {
        public uint cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;          // ANSI
        public IntPtr lpParameters;
        public IntPtr lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr lpTitle;
        public IntPtr lpVerbW;
        public IntPtr lpParametersW;
        public IntPtr lpTitleW;
        public POINT ptInvoke;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    // ===== P/Invoke =====

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern IntPtr ILCreateFromPath([MarshalAs(UnmanagedType.LPWStr)] string path);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);

    [DllImport("shell32.dll")]
    private static extern int SHCreateDataObject(
        IntPtr pidlFolder, uint cidl, IntPtr[] apidl, IntPtr pdtInner, ref Guid iid, out IntPtr ppv);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern int DestroyMenu(IntPtr hmenu);

    [DllImport("user32.dll")]
    private static extern int GetMenuItemCount(IntPtr hmenu);

    [DllImport("user32.dll")]
    private static extern IntPtr GetSubMenu(IntPtr hmenu, int pos);

    [DllImport("user32.dll")]
    private static extern uint GetMenuItemID(IntPtr hmenu, int pos);

    [DllImport("user32.dll")]
    private static extern uint GetMenuState(IntPtr hmenu, int pos, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMenuStringW(IntPtr hmenu, int pos, [Out] StringBuilder text, int cchMax, uint flags);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHStrDupW([MarshalAs(UnmanagedType.LPWStr)] string src, out IntPtr dest);

    private const uint MfByPosition = 0x400;
    private const uint MfSeparator = 0x800;
    private const uint CmicMaskUnicode = 0x00010000;
    private const int SwShownormal = 1;
    private static Guid IidDataObject = new("0000010E-0000-0000-C000-000000000046");

    // ===== 对外 API =====

    /// <summary>对一组路径执行 COM QueryContextMenu，返回菜单树（Invoke 闭包持 COM 引用）。失败返回空。</summary>
    public static List<ShellVerbItem> Query(IReadOnlyList<string> paths, IReadOnlyList<string> handlerClsids)
    {
        var result = new List<ShellVerbItem>();
        if (paths.Count == 0)
        {
            return result;
        }

        IntPtr pidlFolder = IntPtr.Zero;
        var apidl = new List<IntPtr>();
        IntPtr pDataObj = IntPtr.Zero;
        var createdMenus = new List<IntPtr>();
        var comObjects = new List<object>();

        try
        {
            var dir = Path.GetDirectoryName(paths[0]);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                pidlFolder = ILCreateFromPath(dir);
            }
            foreach (var p in paths)
            {
                var pidl = ILCreateFromPath(p);
                if (pidl != IntPtr.Zero)
                {
                    apidl.Add(pidl);
                }
            }
            if (apidl.Count == 0)
            {
                return result;
            }

            if (SHCreateDataObject(pidlFolder, (uint)apidl.Count, apidl.ToArray(), IntPtr.Zero, ref IidDataObject, out pDataObj) != 0
                || pDataObj == IntPtr.Zero)
            {
                return result;
            }

            foreach (var clsid in handlerClsids)
            {
                try
                {
                    var type = Type.GetTypeFromCLSID(new Guid(clsid));
                    if (type is null)
                    {
                        continue;
                    }
                    var instance = Activator.CreateInstance(type);
                    if (instance is null)
                    {
                        continue;
                    }
                    comObjects.Add(instance);

                    if (instance is not IShellExtInit init)
                    {
                        continue;
                    }
                    if (init.Initialize(pidlFolder, pDataObj, IntPtr.Zero) != 0 || instance is not IContextMenu contextMenu)
                    {
                        continue;
                    }

                    var hmenu = CreatePopupMenu();
                    createdMenus.Add(hmenu);
                    var added = contextMenu.QueryContextMenu(hmenu, 0, CmdFirst, CmdLast, CmfNormal);
                    if (added < 0 || GetMenuItemCount(hmenu) <= 0)
                    {
                        continue;
                    }

                    var items = WalkMenu(hmenu, contextMenu, depth: 0);
                    result.AddRange(items);
                }
                catch
                {
                    // 单 handler 失败（未注册/初始化拒绝）静默跳过（M10）
                }
            }
            return result;
        }
        catch
        {
            return result;
        }
        finally
        {
            foreach (var pidl in apidl)
            {
                ILFree(pidl);
            }
            if (pidlFolder != IntPtr.Zero)
            {
                ILFree(pidlFolder);
            }
            foreach (var hmenu in createdMenus)
            {
                DestroyMenu(hmenu);
            }
            // comObjects：RCW 留给 Invoke 闭包使用；引用由调用方通过返回树持有（闭包），其余交给 GC 终结释放。
            if (comObjects.Count == 0)
            {
                // 无可用 handler：立即清空避免误用
                comObjects.Clear();
            }
            _ = pDataObj; // Marshal 释放交给终结器（RCW）
        }
    }

    /// <summary>枚举某目标适用的 ContextMenuHandlers CLSID（场景路径集同 RegistryVerbs + shellex 三层）。</summary>
    internal static List<string> EnumerateHandlers(FileKind kind, string path)
    {
        var ext = Path.GetExtension(path);
        var roots = new List<string>();
        if (kind is FileKind.Folder or FileKind.Drive)
        {
            roots.AddRange(["*\\shellex\\ContextMenuHandlers", "AllFilesystemObjects\\shellex\\ContextMenuHandlers", "Folder\\shellex\\ContextMenuHandlers", "Directory\\shellex\\ContextMenuHandlers"]);
        }
        else
        {
            roots.AddRange(["*\\shellex\\ContextMenuHandlers", "AllFilesystemObjects\\shellex\\ContextMenuHandlers", $"SystemFileAssociations\\{ext}\\shellex\\ContextMenuHandlers"]);
            if (!string.IsNullOrEmpty(ext))
            {
                roots.Add($"{ext}\\shellex\\ContextMenuHandlers");
                var progId = Microsoft.Win32.Registry.GetValue($"HKEY_CLASSES_ROOT\\{ext}", null, null) as string;
                if (!string.IsNullOrWhiteSpace(progId))
                {
                    roots.Add($"{progId}\\shellex\\ContextMenuHandlers");
                }
            }
        }

        var clsids = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(root);
                if (key is null)
                {
                    continue;
                }
                foreach (var name in key.GetSubKeyNames())
                {
                    using var sub = key.OpenSubKey(name);
                    var clsid = sub?.GetValue(null) as string ?? name;
                    if (Guid.TryParse(clsid.Trim('{', ' ', '}').Length == 36
                            ? "{" + clsid.Trim('{', ' ', '}') + "}"
                            : clsid, out var guid) && seen.Add(guid.ToString("D")))
                    {
                        clsids.Add(guid.ToString("B"));
                    }
                }
            }
            catch
            {
                // 单场景读失败跳过（M10）
            }
        }
        return clsids;
    }

    private static List<ShellVerbItem> WalkMenu(IntPtr hmenu, IContextMenu contextMenu, int depth)
    {
        var items = new List<ShellVerbItem>();
        if (depth > 3)
        {
            return items; // 深度护栏
        }

        var count = GetMenuItemCount(hmenu);
        for (var pos = 0; pos < count; pos++)
        {
            var state = GetMenuState(hmenu, pos, MfByPosition);
            if ((state & MfSeparator) != 0)
            {
                items.Add(new ShellVerbItem(string.Empty, IsSeparator: true, IsSubMenu: false, [], null));
                continue;
            }

            var text = GetItemText(hmenu, pos);
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            var sub = GetSubMenu(hmenu, pos);
            if (sub != IntPtr.Zero)
            {
                items.Add(new ShellVerbItem(text, false, true, WalkMenu(sub, contextMenu, depth + 1), null));
                continue;
            }

            var id = GetMenuItemID(hmenu, pos);
            if (id < CmdFirst || id > CmdLast)
            {
                continue; // 项不归本查询段（系统自带插入项）
            }

            var verb = GetVerb(contextMenu, id - CmdFirst);
            var captured = (contextMenu, verb);
            items.Add(new ShellVerbItem(
                text, false, false, [],
                Invoke: () =>
                {
                    try
                    {
                        InvokeVerb(captured.contextMenu, captured.verb);
                    }
                    catch
                    {
                        // COM 调用失败静默（M10）
                    }
                }));
        }
        return items;
    }

    private static string GetItemText(IntPtr hmenu, int pos)
    {
        var buffer = new StringBuilder(256);
        var len = GetMenuStringW(hmenu, pos, buffer, buffer.Capacity, MfByPosition);
        return len > 0 ? buffer.ToString() : string.Empty;
    }

    private static string GetVerb(IContextMenu contextMenu, uint offset)
    {
        var buffer = new StringBuilder(256);
        try
        {
            if (contextMenu.GetCommandString((UIntPtr)offset, GcsVerbW, IntPtr.Zero, buffer, (uint)buffer.Capacity) == 0)
            {
                return buffer.ToString();
            }
        }
        catch
        {
            // 部分 handler 未实现 GetCommandString
        }
        return string.Empty;
    }

    private static void InvokeVerb(IContextMenu contextMenu, string verb)
    {
        if (string.IsNullOrEmpty(verb))
        {
            return; // 无 verb 的动态项（拖放类/ExplorerCommand）v1 不支持
        }

        SHStrDupW(verb, out var lpVerbW);
        try
        {
            var info = new CMINVOKECOMMANDINFOEX
            {
                cbSize = (uint)Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                fMask = CmicMaskUnicode,
                nShow = SwShownormal,
                lpVerbW = lpVerbW,
            };
            var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<CMINVOKECOMMANDINFOEX>());
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                _ = contextMenu.InvokeCommand(ptr);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(lpVerbW);
        }
    }
}
