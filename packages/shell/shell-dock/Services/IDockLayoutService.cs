using System.Windows;
using BetterDesktop.Shell.Dock.Models;

namespace BetterDesktop.Shell.Dock.Services;

/// <summary>
/// Dock 布局服务：负责定位、间距、自动隐藏触发与显示/隐藏状态切换。
/// </summary>
public interface IDockLayoutService
{
    /// <summary>
    /// 根据屏幕尺寸与图标数量计算 Dock 布局度量。
    /// </summary>
    DockLayoutMetrics Measure(int screenWidth, int screenHeight, int iconCount);

    /// <summary>
    /// 判断鼠标是否触发 Dock 显示（底部边缘悬停）。
    /// </summary>
    bool ShouldShowOnEdgeHover(Point cursorScreenPoint);

    /// <summary>
    /// 判断当前是否应因全屏窗口覆盖而隐藏 Dock。
    /// </summary>
    bool ShouldHideOnFullscreen();

    /// <summary>
    /// 枚举所有显示器的物理矩形（整屏，含任务栏区域）。
    /// </summary>
    IReadOnlyList<Rect> GetAllScreens();

    /// <summary>
    /// 根据多显示器策略（system.multiMonitorStrategy）返回 Dock 应出现的目标屏幕列表。
    /// primary → 仅主屏；all / independent → 所有显示器（每屏一个 Dock）。
    /// </summary>
    IReadOnlyList<Rect> GetDockTargetScreens();

    /// <summary>
    /// 把给定屏幕矩形底部居中定位（Dock 默认贴屏幕底部、水平居中）。
    /// 返回 Dock 窗口应有的 Left / Top（已含底部留白）。
    /// </summary>
    (double Left, double Top) BottomCenterForScreen(Rect screen, double dockWidth, double dockHeight);

    /// <summary>Dock 距屏幕底部的高度（px）。可在运行时由 dock 视觉配置更新。</summary>
    double BottomMargin { get; set; }
}
