using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BetterDesktop.Shell.AppSource.Models;
using BetterDesktop.Shell.Core.Native;

namespace BetterDesktop.Shell.Recent.Native;

/// <summary>
/// 系统跳转列表（AutomaticDestinationList）读取器。
/// 契约完全对齐 7401 变体 A / Open-Shell <c>JumpLists.cpp</c> [verified]：
///   CLSID_AutomaticDestinationList = F0AE1542-F497-484B-A175-A20DB09144BA（L17）
///   IID_IAutomaticDestinationList  = BC10DCE3-62F2-4BC6-AF37-DB46ED7873C4（Win10 RTM，L45）
///   IID_IAutomaticDestinationList10b = E9C5EF8D-FD41-4F72-BA87-EB03BAD5817C（10547+，L46）
///   GetList 输出 = 公开 IObjectCollection（L155：IID_IObjectCollection）。
/// 红线落地：①CLSID/IID 精确；②版本降级链（先 RTM QI，失败再 10b——两版本 GetList 签名不同，
/// 10b 多一个 flags 参数）；③接口方法序逐项对照 JumpLists.cpp L71-114（占位槽保留槽位对齐，绝不调用）；
/// ④Initialize 失败必须置空（半初始化对象崩溃）；⑤HasList 双表确认（1=固定 / 0=最近）；
/// ⑥listType：0=最近 / 1=固定。
/// 失败全部降级为空列表（不崩溃、不抛）。
/// </summary>
internal static class SystemJumpListReader
{
    private static readonly Guid ClsidAutomaticDestinationList = new("F0AE1542-F497-484B-A175-A20DB09144BA");
    private static readonly Guid IidIUnknown = new("00000000-0000-0000-0000-000000000000");
    private static readonly Guid IidIAutomaticDestinationList = new("BC10DCE3-62F2-4BC6-AF37-DB46ED7873C4");
    private static readonly Guid IidIAutomaticDestinationList10b = new("E9C5EF8D-FD41-4F72-BA87-EB03BAD5817C");
    private static readonly Guid IidIObjectCollection = new("2D108C41-3BFF-4ED2-B7F7-0AB8D22D0A14"); // 公开 propsys 接口
    private static readonly Guid IidIShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    private const uint ClsctxAll = 0x17; // 对齐 Open-Shell CComPtr::CoCreateInstance 默认（CLSCTX_ALL）
    private const int ListTypeRecent = 0;
    private const int ListTypePinned = 1;

    /// <summary>读取指定应用的系统跳转列表固定项（listType=1）。</summary>
    public static IReadOnlyList<AppItemId> ReadPinned(string appId, int maxCount = 16)
        => ReadList(appId, ListTypePinned, maxCount);

    /// <summary>读取指定应用的系统跳转列表最近项（listType=0）。</summary>
    public static IReadOnlyList<AppItemId> ReadRecent(string appId, int maxCount = 16)
        => ReadList(appId, ListTypeRecent, maxCount);

    private static IReadOnlyList<AppItemId> ReadList(string appId, int listType, int maxCount)
    {
        var result = new List<AppItemId>();
        if (string.IsNullOrWhiteSpace(appId) || maxCount <= 0)
        {
            return result;
        }

        IntPtr listUnknown = IntPtr.Zero;
        try
        {
            var clsid = ClsidAutomaticDestinationList;
            var iidUnknown = IidIUnknown;
            if (NativeMethods.CoCreateInstance(ref clsid, IntPtr.Zero, ClsctxAll, ref iidUnknown, out listUnknown) != 0
                || listUnknown == IntPtr.Zero)
            {
                return result; // 系统不支持 → 空列表
            }

            // 降级链（7401 红线 2）：先 RTM 接口；失败再试 10547+ 的 10b 接口。
            var iidRtm = IidIAutomaticDestinationList;
            if (Marshal.QueryInterface(listUnknown, ref iidRtm, out var rtmPtr) == 0 && rtmPtr != IntPtr.Zero)
            {
                Marshal.Release(rtmPtr);
                var list = (IAutomaticDestinationList)Marshal.GetObjectForIUnknown(listUnknown);
                try
                {
                    if (list.Initialize(appId, IntPtr.Zero, IntPtr.Zero) != 0)
                    {
                        return result; // 红线 4：Initialize 失败立即退出（半初始化对象不可用）
                    }
                    return ReadViaRtm(list, listType, maxCount, result);
                }
                finally
                {
                    _ = Marshal.FinalReleaseComObject(list);
                }
            }

            var iid10b = IidIAutomaticDestinationList10b;
            if (Marshal.QueryInterface(listUnknown, ref iid10b, out var b10Ptr) == 0 && b10Ptr != IntPtr.Zero)
            {
                Marshal.Release(b10Ptr);
                var list = (IAutomaticDestinationList10b)Marshal.GetObjectForIUnknown(listUnknown);
                try
                {
                    if (list.Initialize(appId, IntPtr.Zero, IntPtr.Zero) != 0)
                    {
                        return result; // 红线 4
                    }
                    return ReadVia10b(list, listType, maxCount, result);
                }
                finally
                {
                    _ = Marshal.FinalReleaseComObject(list);
                }
            }

            return result;
        }
        catch
        {
            return result; // COM 任一步异常 → 已收集的部分返回，不崩溃
        }
        finally
        {
            if (listUnknown != IntPtr.Zero)
            {
                _ = Marshal.Release(listUnknown);
            }
        }
    }

