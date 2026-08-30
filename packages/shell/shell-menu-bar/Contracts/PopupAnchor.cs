// BetterDesktop.Shell.MenuBar — 弹窗锚点定位工具
// 菜单栏按钮弹出的面板都要"锚在按钮正下方、不超工作区边界"，逻辑通用收口在此。

using System.Windows;

namespace BetterDesktop.Shell.MenuBar.Contracts;

/// <summary>弹窗锚点计算工具：把 (popupWidth, popupHeight) 放到按钮锚点正下方，必要时回钳工作区。</summary>
internal static class PopupAnchor
{
    /// <summary>按钮上沿与弹窗顶部的间距（保持视觉呼吸，避免贴边）。</summary>
    private const int VerticalGap = 4;

    /// <summary>
    /// 计算弹窗 TopLeft 屏幕坐标。
    /// </summary>
    /// <param name="anchorScreenTopLeft">菜单栏按钮左上角在屏幕坐标系的位置。</param>
    /// <param name="buttonWidth">按钮宽度，用于横向对齐（弹窗默认右端对齐按钮，仿 macOS）。</param>
    /// <param name="popupSize">弹窗期望尺寸（宽度/高度）。</param>
    /// <param name="menuBarHeight">菜单栏实际高度（逻辑像素），用于把弹窗锚在菜单栏正下方。
    /// 默认取 <see cref="MenuBarMetrics.MenuBarHeight"/>（与 MenuBarWindow 同源），切勿再硬编码 32 等旧值。</param>
    /// <returns>弹窗应放置的屏幕 TopLeft 坐标。</returns>
    public static Point Compute(Point anchorScreenTopLeft, double buttonWidth, Size popupSize, double menuBarHeight = MenuBarMetrics.MenuBarHeight)
    {
        var workArea = SystemParameters.WorkArea;

        // 竖直：菜单栏底部（按钮左上沿 + 菜单栏高度）+ 间距。
        // 过去硬编码 +32，但真实菜单栏高度是 16（MenuBarWindow.Height），导致弹窗比菜单栏下沿低约 16px 悬空。
        var menuBarBottom = anchorScreenTopLeft.Y + menuBarHeight;
        var y = menuBarBottom + VerticalGap;

        // 横向：仿 macOS，弹窗右端对齐按钮右端
        var x = anchorScreenTopLeft.X + buttonWidth - popupSize.Width;

        // 回钳工作区（右/下不越界）
        if (x + popupSize.Width > workArea.Right)
        {
            x = workArea.Right - popupSize.Width;
        }
        if (x < workArea.Left)
        {
            x = workArea.Left;
        }
        if (y + popupSize.Height > workArea.Bottom)
        {
            y = workArea.Bottom - popupSize.Height;
        }

        return new Point(x, y);
    }
}
