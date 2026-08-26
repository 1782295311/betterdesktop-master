using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BetterDesktop.Shell.AppSource.Native;

/// <summary>
/// Shell 命名空间（IShellItem / IShellFolder / AppsFolder）COM 互操作。
/// 用于枚举 `shell:appsfolder`（FOLDERID_AppsFolder）虚拟文件夹中的 UWP / Store 应用。
/// 铁律：COM 接口 Guid 与 vtable 方法顺序必须与 Windows SDK 定义一致，不得自行调整。
/// 全部接口方法标 [PreserveSig] 返回 HRESULT，调用点显式检查（== 0 为成功）。
/// </summary>
internal static class ShellItemInterop
{
    // ---- 接口 IID ----
    internal static readonly Guid IID_IShellFolder = new("000214E6-0000-0000-C000-000000000046");
    internal static readonly Guid IID_IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    internal static readonly Guid IID_IShellItem2 = new("7e9fb0d3-919f-4307-ab2e-9b1860310c93");
    internal static readonly Guid IID_IShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    // ---- 常量 ----
    internal static readonly Guid BHID_SFObject = new("3981e224-559e-11d3-8f3e-00c04fa1cf22");
    internal static readonly Guid FOLDERID_AppsFolder = new("1e87508d-89c2-42f0-8a7e-645a0f50ca58");

    // PKEY_AppUserModel_*（System.AppUserModel 属性集）
    private static readonly Guid PKEY_AppUserModel_FmtId = new("9f4c2855-9f79-4b39-a8d0-e1d42de1d5f3");
    private const uint PidAppUserModelId = 5;
    private const uint PidPackageFamilyName = 16;
    private const uint PidIsSystemComponent = 20;

    private const uint ShcontfFolders = 0x20;
    private const uint ShcontfNonFolders = 0x40;

    /// <summary>SIGDN_NORMALDISPLAY：普通显示名。</summary>
    private const uint SigdnNormalDisplay = 0;

    [Flags]
    private enum SIIGBF
    {
        ResizeToFit = 0x0,
        BiggerSizeOk = 0x1
    }

