// BetterDesktop.Shell.Desktop — 自绘右键菜单样式工厂（旧回退路径专用）
// 2026-09-02 统一收口：样式唯一权威源 = shell-context-menu/MenuStyling（主题令牌 XamlReader.Parse）。
// 本文件只服务旧回退路径（context-menu.migrated=false 或 IMenuService 缺失）——为消除双样式源
// 漂移，CreateMenu 直接委托 MenuStyling.CreateMenu()，本文件不再自持任何 XAML。
// 主路径（IMenuService）经 MenuHost 渲染，与这里的令牌完全同源。
//
// 【实现说明（踩坑记录）】
//   本文件曾用 FrameworkElementFactory.SetValue(dp, DynamicResourceExtension.ProvideValue(null))
//   构建模板 —— 运行时报 ArgumentException：ResourceReferenceExpression 不能作为 Background 的
//   有效值，导致 DesktopWindow 构造失败、自绘桌面整体不显示（explorer 图标又被隐藏 → 桌面空白）。
//   原因：FrameworkElementFactory.SetValue 不做延迟求值，而实例 SetValue/SetResourceReference 会。
//   正解：用 XamlReader.Parse 解析 XAML 字符串模板/样式，{DynamicResource} 由 XAML 解析器原生处理。

using System;
using System.Windows;
using System.Windows.Controls;
using BetterDesktop.Shell.ContextMenus.Services;

namespace BetterDesktop.Shell.Desktop.Controls;

/// <summary>自绘右键菜单样式工厂（旧回退路径；样式委托 shell-context-menu/MenuStyling 唯一权威源）。</summary>
internal static class DesktopMenuStyling
{
    /// <summary>创建自绘主题 ContextMenu（根模板 + 菜单项样式 + 分隔线样式；样式与统一弹层同源）。</summary>
    public static ContextMenu CreateMenu() => MenuStyling.CreateMenu();

    /// <summary>向自绘菜单追加一个普通菜单项（默认项加粗）。</summary>
    public static void AddItem(ContextMenu menu, string header, Action onClick, bool isDefault = false)
    {
        var item = new MenuItem
        {
            Header = header,
            FontWeight = isDefault ? FontWeights.SemiBold : FontWeights.Normal
        };
        item.Click += (_, _) =>
        {
            try { onClick(); }
            catch { /* 操作失败静默（M10） */ }
        };
        menu.Items.Add(item);
    }

    /// <summary>向自绘菜单追加一条分隔线。</summary>
    public static void AddSeparator(ContextMenu menu) => menu.Items.Add(new Separator());
}
