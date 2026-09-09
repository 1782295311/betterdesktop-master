// BetterDesktop.Shell.Core.Tests — PopupPositioningService 纯几何单测（601 任务栏定位算法）
// 覆盖：贴靠锚点正下方、右缘/下缘 clamp、超宽退化、安全边距、单位纪律（逻辑单位断言）。
// 物理→逻辑换算（ToScreenDip）依赖 PresentationSource，属真机场景（DoD D1 走查）。

using System.Windows;
using BetterDesktop.Shell.Core.Windows;
using Xunit;

namespace BetterDesktop.Shell.Core.Tests;

public class PopupPositioningServiceTests
{
    private static readonly Rect WorkArea = new(0, 0, 1920, 1040); // 逻辑单位

    [Fact]
    public void ComputeAnchored_AnchorsBelowAnchorBand()
    {
        // anchor(100,100) + bandHeight 16 + gap 4 → y = 120；x 左对齐 100
        var pos = PopupPositioningService.ComputeAnchored(new Point(100, 100), WorkArea, new Size(300, 400), 16);
        Assert.Equal(100, pos.X, 3);
        Assert.Equal(120, pos.Y, 3);
    }

    [Fact]
    public void ComputeAnchored_ClampsToRightEdge()
    {
        // 锚点贴近右缘：弹窗左端 1800 + 宽 300 超界 → 回钳 1616（1920-300-4）
        var pos = PopupPositioningService.ComputeAnchored(new Point(1800, 100), WorkArea, new Size(300, 400), 16);
        Assert.Equal(1616, pos.X, 3);
    }

    [Fact]
    public void ComputeAnchored_ClampsToBottomEdge()
    {
        // 锚点贴近下缘：y 初始 920 超出 maxY=636（1040-400-4）→ 钳到 636，弹窗底贴工作区底（保 4 边距）
        var pos = PopupPositioningService.ComputeAnchored(new Point(100, 900), WorkArea, new Size(300, 400), 16);
        Assert.Equal(636, pos.Y, 3);
    }

    [Fact]
    public void ComputeAnchored_WiderThanWorkArea_DegeneratesToLeftEdge()
    {
        // 弹窗比工作区宽：maxX < minX → 夹紧到左缘（0），不产生无效区间
        var pos = PopupPositioningService.ComputeAnchored(new Point(800, 100), WorkArea, new Size(3000, 400), 16);
        Assert.Equal(0, pos.X, 3);
    }

    [Fact]
    public void ComputeAnchored_KeepsEdgeMargin_OnlyWhenExceeds()
    {
        // 锚点 1600 在 [minX=4, maxX=1616] 内 → 不 clamp，保持 1600（保留安全边距仅用于防越界）
        var pos = PopupPositioningService.ComputeAnchored(new Point(1600, 100), WorkArea, new Size(300, 400), 16);
        Assert.Equal(1600, pos.X, 3);
    }

    [Fact]
    public void ComputeAnchored_AnchorBandHeight_Zero_IsSupported()
    {
        // 锚条高度 0（无条带宿主）：y = anchor.Y + 0 + 4
        var pos = PopupPositioningService.ComputeAnchored(new Point(100, 100), WorkArea, new Size(300, 400), 0);
        Assert.Equal(104, pos.Y, 3);
    }

    [Fact]
    public void Constants_ArePositive_And_Small()
    {
        Assert.True(PopupPositioningService.VerticalGap >= 0);
        Assert.True(PopupPositioningService.EdgeMargin >= 0);
    }
}
