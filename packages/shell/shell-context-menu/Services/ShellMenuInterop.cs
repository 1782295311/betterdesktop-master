// BetterDesktop.Shell.ContextMenus — ShellEx COM 透传（M2 计划 §3.1）
// ContextMenuHandlers\<CLSID> → CoCreateInstance → IShellExtInit.Initialize(CF_HDROP) →
// IContextMenu.QueryContextMenu → HMENU 树解析 → 点击 InvokeCommand（GCS_VERBW）。
//
// 【2026-09-03 诊断修复批】
//  P0-B IContextMenu2/3 + WM_INITMENUPOPUP 懒填充泵：360压缩/7-Zip/显卡/国产云盘类 handler 的
//       子菜单是懒填充的——只有宿主把 WM_INITMENUPOPUP(0x0117) 转发给
//       IContextMenu2.HandleMenuMsg / IContextMenu3.HandleMenuMsg2 时才往里填项。此前声明了
//       HandleMenuMsg 却从未调用（全库 grep 证实），WalkMenu 直接 GetSubMenu 递归拿到的是空菜单
//       → "母项/图标在、展开是空的"。现进入每个子菜单前先合成转发（IContextMenu3 优先）。
//  P0-B 配套 owner-draw 项文本回退链：GetMenuStringW 空 → GCS_VERBW(4) → GCS_HELPTEXTW(5)，
//       不再因空文本被丢（注：HELPTEXT 回退项的 canonical verb 可能为空，点击由 InvokeVerb
//       空 verb 保护，静默不炸）。
//  P2-D 常驻 STA 工作线程（StaComWorker）：RCW 在创建套间内使用；InvokeCommand 点击时经
//       Dispatcher 编队回创建线程执行。此前每 Query 起一个即抛的临时 STA 线程 + Join，
//       线程退出后跨套间调用必然失败。
//  P2-B QueryContextMenu 按 Shift 状态传 CMF_EXTENDEDVERBS(0x100)。
//  P2-A ContextMenuHandlers 注册表扫描补 Registry32 视图（WOW6432Node）：64 位进程对 32 位
//       handler 仅枚举 + 诊断标注（in-proc 无法跨位数载入，out-of-proc broker 登记为 beyond）。
//  pDataObj 引用计数修复：SHCreateDataObject 产出的引用由本方持有，finally 显式 Marshal.Release
//       （此前留裸 IntPtr 等"终结器"——裸指针没有终结器，是实打实的 COM 泄漏）。
//  背景场景（桌面空白）：QueryBackground 以 pidlFolder=桌面、pDataObj=NULL 初始化（背景 handler
//       的标准形态，如 NVIDIA 控制面板等）。
// 【风险明示】COM handler 在进程内运行，SEH 级崩溃无法托管隔离——host 有 watchdog/recovery 兜底；
//           out-of-proc broker 登记为 beyond。每个 handler 全程 try/catch（托管层异常不外泄）。
// 【性能】Query+Initialize 结果按 (clsid, 路径集, Shift) 缓存 60s；首次未命中由贡献者后台预热（秒开）。

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.ContextMenus.Contracts;
using BetterDesktop.Shell.Core.Native;
using Microsoft.Win32;

namespace BetterDesktop.Shell.ContextMenus.Services;

