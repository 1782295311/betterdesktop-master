// BetterDesktop.Shell.Desktop — shell 命名空间虚拟项（"::{CLSID}"）图标提取。
// 此电脑/回收站等虚拟项不是文件系统对象，ManagedShell IconHelper.GetIconByFilename 按路径取必失败；
// 须走 SHParseDisplayName → SHGetFileInfo(PIDL, SHGFI_ICON) 取与 explorer 桌面一致的主题图标。
// 项数量少（4 个左右），UI 线程同步取开销可忽略。

using System;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterDesktop.Shell.Core.Native;

namespace BetterDesktop.Shell.Desktop.Services;

/// <summary>shell 命名空间虚拟项图标提取（"::{CLSID}" → ImageSource）。</summary>
internal static class ShellNamespaceHelper
{
    private const uint ShgfiIcon = 0x0100;
    private const uint ShgfiLargeIcon = 0x0000;
    private const uint ShgfiPidl = 0x0008;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }


    /// <summary>取虚拟项主题图标；失败返回 null（UI 留空白，不阻断渲染）。</summary>
    public static ImageSource? GetIcon(string clsidPath)
    {
        IntPtr pidl = IntPtr.Zero;
        try
        {
            if (NativeMethods.SHParseDisplayName(clsidPath, IntPtr.Zero, out pidl, 0, out _) != 0 || pidl == IntPtr.Zero)
            {
                return null;
            }

            var info = new NativeMethods.SHFILEINFO();
            if (NativeMethods.SHGetFileInfo(pidl, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), ShgfiIcon | ShgfiLargeIcon | ShgfiPidl) == IntPtr.Zero
                || info.hIcon == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                // Bmp 转换：CreateBitmapSourceFromHIcon 对 32bpp alpha HICON 表现稳定（与 explorer 图标一致）
                var bs = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    info.hIcon,
                    System.Windows.Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                bs.Freeze();
                return bs;
            }
            finally
            {
                _ = NativeMethods.DestroyIcon(info.hIcon);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (pidl != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(pidl);
            }
        }
    }
}
