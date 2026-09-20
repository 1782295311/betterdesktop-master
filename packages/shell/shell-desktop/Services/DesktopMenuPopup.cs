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
//
// 【2026-09-17 两处修复（用户实测）】
//   ① 菜单项图标：MenuItemDef.Icon → MenuItem.Icon（16px 图标槽）。用户实测「打开方式」
//      只列应用名看不出是哪个软件；「打开」项同时给文件自身图标（与 explorer 右键一致）。
//   ② 贴边收敛：AbsolutePoint 定位原本完全不做工作区约束——菜单够长时底部项（属性 / 解压到…）
//      会落到屏幕外，用户侧表现为"这两个功能点了没反应"（其实根本点不到）。
//      现改为：构建期预测量 → 越界则贴边收敛（右/下越界向左/上收，必要时翻到光标上方），
//      打开后再按真实 ActualWidth/Height 复核一次（预测量与实际渲染尺寸可能有几像素差）。
//      警告：贴边收敛只保证**可见**，超高菜单（> 工作区高度）仍会被工作区裁剪——本机 19 项
//      菜单约 500 DIP，工作区 1152 DIP，远未触顶，故不引入 ScrollViewer（进子菜单滚动会破坏
//      WPF 菜单的键盘导航与子菜单弹出语义，收益不抵风险）。

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Controls.Primitives;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.ContextMenus.Contracts;
using BetterDesktop.Shell.Core.Native;

namespace BetterDesktop.Shell.Desktop.Services;

/// <summary>桌面自绘右键弹层：MenuItemDef 序列 → WPF ContextMenu（复刻 DockMenuPopup 范式）。</summary>
public static class DesktopMenuPopup
{
    /// <summary>与工作区边缘保留的最小安全边距（避免贴死屏幕边）。</summary>
    private const double EdgeMargin = 4;

    /// <summary>菜单项图标槽尺寸（与 explorer 右键菜单一致）。</summary>
    private const double IconSlot = 16;

