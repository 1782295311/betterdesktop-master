// BetterDesktop.Shell.Core — 弹窗定位公共服务（601 flyout-position 纯几何 + 精确 DIP 换算）
//
// 【来源】收口 shell-menu-bar/Contracts/PopupAnchor.cs + MenuBarScreen.ToLogical：
//   - ComputeAnchored = PopupAnchor.ComputeCore（贴靠锚点下方 + clamp 工作区全可见）
//   - ToScreenDip = MenuBarScreen.ToLogical（Visual.PointToScreen 物理像素 → WPF 逻辑单位）
// 【单位纪律】本服务对外一律 WPF 逻辑单位（与 Window.Left/Top 同域）；物理→逻辑换算只在 ToScreenDip。
// 【601 生死线】锚定"触发图标/菜单栏条带"而非任务栏子窗口链；空间不足翻转（预留扩展点，当前不启用）；
//   clamp 到所在显示器工作区保证全可见。ABM_GETTASKBARPOS 双路径为可选注入（AppBarReservation 已承载，
//   MenuBar 场景不启用，行为不变优先——见计划 2026-09-07 B1）。

using System;
using System.Windows;
using System.Windows.Media;

namespace BetterDesktop.Shell.Core.Windows;

/// <summary>
/// 弹窗定位公共能力：把面板放到锚点正下方并回钳所在工作区（全部逻辑单位）；
/// 以及把 Visual 相对坐标精确换算为屏幕逻辑坐标（DPI 无关）。
/// </summary>
public static class PopupPositioningService
{
    /// <summary>锚点上沿与弹窗顶部的间距（保持视觉呼吸，避免贴边）。</summary>
    public const double VerticalGap = 4;

    /// <summary>弹窗与工作区左右/下边缘之间保留的最小安全边距（避免贴死屏幕边）。</summary>
    public const double EdgeMargin = 4;

    /// <summary>
    /// 把 <c>Visual.PointToScreen</c> 得到的**物理像素**坐标换算为**逻辑单位**（WPF DIP）。
    /// 换算失败（未挂 PresentationSource / headless）时原样返回，保证不抛。
    /// </summary>
    public static Point ToScreenDip(Visual visual, Point relativePoint)
    {
        try
        {
            var physical = visual.PointToScreen(relativePoint);
            return ToScreenDipFromPhysical(physical, visual);
        }
        catch
        {
            // headless / 预览工厂场景：无 PresentationSource，原样返回（不改变既有行为）
            return visual.PointToScreen(relativePoint);
        }
    }

    /// <summary>
    /// 物理像素 → WPF 逻辑单位（DIP）。入参为已转换的物理点（调用方自行 PointToScreen）；
    /// 换算失败（未挂 PresentationSource / headless）时原样返回，保证不抛。
    /// </summary>
    public static Point ToScreenDipFromPhysical(Point physicalPoint, Visual visual)
    {
        try
        {
            var source = PresentationSource.FromVisual(visual);
            var transform = source?.CompositionTarget?.TransformFromDevice;
            // TransformFromDevice 是 Matrix?（可空 struct），必须取值后再 Transform
            return transform is { } m ? m.Transform(physicalPoint) : physicalPoint;
        }
        catch
        {
            // headless / 预览工厂场景：无 PresentationSource，原样返回（不改变既有行为）
            return physicalPoint;
        }
    }

    /// <summary>
    /// 纯几何计算（入参均为逻辑单位）：把弹窗放到锚点下方并回钳到给定工作区。
    /// 竖直：锚点 + 锚条高度 + 间距；横向：弹窗左端对齐锚点左端（正下方展开），贴近边缘由回钳保证不越界。
    /// </summary>
    /// <param name="anchor">锚点（逻辑单位，通常为触发元素左上角屏幕坐标）。</param>
    /// <param name="workArea">锚点所在显示器工作区（逻辑单位）。</param>
    /// <param name="popupSize">弹窗期望尺寸（逻辑单位）。</param>
    /// <param name="anchorBandHeight">锚条（菜单栏/任务栏/按钮条带）高度（逻辑单位），弹窗锚在其正下方。</param>
    public static Point ComputeAnchored(
        Point anchor,
        Rect workArea,
        Size popupSize,
        double anchorBandHeight)
    {
        // 竖直：锚条底部（锚点 + 锚条高度）+ 间距。
        // 历史教训：曾硬编码 +32，但真实菜单栏高度是 16，导致弹窗比锚条下沿低约 16px 悬空。
        var y = anchor.Y + anchorBandHeight + VerticalGap;

        // 横向：弹窗左端对齐锚点左端 —— 在触发图标**正下方**展开（cairoshell 规范）。
        var x = anchor.X;

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

        var minY = workArea.Top + anchorBandHeight + VerticalGap;
        var maxY = workArea.Bottom - popupSize.Height - EdgeMargin;
        if (maxY < minY)
        {
            maxY = minY;
        }
        y = Math.Clamp(y, minY, maxY);

        return new Point(x, y);
    }
}
