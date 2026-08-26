using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.AppSource.Contracts;
using BetterDesktop.Shell.AppSource.Models;
using BetterDesktop.Shell.Pinning.Contracts;
using AppSourceEnum = BetterDesktop.Shell.AppSource.Models.AppSource;

namespace BetterDesktop.Shell.Pinning.Services;

/// <summary>
/// 通用固定服务实现：按 zone 分区的 JSON 持久化 + 旧 dock-pinned.json 一次性迁移。
/// 内存中以 Dictionary&lt;zone, List&lt;PinnedItem&gt;&gt; 维护；所有写操作加锁并自动持久化（M10 容错：异常记日志不冒泡）。
/// 持久化通过 DTO（基本类型字段）中转，避免直接序列化 <see cref="AppItem"/>（其 Id 为 readonly struct，STJ 无法回填）。
/// </summary>
public sealed class PinningService : IPinningService
{
    private readonly object _sync = new();
    private readonly Dictionary<string, List<PinnedItem>> _zones = new(StringComparer.OrdinalIgnoreCase);
    private readonly IAppSourceService _appSourceService;
    private readonly IKernelLogger _logger;
    private readonly string _storagePath;
    private readonly string _legacyPath;

    public event EventHandler<PinnedChangedEventArgs>? PinnedChanged;

