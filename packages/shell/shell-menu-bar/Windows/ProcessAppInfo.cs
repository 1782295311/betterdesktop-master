// ProcessAppInfo —— 依据进程 ID 解析应用可执行文件路径、可读名称与真实图标。
// 声音面板的"应用"行用真实进程图标 + 友好名称替换原先的占位符/裸 exe 名。
// 进程路径查询用托管 Process API；图标经 System.Drawing.ExtractAssociatedIcon 提取并转 BitmapSource。
// 结果按 PID 小 TTL 缓存，降低 1s 面板定时刷新的开销。
//
// H1 治理（2026-09-03，设计方案见 docs/design-proposals/2026-09-03-H1-静态缓存治理.md）：
// 原实现两个独立静态字典（_cache 键=PID 带 TTL、_icons 键=PID 无 TTL 无清理），二者都只增不逐：
// 死进程 PID 永久驻留；且 _icons 无 TTL——PID 被复用后图标/名称张冠李戴（复用防护注释只覆盖 _cache）。
// 治理设计：
//  1)【单一事实源】图标并入 Info 条目，与 Path/Name 同生命周期、同 TTL（成功 60s / 负缓存 10s）——
//     图标陈旧窗口从「无限期」收敛到 ≤60s，字典从两个收敛到一个。
//  2)【PID 复用语义】每条新查询记录进程 StartTime（UTC Ticks）；TTL 到期重查时若 StartTime 变化
//     （PID 已被复用），强制全量重解析路径/名称/图标，不复用任何旧值；负缓存不适用于复用场景。
//     TTL 窗口内（60s）的复用接受为有界陈旧（面板行本随音频会话刷新，60s 上限可感知但可纠正）。
//  3)【机会式清扫】操作计数每满 SweepEvery 次触发一次清扫：删除 Stamp 超过 SweepAfterMs 的死条目
//     （含负缓存），内存上界 = 峰值并发存活进程数级别，不再随历史 PID 总数单调增长。
// A/B 门槛（测试 ProcessAppInfoTests 守护）：清扫上界 + 复用重解析 + 图标随条目过期。
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
    internal sealed class Info
    {
        public string Path = string.Empty;
        public string Name = string.Empty;
        public long Stamp;
        /// <summary>进程启动时间（DateTime UTC Ticks）；-1 = 当时取不到（受保护进程），跳过复用比对。</summary>
        public long StartTick = -1;
        /// <summary>图标与条目同生命周期（原独立 _icons 字典已并入，无 TTL 无清理的问题随之消除）。</summary>
        public ImageSource? Icon;
    }

    internal static readonly ConcurrentDictionary<int, Info> Cache = new();

    // ---- 清扫参数（测试经内部缝覆盖） ----
    internal static int SweepEvery = 512;          // 每 512 次查询触发一次机会式清扫
    internal static long SweepAfterMs = 600_000;   // 条目 10 分钟未被触碰即视为死条目清除

    private static int _opsSinceSweep;

    /// <summary>当前缓存条目数（测试/诊断用）。</summary>
    internal static int Count => Cache.Count;

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

    /// <summary>返回指定进程的应用图标；取不到返回 null（调用方显示占位符）。
    /// 图标随 Info 条目同 TTL：过期后随下一次 GetInfo 全量重解析（PID 复用不会再展示旧图标）。</summary>
    public static ImageSource? GetIcon(int pid)
    {
        var info = GetInfo(pid);
        return info.Icon;
    }

    internal static Info GetInfo(int pid)
    {
        if (pid <= 0) return new Info();

        MaybeSweep();

        // TTL（毫秒）：成功读到路径后 60s 复用，避免每秒进程查询开销。
        const long cacheMs = 60_000;
        if (Cache.TryGetValue(pid, out var hit)
            && !string.IsNullOrEmpty(hit.Path)
            && hit.Stamp + cacheMs > Environment.TickCount64)
        {
            hit.Stamp = Environment.TickCount64; // LRU 触碰：存活条目刷新 Stamp，清扫按最后访问计
            return hit;
        }
        // 负缓存也短暂复用，防止对不存在/受保护进程反复查询。
        if (Cache.TryGetValue(pid, out var neg)
            && string.IsNullOrEmpty(neg.Path)
            && neg.Stamp + 10_000 > Environment.TickCount64)
        {
            return neg;
        }

        var entry = new Info { Stamp = Environment.TickCount64 };
        try
        {
            using var p = Process.GetProcessById(pid);
            try { entry.StartTick = p.StartTime.Ticks; }
            catch { entry.StartTick = -1; } // 受保护进程取不到 StartTime：跳过复用比对
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
        if (!string.IsNullOrEmpty(entry.Path))
        {
            entry.Icon = ExtractIcon(entry.Path);
        }
        Cache[pid] = entry;
        return entry;
    }

    /// <summary>机会式清扫：删除 Stamp 超过 SweepAfterMs 的死条目。摊销 O(n)，每次触发间隔 ≥ SweepEvery 次查询。</summary>
    private static void MaybeSweep()
    {
        if (++_opsSinceSweep < SweepEvery)
        {
            return;
        }
        _opsSinceSweep = 0;
        SweepExpired(Environment.TickCount64);
    }

    internal static void SweepExpired(long nowMs)
    {
        foreach (var (pid, entry) in Cache)
        {
            if (nowMs - entry.Stamp > SweepAfterMs)
            {
                Cache.TryRemove(pid, out _);
            }
        }
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
