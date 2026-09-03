// BetterDesktop.Shell.ContextMenus — 注册表 + COM 右键项贡献者（M2 计划 §3.3）
// 合并两类第三方项：静态 verb（RegistryVerbs，缓存命中即出）+ COM handler（ShellMenuInterop，
// 缓存命中即出 / 未命中后台预热返回空——首帧秒开，第二次右键出现）。
// Group=Contribution；Id 前缀 reg:/com:；Extended 语义沿用（Shift 过滤归 MenuService）。

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.ContextMenus.Contracts;

namespace BetterDesktop.Shell.ContextMenus.Services;

public sealed class ShellMenuContributor : IContextMenuContributor
{
    /// <summary>BuiltInOps(-150) 之后。</summary>
    public int Priority => -180;

    public MenuScope Scope { get; }

    private readonly Func<string, bool>? _isClsidDisabled;

    internal static TimeSpan ComCacheTtl = TimeSpan.FromSeconds(60);
    private static readonly ConcurrentDictionary<string, (List<ShellVerbItem> Items, DateTime Stamp)> ComCache = new();

    public ShellMenuContributor(MenuScope scope, Func<string, bool>? isClsidDisabled = null)
    {
        Scope = scope;
        _isClsidDisabled = isClsidDisabled;
    }

    public IReadOnlyList<MenuItemDef> Build(MenuRequest request)
    {
        if (request.File is not { } id
            || string.IsNullOrWhiteSpace(id.Path)
            || id.Kind is FileKind.InRecycleBin or FileKind.ShellNamespace)
        {
            return [];
        }
        if (!File.Exists(id.Path) && !Directory.Exists(id.Path))
        {
            return [];
        }

        var items = new List<MenuItemDef>();
        var paths = request.SelectedPaths is { Count: > 0 } selected ? selected : [id.Path];

        // ---- 静态 verb（缓存命中即出；未命中后台预热） ----
        foreach (var verb in RegistryVerbs.GetFor(id.Kind, id.Path))
        {
            var captured = verb;
            items.Add(new MenuItemDef
            {
                Id = $"reg:{captured.KeyName}:{items.Count}",
                Text = captured.Text,
                Group = MenuGroup.Contribution,
                IconKey = captured.IconPath is null ? null : $"tool:{captured.IconPath}",
                Extended = captured.Extended,
                Command = () => RegistryVerbs.Invoke(captured, paths),
            });
        }

        // ---- COM handlers（缓存命中即出；未命中后台预热） ----
        var cacheKey = $"com|{id.Kind}|{string.Join(";", paths)}";
        if (ComCache.TryGetValue(cacheKey, out var hit) && DateTime.UtcNow - hit.Stamp < ComCacheTtl)
        {
            items.AddRange(MapComItems(hit.Items, paths, prefix: $"com:{items.Count}"));
        }
        else
        {
            ComCache[cacheKey] = ([], DateTime.UtcNow);
            var kind = id.Kind;
            var path = id.Path;
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var clsids = ShellMenuInterop.EnumerateHandlers(kind, path)
                        .Where(c => _isClsidDisabled?.Invoke(c) != true)
                        .ToList();
                    var tree = ShellMenuInterop.Query(paths, clsids);
                    ComCache[cacheKey] = (tree, DateTime.UtcNow);
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Trace("shell.contextmenu", $"COM 预热失败 {path}: {ex.Message}");
                }
            });
        }

        return items;
    }

    private static IEnumerable<MenuItemDef> MapComItems(
        IReadOnlyList<ShellVerbItem> items, IReadOnlyList<string> paths, string prefix, int depth = 0)
    {
        if (depth > 3)
        {
            yield break;
        }
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var id = $"{prefix}:{i}";
            if (item.IsSeparator)
            {
                yield return new MenuItemDef { Id = id, Text = string.Empty, Kind = MenuItemKind.Separator, Group = MenuGroup.Contribution };
                continue;
            }
            if (item.IsSubMenu)
            {
                yield return new MenuItemDef
                {
                    Id = id, Text = item.Text, Kind = MenuItemKind.Submenu, Group = MenuGroup.Contribution,
                    Children = MapComItems(item.Children, paths, id, depth + 1).ToList(),
                };
                continue;
            }
            if (item.Invoke is null)
            {
                continue;
            }
            yield return new MenuItemDef
            {
                Id = id, Text = item.Text, Group = MenuGroup.Contribution,
                Command = item.Invoke,
            };
        }
    }
}
