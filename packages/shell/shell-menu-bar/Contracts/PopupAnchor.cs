// BetterDesktop.Shell.MenuBar — 弹窗锚点定位工具
// 菜单栏按钮弹出的面板都要"锚在按钮正下方、不超所在显示器工作区边界"，逻辑通用收口在此。
//
// 【单位纪律】本文件对外一律使用 **WPF 逻辑单位**（与 Window.Left/Top 同域）。
//   Visual.PointToScreen() 给的是物理像素，Window.Left 收的是逻辑单位，两者不能直接相加——
//   换算与"取哪个显示器"都由 MenuBarScreen 承担，本文件只做纯几何计算。

using System.Windows;
using System.Windows.Media;

namespace BetterDesktop.Shell.MenuBar.Contracts;

/// <summary>弹窗锚点计算工具：把 (popupWidth, popupHeight) 放到按钮锚点正下方，必要时回钳工作区。</summary>
internal static class PopupAnchor
{
    /// <summary>按钮上沿与弹窗顶部的间距（保持视觉呼吸，避免贴边）。</summary>
    private const int VerticalGap = 4;

    /// <summary>弹窗与工作区左右/下边缘之间保留的最小安全边距（避免贴死屏幕边）。</summary>
    private const double EdgeMargin = 4;

    /// <summary>
    /// 计算弹窗 TopLeft 坐标（**逻辑单位**，可直接赋给 Window.Left/Top）。
    /// </summary>
    /// <param name="anchorVisual">被点击的菜单栏按钮（用于取屏幕坐标、DPI 与所在显示器）。</param>
    /// <param name="buttonWidth">按钮宽度（逻辑单位），用于横向对齐（弹窗默认右端对齐按钮，仿 macOS）。</param>
    /// <param name="popupSize">弹窗期望尺寸（逻辑单位）。</param>
    /// <param name="menuBarHeight">菜单栏实际高度（逻辑像素），用于把弹窗锚在菜单栏正下方。
    /// 默认取 <see cref="MenuBarMetrics.MenuBarHeight"/>（与 MenuBarWindow 同源），切勿再硬编码 32 等旧值。</param>
    public static Point Compute(
        Visual anchorVisual,
        double buttonWidth,
        Size popupSize,
        double menuBarHeight = MenuBarMetrics.MenuBarHeight)
    {
        var physical = anchorVisual.PointToScreen(new Point(0, 0));

        // 关键修正（原实现两处缺陷）：
        //   1) 锚点换算：物理像素 → 逻辑单位，否则非 100% DPI 缩放时弹窗整体偏移。
        //   2) 工作区取"锚点所在显示器"而非主屏，否则多显示器下副屏弹窗会被钳到主屏。
        var anchor = MenuBarScreen.ToLogical(anchorVisual, physical);
        return ComputeCore(anchor, MenuBarScreen.GetWorkArea(physical), buttonWidth, popupSize, menuBarHeight);
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
        var scale = MenuBarScreen.GetScale(anchorPhysicalPoint);
        var anchor = new Point(anchorPhysicalPoint.X / scale, anchorPhysicalPoint.Y / scale);
        return ComputeCore(anchor, MenuBarScreen.GetWorkArea(anchorPhysicalPoint), buttonWidth, popupSize, menuBarHeight);
    }

    /// <summary>纯几何计算（入参均为逻辑单位）：把弹窗放到锚点下方，并回钳到给定工作区。</summary>
    private static Point ComputeCore(
        Point anchor,
        Rect workArea,
        double buttonWidth,
        Size popupSize,
        double menuBarHeight)
    {

        // 竖直：菜单栏底部（按钮上沿 + 菜单栏高度）+ 间距。
        // 过去硬编码 +32，但真实菜单栏高度是 16（MenuBarWindow.Height），导致弹窗比菜单栏下沿低约 16px 悬空。
        var y = anchor.Y + menuBarHeight + VerticalGap;

        // 横向：仿 macOS，弹窗右端对齐按钮右端
        var x = anchor.X + buttonWidth - popupSize.Width;

        // 回钳到所在显示器工作区（右/下不越界，并保留安全边距）
        var minX = workArea.Left + EdgeMargin;
        var maxX = workArea.Right - popupSize.Width - EdgeMargin;
        if (maxX < minX)
        {
            // 弹窗比工作区还宽：退化为左对齐并夹紧，避免算出 maxX < minX 的无效区间
            minX = workArea.Left;
            maxX = minX;
        }
        x = Math.Clamp(x, minX, maxX);

        var minY = workArea.Top + menuBarHeight + VerticalGap;
        var maxY = workArea.Bottom - popupSize.Height - EdgeMargin;
        if (maxY < minY)
        {
            maxY = minY;
        }
        y = Math.Clamp(y, minY, maxY);

        return new Point(x, y);
    }
}
