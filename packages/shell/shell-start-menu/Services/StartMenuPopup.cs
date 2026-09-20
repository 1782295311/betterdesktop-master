// BetterDesktop.Shell.StartMenu — 开始菜单主体自管弹层（2026-09-05 架构收口）
//
// 用途：电源菜单等开始菜单自身申明的少量菜单（条目右键已全部转系统原生，
// 不再走本渲染器）。谁的菜单谁管理——不再依赖中央 IMenuService/MenuHost。

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.ContextMenus.Contracts;
using BetterDesktop.Shell.Core.Windows;

namespace BetterDesktop.Shell.StartMenu.Services;

/// <summary>开始菜单自管弹层：MenuItemDef → WPF ContextMenu。</summary>
public static class StartMenuPopup
{
    /// <summary>在 anchor 底边中点下方弹菜单（电源按钮惯例位）。</summary>
    public static void ShowBelow(IReadOnlyList<MenuItemDef> items, FrameworkElement anchor)
        => Show(items, BelowOf(anchor));

    /// <summary>在指定屏幕 DIP 坐标弹菜单。</summary>
    public static void Show(IReadOnlyList<MenuItemDef> items, Point screenDip)
    {
        var menu = new ContextMenu();
        foreach (var def in items)
        {
            if (def.Kind == MenuItemKind.Separator)
            {
                menu.Items.Add(new Separator());
            }
            else
            {
                _ = menu.Items.Add(BuildItem(def));
            }
        }
        if (menu.Items.Count == 0)
        {
            return;
        }
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.AbsolutePoint;
        menu.HorizontalOffset = screenDip.X;
        menu.VerticalOffset = screenDip.Y;
        menu.IsOpen = true;
    }

    /// <summary>元素底边中点下方 4px 的屏幕 DIP 坐标。</summary>
    public static Point BelowOf(FrameworkElement el)
    {
        return PopupPositioningService.ToScreenDip(el, new Point(el.ActualWidth / 2, el.ActualHeight + 4));
    }

    private static MenuItem BuildItem(MenuItemDef def)
    {
        var item = new MenuItem { Header = def.Text, IsEnabled = def.IsEnabled };
        if (def.IsDefault || def.Highlighted) // 2026-09-10：无损转换项高亮（加粗，同 IsDefault 模式）
        {
            item.FontWeight = FontWeights.Bold;
        }
        if (!string.IsNullOrEmpty(def.GestureText))
        {
            item.InputGestureText = def.GestureText;
        }
        if (def.IsChecked)
        {
            item.IsCheckable = true;
            item.IsChecked = true;
        }

        if (def.Kind == MenuItemKind.Submenu)
        {
            foreach (var child in def.Children ?? [])
            {
                if (child.Kind == MenuItemKind.Separator)
                {
                    item.Items.Add(new Separator());
                }
                else
                {
                    _ = item.Items.Add(BuildItem(child));
                }
            }
            return item;
        }

        item.Click += (_, _) =>
        {
            try { def.Command?.Invoke(); }
            catch (Exception ex)
            {
                DiagnosticLog.Trace("shell.start", $"菜单命令 {def.Id} 失败: {ex.Message}");
            }
        };
        return item;
    }
}
