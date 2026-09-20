using System;
using System.Collections.Generic;
using System.IO;
using BetterDesktop.Shell.ContextMenus.Contracts;

namespace BetterDesktop.Shell.AppSource.Models;

/// <summary>
/// 应用条目右键菜单的**动作回调**（由各界面注入）。
/// <para>构建器只判定「项集与可用性」，**不执行动作** —— 因此本类是纯数据，可脱离 UI 单测；
/// 同时避免 <c>shell-dock</c> 与 <c>shell-menu-bar</c> 互相依赖（跨包纪律 §3）。</para>
/// </summary>
public sealed class AppEntryMenuActions
{
    /// <summary>启动 / 打开应用（各界面启动语义不同：dock 有 UWP/系统项分支，搜索面板走 SearchResult）。</summary>
    public Action? Launch { get; init; }

    /// <summary>以管理员身份运行（仅对可执行扩展出现）。</summary>
    public Action? RunAsAdmin { get; init; }

    /// <summary>在所在目录打开终端。</summary>
    public Action? OpenInTerminal { get; init; }

    /// <summary>打开文件属性对话框。</summary>
    public Action? ShowProperties { get; init; }

    /// <summary>固定到 Dock。为 null 时**不显示**固定项（固定服务不可用 → 整项省略，不显示空菜单项）。</summary>
    public Action? Pin { get; init; }

    /// <summary>从 Dock 移除。</summary>
    public Action? Unpin { get; init; }

    /// <summary>在资源管理器中定位（目录则直接打开）。</summary>
    public Action? RevealInExplorer { get; init; }

    /// <summary>复制完整路径到剪贴板。</summary>
    public Action? CopyPath { get; init; }

    /// <summary>从当前分组移出。</summary>
    public Action? RemoveFromGroup { get; init; }

    /// <summary>移动到指定分组（参数 = 目标分组名）。</summary>
    public Action<string>? MoveToGroup { get; init; }

    /// <summary>新建分组…</summary>
    public Action? NewGroup { get; init; }

    /// <summary>卸载（仅在 <see cref="AppEntryMenuContext.UninstallCommand"/> 非空时出现）。</summary>
    public Action? Uninstall { get; init; }
}

/// <summary>
/// <see cref="AppEntryMenuBuilder"/> 的输入：**只描述事实**（有没有路径 / 是否已固定 / 有哪些分组），
/// 外加界面提供的动作回调与少量呈现差异（启动项文案）。
/// </summary>
public sealed record AppEntryMenuContext
{
    /// <summary>可定位/可复制的真实文件路径（无 → 「打开所在目录 / 复制路径」整项不出现）。</summary>
    public string? Path { get; init; }

    /// <summary>当前是否已固定在 dock。</summary>
    public bool IsPinned { get; init; }

    /// <summary>启动项文案：dock 用「启动」，搜索结果用「打开」（语义确有差异，故可配）。</summary>
    public string LaunchText { get; init; } = "启动";

    /// <summary>启动项是否加粗（搜索结果把「打开」当默认动作）。</summary>
    public bool LaunchIsDefault { get; init; }

    /// <summary>卸载命令（注册表来源才有；空 → 卸载项不出现）。</summary>
    public string? UninstallCommand { get; init; }

    /// <summary>可用分组名（空集合 + 无 <see cref="Actions"/>.NewGroup → 分组项不出现）。</summary>
    public IReadOnlyList<string> Groups { get; init; } = Array.Empty<string>();

    /// <summary>当前所属分组（空 = 不在任何分组，故无「移出」项）。</summary>
    public string? CurrentGroup { get; init; }

    /// <summary>动作回调。</summary>
    public AppEntryMenuActions Actions { get; init; } = new();
}

