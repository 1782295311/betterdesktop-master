// BetterDesktop.Shell.Desktop — 自绘菜单图标提供者（2026-09-17）
//
// 【为什么需要】用户实测：桌面自绘右键菜单的「打开方式」子菜单只列应用名、没有任何图标，
//   一排「WPS Office / Doubao / Google Chrome / Microsoft Edge / Firefox / Okular / Quark / VS Code」
//   光看文字认不出是哪个软件（同名/近名应用易混）。菜单项契约 MenuItemDef.Icon 由此填充。
//
// 【来源单一】复用本包**已有**的图标通道（ManagedShell IconHelper + IconImageConverter，与
//   桌面图标网格 LoadFileIconAsync 同一条链），不新开第二套 shell 图标管线（避免两处行为漂移）。
//
// 【尺寸】IconSize.Small —— ManagedShell 枚举实证 Small=1 对应 16px 系统小图标，
//   正好是菜单行的图标槽尺寸（与 explorer 右键菜单一致）。
//
// 【线程】菜单在 UI 线程构建时同步取（右键瞬间）。为把开销压到最低：
//   进程级缓存（路径 → ImageSource，命中即零 IO）+ 结果一律 Freeze（冻结后跨线程可复用）。
//   首次右键会为 8 个候选各取一次图标（毫秒级 shell 调用），其后恒走缓存。
//
// 【失败契约】取不到图标返回 null（调用方不画图标）；异常不得冒泡打断菜单构建（M10）。
//
// 消费方：DesktopIconsControl（打开方式候选 = 应用 exe 图标；打开项 = 文件/目录自身图标）。

using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using BetterDesktop.Kernel.Core;

namespace BetterDesktop.Shell.Desktop.Services;

/// <summary>自绘菜单的小图标（16px）解析器：文件/目录/应用 exe 通用，进程级缓存。</summary>
public static class MenuIconProvider
{
    /// <summary>缓存上限（超出整体清空：菜单图标集合很小，简单策略足够且无锁竞争热点）。</summary>
    private const int MaxCache = 512;

    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    /// <summary>
    /// 取路径的 16px 小图标（文件/目录/可执行文件通用）。取不到返回 null（调用方不占位）。
    /// </summary>
    /// <param name="path">真实文件系统路径；shell 虚拟项（"::{CLSID}"）等非路径输入直接返回 null。</param>
    public static ImageSource? Get(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
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

        var icon = Extract(path);

        lock (Gate)
        {
            if (Cache.Count >= MaxCache)
            {
                Cache.Clear();
            }
            Cache[path] = icon;
        }
        return icon;
    }

    /// <summary>清缓存（图标基本不变，仅诊断/皮肤切换等场景需要）。</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            Cache.Clear();
        }
    }

    private static ImageSource? Extract(string path)
    {
        // 先判存在：不存在时 IconHelper 会返回"未知文件"默认图标，菜单里看着像坏图。
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return null;
        }

        try
        {
            var hIcon = ManagedShell.Common.Helpers.IconHelper.GetIconByFilename(
                path, ManagedShell.Common.Enums.IconSize.Small);
            if (hIcon == IntPtr.Zero)
            {
                return null;
            }

            var source = ManagedShell.Common.Helpers.IconImageConverter.GetImageFromHIcon(hIcon);
            if (source is null)
            {
                return null;
            }

            // 冻结：菜单图标会被缓存并可能在其它线程/后续会话复用（未冻结的 Freezable 绑定线程）。
            if (!source.IsFrozen && source.CanFreeze)
            {
                source.Freeze();
            }
            return source;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.desktop", $"菜单图标提取失败 {path}: {ex.Message}");
            return null;
        }
    }
}