    /// <summary>
    /// 在指定屏幕 DIP 坐标弹菜单。分隔线用 MenuItemKind.Separator 声明。
    /// </summary>
    /// <param name="items">菜单项（空集合直接返回，不弹空菜单）。</param>
    /// <param name="screenDip">弹出点（屏幕逻辑坐标）。</param>
    /// <param name="dpiSource">
    /// 用于 DPI 换算的视觉元素（贴边收敛要把物理工作区换算成逻辑单位）。
    /// 传触发控件本身最准（PerMonitorV2 下窗口 DPI 可能与主屏不同）；null 时退回主窗口。
    /// </param>
    /// <param name="onClosed">
    /// 菜单关闭回调（[2026-09-17] 独立进程 BetterDesktop.DesktopControl 是"弹完即退"的短命进程，
    /// 靠它决定何时退出；宿主内调用传 null）。**提前返回（无项可弹）时也会回调**，
    /// 否则短命进程会挂住不退。
    /// </param>
    public static void Show(
        IReadOnlyList<MenuItemDef> items,
        Point screenDip,
        FrameworkElement? dpiSource = null,
        Action? onClosed = null)
    {
        if (items.Count == 0)
        {
            onClosed?.Invoke();
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

        menu.Placement = PlacementMode.AbsolutePoint;

        // 贴边收敛：先按预测量落位，打开后再按真实尺寸复核（见文件头 ② 说明）。
        var work = WorkAreaDip(screenDip, dpiSource);
        var desired = MeasureMenu(menu);
        var start = ClampToWorkArea(work, screenDip, desired);
        menu.HorizontalOffset = start.X;
        menu.VerticalOffset = start.Y;

        if (work is not null)
        {
            menu.Opened += (_, _) =>
            {
                var actual = new Size(menu.ActualWidth, menu.ActualHeight);
                if (actual.Width <= 0 || actual.Height <= 0)
                {
                    return;
                }
                var fixedUp = ClampToWorkArea(work, screenDip, actual);
                if (Math.Abs(fixedUp.X - start.X) > 1 || Math.Abs(fixedUp.Y - start.Y) > 1)
                {
                    menu.HorizontalOffset = fixedUp.X;
                    menu.VerticalOffset = fixedUp.Y;
                }
            };
        }

        if (onClosed is not null)
        {
            // 关闭即回调（短命进程据此退出）。放最后注册：前面任何一步提前返回都不会留下半开的菜单。
            menu.Closed += (_, _) => onClosed();
        }

        menu.IsOpen = true;
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
        if (def.Icon is not null)
        {
            item.Icon = new Image
            {
                Source = def.Icon,
                Width = IconSlot,
                Height = IconSlot,
                Stretch = Stretch.Uniform,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true
            };
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

    // ======== 定位（贴边收敛） ========

    /// <summary>构建期预测量菜单期望尺寸；失败/未挂模板返回 (0,0)（调用方退化为不收敛）。</summary>
    private static Size MeasureMenu(ContextMenu menu)
    {
        try
        {
            menu.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return menu.DesiredSize;
        }
        catch
        {
            // 测量失败不阻断菜单显示（M10）：退化为"光标即左上角"的旧行为
            return default;
        }
    }

    /// <summary>
    /// 把菜单左上角收敛进工作区：右侧越界 → 向左收；下方越界 → 向上翻（优先翻到光标上方，
    /// 保证底部项可见）；仍不够则贴边夹紧。
    /// </summary>
    private static Point ClampToWorkArea(Rect? work, Point anchor, Size size)
    {
        if (work is not { } area || size.Width <= 0 || size.Height <= 0)
        {
            return anchor;
        }

        var x = anchor.X;
        var y = anchor.Y;

        var maxX = area.Right - EdgeMargin - size.Width;
        if (x > maxX)
        {
            x = Math.Max(area.Left + EdgeMargin, maxX);
        }

        var maxY = area.Bottom - EdgeMargin - size.Height;
        if (y > maxY)
        {
            // 优先翻到锚点上方（贴近触发点，手感与 explorer 一致），而不是直接贴屏幕底
            var flipped = anchor.Y - size.Height;
            y = flipped >= area.Top + EdgeMargin ? flipped : Math.Max(area.Top + EdgeMargin, maxY);
        }

        return new Point(x, y);
    }

    /// <summary>
    /// 弹出点所在显示器的工作区（**逻辑单位**，与 screenDip 同域）；取不到返回 null（不收敛）。
    /// 换算纪律：MonitorFromPoint 要物理坐标 → ToDevice 变换；回来时用 FromDevice 逆变换。
    /// </summary>
    private static Rect? WorkAreaDip(Point screenDip, FrameworkElement? dpiSource)
    {
        try
        {
            var source = PresentationSource.FromVisual(dpiSource ?? Application.Current?.MainWindow);
            var toDevice = source?.CompositionTarget?.TransformToDevice;
            var fromDevice = source?.CompositionTarget?.TransformFromDevice;

            var physical = toDevice is { } td ? td.Transform(screenDip) : screenDip;
            var monitor = NativeMethods.MonitorFromPoint(
                new NativeMethods.POINT { X = (int)Math.Round(physical.X), Y = (int)Math.Round(physical.Y) },
                NativeMethods.MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero)
            {
                return null;
            }

            var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            if (!NativeMethods.GetMonitorInfo(monitor, ref info))
            {
                return null;
            }

            var w = info.rcWork;
            var topLeft = new Point(w.Left, w.Top);
            var bottomRight = new Point(w.Right, w.Bottom);
            if (fromDevice is { } fd)
            {
                topLeft = fd.Transform(topLeft);
                bottomRight = fd.Transform(bottomRight);
            }
            return new Rect(topLeft, bottomRight);
        }
        catch
        {
            // 无 PresentationSource / headless：不收敛（保持旧行为，不抛）
            return null;
        }
    }
}
