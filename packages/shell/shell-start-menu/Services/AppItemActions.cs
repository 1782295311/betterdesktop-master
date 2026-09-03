using System;
using System.Collections.Generic;
using BetterDesktop.Shell.AppSource.Models;
using BetterDesktop.Shell.ContextMenus.Contracts;

namespace BetterDesktop.Shell.StartMenu.Services;

/// <summary>
/// 程序项右键菜单构建（Step 8：开始菜单接管 AppGrabber 的固定/管理员/位置/卸载能力）。
/// 供程序树、所有应用列表、搜索结果复用。
/// 2026-09-02 统一收口：不再构建 WPF ContextMenu（系统样式/非 ShellWindow/失焦语义不可控），
/// 返回 MenuItemDef 列表，由 MenuSurface.Attach 接到统一弹层（ShellWindow + 主题令牌）。
/// 命令异常由 MenuHost 统一捕获记录，此处不再逐项 try/catch。
/// </summary>
internal static class AppItemActions
{
    public static IReadOnlyList<MenuItemDef> BuildItems(AppItem app, StartMenuService service)
    {
        var items = new List<MenuItemDef>
        {
            new()
            {
                Id = "start.launch",
                Text = "启动",
                Command = () =>
                {
                    service.ActivateOrLaunch(app);
                    service.Hide();
                },
            },
            Sep("start.sep1"),
            new() { Id = "start.pin.dock", Text = "固定到 Dock", Command = () => service.PinToZone(app, "dock") },
            new() { Id = "start.pin.startmenu", Text = "固定到开始菜单", Command = () => service.PinToZone(app, "startmenu") },
            new() { Id = "start.pin.taskbar", Text = "固定到任务栏", Command = () => service.PinToZone(app, "taskbar") },
            Sep("start.sep2"),
            new() { Id = "start.admin", Text = "以管理员运行", Command = () => service.LaunchAsAdmin(app) },
            new() { Id = "start.location", Text = "打开文件位置", Command = () => service.OpenFileLocation(app) },
        };
        if (!string.IsNullOrWhiteSpace(app.UninstallCommand))
        {
            items.Add(new() { Id = "start.uninstall", Text = "卸载", Command = () => service.Uninstall(app) });
        }

        return items;
    }

    private static MenuItemDef Sep(string id) => new()
    {
        Id = id,
        Text = string.Empty,
        Kind = MenuItemKind.Separator,
    };
}