    // ---- 结构 ----
    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid FmtId;
        public uint Pid;
    }

    /// <summary>STRRET（union）。项目强制 x64，指针统一 8 字节偏移。</summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct STRRET
    {
        [FieldOffset(0)]
        public uint UType;

        [FieldOffset(8)]
        public IntPtr POleStr;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int Cx;
        public int Cy;
    }

    // ---- COM 接口（[PreserveSig]：返回 HRESULT，调用点显式判 0）----
    [ComImport]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig]
        int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        [PreserveSig]
        int GetParent([MarshalAs(UnmanagedType.Interface)] out IShellItem ppsi);
        [PreserveSig]
        int GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        [PreserveSig]
        int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig]
        int Compare([MarshalAs(UnmanagedType.Interface)] IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport]
    [Guid("7e9fb0d3-919f-4307-ab2e-9b1860310c93")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem2 : IShellItem
    {
        [PreserveSig]
        int GetPropertyStore(uint flags, ref Guid riid, out IntPtr ppv);
        [PreserveSig]
        int GetPropertyStoreForFlags(uint flags, ref Guid storeFactory, ref Guid riid, out IntPtr ppv);
        [PreserveSig]
        int GetProperty(ref PROPERTYKEY key, out IntPtr pv);
        [PreserveSig]
        int GetCLSID(ref PROPERTYKEY key, out Guid pclsid);
        [PreserveSig]
        int GetFileTime(ref PROPERTYKEY key, out long pft);
        [PreserveSig]
        int GetInt32(ref PROPERTYKEY key, out int pi);
        [PreserveSig]
        int GetString(ref PROPERTYKEY key, [MarshalAs(UnmanagedType.LPWStr)] out string psz);
        [PreserveSig]
        int GetUInt32(ref PROPERTYKEY key, out uint pui);
        [PreserveSig]
        int GetUInt64(ref PROPERTYKEY key, out ulong pull);
        [PreserveSig]
        int GetBool(ref PROPERTYKEY key, out bool pf);
    }

    [ComImport]
    [Guid("000214E6-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig]
        int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName, out uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
        [PreserveSig]
        int EnumObjects(IntPtr hwnd, uint grfFlags, [MarshalAs(UnmanagedType.Interface)] out IEnumIDList ppenumIDList);
        [PreserveSig]
        int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        [PreserveSig]
        int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        [PreserveSig]
        int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        [PreserveSig]
        int CreateViewObject(IntPtr hwndOwner, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        [PreserveSig]
        int GetAttributesOf(uint cidl, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IntPtr[] apidl, ref uint rgfInOut);
        [PreserveSig]
        int GetUIObjectOf(IntPtr hwndOwner, uint cidl, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] IntPtr[] apidl, ref Guid riid, IntPtr rgfReserved, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        [PreserveSig]
        int GetDisplayNameOf(IntPtr pidl, uint uFlags, out STRRET pName);
        [PreserveSig]
        int SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszName, uint uFlags, out IntPtr ppidlOut);
    }

    [ComImport]
    [Guid("000214F2-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumIDList
    {
        [PreserveSig]
        int Next(uint celt, out IntPtr rgelt, out uint pceltFetched);
        [PreserveSig]
        int Skip(uint celt);
        [PreserveSig]
        int Reset();
        [PreserveSig]
        int Clone([MarshalAs(UnmanagedType.Interface)] out IEnumIDList ppenum);
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
    }

    // ---- P/Invoke ----
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrRetToStrW(ref STRRET pstr, IntPtr pidl, out IntPtr ppsz);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    /// <summary>
    /// 枚举 Apps 文件夹，返回每个应用项的（显示名, AUMID, 包族名, 是否系统组件）。
    /// 枚举失败（shell 服务不可用等）返回空列表，不抛异常。
    /// </summary>
    public static List<AppsFolderEntry> EnumerateAppsFolder()
    {
        var entries = new List<AppsFolderEntry>();
        try
        {
            var iidItem2 = IID_IShellItem2;
            if (SHCreateItemFromParsingName("shell:AppsFolder", IntPtr.Zero, ref iidItem2, out var rawItem) != 0 || rawItem is null)
            {
                return entries;
            }

            var item = (IShellItem2)rawItem;
            try
            {
                var bhid = BHID_SFObject;
                var iidFolder = IID_IShellFolder;
                if (item.BindToHandler(IntPtr.Zero, ref bhid, ref iidFolder, out var rawFolder) != 0 || rawFolder is null)
                {
                    return entries;
                }

                var folder = (IShellFolder)rawFolder;
                if (folder.EnumObjects(IntPtr.Zero, ShcontfFolders | ShcontfNonFolders, out var enumList) != 0 || enumList is null)
                {
                    Marshal.ReleaseComObject(folder);
                    return entries;
                }

                try
                {
                    while (true)
                    {
                        enumList.Next(1, out var pidl, out var fetched);
                        if (fetched == 0 || pidl == IntPtr.Zero)
                        {
                            break;
                        }

                        try
                        {
                            var (name, aumid, pfn, isSystem) = ReadEntry(folder, pidl);
                            if (!string.IsNullOrWhiteSpace(aumid))
                            {
                                entries.Add(new AppsFolderEntry(name, aumid, pfn, isSystem));
                            }
                        }
                        finally
                        {
                            Marshal.FreeCoTaskMem(pidl);
                        }
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(enumList);
                }

                Marshal.ReleaseComObject(folder);
            }
            finally
            {
                Marshal.ReleaseComObject(item);
            }
        }
        catch
        {
            // 枚举失败静默（shell 服务异常不阻断）。
        }

        return entries;
    }

    private static (string Name, string Aumid, string? PackageFamilyName, bool IsSystemComponent) ReadEntry(IShellFolder folder, IntPtr pidl)
    {
        var name = string.Empty;
        var aumid = string.Empty;
        string? pfn = null;
        var isSystem = false;

        // 显示名：GetDisplayNameOf + StrRetToStrW
        try
        {
            if (folder.GetDisplayNameOf(pidl, SigdnNormalDisplay, out var strret) == 0
                && StrRetToStrW(ref strret, pidl, out var psz) == 0
                && psz != IntPtr.Zero)
            {
                name = Marshal.PtrToStringUni(psz) ?? string.Empty;
                CoTaskMemFree(psz);
            }
        }
        catch
        {
            // 显示名失败不影响 AUMID 提取
        }

        // AUMID / 包族名 / 系统组件标记：pidl → IShellItem2 → GetString/GetBool
        try
        {
            var item2 = BindToShellItem2(folder, pidl);
            if (item2 is null)
            {
                return (name, string.Empty, null, false);
            }

            try
            {
                var keyAumid = new PROPERTYKEY { FmtId = PKEY_AppUserModel_FmtId, Pid = PidAppUserModelId };
                if (item2.GetString(ref keyAumid, out var aumidValue) == 0)
                {
                    aumid = aumidValue ?? string.Empty;
                }

                var keyPfn = new PROPERTYKEY { FmtId = PKEY_AppUserModel_FmtId, Pid = PidPackageFamilyName };
                if (item2.GetString(ref keyPfn, out var pfnValue) == 0 && !string.IsNullOrWhiteSpace(pfnValue))
                {
                    pfn = pfnValue;
                }

                var keySys = new PROPERTYKEY { FmtId = PKEY_AppUserModel_FmtId, Pid = PidIsSystemComponent };
                if (item2.GetBool(ref keySys, out var sysValue) == 0)
                {
                    isSystem = sysValue;
                }
            }
            finally
            {
                Marshal.ReleaseComObject(item2);
            }
        }
        catch
        {
            // 属性提取失败返回已获得部分
        }

        return (name, aumid, pfn, isSystem);
    }

    private static IShellItem2? BindToShellItem2(IShellFolder folder, IntPtr pidl)
    {
        try
        {
            var iidItem2 = IID_IShellItem2;
            if (folder.BindToObject(pidl, IntPtr.Zero, ref iidItem2, out var ppv) == 0 && ppv is IShellItem2 item2)
            {
                return item2;
            }
        }
        catch
        {
            // 绑定失败返回 null
        }

        return null;
    }

    /// <summary>
    /// 按 AUMID 取应用图标（IShellItemImageFactory → HBITMAP → BitmapSource）。
    /// 失败返回 null。
    /// </summary>
    public static ImageSource? GetAppIcon(string aumid, int size)
    {
        try
        {
            var iidFactory = IID_IShellItemImageFactory;
            if (SHCreateItemFromParsingName($"shell:AppsFolder\\{aumid}", IntPtr.Zero, ref iidFactory, out var rawFactory) != 0 || rawFactory is null)
            {
                return null;
            }

            var factory = (IShellItemImageFactory)rawFactory;
            try
            {
                var iconSize = new SIZE { Cx = size, Cy = size };
                if (factory.GetImage(iconSize, SIIGBF.ResizeToFit | SIIGBF.BiggerSizeOk, out var hBitmap) != 0 || hBitmap == IntPtr.Zero)
                {
                    return null;
                }

                try
                {
                    var source = Imaging.CreateBitmapSourceFromHBitmap(
                        hBitmap,
                        IntPtr.Zero,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                    return source;
                }
                finally
                {
                    DeleteObject(hBitmap);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Apps 文件夹单条目。</summary>
internal sealed record AppsFolderEntry(string Name, string AppUserModelId, string? PackageFamilyName, bool IsSystemComponent);
