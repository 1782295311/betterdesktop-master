// ProcessAppInfo —— 依据进程 ID 解析应用可执行文件路径、可读名称与真实图标。
// 声音面板的"应用"行用真实进程图标 + 友好名称替换原先的占位符/裸 exe 名。
// 进程路径查询用托管 Process API；图标经 System.Drawing.ExtractAssociatedIcon 提取并转 BitmapSource。
// 结果按 PID 小 TTL 缓存，降低 1s 面板定时刷新的开销。
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BetterDesktop.Shell.MenuBar.Windows;

internal static class ProcessAppInfo
{
    private sealed class Info
    {
        public string Path = string.Empty;
        public string Name = string.Empty;
        public long Stamp;
    }

    // PID 复用风险：进程退出后 PID 可能被其他进程占用。用 TTL 限制，过期后重查以更正。
    private static readonly ConcurrentDictionary<int, Info> _cache = new();
    private static readonly ConcurrentDictionary<int, ImageSource?> _icons = new();

    /// <summary>返回指定进程的可执行文件路径；取不到返回空串。</summary>
    public static string GetPath(int pid)
    {
        return GetInfo(pid).Path;
    }

    /// <summary>返回指定进程的可读应用名（版本信息 FileDescription 优先，回退文件名）。</summary>
    public static string GetName(int pid)
    {
        return GetInfo(pid).Name;
    }

    /// <summary>返回指定进程的应用图标；取不到返回 null（调用方显示占位符）。</summary>
    public static ImageSource? GetIcon(int pid)
    {
        if (pid <= 0) return null;
        if (_icons.TryGetValue(pid, out var cached)) return cached;

        var info = GetInfo(pid);
        var icon = string.IsNullOrEmpty(info.Path) ? null : ExtractIcon(info.Path);
        _icons[pid] = icon; // 允许存 null，避免反复失败
        return icon;
    }

    private static Info GetInfo(int pid)
    {
        if (pid <= 0) return new Info();

        // TTL（毫秒）：成功读到路径后 60s 复用，避免每秒进程查询开销。
        const long cacheMs = 60_000;
        if (_cache.TryGetValue(pid, out var hit)
            && !string.IsNullOrEmpty(hit.Path)
            && hit.Stamp + cacheMs > Environment.TickCount64)
        {
            return hit;
        }
        // 负缓存也短暂复用，防止对不存在/受保护进程反复查询。
        if (_cache.TryGetValue(pid, out var neg)
            && string.IsNullOrEmpty(neg.Path)
            && neg.Stamp + 10_000 > Environment.TickCount64)
        {
            return neg;
        }

        var entry = new Info { Stamp = Environment.TickCount64 };
        try
        {
            using var p = Process.GetProcessById(pid);
            try
            {
                entry.Path = p.MainModule?.FileName ?? string.Empty;
            }
            catch
            {
                entry.Path = string.Empty;
            }
        }
        catch
        {
            entry.Path = string.Empty;
        }

        entry.Name = ResolveFriendlyName(entry.Path, pid);
        _cache[pid] = entry;
        return entry;
    }

    private static string ResolveFriendlyName(string path, int pid)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        try
        {
            var version = FileVersionInfo.GetVersionInfo(path);
            if (!string.IsNullOrWhiteSpace(version.FileDescription))
            {
                return version.FileDescription;
            }
            if (!string.IsNullOrWhiteSpace(version.ProductName))
            {
                return version.ProductName;
            }
        }
        catch
        {
            // 取值失败回退文件名
        }
        var fileName = Path.GetFileNameWithoutExtension(path);
        return string.IsNullOrWhiteSpace(fileName) ? $"进程 #{pid}" : fileName!;
    }

    private static ImageSource? ExtractIcon(string path)
    {
        try
        {
            using var ico = Icon.ExtractAssociatedIcon(path);
            if (ico is null) return null;
            // CreateBitmapSourceFromHIcon 会复制像素，随后可安全 Dispose 原始图标。
            var bitmap = Imaging.CreateBitmapSourceFromHIcon(
                ico.Handle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}
