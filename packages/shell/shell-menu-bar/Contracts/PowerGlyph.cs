// BetterDesktop.Shell.MenuBar — 电源方案图标的单一绘制实现（问题 5）
//
// 【原来的问题】性能模式列表的图标走 Segoe MDL2 Assets 字体码位（\uE74E / \uE9D2 / \uE840 / \uE783），
// 码位是凭印象填的，注释写着"节能=叶子、性能=涡轮、卓越=闪电、平衡=仪表"，
// 实际渲染出来的字形与这些语义对不上（有的甚至是别的符号或空白）——
// 用户反馈"电池里面的图标画的与其功能不符"。
// 更根本的问题是：依赖字体字形本身就不稳（本项目其它面板已经明确"不依赖字体字形"），
// 字体缺失时直接显示方块。
//
// 【本实现】24×24 栅格纯自绘，四种方案各一个语义明确的图形：
//   节能 = 叶子（叶脉） / 平衡 = 天平 / 高性能 = 速度表（指针偏右） / 卓越性能 = 闪电

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace BetterDesktop.Shell.MenuBar.Contracts;

/// <summary>电源方案（性能模式）的语义分类——图标由 <see cref="PowerGlyph"/> 自绘，不再用字体码位。</summary>
// 注意：PowerPlanItem 是 public record，这里的枚举必须同级可见，否则报 CS0051。
public enum PowerPlanKind
{
    /// <summary>平衡（系统默认）。</summary>
    Balanced,
    /// <summary>节能（省电）。</summary>
    PowerSaver,
    /// <summary>高性能。</summary>
    HighPerformance,
    /// <summary>卓越性能。</summary>
    UltimatePerformance
}

/// <summary>电源方案图标（纯自绘，24×24 栅格，与 <see cref="WifiGlyph"/> 同一套规范）。</summary>
internal static class PowerGlyph
{
    /// <summary>设计栅格边长。</summary>
    public const double GridSize = 24;

    private const double StrokeWidth = 1.8;

    /// <summary>构造方案图标：填充/描边统一用 <paramref name="brush"/>。</summary>
    public static Canvas Create(PowerPlanKind kind, Brush brush)
    {
        var canvas = new Canvas { Width = GridSize, Height = GridSize };
        switch (kind)
        {
            case PowerPlanKind.PowerSaver:
                BuildLeaf(canvas, brush);
                break;
            case PowerPlanKind.HighPerformance:
                BuildGauge(canvas, brush, needleToRight: true);
                break;
            case PowerPlanKind.UltimatePerformance:
                BuildBolt(canvas, brush);
                break;
            default:
                BuildBalance(canvas, brush);
                break;
        }
        return canvas;
    }

    /// <summary>节能 = 叶子（叶身 + 叶脉）。</summary>
    private static void BuildLeaf(Canvas canvas, Brush brush)
    {
        canvas.Children.Add(new Path
        {
            // 从右上到左下的经典叶片轮廓（两段贝塞尔合成的尖头叶）
            Data = Geometry.Parse("M 20.5 3.5 C 20.5 12.5 14 19 4.5 20.5 C 4.5 11.5 11 5 20.5 3.5 Z"),
            Fill = brush
        });
        canvas.Children.Add(new Path
        {
            Data = Geometry.Parse("M 16.5 7.5 C 12.5 10.5 9.5 13.5 7.5 18"),
            Stroke = brush,
            StrokeThickness = StrokeWidth,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        });
    }

    /// <summary>平衡 = 天平（横梁 + 支柱 + 底座 + 两侧秤盘）。</summary>
    private static void BuildBalance(Canvas canvas, Brush brush)
    {
        // 横梁
        canvas.Children.Add(Line(4, 9, 20, 9, brush));
        // 支点 + 支柱 + 底座
        canvas.Children.Add(new Ellipse
        {
            Width = 3.2, Height = 3.2, Fill = brush
        });
        var pivot = (Ellipse)canvas.Children[^1];
        Canvas.SetLeft(pivot, 12 - 1.6);
        Canvas.SetTop(pivot, 9 - 1.6);
        canvas.Children.Add(Line(12, 9, 12, 20, brush));
        canvas.Children.Add(Line(8, 20, 16, 20, brush));
        // 吊线与秤盘
        canvas.Children.Add(Line(4, 9, 4, 12.6, brush));
        canvas.Children.Add(Line(20, 9, 20, 12.6, brush));
        canvas.Children.Add(new Path
        {
            Data = Geometry.Parse("M 1.6 12.6 Q 4 17.2 6.4 12.6 Z"),
            Fill = brush
        });
        canvas.Children.Add(new Path
        {
            Data = Geometry.Parse("M 17.6 12.6 Q 20 17.2 22.4 12.6 Z"),
            Fill = brush
        });
    }

    /// <summary>速度表（半圆表盘 + 指针）。指针居中=平衡，偏右=高性能。</summary>
    private static void BuildGauge(Canvas canvas, Brush brush, bool needleToRight)
    {
        // 上半圆表盘（180°）
        canvas.Children.Add(new Path
        {
            Data = Geometry.Parse("M 4 17 A 8 8 0 0 1 20 17"),
            Stroke = brush,
            StrokeThickness = StrokeWidth,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        });
        // 指针：平衡指正上方，高性能偏右上方
        var tip = needleToRight ? (17.4, 11.4) : (12.0, 9.6);
        canvas.Children.Add(Line(12, 17, tip.Item1, tip.Item2, brush));
        canvas.Children.Add(new Ellipse
        {
            Width = 2.6, Height = 2.6, Fill = brush
        });
        var hub = (Ellipse)canvas.Children[^1];
        Canvas.SetLeft(hub, 12 - 1.3);
        Canvas.SetTop(hub, 17 - 1.3);
    }

    /// <summary>卓越性能 = 闪电。</summary>
    private static void BuildBolt(Canvas canvas, Brush brush)
    {
        canvas.Children.Add(new Path
        {
            Data = Geometry.Parse("M 14 2 L 5.5 13.5 H 11 L 9.5 22 L 18.5 10 H 13 Z"),
            Fill = brush
        });
    }

    /// <summary>充电中叠加在电池条上的小闪电（画在电池栅格内，尺寸更小）。</summary>
    public static Path CreateChargeBolt(Brush brush) => new()
    {
        Data = Geometry.Parse("M 8.6 1.6 L 3.4 8.2 H 6.6 L 5.8 13.4 L 11.2 6.4 H 7.9 Z"),
        Fill = brush
    };

    private static Line Line(double x1, double y1, double x2, double y2, Brush brush) => new()
    {
        X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
        Stroke = brush,
        StrokeThickness = StrokeWidth,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round
    };
}
