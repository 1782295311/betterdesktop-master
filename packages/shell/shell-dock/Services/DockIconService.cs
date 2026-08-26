using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterDesktop.Shell.AppSource.Contracts;
using BetterDesktop.Shell.AppSource.Models;
using BetterDesktop.Shell.Dock.Models;

namespace BetterDesktop.Shell.Dock.Services;

/// <summary>
/// Dock 图标服务适配器。
/// 自 shell-dock 上移真实图标提取逻辑后，本类仅作为薄适配层：
/// 将 <see cref="DockItemData"/> 转换为 <see cref="AppItem"/>，委托给 app-source 的
/// <see cref="IAppIconService"/> 统一完成提取、缓存与预取，dock 不再持有任何提取实现。
/// 额外的"用户自定义图标文件夹替换"逻辑在此层前置拦截：若文件夹下存在与程序 exe 同名
/// （去扩展名）的 .png/.ico，优先返回用户图标，实现"对部分图标进行替换优化"。
/// </summary>
public sealed class DockIconService : IDockIconService
{
    private readonly IAppIconService _appIconService;
    private readonly DockVisualSettings? _visual;

    public DockIconService(IAppIconService appIconService, DockVisualSettings? visual = null)
    {
        _appIconService = appIconService ?? throw new ArgumentNullException(nameof(appIconService));
        _visual = visual;
    }

    /// <inheritdoc />
    public Task<ImageSource?> GetIconAsync(DockItemData item, CancellationToken ct = default)
    {
        if (item is null)
        {
            return Task.FromResult<ImageSource?>(null);
        }

        // 用户自定义图标文件夹优先：按 exe 文件名（去扩展名）匹配 .png/.ico。
        var overrideIcon = TryLoadCustomIcon(item);
        if (overrideIcon is not null)
        {
            return Task.FromResult<ImageSource?>(overrideIcon);
        }

        return _appIconService.GetIconAsync(ToAppItem(item), ct);
    }

    /// <summary>在 <see cref="DockVisualSettings.CustomIconFolder"/> 下查找与程序 exe 同名（去扩展名）的
    /// .png/.ico 文件；命中则解码为冻结的 ImageSource 返回，否则返回 null。匹配规则：文件名（不含扩展名）
    /// 与 exe 文件名相同即视为替换目标，便于"对部分图标替换"而无需逐个映射。</summary>
    private ImageSource? TryLoadCustomIcon(DockItemData item)
    {
        if (_visual is null) return null;
        var folder = _visual.CustomIconFolder;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return null;

        var target = item.TargetPath;
        if (string.IsNullOrWhiteSpace(target)) return null;
        var exeName = Path.GetFileNameWithoutExtension(target);
        if (string.IsNullOrWhiteSpace(exeName)) return null;

        foreach (var ext in new[] { ".png", ".ico" })
        {
            var candidate = Path.Combine(folder, exeName + ext);
            if (!File.Exists(candidate)) continue;
            try
            {
                var img = new BitmapImage();
                img.BeginInit();
                img.UriSource = new Uri(candidate, UriKind.Absolute);
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.EndInit();
                img.Freeze();
                return img;
            }
            catch
            {
                // 解码失败跳过该候选，回退默认图标提取。
            }
        }
        return null;
    }

    /// <inheritdoc />
    public void Invalidate(DockItemId id)
    {
        _appIconService.Invalidate(new AppItemId(id.ToString()));
    }

    /// <inheritdoc />
    public Task PrefetchAsync(IEnumerable<DockItemData> items, CancellationToken ct = default)
    {
        if (items is null)
        {
            return Task.CompletedTask;
        }

        var appItems = new List<AppItem>();
        foreach (var item in items)
        {
            if (item is not null)
            {
                appItems.Add(ToAppItem(item));
            }
        }

        return _appIconService.PrefetchAsync(appItems, ct);
    }

    /// <summary>
    /// 将 Dock 项目映射为 app-source 应用项（仅携带图标提取所需的字段）。
    /// </summary>
    internal static AppItem ToAppItem(DockItemData item)
    {
        return new AppItem
        {
            Id = new AppItemId(item.Id.ToString()),
            Name = item.Name,
            ShortcutPath = item.ShortcutPath,
            TargetPath = item.TargetPath,
            Source = BetterDesktop.Shell.AppSource.Models.AppSource.StartMenu,
            IconCacheKey = item.IconCacheKey
        };
    }
}
