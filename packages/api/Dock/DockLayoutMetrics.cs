using System.Windows;

namespace BetterDesktop.Shell.Dock.Models;

/// <summary>
/// Dock 布局度量结果。
/// </summary>
public sealed record DockLayoutMetrics
{
    /// <summary>
    /// 窗口在屏幕坐标系中的位置。
    /// </summary>
    public Rect Bounds { get; init; }

    /// <summary>
    /// 图标基准尺寸（像素）。
    /// </summary>
    public double IconSize { get; init; }

    /// <summary>
    /// 相邻 Dock 项之间的水平间距（像素）。
    /// </summary>
    public double ItemSpacing { get; init; }

    /// <summary>
    /// 标签高度预算（用于悬停时标签展开空间预留）。
    /// </summary>
    public double LabelHeight { get; init; }

    /// <summary>
    /// 自动隐藏触发的屏幕边缘热区矩形。
    /// </summary>
    public Rect EdgeHoverRect { get; init; }
}
