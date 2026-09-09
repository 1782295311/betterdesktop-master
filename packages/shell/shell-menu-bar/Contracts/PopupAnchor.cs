// BetterDesktop.Shell.MenuBar — 弹窗锚点定位工具（薄壳）
// 2026-09-07 弹窗体系上提（P0-2/B1）：几何计算已上提 shell-core/Windows/PopupPositioningService
// （601 范式：贴靠锚点下方 + clamp 工作区全可见；精确 DIP 换算）。
// 本文件保留菜单栏特有的「锚点来源」语义（锚定按钮/物理点 + 所在显示器工作区 + 菜单栏高度），
// 对外接口与单位纪律不变（一律 WPF 逻辑单位）。

using System.Windows;
using System.Windows.Media;
using BetterDesktop.Shell.Core.Windows;

namespace BetterDesktop.Shell.MenuBar.Contracts;

/// <summary>弹窗锚点计算工具：把 (popupWidth, popupHeight) 放到按钮锚点正下方，必要时回钳工作区。</summary>
internal static class PopupAnchor
{
    /// <summary>
    /// 计算弹窗 TopLeft 坐标（**逻辑单位**，可直接赋给 Window.Left/Top）。
    /// </summary>
    /// <param name="anchorVisual">被点击的菜单栏按钮（用于取屏幕坐标、DPI 与所在显示器）。</param>
    /// <param name="buttonWidth">按钮宽度（逻辑单位）。当前对齐策略为弹窗左端对齐按钮左端（正下方展开），此参数仅保留签名兼容。</param>
    /// <param name="popupSize">弹窗期望尺寸（逻辑单位）。</param>
    /// <param name="menuBarHeight">菜单栏实际高度（逻辑像素），用于把弹窗锚在菜单栏正下方。
    /// 默认取 <see cref="MenuBarMetrics.MenuBarHeight"/>（与 MenuBarWindow 同源），切勿再硬编码 32 等旧值。</param>
    public static Point Compute(
        Visual anchorVisual,
        double buttonWidth,
        Size popupSize,
        double menuBarHeight = MenuBarMetrics.MenuBarHeight)
    {
        _ = buttonWidth; // 保留参数签名兼容调用方；对齐不再依赖按钮宽度

        // 锚点换算：物理像素 → 逻辑单位（TransformFromDevice 精确路径），否则非 100% DPI 缩放时弹窗整体偏移。
        var anchor = PopupPositioningService.ToScreenDip(anchorVisual, new Point(0, 0));
        // 工作区取"锚点所在显示器"而非主屏，否则多显示器下副屏弹窗会被钳到主屏。
        var physical = anchorVisual.PointToScreen(new Point(0, 0));
        return PopupPositioningService.ComputeAnchored(anchor, MenuBarScreen.GetWorkArea(physical), popupSize, menuBarHeight);
    }

    /// <summary>
    /// 兼容重载：调用方只有物理像素坐标时用此版本（典型场景：
    /// <c>IMenuBarExtension.OpenPopup</c> 契约只传 Point，拿不到按钮本体）。
    /// 换算退化为"按锚点所在显示器的 DPI 折算"，精度略低于上面的 Visual 版本
    /// （后者用 TransformFromDevice），但同样修正了原实现"物理点直接当逻辑点用"的错误。
    /// </summary>
    public static Point Compute(
        Point anchorPhysicalPoint,
        double buttonWidth,
        Size popupSize,
        double menuBarHeight = MenuBarMetrics.MenuBarHeight)
    {
        _ = buttonWidth;
        var scale = MenuBarScreen.GetScale(anchorPhysicalPoint);
        var anchor = new Point(anchorPhysicalPoint.X / scale, anchorPhysicalPoint.Y / scale);
        return PopupPositioningService.ComputeAnchored(anchor, MenuBarScreen.GetWorkArea(anchorPhysicalPoint), popupSize, menuBarHeight);
    }
}
