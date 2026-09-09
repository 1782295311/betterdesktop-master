// BetterDesktop.Shell.Desktop — 桌面自绘右键弹层（2026-09-07 回归自绘）
//
// 【架构拍板】系统右键接入多轮实测后用户决定回归自绘右键菜单：
//   - 系统原生菜单（跨进程委托 explorer / DefView 转发）类型正确但不稳定（拖动关联崩溃、
//     菜单类型错乱、范围问题等），且本机任何非 explorer 进程 GetUIObjectOf 聚合第三方扩展
//     必然崩溃（0xC0000005 实锤）——IContextMenu 管线不可在本进程触碰。
//   - 本弹层 = WPF ContextMenu（与 shell-dock DockMenuPopup 同范式）：MenuItemDef 序列 →
//     WPF MenuItem，零 IContextMenu/GetUIObjectOf/CreateViewObject 依赖，纯托管安全。
//   - 代价（明确告知）：自绘菜单不含第三方 shell 扩展项（360 压缩/夸克/百度网盘等），
//     仅基础操作（打开/剪切/复制/删除/重命名/属性/新建/刷新/排序等）。
//
// 渲染载体 = WPF ContextMenu（Popups 是独立顶层窗口，AbsolutePoint 屏幕坐标定位；
// 失焦自关/子菜单/Esc 由 WPF 原生处理）。

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.ContextMenus.Contracts;

namespace BetterDesktop.Shell.Desktop.Services;

/// <summary>桌面自绘右键弹层：MenuItemDef 序列 → WPF ContextMenu（复刻 DockMenuPopup 范式）。</summary>
public static class DesktopMenuPopup
{
    /// <summary>在指定屏幕 DIP 坐标弹菜单。分隔线用 MenuItemKind.Separator 声明。</summary>
    public static void Show(IReadOnlyList<MenuItemDef> items, Point screenDip)
    {
        if (items.Count == 0)
        {
            return;
        }

        var menu = new ContextMenu();
        foreach (var def in items)
        {
            if (def.Kind == MenuItemKind.Separator)
            {
                _ = menu.Items.Add(new Separator());
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

    private static MenuItem BuildItem(MenuItemDef def)
    {
        var item = new MenuItem { Header = def.Text, IsEnabled = def.IsEnabled };
        if (def.IsDefault)
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
                    _ = item.Items.Add(new Separator());
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
            try
            {
                def.Command?.Invoke();
            }
            catch (Exception ex)
            {
                DiagnosticLog.Trace("shell.desktop", $"菜单命令 {def.Id} 失败: {ex.Message}");
            }
        };
        return item;
    }
}
