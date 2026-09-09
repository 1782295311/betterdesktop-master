using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using BetterDesktop.Shell.AppSource.Contracts;
using BetterDesktop.Shell.AppSource.Models;
using ManagedShell.Common.Enums;
using ManagedShell.Common.Helpers;

namespace BetterDesktop.Shell.AppSource.Services;

/// <summary>
/// 基于 ManagedShell 的图标提取服务（统一入口，自 shell-dock 上移）。
/// 提取逻辑参考 Cairo/ManagedShell 渲染方案；普通应用取 ExtraLarge(48) 原生帧，
/// 商店应用用 Jumbo(256) 高清源。内置内存缓存 + 预取，供 Dock/应用提取器等 UI 复用。
/// </summary>
public sealed class Win32ShellIconService : IAppIconService
{
    private readonly ConcurrentDictionary<string, ImageSource> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public async Task<ImageSource?> GetIconAsync(AppItem item, CancellationToken ct = default)
    {
        if (item is null)
        {
            return null;
        }

        var key = ResolveCacheKey(item);
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var path = ResolveExtractPath(item);

        // UWP / Store 应用（无文件路径）：经 IShellItemImageFactory 取应用图标。
        if (string.IsNullOrWhiteSpace(path))
        {
            if (string.IsNullOrWhiteSpace(item.AppUserModelId))
            {
                return null;
            }

            var aumidIcon = await Task.Run(
                () => BetterDesktop.Shell.AppSource.Native.ShellItemInterop.GetAppIcon(item.AppUserModelId!, 44),
                ct).ConfigureAwait(false);
            if (aumidIcon is not null)
            {
                _cache[key] = aumidIcon;
            }

            return aumidIcon;
        }

        var icon = await ExtractIconAsync(path, ct).ConfigureAwait(false);
        if (icon is not null)
        {
            _cache[key] = icon;
        }

        return icon;
    }

    /// <inheritdoc />
    public void Invalidate(AppItemId id)
    {
        _cache.TryRemove(id.ToString(), out _);
    }

    /// <inheritdoc />
    public async Task PrefetchAsync(IEnumerable<AppItem> items, CancellationToken ct = default)
    {
        if (items is null)
        {
            return;
        }

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            await GetIconAsync(item, ct).ConfigureAwait(false);
        }
    }

    private static string ResolveCacheKey(AppItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.IconCacheKey))
        {
            return item.IconCacheKey!;
        }

        if (!string.IsNullOrWhiteSpace(item.TargetPath))
        {
            return item.TargetPath!;
        }

        return item.ShortcutPath ?? item.Id.ToString();
    }

    /// <summary>
    /// P3a（7444 契约 A）：高清图标路径——普通应用也尝试 Jumbo(256)（SHExtractIconsW 私有导出，
    /// 失败降级公开 ExtractIconEx，再失败回 null 由调用方走默认 GetIconAsync 的 48px 行为）。
    /// 只作 dock 高清增强：不改 GetIconAsync 默认行为（防字形过度缩放回归，计划禁区⑨）。
    /// </summary>
    public async Task<ImageSource?> GetHighResIconAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return await Task.Factory.StartNew(
            () =>
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    // .lnk 先解析真实目标（与默认路径同策略，图标来自目标程序）。
                    var effectivePath = path;
                    if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                    {
                        var (_, targetPath, _) = ShellLinkResolver.Resolve(path);
                        if (!string.IsNullOrEmpty(targetPath))
                        {
                            effectivePath = targetPath;
                        }
                    }

                    // 高清链：SHExtractIconsW(256) → ExtractIconEx(256) → null（调用方回落 48 默认）。
                    var hIcon = Native.HighResIconExtractor.TryExtract(effectivePath, 0, 256);
                    if (hIcon == IntPtr.Zero)
                    {
                        hIcon = Native.HighResIconExtractor.ExtractViaPublicApi(effectivePath, 0);
                    }
                    if (hIcon == IntPtr.Zero)
                    {
                        return null;
                    }

                    // GetImageFromHIcon 内部会自动 Freeze 并 DestroyIcon(hIcon)（红线 6：句柄不泄漏）。
                    var image = IconImageConverter.GetImageFromHIcon(hIcon);
                    return (ImageSource?)image;
                }
                catch
                {
                    return null;
                }
            },
            ct,
            TaskCreationOptions.None,
            IconHelper.IconScheduler).ConfigureAwait(false);
    }

    private static string ResolveExtractPath(AppItem item)
    {
        // 优先真实目标路径，回退快捷方式路径。
        if (!string.IsNullOrWhiteSpace(item.TargetPath))
        {
            return item.TargetPath!;
        }

        return item.ShortcutPath ?? string.Empty;
    }

    private static Task<ImageSource?> ExtractIconAsync(string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.FromResult<ImageSource?>(null);
        }

        // 通过 ManagedShell 的 COM 任务调度器执行，保证线程模型一致。
        return Task.Factory.StartNew(
            () =>
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    // 快捷方式的图标应来自其目标程序，先解析真实目标路径；
                    // 否则 GetIconByFilename 会取到快捷方式壳图标。
                    var effectivePath = path;
                    if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                    {
                        // 复用 app-source 的 ShellLinkResolver 解析真实目标。
                        var (_, targetPath, _) = ShellLinkResolver.Resolve(path);
                        if (!string.IsNullOrEmpty(targetPath))
                        {
                            effectivePath = targetPath;
                        }
                    }

                    var hIcon = IconHelper.GetIconByFilename(effectivePath, ChooseIconSize(effectivePath));
                    if (hIcon == IntPtr.Zero)
                    {
                        return null;
                    }

                    // GetImageFromHIcon 内部会自动 Freeze 并 DestroyIcon(hIcon)。
                    var image = IconImageConverter.GetImageFromHIcon(hIcon);
                    return (ImageSource?)image;
                }
                catch
                {
                    // 图标提取失败不阻塞调用方。
                    return null;
                }
            },
            ct,
            TaskCreationOptions.None,
            IconHelper.IconScheduler);
    }

    /// <summary>
    /// 商店应用用 Jumbo(256) 高清源；普通应用用 ExtraLarge(48) 原生帧，
    /// 兼顾清晰度与字形占满图标框，避免过度缩放。
    /// </summary>
    private static IconSize ChooseIconSize(string path)
    {
        if (path.StartsWith("appx:", StringComparison.OrdinalIgnoreCase))
        {
            return IconSize.Jumbo;
        }

        return IconSize.ExtraLarge;
    }
}