/// <summary>
/// 应用条目右键菜单**项集的唯一构建点**（应用提取器 + 菜单栏搜索共用），输出既有
/// <see cref="MenuItemDef"/> 模型，渲染仍由各界面自理。
/// <para>
/// <b>为什么要有这个类</b>：此前两处各自用 <c>if</c> 链拼项集，同一件事（如「固定态」）在两处的
/// 判定与文案逐渐漂移，加上 Id 语义分裂（见 <c>AppSourceService.CreateStableId</c>），
/// 用户侧表现为「右键功能时有时无」。集中后：项的**出现条件**只有一处，缺失即显式不显示。
/// </para>
/// <para>
/// <b>纪律</b>：本类不得引用任何 UI 类型；不得执行动作（只挂回调）；新增项一律沿用 <c>grab.*</c> 前缀 Id。
/// </para>
/// </summary>
public static class AppEntryMenuBuilder
{
    /// <summary>构建项集（顺序即展示顺序）。无可用项时返回空列表，由调用方决定是否弹菜单。</summary>
    public static IReadOnlyList<MenuItemDef> Build(AppEntryMenuContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var actions = context.Actions;
        var items = new List<MenuItemDef>();

        // 1) 启动 / 打开（默认动作）
        if (actions.Launch is not null)
        {
            items.Add(new MenuItemDef
            {
                Id = "grab.launch",
                Text = context.LaunchText,
                IsDefault = context.LaunchIsDefault,
                Command = actions.Launch,
            });
        }

        // 以管理员身份运行：仅可执行扩展（runas 对快捷方式 / URL / UWP AUMID 无意义）
        if (IsElevatable(context.Path) && actions.RunAsAdmin is not null)
        {
            items.Add(new MenuItemDef
            {
                Id = "grab.admin",
                Text = "以管理员身份运行",
                Command = actions.RunAsAdmin,
            });
        }

        // 固定 ↔ 移除：固定服务不可用时整项省略（不显示点了没反应的项）
        if (context.IsPinned)
        {
            if (actions.Unpin is not null)
            {
                items.Add(new MenuItemDef { Id = "grab.remove", Text = "从 Dock 移除", Command = actions.Unpin });
            }
        }
        else if (actions.Pin is not null)
        {
            items.Add(new MenuItemDef { Id = "grab.pin", Text = "固定到 Dock", Command = actions.Pin });
        }

        // 3) 位置与路径：需要真实路径（无路径的项点了也无意义）
        if (!string.IsNullOrWhiteSpace(context.Path))
        {
            if (actions.RevealInExplorer is not null)
            {
                items.Add(new MenuItemDef { Id = "grab.dir", Text = "打开所在目录", Command = actions.RevealInExplorer });
            }

            if (actions.CopyPath is not null)
            {
                items.Add(new MenuItemDef { Id = "grab.copy-path", Text = "复制路径", Command = actions.CopyPath });
            }
        }

        // 在终端中打开：可执行扩展才有意义（把终端开在它所在目录，便于自己带参数运行）
        if (IsElevatable(context.Path) && actions.OpenInTerminal is not null)
        {
            items.Add(new MenuItemDef
            {
                Id = "grab.terminal",
                Text = "在终端中打开",
                Command = actions.OpenInTerminal,
            });
        }

        // 分组：移出当前组 + 移动到其他组 ▸（含新建）。无分组数据且无新建入口 → 整项不出现
        var hasGrouping = context.Groups.Count > 0
            || !string.IsNullOrWhiteSpace(context.CurrentGroup)
            || actions.NewGroup is not null;
        if (hasGrouping)
        {
            if (actions.RemoveFromGroup is not null && !string.IsNullOrWhiteSpace(context.CurrentGroup))
            {
                items.Add(new MenuItemDef
                {
                    Id = "grab.ungroup",
                    Text = $"从「{context.CurrentGroup}」移出",
                    Command = actions.RemoveFromGroup,
                });
            }

            var children = new List<MenuItemDef>();
            if (actions.MoveToGroup is not null)
            {
                foreach (var group in context.Groups)
                {
                    if (string.Equals(group, context.CurrentGroup, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    children.Add(new MenuItemDef
                    {
                        Id = $"grab.moveto.{group}",
                        Text = group,
                        Command = () => actions.MoveToGroup(group),
                    });
                }
            }

            if (actions.NewGroup is not null)
            {
                children.Add(new MenuItemDef { Id = "grab.newgroup", Text = "新建分组…", Command = actions.NewGroup });
            }

            if (children.Count > 0)
            {
                items.Add(new MenuItemDef
                {
                    Id = "grab.moveto",
                    Text = "移动到分组",
                    Kind = MenuItemKind.Submenu,
                    Children = children,
                });
            }
        }

        // 属性：Shell 原生对话框（信息类，放在卸载之前收尾）
        if (!string.IsNullOrWhiteSpace(context.Path) && actions.ShowProperties is not null)
        {
            items.Add(new MenuItemDef
            {
                Id = "grab.properties",
                Text = "属性",
                Command = actions.ShowProperties,
            });
        }

        // 卸载：仅注册表来源（有卸载命令）才有 —— 全程序模式的裸 exe 直接删文件不清理残留，不暴露
        if (!string.IsNullOrWhiteSpace(context.UninstallCommand) && actions.Uninstall is not null)
        {
            items.Add(new MenuItemDef { Id = "grab.uninstall", Text = "卸载…", Command = actions.Uninstall });
        }

        return items;
    }

    /// <summary>
    /// 「以管理员身份运行 / 在终端中打开」的适用路径：必须是可执行扩展。
    /// <para>对 <c>.lnk</c> / <c>.url</c> / UWP AUMID（非文件路径）提权与开终端都无意义，
    /// 故这两项不按「有路径」判定，而按扩展名判定。</para>
    /// </summary>
    private static bool IsElevatable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var extension = Path.GetExtension(path);
        if (string.IsNullOrEmpty(extension))
        {
            return false;
        }

        return extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".com", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".msc", StringComparison.OrdinalIgnoreCase);
    }
}
