// BetterDesktop.Shell.Dock — Dock 主体自管右键弹层（2026-09-05 架构收口）
//
// 【架构拍板】自绘右键菜单不再走中央管线（原 shell-context-menu 的
// IMenuService/MenuHost/MenuSurface 已退役）：谁的菜单谁管理——dock 图标与应用
// 提取器的菜单由 dock 自己构建、自己渲染、自己关闭；主体没申明菜单的表面
// （桌面/开始菜单/文件管理器）一律交给系统原生菜单（NativeMenuPopup）。
//
// 渲染载体 = WPF ContextMenu（dock 是顶层窗口，无嵌入 child window 的消息坑；
// 定位/失焦自关/子菜单由 WPF 原生处理，dock 自持样式自由度）。

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.ContextMenus.Contracts;
using BetterDesktop.Shell.Core.Windows;

namespace BetterDesktop.Shell.Dock.Services;

/// <summary>模板填充器（dock 自有实现；收集模板产出的扁平项序）。</summary>
public sealed class DockMenuBuilder : IMenuTemplateBuilder
{
    private readonly List<object> _entries = [];

    /// <summary>已收集的条目（MenuItemDef 或 SeparatorMark，按加入顺序）。</summary>
    public IReadOnlyList<object> Entries => _entries;

    /// <summary>已产出的菜单项（不含分隔线）。</summary>
    public IEnumerable<MenuItemDef> Items
    {
        get
        {
            foreach (var e in _entries)
            {
                if (e is MenuItemDef d)
                {
                    yield return d;
                }
            }
        }
    }

    public void AddItem(MenuItemDef item) => _entries.Add(item);

    public void AddSeparator(MenuGroup group) => _entries.Add(new SeparatorMark());
}

/// <summary>分隔线标记。</summary>
public sealed record SeparatorMark;

/// <summary>Dock 主体自管弹层：MenuItemDef 序列 → WPF ContextMenu。</summary>
public static class DockMenuPopup
{
    /// <summary>在光标处弹菜单（anchor 用于坐标换算）。</summary>
    public static void ShowAtCursor(IReadOnlyList<object> entries, FrameworkElement anchor)
        => Show(entries, AtCursor(anchor));

    /// <summary>在指定屏幕 DIP 坐标弹菜单。</summary>
    public static void Show(IReadOnlyList<object> entries, Point screenDip)
    {
        var menu = new ContextMenu();
        foreach (var entry in entries)
        {
            switch (entry)
            {
                case SeparatorMark:
                    menu.Items.Add(new Separator());
                    break;
                case MenuItemDef def when def.Kind == MenuItemKind.Separator:
                    menu.Items.Add(new Separator());
                    break;
                case MenuItemDef def:
                    _ = menu.Items.Add(BuildItem(def));
                    break;
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

    /// <summary>当前光标相对 anchor 的屏幕 DIP 坐标。</summary>
    public static Point AtCursor(FrameworkElement anchor)
    {
        return PopupPositioningService.ToScreenDip(anchor, Mouse.GetPosition(anchor));
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
                DiagnosticLog.Trace("shell.dock", $"菜单命令 {def.Id} 失败: {ex.Message}");
            }
        };
        return item;
    }
}
