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

    /// <summary>第三方入口展开策略设置键（true = 打散到第一级；默认 false = 收纳为子菜单）。</summary>
    public const string FlattenKey = "context-menu.com.flatten";

    private readonly Func<string, bool>? _isClsidDisabled;
    private readonly Func<bool>? _shouldFlatten;

    internal static TimeSpan ComCacheTtl = TimeSpan.FromSeconds(60);
    private static readonly ConcurrentDictionary<string, (List<ShellVerbItem> Items, DateTime Stamp)> ComCache = new();

    /// <summary>
    /// shouldFlatten：true = 第三方入口打散到第一级（厂商顶层子菜单整体上提一层）；
    /// false（默认）= 收纳为子菜单（explorer 同款，handler 原生结构原样透传）。
    /// </summary>
    public ShellMenuContributor(MenuScope scope, Func<string, bool>? isClsidDisabled = null, Func<bool>? shouldFlatten = null)
    {
        Scope = scope;
        _isClsidDisabled = isClsidDisabled;
        _shouldFlatten = shouldFlatten;
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
            // 展开策略读设置实时生效：原始树缓存不动，映射时按开关打散/收纳
            items.AddRange(MapShellItems(
                hit.Items, paths, prefix: $"com:{items.Count}",
                flatten: _shouldFlatten?.Invoke() ?? false));
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

    /// <summary>
    /// ShellVerbItem 树 → MenuItemDef。
    /// flatten=false：handler 原生结构原样透传（顶层子菜单保持子菜单）；
    /// flatten=true：**顶层**子菜单整体上提一层（打散厂商入口；更深层级仍是子菜单），
    /// 分隔线去重（去首尾、折叠连续）——打散后厂商内部分隔线容易贴在一起。
    /// </summary>
    internal static IEnumerable<MenuItemDef> MapShellItems(
        IReadOnlyList<ShellVerbItem> items, IReadOnlyList<string> paths, string prefix, bool flatten, int depth = 0)
    {
        if (depth > 3)
        {
            yield break;
        }

        var lastWasSeparator = true; // 头部分隔线直接吞掉
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var id = $"{prefix}:{i}";

            if (item.IsSeparator)
            {
                // 尾部/连续分隔线折叠（flatten 打散后尤其常见）
                if (lastWasSeparator)
                {
                    continue;
                }
                lastWasSeparator = true;
                yield return new MenuItemDef { Id = id, Text = string.Empty, Kind = MenuItemKind.Separator, Group = MenuGroup.Contribution };
                continue;
            }

            if (item.IsSubMenu)
            {
                if (flatten && depth == 0)
                {
                    // 打散：上提该子菜单的子项到当前层（厂商入口名不保留——命令文本自描述）
                    foreach (var child in MapShellItems(item.Children, paths, id, flatten: true, depth + 1))
                    {
                        if (child.Kind != MenuItemKind.Separator)
                        {
                            lastWasSeparator = false;
                        }
                        yield return child;
                    }
                    continue;
                }

                lastWasSeparator = false;
                yield return new MenuItemDef
                {
                    Id = id, Text = item.Text, Kind = MenuItemKind.Submenu, Group = MenuGroup.Contribution,
                    Children = MapShellItems(item.Children, paths, id, flatten: false, depth + 1).ToList(),
                };
                continue;
            }

            if (item.Invoke is null)
            {
                continue;
            }
            lastWasSeparator = false;
            yield return new MenuItemDef
            {
                Id = id, Text = item.Text, Group = MenuGroup.Contribution,
                Command = item.Invoke,
            };
        }
    }
}
