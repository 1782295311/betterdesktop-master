using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterDesktop.Shell.AppSource.Contracts;
using BetterDesktop.Shell.AppSource.Models;
using BetterDesktop.Shell.IndexIpc;
using ManagedShell.Common.Enums;
using ManagedShell.Common.Helpers;

namespace BetterDesktop.Shell.AppSource.Services;

/// <summary>
/// 图标提取服务（统一入口，自 shell-dock 上移）。
///
/// **两条数据源**（M4）：
/// ① **引擎（优先）**：索引引擎常驻进程提取**一档 256×256 PNG**（用户硬约束：不做「用多大就申请多大」
///    的多档），C# 侧按显示尺寸 `DecodePixelWidth` **倍缩解码**——不在 C# 物化大图；
/// ② **本地（逐字回退）**：引擎未装配 / 被强制 local / 取不到该项 → ManagedShell 提取
///    （普通应用 ExtraLarge(48) 原生帧，商店应用 Jumbo(256)），与 M4 之前的行为完全一致。
///
/// 内置内存缓存 + 预取，供 Dock / 应用提取器 / 开始菜单 / 搜索 / 新装通知等 UI 复用。
/// </summary>
public sealed class Win32ShellIconService : IAppIconService
{
    private readonly ConcurrentDictionary<string, ImageSource> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>索引引擎客户端（M4）。null = 未装配 → 全部走本地提取。</summary>
    private readonly IndexIpcClient? _indexClient;

    /// <summary>
    /// 引擎图标的**解码尺寸**（默认 64）。这是 C# 侧的内存闸门：引擎给的是一档 256×256 PNG，
    /// 若在 C# 物化 256px 位图，应用提取器上千项就是几百 MB；按显示尺寸解码后每张仅 ~16KB。
    /// <para>可用 <c>BETTERDESKTOP_ICON_DECODE_PX</c> 调整（夹取 16–256）——「一档大图 + 倍缩」的倍率旋钮。</para>
    /// </summary>
    private static readonly int EngineIconDecodePx = ResolveEngineIconDecodePx();

    /// <summary><c>BETTERDESKTOP_ICON_SOURCE=local</c> → 禁用引擎图标（A/B 对照与排障）。</summary>
    private static readonly bool EngineIconEnabled = ResolveEngineIconEnabled();

    private const int DefaultEngineIconDecodePx = 64;

    /// <param name="indexClient">索引引擎客户端；null 或后端被强制 local 时走本地提取（逐字回退）。</param>
    public Win32ShellIconService(IndexIpcClient? indexClient = null)
    {
        _indexClient = indexClient;
    }

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

        // 【M4】引擎优先：一档 256px PNG → 按显示尺寸解码。取不到就**逐字**走下面的本地路径
        // （引擎缺席、后端被强制 local、UWP 无文件路径、项不在索引里、字节坏 —— 全部回退）。
        var engineIcon = await TryGetEngineIconAsync(item, key, ct).ConfigureAwait(false);
        if (engineIcon is not null)
        {
            return engineIcon;
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

    // ---------------------------------------------------------------- M4：引擎图标数据源

    /// <summary>
    /// 引擎图标路径（M4）：取**一档 256×256 PNG** → 按 <see cref="EngineIconDecodePx"/> 解码。
    ///
    /// <para>任何一步不满足都返回 null（调用方**逐字回退**本地提取）：未装配客户端 / 后端被强制 local /
    /// 该项不是文件路径（UWP 的 AUMID）/ 引擎没这条 / 提取失败（`missing`）/ base64 或 PNG 坏 / IPC 故障。</para>
    ///
    /// <para>为什么不抛：图标是**非关键资源** —— 一张取不到只该退化成本地提取或 glyph 兜底，
    /// 绝不能让图标异常冒到 UI 线程（M10）。</para>
    /// </summary>
    private async Task<ImageSource?> TryGetEngineIconAsync(AppItem item, string cacheKey, CancellationToken ct)
    {
        var client = _indexClient;
        if (client is null || !EngineIconEnabled)
        {
            return null;
        }

        var engineKey = ResolveEngineIconKey(item);
        if (engineKey is null)
        {
            return null;
        }

        try
        {
            var result = await client.TryGetIconsAsync(new[] { engineKey }, ct: ct).ConfigureAwait(false);
            var bytes = result is { Icons.Count: > 0 } ? result.Icons[0].TryDecodePng() : null;
            if (bytes is null)
            {
                return null;
            }

            var image = DecodePng(bytes, EngineIconDecodePx);
            if (image is not null)
            {
                _cache[cacheKey] = image;
            }

            return image;
        }
        catch (Exception)
        {
            // 引擎图标失败绝不冒泡：交由本地提取路径处理（不静默——本地成功与否都会被 UI 反映）
            return null;
        }
    }

    /// <summary>
    /// 引擎取图标的键：**必须是真实文件路径**（.lnk 先解析成目标 —— 与本地提取同策略，
    /// 否则会取到快捷方式壳图标）。
    /// <para>UWP/Store 的 `AppUserModelId`（形如 `Microsoft.WindowsTerminal_8wekyb3d8bbwe!App`）
    /// 不是路径，引擎按路径提取不了 → 返回 null，交本地 `IShellItemImageFactory` 分支处理。</para>
    /// </summary>
    private static string? ResolveEngineIconKey(AppItem item)
    {
        var path = ResolveExtractPath(item);
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
        {
            return null;
        }

        if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var (_, targetPath, _) = ShellLinkResolver.Resolve(path);
            if (!string.IsNullOrEmpty(targetPath))
            {
                return targetPath;
            }
        }

        return path;
    }

    /// <summary>
    /// PNG 字节 → 冻结位图，**在解码阶段就降到目标尺寸**（`DecodePixelWidth`）——
    /// 这样 C# 从不物化 256px 位图（上千项时那是几百 MB），只保留显示所需的那点像素。
    /// </summary>
    private static ImageSource? DecodePng(byte[] png, int decodePx)
    {
        try
        {
            using var stream = new MemoryStream(png, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            // OnLoad：立刻读进内存 → 之后可安全关闭流（OnDemand 会在关流后抛）
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = decodePx;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze(); // 冻结 = 跨线程可用 + 免每帧校验（UI 渲染热路径）
            return bitmap;
        }
        catch (Exception)
        {
            // 坏 PNG / 解码失败 → 回退本地提取
            return null;
        }
    }

    /// <summary>引擎图标解码尺寸：默认 64，<c>BETTERDESKTOP_ICON_DECODE_PX</c> 可覆盖（夹取 16–256）。</summary>
    private static int ResolveEngineIconDecodePx()
    {
        var raw = Environment.GetEnvironmentVariable("BETTERDESKTOP_ICON_DECODE_PX");
        return int.TryParse(raw, out var px)
            ? Math.Clamp(px, 16, 256)
            : DefaultEngineIconDecodePx;
    }

    /// <summary><c>BETTERDESKTOP_ICON_SOURCE=local</c> → 强制本地提取（与索引后端的门控惯例一致）。</summary>
    private static bool ResolveEngineIconEnabled()
    {
        var value = Environment.GetEnvironmentVariable("BETTERDESKTOP_ICON_SOURCE");
        return !string.Equals(value, "local", StringComparison.OrdinalIgnoreCase);
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
