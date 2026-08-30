// BetterDesktop.Shell.MenuBar — Wi‑Fi / 有线 图标的单一绘制实现（问题 2 + 问题 4）
//
// 【为什么要抽出来】此前同一枚 Wi‑Fi 扇形被抄了两份：
//   - Status/MenuBarStatusStrip.cs 的 WifiSignalIcon（菜单栏右区）
//   - Windows/NetworkPanelWindow.cs 的 BuildWifiGlyph（NETWORK 面板 WLAN 小卡片）
// 两份都画错了同一件事：画布高度给小了，扇形顶点在 (8,11.5)、最外弧半径 7、
// 底部圆点却落在 y 10.4~12.6 —— 圆点溢出画布被裁掉半截，三条弧的间距也不均匀
// （半径 3/5/7 间距 2，缩到 16px 后弧之间只剩不到 1px 缝，糊成一团）。
// 更糟的是两处都"永远满格"：无论断网、走有线还是信号微弱，扇形三条弧始终全亮，
// 图标不传递任何真实状态。
//
// 【本实现的三个修正】
//   1. 统一 24×24 设计栅格：顶点 (12,18.2)，半径 5 / 9.5 / 14（间距 4.5），
//      整个图形（含圆点与描边外扩）在栅格内留有余量，缩放绝不裁切。
//   2. 支持 0~3 级信号强度：未点亮的弧/点用 Opacity 降到 30%，
//      而不是换一个写死的灰色——这样在亮色/暗色主题下都能自动保持对比度。
//   3. 有线（以太网）走独立的水晶头图标，不再张冠李戴显示 Wi‑Fi 扇形。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace BetterDesktop.Shell.MenuBar.Contracts;

/// <summary>Wi‑Fi 扇形 / 有线水晶头图标（纯自绘，不依赖字体字形，避免部分系统显示成方块）。</summary>
internal static class WifiGlyph
{
    /// <summary>设计栅格边长：所有坐标都写在这个 24×24 网格里，由调用方用 Viewbox 缩放到目标尺寸。</summary>
    public const double GridSize = 24;

    private const double ApexX = 12;      // 扇形顶点 X
    private const double ApexY = 18.6;    // 扇形顶点 Y（含圆点在内整体垂直居中于栅格）
    private const double StrokeWidth = 2.6;
    private const double DotRadius = 2.6;

    /// <summary>三条弧的半径（由内到外）。间距 4.4：缩到 16px 后弧间仍有可见缝隙。</summary>
    private static readonly double[] Radii = { 5.4, 9.8, 14.2 };

    private const double Cos45 = 0.7071067811865476;

    /// <summary>未点亮弧 / 圆点的不透明度。</summary>
    public const double DimOpacity = 0.30;

    /// <summary>
    /// 把 0-100 的信号质量折算成 0~3 级（点亮的弧数）。
    /// 0 = 未知或断开（只留一个暗点），1/2/3 = 弱/中/强。
    /// </summary>
    public static int LevelFromQuality(int quality)
    {
        if (quality <= 0) return 0;
        if (quality >= 70) return 3;
        if (quality >= 45) return 2;
        return 1;
    }

    /// <summary>第 <paramref name="index"/> 条弧（0=最内）的几何：以顶点为圆心、90° 向上的扇弧。</summary>
    public static Geometry ArcGeometry(int index)
    {
        var r = Radii[index];
        var off = r * Cos45;
        // 起笔在左上 45°，顺时针扫到右上 45°，正好是朝上的 90° 扇弧。
        return Geometry.Parse(
            $"M {ApexX - off:F2},{ApexY - off:F2} A {r:F2},{r:F2} 0 0 1 {ApexX + off:F2},{ApexY - off:F2}");
    }

    /// <summary>
    /// 构造 Wi‑Fi 扇形（3 条弧 + 底部圆点）。
    /// </summary>
    /// <param name="brush">描边/填充画刷（调用方负责其主题同步）。</param>
    /// <param name="level">点亮的弧数 0~3。</param>
    /// <param name="canvas">输出画布（24×24），供调用方后续改 level。</param>
    /// <param name="arcs">三条弧（由内到外），供调用方按 level 调 Opacity。</param>
    /// <param name="dot">底部圆点。</param>
    public static void BuildFan(Brush brush, int level, out Canvas canvas, out Path[] arcs, out Ellipse dot)
    {
        canvas = new Canvas { Width = GridSize, Height = GridSize };

        arcs = new Path[Radii.Length];
        for (int i = 0; i < Radii.Length; i++)
        {
            var arc = new Path
            {
                Data = ArcGeometry(i),
                Stroke = brush,
                StrokeThickness = StrokeWidth,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
            arcs[i] = arc;
            canvas.Children.Add(arc);
        }

        dot = new Ellipse
        {
            Width = DotRadius * 2,
            Height = DotRadius * 2,
            Fill = brush
        };
        Canvas.SetLeft(dot, ApexX - DotRadius);
        Canvas.SetTop(dot, ApexY - DotRadius);
        canvas.Children.Add(dot);

        ApplyLevel(arcs, dot, level);
    }

    /// <summary>按等级刷新弧与圆点的点亮状态（未点亮者降透明度，画刷本身不变）。</summary>
    public static void ApplyLevel(Path[] arcs, Ellipse dot, int level)
    {
        var lit = level < 0 ? 0 : level > arcs.Length ? arcs.Length : level;
        for (int i = 0; i < arcs.Length; i++)
        {
            // 弧由内到外点亮：第 i 条弧在 i < lit 时点亮。
            var on = i < lit;
            arcs[i].Opacity = on ? 1.0 : DimOpacity;
        }
        // 圆点是"设备本体"，只要不是完全无信号就点亮。
        dot.Opacity = lit > 0 ? 1.0 : DimOpacity;
    }

    /// <summary>
    /// 构造有线（以太网）水晶头图标：插头外壳 + 两根针脚 + 一段网线。
    /// 走有线时显示它，而不是张冠李戴地显示 Wi‑Fi 扇形。
    /// </summary>
    public static Canvas BuildWired(Brush brush)
    {
        var canvas = new Canvas { Width = GridSize, Height = GridSize };

        // 插头外壳（空心圆角矩形）
        var shell = new Rectangle
        {
            Width = 12,
            Height = 8,
            RadiusX = 2,
            RadiusY = 2,
            Stroke = brush,
            StrokeThickness = StrokeWidth
        };
        Canvas.SetLeft(shell, 6);
        Canvas.SetTop(shell, 5);
        canvas.Children.Add(shell);

        // 两根针脚
        foreach (var x in new[] { 9.5, 14.5 })
        {
            canvas.Children.Add(new Line
            {
                X1 = x, Y1 = 13, X2 = x, Y2 = 15.6,
                Stroke = brush,
                StrokeThickness = StrokeWidth,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });
        }

        // 网线
        canvas.Children.Add(new Line
        {
            X1 = 12, Y1 = 15.6, X2 = 12, Y2 = 21,
            Stroke = brush,
            StrokeThickness = StrokeWidth,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        });

        return canvas;
    }

    /// <summary>把栅格画布包成指定显示尺寸的 Viewbox（Uniform 缩放，绝不裁切）。</summary>
    public static FrameworkElement Wrap(Canvas canvas, double width, double height)
        => new Viewbox
        {
            Width = width,
            Height = height,
            Stretch = Stretch.Uniform,
            Child = canvas
        };
}