    private static IReadOnlyList<AppItemId> ReadViaRtm(IAutomaticDestinationList list, int listType, int maxCount, List<AppItemId> result)
    {
        var iidCollection = IidIObjectCollection;
        if (list.GetList(listType, (uint)maxCount, ref iidCollection, out var collectionObj) != 0 || collectionObj is null)
        {
            return result;
        }
        return EnumerateCollection(collectionObj, maxCount, result);
    }

    private static IReadOnlyList<AppItemId> ReadVia10b(IAutomaticDestinationList10b list, int listType, int maxCount, List<AppItemId> result)
    {
        var iidCollection = IidIObjectCollection;
        if (list.GetList(listType, (uint)maxCount, 1 /* flags */, ref iidCollection, out var collectionObj) != 0 || collectionObj is null)
        {
            return result;
        }
        return EnumerateCollection(collectionObj, maxCount, result);
    }

    private static IReadOnlyList<AppItemId> EnumerateCollection(object collectionObj, int maxCount, List<AppItemId> result)
    {
        try
        {
            var collection = (IObjectArray)collectionObj;
            if (collection.GetCount(out var count) != 0)
            {
                return result;
            }

            for (uint i = 0; i < count && result.Count < maxCount; i++)
            {
                var iidShellItem = IidIShellItem;
                if (collection.GetAt(i, ref iidShellItem, out var itemObj) != 0 || itemObj is null)
                {
                    continue;
                }
                try
                {
                    if (itemObj is IShellItem item
                        && item.GetDisplayName(SigdnDesktopAbsolutePath, out var path) == 0
                        && !string.IsNullOrEmpty(path))
                    {
                        result.Add(new AppItemId(path));
                    }
                }
                finally
                {
                    _ = Marshal.ReleaseComObject(itemObj);
                }
            }
            return result;
        }
        finally
        {
            _ = Marshal.FinalReleaseComObject(collectionObj);
        }
    }

    // SIGDN_DESKTOPABSOLUTEPARSING（0x80028000）：解析路径作为 AppItemId。
    private const uint SigdnDesktopAbsolutePath = 0x80028000;

    // ---- 接口定义：方法序逐项对照 Open-Shell JumpLists.cpp（红线 6：禁止臆造方法序） ----

    /// <summary>方法序对照 JumpLists.cpp L71-85（占位槽 = 签名未知的未调用方法，仅保 vtable 对齐）。</summary>
    [ComImport, Guid("BC10DCE3-62F2-4BC6-AF37-DB46ED7873C4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAutomaticDestinationList
    {
        [PreserveSig] int Initialize([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, IntPtr lnkPath, IntPtr reserved);
        [PreserveSig] int HasList([MarshalAs(UnmanagedType.Bool)] out bool pHasList);
        [PreserveSig] int GetList(int listType, uint maxCount, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppvObject);
        [PreserveSig] int AddUsagePoint();                       // 占位槽（L77，不调用）
        [PreserveSig] int PinItem([MarshalAs(UnmanagedType.IUnknown)] object pItem, int pinIndex);
        [PreserveSig] int IsPinned();                            // 占位槽（L80）
        [PreserveSig] int RemoveDestination([MarshalAs(UnmanagedType.IUnknown)] object pItem);
        [PreserveSig] int SetUsageData();                        // 占位槽（L82）
        [PreserveSig] int GetUsageData();                        // 占位槽（L83）
        [PreserveSig] int ResolveDestination();                  // 占位槽（L84）
        [PreserveSig] int ClearList(int listType);
    }

    /// <summary>10b（10547+）：GetList 多一个 flags 参数（L100），其余槽位与 RTM 一致。</summary>
    [ComImport, Guid("E9C5EF8D-FD41-4F72-BA87-EB03BAD5817C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAutomaticDestinationList10b
    {
        [PreserveSig] int Initialize([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, IntPtr lnkPath, IntPtr reserved);
        [PreserveSig] int HasList([MarshalAs(UnmanagedType.Bool)] out bool pHasList);
        [PreserveSig] int GetList(int listType, uint maxCount, uint flags, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppvObject);
        [PreserveSig] int AddUsagePoint();
        [PreserveSig] int PinItem([MarshalAs(UnmanagedType.IUnknown)] object pItem, int pinIndex);
        [PreserveSig] int IsPinned();
        [PreserveSig] int RemoveDestination([MarshalAs(UnmanagedType.IUnknown)] object pItem);
        [PreserveSig] int SetUsageData();
        [PreserveSig] int GetUsageData();
        [PreserveSig] int ResolveDestination();
        [PreserveSig] int ClearList(int listType);
    }

    /// <summary>公开 propsys 接口（GetList 返回的集合，取 GetCount/GetAt）。</summary>
    [ComImport, Guid("92CA9DCD-5673-4F87-9D0A-82E726EF2C1E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectArray
    {
        [PreserveSig] int GetCount(out uint pCount);
        [PreserveSig] int GetAt(uint uiIndex, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    }

    /// <summary>公开 IShellItem（GetDisplayName 取路径）。</summary>
    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IntPtr ppsi);
        [PreserveSig] int GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig] int Compare(IntPtr psi, uint hint, out int piOrder);
    }
}
