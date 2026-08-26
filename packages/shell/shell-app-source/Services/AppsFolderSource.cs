using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BetterDesktop.Shell.AppSource.Models;
using BetterDesktop.Shell.AppSource.Native;

namespace BetterDesktop.Shell.AppSource.Services;

/// <summary>
/// `shell:appsfolder`（FOLDERID_AppsFolder）数据源：枚举 UWP / Microsoft Store 应用。
/// 产出 <see cref="AppSource.Store"/> 来源的 <see cref="AppItem"/>，Id 以 AppUserModelId 为稳定主键
/// （见 <see cref="AppItemId"/> 注释规则）。
/// 过滤规则：
/// 1. 无 AUMID 的项（系统虚拟项/占位）跳过；
/// 2. 标记为系统组件（PKEY_AppUserModel_IsSystemComponent）的项跳过；
/// 3. 与用户/公共开始菜单 Programs 目录下同名 .lnk 的项跳过（该 UWP 已由开始菜单来源覆盖，
///    避免干净模式重复展示）。
/// </summary>
public sealed class AppsFolderSource
{
    private readonly string _startMenuPrograms;
    private readonly string _commonStartMenuPrograms;

    public AppsFolderSource()
    {
        _startMenuPrograms = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
        _commonStartMenuPrograms = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs");
    }

    /// <summary>
    /// 枚举 Store / UWP 应用（带过滤）。失败返回空列表，不抛异常。
    /// </summary>
    public IReadOnlyList<AppItem> ScanStoreApps()
    {
        var entries = ShellItemInterop.EnumerateAppsFolder();
        if (entries.Count == 0)
        {
            return Array.Empty<AppItem>();
        }

        // 开始菜单已有同名 .lnk → 该 UWP 已由开始菜单来源覆盖，跳过避免重复。
        var startMenuNames = CollectStartMenuNames();

        var items = new List<AppItem>(entries.Count);
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.AppUserModelId) || entry.IsSystemComponent)
            {
                continue;
            }

            var name = string.IsNullOrWhiteSpace(entry.Name) ? entry.AppUserModelId : entry.Name.Trim();
            if (startMenuNames.Contains(name))
            {
                continue;
            }

            items.Add(new AppItem
            {
                Id = new AppItemId(entry.AppUserModelId),
                Name = name,
                Source = BetterDesktop.Shell.AppSource.Models.AppSource.Store,
                AppUserModelId = entry.AppUserModelId,
                PackageFamilyName = entry.PackageFamilyName,
                IconCacheKey = "aumid:" + entry.AppUserModelId
            });
        }

        return items;
    }

    /// <summary>收集用户 + 公共开始菜单 Programs 下 .lnk 的显示名（去扩展名），用于跨来源去重。</summary>
    private HashSet<string> CollectStartMenuNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in new[] { _startMenuPrograms, _commonStartMenuPrograms })
        {
            try
            {
                if (!Directory.Exists(dir))
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories))
                {
                    var display = Path.GetFileNameWithoutExtension(file)?.Trim();
                    if (!string.IsNullOrWhiteSpace(display))
                    {
                        names.Add(display);
                    }
                }
            }
            catch
            {
                // 单个目录读取失败不阻断
            }
        }

        return names;
    }
}
