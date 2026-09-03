// BetterDesktop.Shell.ContextMenus — 菜单图标缓存
// 从 exe 提取小图标（SHGetFileInfo，无额外包依赖），进程级缓存（路径 → 冻结的 ImageSource）。
// 消费方：快捷工具的设置列表行 + 右键菜单项（MenuItemDef.IconKey = "tool:<exePath>"）。

using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BetterDesktop.Shell.ContextMenus.Services;

/// <summary>菜单图标缓存（exe 路径 → 16px ImageSource；进程级）。</summary>
public static class MenuIconCache
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    /// <summary>取文件/程序图标（缓存命中直接返回；未命中同步提取）。失败返回 null（调用方留空）。</summary>
    public static ImageSource? Get(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        lock (Gate)
        {
            if (Cache.TryGetValue(path, out var cached))
            {
                return cached;
            }
        }

        ImageSource? source = null;
        try
        {
            var info = new SHFILEINFOW();
            var size = (uint)Marshal.SizeOf<SHFILEINFOW>();
            if (SHGetFileInfo(path, 0, ref info, size, ShgfiIcon | ShgfiSmallIcon) != IntPtr.Zero && info.hIcon != IntPtr.Zero)
            {
                source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                _ = DestroyIcon(info.hIcon);
            }
        }
        catch
        {
            // 图标提取失败留空（M10）
            source = null;
        }

        lock (Gate)
        {
            Cache[path] = source;
        }
        return source;
    }

    /// <summary>仅查缓存（不触发提取）：命中返回 true。用于"菜单先出、图标后台补全"路径。</summary>
    public static bool TryGetCached(string path, out ImageSource? source)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(path, out var cached))
            {
                source = cached;
                return true;
            }
        }

        source = null;
        return false;
    }

    private const uint ShgfiIcon = 0x100;
    private const uint ShgfiSmallIcon = 0x1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFOW
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
        ref SHFILEINFOW psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