// ── 本文件方法级白话索引（ShellEx 扩展 COM 透传，白话 → 方法）──
//   "实例化某个扩展 CLSID 并 Query 出它贡献的菜单项（文件目标）" → Query
//   "桌面背景场景下 Query 扩展菜单项" → QueryBackground
//   "列出某场景注册的 ShellEx handler CLSID（ContextMenuHandlers 等）" → EnumerateHandlers
//   "由 文件类型/感知类型/ProgID 推导场景注册表根路径" → SceneHandlerRoots
//   "扩展名 → 感知类型 / ProgID（场景根推导取数）" → PerceivedTypeOf / ResolveProgId / ProgIdValid（2026-09-08 自 RegistryVerbs 迁入）
//   唯一生产消费方：ShellExMenuPreview（设置里的 ShellEx 只读预览）；COM 调用全部经 StaComWorker 常驻 STA 线程。
// ────────────────────────────────────

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
    private const uint CmfExtendedVerbs = 0x100; // P2-B：Shift 扩展动词（SDK shlobj_core.h 实证）
    private const uint GcsVerbW = 4;
    private const uint GcsHelpTextW = 5;
    private const uint WmInitMenuPopup = 0x0117;
    private const uint CmdFirst = 1;
    private const uint CmdLast = 0x7FFF;

    // ===== COM 接口定义（shell 标准；IID 已对照 shobjidl_core.h 全部核实） =====
    // 【崩溃根因 2026-09-05 实锤】IContextMenu 与 IShellExtInit 的 IID 原先互换：
    // IContextMenu(000214E4) 被标成 000214E8（真 IShellExtInit）→ Initialize/QueryContextMenu
    // 各调到对方的 vtable 槽 → QueryContextMenu 实调 Initialize 把 HMENU 当 pidl 解引用 = AV
    //（9-04 背景右键崩溃与 9-05 图标右键崩溃同根因；当时 OleInitialize 归因不完整）。

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

    /// <summary>IContextMenu2：增加 HandleMenuMsg（owner-draw / WM_INITMENUPOPUP 懒填充依赖此转发）。</summary>
    [ComImport]
    [Guid("000214F4-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu2
    {
        // ---- IContextMenu ----
        [PreserveSig]
        int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);

        [PreserveSig]
        int InvokeCommand(IntPtr pici);

        [PreserveSig]
        int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pwReserved, [Out] StringBuilder pszName, uint cchMax);

        // ---- IContextMenu2 ----
        [PreserveSig]
        int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
    }

    /// <summary>IContextMenu3：再增加 HandleMenuMsg2（优先用；实现面是 IContextMenu2 的超集转发）。</summary>
    [ComImport]
    [Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3
    {
        // ---- IContextMenu ----
        [PreserveSig]
        int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);

        [PreserveSig]
        int InvokeCommand(IntPtr pici);

        [PreserveSig]
        int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pwReserved, [Out] StringBuilder pszName, uint cchMax);

        // ---- IContextMenu2 ----
        [PreserveSig]
        int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);

        // ---- IContextMenu3 ----
        [PreserveSig]
        int HandleMenuMsg2(uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr plResult);
    }

    [ComImport]
    [Guid("000214E8-0000-0000-C000-000000000046")]
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
        public IntPtr lpDirectoryW;
        public IntPtr lpTitleW;
        public POINT ptInvoke;
        // 【审查修复 2026-09-05】补 lpDirectoryW（shobjidl_core.h 真实结构 104 字节；缺它则
        // lpTitleW/ptInvoke 整体错位 8 字节，handler 读 lpTitleW 会得到无效指针）。与 NativeMenuPopup 同修。
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    // ===== P/Invoke =====

    [DllImport("shell32.dll")]
    private static extern int SHCreateDataObject(
        IntPtr pidlFolder, uint cidl, IntPtr[] apidl, IntPtr pdtInner, ref Guid iid, out IntPtr ppv);

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

    /// <summary>
    /// Open With ContextMenuHandler（"打开方式"应用清单）。
    /// 【2026-09-03 实证修正】此前硬编码 "09799AFB-AD67-49f3-94AC-5B9D9D9ADA6E" 是错误 CLSID
    /// （屏蔽从未生效，.url/.lnk 上 20+ 应用倾倒照旧）。正确值经 ContextMenuManager-master
    /// GuidInfosDic.ini 权威核对 [verified]：09799AFB-AD67-11D1-ABCD-00C04FC30936（shell32）。
    /// 清单内容源 = HKCR\Applications\&lt;app&gt;\shell&lt;verb&gt;（OpenWithList.cs 实证）。
    /// </summary>
    private const string OpenWithHandlerClsid = "09799AFB-AD67-11D1-ABCD-00C04FC30936";

    // ===== 对外 API =====

    /// <summary>
    /// 对一组路径执行 COM QueryContextMenu，返回菜单树（Invoke 闭包持 COM 引用）。失败返回空。
    /// handlerNames：CLSID→注册名（诊断日志用；可为 null）。extendedVerbs：Shift 扩展动词（P2-B）。
    /// 【STA 生死线】IShellExtInit/IContextMenu 是 STA-only COM——全部经 StaComWorker 在常驻
    /// STA 线程创建与调用（调用方无须关心套间；后台预热线程阻塞等待完成）。
    /// </summary>
    public static List<ShellVerbItem> Query(
        IReadOnlyList<string> paths, IReadOnlyList<string> handlerClsids,
        IReadOnlyDictionary<string, string>? handlerNames = null, bool extendedVerbs = false)
        => StaComWorker.Run(() => QueryCore(paths, handlerClsids, handlerNames, extendedVerbs, background: false));

    /// <summary>桌面空白（Background）场景：pidlFolder=桌面、pDataObj=NULL（背景 handler 标准初始化形态）。</summary>
    public static List<ShellVerbItem> QueryBackground(
        IReadOnlyList<string> handlerClsids, IReadOnlyDictionary<string, string>? handlerNames = null)
        => StaComWorker.Run(() => QueryCore([], handlerClsids, handlerNames, extendedVerbs: false, background: true));

    private static List<ShellVerbItem> QueryCore(
        IReadOnlyList<string> paths, IReadOnlyList<string> handlerClsids,
        IReadOnlyDictionary<string, string>? handlerNames, bool extendedVerbs, bool background)
    {
        var result = new List<ShellVerbItem>();
        if (!background && paths.Count == 0)
        {
            return result;
        }

        IntPtr pidlFolder = IntPtr.Zero;
        var apidl = new List<IntPtr>();
        IntPtr pDataObj = IntPtr.Zero;
        var createdMenus = new List<IntPtr>();

        try
        {
            var dir = background
                ? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
                : Path.GetDirectoryName(paths[0]);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                pidlFolder = NativeMethods.ILCreateFromPath(dir);
            }

            if (!background)
            {
                foreach (var p in paths)
                {
                    var pidl = NativeMethods.ILCreateFromPath(p);
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
            }

            var uFlags = extendedVerbs && !background ? CmfNormal | CmfExtendedVerbs : CmfNormal;
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

                    if (instance is not IShellExtInit init)
                    {
                        continue;
                    }
                    if (init.Initialize(pidlFolder, pDataObj, IntPtr.Zero) != 0 || instance is not IContextMenu contextMenu)
                    {
                        continue;
                    }

                    var hmenu = NativeMethods.CreatePopupMenu();
                    createdMenus.Add(hmenu);
                    // 2026-09-04 崩溃定位探针：QueryContextMenu 内 AV 不可捕获（第三方 handler native 崩），
                    // 但 DiagnosticLog 同步落盘——崩溃后日志最后一个 begin 行即元凶 CLSID（屏蔽名单的依据）。
                    DiagnosticLog.Trace("shell.contextmenu", $"query begin {clsid} background={background} flags=0x{uFlags:X}");
                    var added = contextMenu.QueryContextMenu(hmenu, 0, CmdFirst, CmdLast, uFlags);
                    if (added < 0 || NativeMethods.GetMenuItemCount(hmenu) <= 0)
                    {
                        continue;
                    }

                    // QI 到哪层用哪层：IContextMenu3（HandleMenuMsg2）→ IContextMenu2（HandleMenuMsg）
                    var cm2 = contextMenu as IContextMenu2;
                    var cm3 = contextMenu as IContextMenu3;
                    var items = WalkMenu(hmenu, contextMenu, cm2, cm3, depth: 0);
                    result.AddRange(items);
                    // 诊断：每个 handler 的产出（问题 3/4 定位依据——哪个 handler 出了什么一目了然）
                    var texts = items.Count == 0 ? "无" : string.Join(" | ", items
                        .Where(t => !t.IsSeparator)
                        .Select(t => t.IsSubMenu ? t.Text + "▸(" + t.Children.Count + ")" : t.Text)
                        .Take(6));
                    DiagnosticLog.Trace("shell.contextmenu",
                        $"handler {clsid}{(handlerNames is not null && handlerNames.TryGetValue(clsid, out var n) ? $"({n})" : string.Empty)} -> {items.Count} 项: {texts}");
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
                NativeMethods.ILFree(pidl);
            }
            if (pidlFolder != IntPtr.Zero)
            {
                NativeMethods.ILFree(pidlFolder);
            }
            foreach (var hmenu in createdMenus)
            {
                NativeMethods.DestroyMenu(hmenu);
            }
            // pDataObj 引用计数归本方（SHCreateDataObject 出参）→ 显式释放；
            // handler 若留存引用会自行 AddRef，此处 Release 只还我们自己的那一计。
            if (pDataObj != IntPtr.Zero)
            {
                _ = Marshal.Release(pDataObj);
            }
        }
    }

    /// <summary>扩展名 → 感知类型（HKCR\<ext> 的 PerceivedType 值；读取失败按空串，M10）。
    /// 【2026-09-08 迁移】自 RegistryVerbs（已删）——场景根推导的取数源，仅本类消费。</summary>
    internal static string PerceivedTypeOf(string ext)
    {
        if (string.IsNullOrEmpty(ext))
        {
            return string.Empty;
        }
        try
        {
            return Registry.GetValue($@"HKEY_CLASSES_ROOT\{ext}", "PerceivedType", null) as string ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>扩展名 → ProgID（UserChoice 优先，回退 HKCR\<ext> 默认值；均需 ProgIdValid 校验）。
    /// 【2026-09-08 迁移】自 RegistryVerbs（已删）——场景根推导的取数源，仅本类消费。</summary>
    internal static string? ResolveProgId(string ext)
    {
        if (string.IsNullOrEmpty(ext))
        {
            return null;
        }
        try
        {
            var userChoice = Registry.GetValue(
                $@"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{ext}\UserChoice",
                "ProgId", null) as string;
            var mergedDefault = Registry.GetValue($@"HKEY_CLASSES_ROOT\{ext}", null, null) as string;
            foreach (var candidate in new[] { userChoice, mergedDefault })
            {
                if (ProgIdValid(candidate))
                {
                    return candidate;
                }
            }
        }
        catch
        {
            // 解析失败按无 ProgID 场景（M10）
        }
        return null;
    }

    /// <summary>ProgID 合法性：非空/长度 ≤255/非 Applications\ 前缀/注册表确实存在。
    /// 【2026-09-08 迁移】自 RegistryVerbs（已删）；RegistryKey 持有原生 HKEY，必须 using（G1）。</summary>
    internal static bool ProgIdValid(string? progId)
    {
        if (string.IsNullOrWhiteSpace(progId) || progId.Length > 255)
        {
            return false;
        }
        if (progId.StartsWith(@"Applications\", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        using var key = Registry.ClassesRoot.OpenSubKey(progId);
        return key is not null;
    }

    /// <summary>
    /// 枚举某目标适用的 ContextMenuHandlers CLSID（P1-C 场景表；
    /// background=true 扫 DesktopBackground / Directory\Background 根）。
    /// handlerNames 输出 CLSID→注册名映射（诊断日志用；可为 null）。
    /// 【P2-A】补 Registry32 视图扫描：64 位进程对 32 位 handler 仅枚举 + 诊断标注（无法 in-proc 载入）。
    /// </summary>
    internal static List<string> EnumerateHandlers(
        FileKind kind, string path, Dictionary<string, string>? handlerNames = null, bool background = false)
    {
        var ext = Path.GetExtension(path);
        var roots = SceneHandlerRoots(kind, ext, PerceivedTypeOf(ext), ResolveProgId(ext), background);

        var clsids = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            EnumerateHandlerRoot(Registry.ClassesRoot, root, clsids, seen, handlerNames, is32Bit: false);
            if (Environment.Is64BitOperatingSystem)
            {
                using var hive32 = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Registry32);
                EnumerateHandlerRoot(hive32, root, clsids, seen, handlerNames, is32Bit: true);
            }
        }
        return clsids;
    }

    /// <summary>COM handler 场景根（P1-C：shell → shellex\ContextMenuHandlers；感知类型/ProgID 取本类 PerceivedTypeOf/ResolveProgId）。</summary>
    internal static List<string> SceneHandlerRoots(FileKind kind, string ext, string perceivedType, string? progId, bool background)
    {
        var roots = new List<string>();
        if (background)
        {
            roots.Add(@"DesktopBackground\shellex\ContextMenuHandlers");
            roots.Add(@"Directory\Background\shellex\ContextMenuHandlers");
            return roots;
        }

        roots.Add(@"*\shellex\ContextMenuHandlers");
        roots.Add(@"AllFilesystemObjects\shellex\ContextMenuHandlers");
        if (kind is FileKind.Folder or FileKind.Drive)
        {
            roots.Add(@"Folder\shellex\ContextMenuHandlers");
            roots.Add(@"Directory\shellex\ContextMenuHandlers");
            if (kind == FileKind.Drive)
            {
                roots.Add(@"Drive\shellex\ContextMenuHandlers");
            }
        }
        else
        {
            if (!string.IsNullOrEmpty(ext))
            {
                roots.Add($@"SystemFileAssociations\{ext}\shellex\ContextMenuHandlers");
                roots.Add($@"{ext}\shellex\ContextMenuHandlers");
                if (!string.IsNullOrWhiteSpace(progId))
                {
                    roots.Add($@"{progId}\shellex\ContextMenuHandlers");
                }
            }
            if (!string.IsNullOrEmpty(perceivedType))
            {
                roots.Add($@"SystemFileAssociations\{perceivedType}\shellex\ContextMenuHandlers");
            }
        }
        return roots;
    }

    private static void EnumerateHandlerRoot(
        RegistryKey hive, string root, List<string> clsids, HashSet<string> seen,
        Dictionary<string, string>? handlerNames, bool is32Bit)
    {
        try
        {
            using var key = hive.OpenSubKey(root);
            if (key is null)
            {
                return;
            }
            foreach (var name in key.GetSubKeyNames())
            {
                using var sub = key.OpenSubKey(name);
                var clsid = sub?.GetValue(null) as string ?? name;
                if (!Guid.TryParse(clsid.Trim('{', ' ', '}').Length == 36
                        ? "{" + clsid.Trim('{', ' ', '}') + "}"
                        : clsid, out var guid) || !seen.Add(guid.ToString("D")))
                {
                    continue;
                }
                // Open With ContextMenuHandler（"打开方式"应用清单）：全类型屏蔽。
                // 原因（2026-09-03 实测差评）：它在 .lnk/.url 等关联稀疏类型上会回退枚举
                // 整个 HKCR\Applications（steam/ubuntu/wsl/VMware 全量倾倒）；本 shell 已自带
                // "打开方式…"对话框项，二者职责重叠。判据双保险：已知 CLSID + 注册名匹配。
                var isOpenWith = guid.ToString("D").Equals(OpenWithHandlerClsid, StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Open With", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("打开方式", StringComparison.OrdinalIgnoreCase);
                if (isOpenWith)
                {
                    DiagnosticLog.Trace("shell.contextmenu", $"handler 屏蔽(OpenWith): {name} = {clsid}");
                    continue;
                }
                // P2-A：32 位 handler 在 64 位进程内无法 in-proc 载入——枚举 + 诊断标注，不入查询集。
                if (is32Bit && Environment.Is64BitProcess)
                {
                    DiagnosticLog.Trace("shell.contextmenu", $"handler 跳过(32位，64位进程无法 in-proc 载入): {name} = {clsid}");
                    continue;
                }
                clsids.Add(guid.ToString("B"));
                if (handlerNames is not null)
                {
                    handlerNames[guid.ToString("B")] = is32Bit ? $"{name} (32位)" : name;
                }
            }
        }
        catch
        {
            // 单场景读失败跳过（M10）
        }
    }

    private static List<ShellVerbItem> WalkMenu(
        IntPtr hmenu, IContextMenu contextMenu, IContextMenu2? cm2, IContextMenu3? cm3, int depth)
    {
        var items = new List<ShellVerbItem>();
        if (depth > 3)
        {
            return items; // 深度护栏
        }

        var count = NativeMethods.GetMenuItemCount(hmenu);
        for (var pos = 0; pos < count; pos++)
        {
            var state = GetMenuState(hmenu, pos, MfByPosition);
            if ((state & MfSeparator) != 0)
            {
                items.Add(new ShellVerbItem(string.Empty, IsSeparator: true, IsSubMenu: false, [], null));
                continue;
            }

            var sub = GetSubMenu(hmenu, pos);
            if (sub != IntPtr.Zero)
            {
                // P0-B：懒填充泵——进子菜单前转发 WM_INITMENUPOPUP（lParam 低字=子菜单位置、高字=0），
                // handler 此刻才往里填项；IContextMenu3 优先（HandleMenuMsg2）。
                ForwardInitMenuPopup(cm3, cm2, sub, pos);
                items.Add(new ShellVerbItem(
                    GetItemText(hmenu, pos, null, null),
                    false, true, WalkMenu(sub, contextMenu, cm2, cm3, depth + 1), null));
                continue;
            }

            var id = GetMenuItemID(hmenu, pos);
            if (id < CmdFirst || id > CmdLast)
            {
                continue; // 项不归本查询段（系统自带插入项）
            }

            var text = GetItemText(hmenu, pos, contextMenu, id - CmdFirst);
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            var verb = GetVerb(contextMenu, id - CmdFirst);
            var captured = (contextMenu, verb);
            items.Add(new ShellVerbItem(
                text, false, false, [],
                Invoke: () =>
                {
                    try
                    {
                        // P2-D：编队回 STA 创建线程执行（RCW 同套间 + Dispatcher 消息泵保证封送可达）
                        StaComWorker.Run(() => InvokeVerb(captured.contextMenu, captured.verb));
                    }
                    catch
                    {
                        // COM 调用失败静默（M10）
                    }
                }));
        }
        return items;
    }

    /// <summary>WM_INITMENUPOPUP 合成转发：IContextMenu3.HandleMenuMsg2 优先，回退 IContextMenu2.HandleMenuMsg。</summary>
    private static void ForwardInitMenuPopup(IContextMenu3? cm3, IContextMenu2? cm2, IntPtr hmenuSub, int pos)
    {
        try
        {
            var lParam = (IntPtr)pos;
            if (cm3 is not null)
            {
                _ = cm3.HandleMenuMsg2(WmInitMenuPopup, hmenuSub, lParam, IntPtr.Zero);
                return;
            }
            cm2?.HandleMenuMsg(WmInitMenuPopup, hmenuSub, lParam);
        }
        catch
        {
            // handler 不支持消息转发（M10）
        }
    }

    /// <summary>项文本：GetMenuStringW 空（owner-draw/懒文本）→ GCS_VERBW → GCS_HELPTEXTW 回退链（P0-B 配套）。</summary>
    private static string GetItemText(IntPtr hmenu, int pos, IContextMenu? contextMenu, uint? idOffset)
    {
        var buffer = new StringBuilder(256);
        var len = GetMenuStringW(hmenu, pos, buffer, buffer.Capacity, MfByPosition);
        if (len > 0)
        {
            return MenuText.FromWin32(buffer.ToString());
        }
        if (contextMenu is not null && idOffset is { } offset)
        {
            var verb = TryGetCommandString(contextMenu, offset, GcsVerbW);
            if (!string.IsNullOrEmpty(verb))
            {
                return MenuText.FromWin32(verb);
            }
            var help = TryGetCommandString(contextMenu, offset, GcsHelpTextW);
            if (!string.IsNullOrEmpty(help))
            {
                return MenuText.FromWin32(help);
            }
        }
        return string.Empty;
    }

    private static string GetVerb(IContextMenu contextMenu, uint offset)
        => TryGetCommandString(contextMenu, offset, GcsVerbW);

    private static string TryGetCommandString(IContextMenu contextMenu, uint offset, uint uType)
    {
        var buffer = new StringBuilder(256);
        try
        {
            if (contextMenu.GetCommandString((UIntPtr)offset, uType, IntPtr.Zero, buffer, (uint)buffer.Capacity) == 0)
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
            return; // 无 verb 的动态项（拖放类/ExplorerCommand）不支持
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
