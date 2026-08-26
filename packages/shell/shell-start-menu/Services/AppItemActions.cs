using System;
using System.Windows.Controls;
using BetterDesktop.Shell.AppSource.Models;

namespace BetterDesktop.Shell.StartMenu.Services;

/// <summary>
/// 程序项右键菜单构建（Step 8：开始菜单接管 AppGrabber 的固定/管理员/位置/卸载能力）。
/// 供程序树、所有应用列表、搜索结果复用。
/// </summary>
internal static class AppItemActions
{
    public static ContextMenu BuildContextMenu(AppItem app, StartMenuService service)
    {
        var menu = new ContextMenu();
        menu.Items.Add(Item("启动", () =>
        {
            service.ActivateOrLaunch(app);
            service.Hide();
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("固定到 Dock", () => service.PinToZone(app, "dock")));
        menu.Items.Add(Item("固定到开始菜单", () => service.PinToZone(app, "startmenu")));
        menu.Items.Add(Item("固定到任务栏", () => service.PinToZone(app, "taskbar")));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("以管理员运行", () => service.LaunchAsAdmin(app)));
        menu.Items.Add(Item("打开文件位置", () => service.OpenFileLocation(app)));
        if (!string.IsNullOrWhiteSpace(app.UninstallCommand))
        {
            menu.Items.Add(Item("卸载", () => service.Uninstall(app)));
        }

        return menu;
    }

    private static MenuItem Item(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) =>
        {
            try
            {
                action();
            }
            catch
            {
                // 动作失败静默（M10）。
            }
        };
        return item;
    }
}
