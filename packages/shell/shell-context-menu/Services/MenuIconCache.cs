// BetterDesktop.Shell.ContextMenus — 菜单图标缓存
// 从 exe 提取小图标（SHGetFileInfo，无额外包依赖），进程级缓存（路径 → 冻结的 ImageSource）。
// 消费方：快捷工具的设置列表行 + 右键菜单项（MenuItemDef.IconKey = "tool:<exePath>"）。
//
// H1 治理（2026-09-03，设计方案见 docs/design-proposals/2026-09-03-H1-静态缓存治理.md）：
// 原实现 Dictionary 只增不逐——拖放/工具目录里成千上万个不同路径会把缓存推向无界增长
//（单条目 ≈2-4KB：16×16×4B 像素 + WPF 包装 + 路径键）。改为【容量封顶的 LRU】：
//  - 键仍是 exe 完整路径（非 PID，无 PID 复用问题；路径对应文件内容，图标不变）；
//  - 命中即移到队首（LRU 语义），插入超容量从队尾逐出最久未用条目；
//  - 失败条目（null）同样占位并同样可被逐出，避免对同一坏路径反复 SHGetFileInfo 的同时不固化垃圾；
//  - 容量 512 ≈ 峰值驻留 ≤2MB，覆盖任何现实场景（开始菜单+桌面+工具目录的全集）。
// A/B 门槛（测试 MenuIconCacheTests 守护）：容量上界 + 命中/逐出正确性 + 逐出后可重提取。

using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BetterDesktop.Shell.ContextMenus.Services;

/// <summary>菜单图标缓存（exe 路径 → 16px ImageSource；进程级，容量封顶 LRU）。</summary>
public static class MenuIconCache
{
    /// <summary>缓存容量上界（条目）。512 × ≈2-4KB ≈ ≤2MB 峰值驻留。</summary>
    public const int Capacity = 512;

    private static readonly Dictionary<string, LinkedListNode<Entry>> Map = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<Entry> Lru = new();
    private static readonly object Gate = new();

    private sealed record Entry(string Path, ImageSource? Icon);

    /// <summary>当前缓存条目数（测试/诊断用）。</summary>
    public static int Count
    {
        get { lock (Gate) return Map.Count; }
    }

    /// <summary>清空缓存（测试/诊断用；生产不调用）。</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            Map.Clear();
            Lru.Clear();
        }
    }

    /// <summary>取文件/程序图标（缓存命中直接返回；未命中同步提取）。失败返回 null（调用方留空）。</summary>
    public static ImageSource? Get(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        lock (Gate)
        {
            if (Map.TryGetValue(path, out var hit))
            {
                // LRU 触碰：命中移到队首
                Lru.Remove(hit);
                Lru.AddFirst(hit);
                return hit.Value.Icon;
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
            if (Map.TryGetValue(path, out var raced))
            {
                // 并发重复提取：丢弃本次结果，保留已有条目（避免同键双占位）
                Lru.Remove(raced);
                Lru.AddFirst(raced);
                return raced.Value.Icon;
            }
            var node = new LinkedListNode<Entry>(new Entry(path, source));
            Map[path] = node;
            Lru.AddFirst(node);
            // 超容量：从队尾逐出最久未用
            while (Map.Count > Capacity && Lru.Last is { } oldest)
            {
                Map.Remove(oldest.Value.Path, out _);
                Lru.RemoveLast();
            }
        }
        return source;
    }

    /// <summary>仅查缓存（不触发提取）：命中返回 true。用于"菜单先出、图标后台补全"路径。</summary>
    public static bool TryGetCached(string path, out ImageSource? source)
    {
        lock (Gate)
        {
            if (Map.TryGetValue(path, out var cached))
            {
                Lru.Remove(cached);
                Lru.AddFirst(cached);
                source = cached.Value.Icon;
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