    public PinningService(IAppSourceService appSourceService, IKernelLogger logger, string? storagePath = null)
    {
        _appSourceService = appSourceService ?? throw new ArgumentNullException(nameof(appSourceService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _storagePath = storagePath ?? Path.Combine(appData, "BetterDesktop", "pinning.json");
        _legacyPath = Path.Combine(Path.GetDirectoryName(_storagePath) ?? Path.GetTempPath(), "dock-pinned.json");

        // 构造即加载（含旧文件迁移），使服务一经 Provide 即可提供完整固定列表。
        Load();
    }

    /// <inheritdoc />
    public IReadOnlyList<PinnedItem> GetPinned(string zone)
    {
        lock (_sync)
        {
            if (!_zones.TryGetValue(zone, out var list) || list is null)
            {
                return Array.Empty<PinnedItem>();
            }

            // 按列表索引回填 Zone / Order，使消费方拿到的是带上下文的快照。
            return list.Select((item, index) => item with { Zone = zone, Order = index }).ToList();
        }
    }

    /// <inheritdoc />
    public void Pin(string zone, AppItem appItem)
    {
        if (appItem is null || appItem.Id.IsEmpty)
        {
            return;
        }

        var changed = false;
        lock (_sync)
        {
            if (!_zones.TryGetValue(zone, out var list) || list is null)
            {
                list = new List<PinnedItem>();
                _zones[zone] = list;
            }

            if (list.Any(x => x.AppItem.Id == appItem.Id))
            {
                return;
            }

            list.Add(new PinnedItem { AppItem = appItem });
            changed = true;
        }

        if (changed)
        {
            Save();
            RaisePinnedChanged(zone);
        }
    }

    /// <inheritdoc />
    public void Unpin(string zone, AppItemId appId)
    {
        if (appId.IsEmpty)
        {
            return;
        }

        var changed = false;
        lock (_sync)
        {
            if (_zones.TryGetValue(zone, out var list) && list is not null)
            {
                var index = list.FindIndex(x => x.AppItem.Id == appId);
                if (index >= 0)
                {
                    list.RemoveAt(index);
                    changed = true;
                }
            }
        }

        if (changed)
        {
            Save();
            RaisePinnedChanged(zone);
        }
    }

    /// <inheritdoc />
    public void Reorder(string zone, IReadOnlyList<AppItemId> order)
    {
        if (order is null || order.Count == 0)
        {
            return;
        }

        var changed = false;
        lock (_sync)
        {
            if (!_zones.TryGetValue(zone, out var list) || list is null)
            {
                return;
            }

            var lookup = list.ToDictionary(x => x.AppItem.Id, x => x);
            var reordered = new List<PinnedItem>();
            foreach (var id in order)
            {
                if (lookup.TryGetValue(id, out var item))
                {
                    reordered.Add(item);
                    lookup.Remove(id);
                }
            }

            // 未在排序列表中的项追加到末尾，保持稳定顺序。
            reordered.AddRange(lookup.Values);

            _zones[zone] = reordered;
            changed = true;
        }

        if (changed)
        {
            Save();
            RaisePinnedChanged(zone);
        }
    }

    /// <inheritdoc />
    public bool IsPinned(string zone, AppItemId appId)
    {
        if (appId.IsEmpty)
        {
            return false;
        }

        lock (_sync)
        {
            return _zones.TryGetValue(zone, out var list) && list is not null
                && list.Any(x => x.AppItem.Id == appId);
        }
    }

    /// <inheritdoc />
    public void Load()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                var file = JsonSerializer.Deserialize<PinningFile>(json);
                if (file is not null)
                {
                    file.Zones ??= new Dictionary<string, List<PinnedItemDto>>();
                    lock (_sync)
                    {
                        _zones.Clear();
                        foreach (var kvp in file.Zones)
                        {
                            var dtos = kvp.Value ?? new List<PinnedItemDto>();
                            _zones[kvp.Key] = dtos.Select(d => new PinnedItem { AppItem = ToAppItem(d.AppItem) }).ToList();
                        }
                    }

                    return;
                }
            }

            // 首次运行：尝试从旧 dock-pinned.json 一次性迁移到 "dock" zone。
            MigrateFromLegacy();
        }
        catch (Exception ex)
        {
            _logger.Error($"[Pinning] Load 失败，保持空固定列表：{ex.Message}");
        }
    }

    /// <inheritdoc />
    public void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_storagePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            PinningFile file;
            lock (_sync)
            {
                file = new PinningFile
                {
                    Zones = _zones.ToDictionary(
                        kvp => kvp.Key,
                        kvp => kvp.Value.Select((item, index) =>
                            new PinnedItemDto { AppItem = ToDto(item.AppItem), Order = index }).ToList())
                };
            }

            var json = JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storagePath, json);
        }
        catch (Exception ex)
        {
            _logger.Error($"[Pinning] Save 失败，内存状态仍保留：{ex.Message}");
        }
    }

    /// <summary>
    /// 旧 dock-pinned.json（List&lt;DockItemData&gt; 格式）一次性迁移到 pinning.json 的 "dock" zone。
    /// 旧文件保留为备份，不删除；迁移条件（pinning.json 不存在）保证仅执行一次。
    /// </summary>
    private void MigrateFromLegacy()
    {
        try
        {
            if (!File.Exists(_legacyPath))
            {
                return;
            }

            var json = File.ReadAllText(_legacyPath);
            var legacy = JsonSerializer.Deserialize<List<LegacyPinnedItem>>(json);
            if (legacy is null || legacy.Count == 0)
            {
                // 旧文件为空：仍写一份空 pinning.json 防止重复迁移。
                Save();
                return;
            }

            var migrated = new List<PinnedItem>();
            foreach (var item in legacy)
            {
                if (item is null || string.IsNullOrWhiteSpace(item.Id?.Value))
                {
                    continue;
                }

                // DockAppType: Uwp(1) → AppSource.Store；其余 → Installed。
                // .url 扩展名会在 Dock 侧经 AppSourceConverter.ToDockAppType 还原为 Url，无需在此保留 AppType。
                var source = item.AppType == 1 ? AppSourceEnum.Store : AppSourceEnum.Installed;
                var appItem = new AppItem
                {
                    Id = new AppItemId(item.Id!.Value),
                    Name = item.Name ?? string.Empty,
                    ShortcutPath = item.ShortcutPath ?? string.Empty,
                    TargetPath = item.TargetPath ?? string.Empty,
                    Source = source,
                    AppUserModelId = item.AppUserModelId,
                    IconCacheKey = item.IconCacheKey,
                    UninstallCommand = item.UninstallCommand
                };

                migrated.Add(new PinnedItem { AppItem = appItem });
            }

            lock (_sync)
            {
                _zones["dock"] = migrated;
            }

            // 落盘新格式（旧文件保留为备份）。
            Save();
            _logger.Info($"[Pinning] 已从 dock-pinned.json 迁移 {migrated.Count} 项到 pinning.json 的 \"dock\" zone");
        }
        catch (Exception ex)
        {
            _logger.Error($"[Pinning] 旧 dock-pinned.json 迁移失败，保持空固定列表：{ex.Message}");
        }
    }

    private void RaisePinnedChanged(string zone)
    {
        PinnedChanged?.Invoke(this, new PinnedChangedEventArgs(zone, GetPinned(zone)));
    }

    // ---- 持久化 DTO（基本类型，避免直接序列化 AppItem / AppItemId） ----

    private static AppItem ToAppItem(AppItemDto dto)
    {
        return new AppItem
        {
            Id = new AppItemId(dto.Id ?? string.Empty),
            Name = dto.Name ?? string.Empty,
            ShortcutPath = dto.ShortcutPath ?? string.Empty,
            TargetPath = dto.TargetPath ?? string.Empty,
            Source = (AppSourceEnum)(dto.Source),
            AppUserModelId = dto.AppUserModelId,
            IconCacheKey = dto.IconCacheKey,
            UninstallCommand = dto.UninstallCommand
        };
    }

    private static AppItemDto ToDto(AppItem a)
    {
        return new AppItemDto
        {
            Id = a.Id.ToString(),
            Name = a.Name,
            ShortcutPath = a.ShortcutPath,
            TargetPath = a.TargetPath,
            Source = (int)a.Source,
            AppUserModelId = a.AppUserModelId,
            IconCacheKey = a.IconCacheKey,
            UninstallCommand = a.UninstallCommand
        };
    }

    private sealed class PinningFile
    {
        public int Schema { get; set; } = 1;

        public Dictionary<string, List<PinnedItemDto>> Zones { get; set; } = new();
    }

    private sealed class PinnedItemDto
    {
        public AppItemDto AppItem { get; set; } = new();

        public int Order { get; set; }
    }

    private sealed class AppItemDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? ShortcutPath { get; set; }
        public string? TargetPath { get; set; }
        public int Source { get; set; }
        public string? AppUserModelId { get; set; }
        public string? IconCacheKey { get; set; }
        public string? UninstallCommand { get; set; }
    }

    private sealed class LegacyPinnedItem
    {
        public LegacyDockItemId? Id { get; set; }
        public string? Name { get; set; }
        public string? ShortcutPath { get; set; }
        public string? TargetPath { get; set; }
        public int AppType { get; set; }
        public string? AppUserModelId { get; set; }
        public string? IconCacheKey { get; set; }
        public string? UninstallCommand { get; set; }
    }

    private sealed class LegacyDockItemId
    {
        public string? Value { get; set; }
    }
}
