// BetterDesktop.Shell.Island — 岛内单线图标字形（SF Symbols 风格的 monoline）
//
// 【为什么自绘而不是用字形字体】① 不赌字体安装（Segoe Fluent Icons 在部分系统/远程桌面会缺失，
// 表现成豆腐块）；② emoji 在同一行里大小/基线不受控，与"科技感"不符；③ 单线图标可随主题令牌换色，
// 且任意 DPI 下都是矢量。字形在设计盒 18×18 内构建一次并冻结，绘制方只做平移（零逐帧分配）。

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace BetterDesktop.Shell.Island.Rendering;

/// <summary>岛可呈现的来源/状态字形。</summary>
public enum IslandGlyph
{
    /// <summary>通用（实心点）。</summary>
    Dot = 0,

    /// <summary>剪贴板。</summary>
    Clipboard = 1,

    /// <summary>媒体播放（音符）。</summary>
    Media = 2,

    /// <summary>格式转换（下行箭头 + 托盘）。</summary>
    Convert = 3,

    /// <summary>成功（对勾）。</summary>
    Check = 4,

    /// <summary>失败/警示（感叹号）。</summary>
    Warning = 5,

    /// <summary>
    /// 不画任何字形（空闲/休眠/收薄过程用）。
    /// <para>
    /// 【为什么需要它】没有活动时窗口传的是"无内容"，若沿用 <see cref="Dot"/> 兜底，
    /// 收薄过程中高度还没降到内容阈值以下，就会闪出一枚白点（2026-09-16 真机截图实证）。
    /// 显式的"无字形"比"兜底一个点"更诚实。
    /// </para>
    /// </summary>
    None = 6,
}

/// <summary>单线图标字形库（设计盒 18×18，构建一次、冻结复用）。</summary>
public static class IslandGlyphs
{
    private static readonly Dictionary<IslandGlyph, Geometry> Cache = new();
    private static readonly object Gate = new();

    /// <summary>设计盒边长（DIP）：绘制方按目标尺寸缩放。</summary>
    public const double DesignSize = 18.0;

    /// <summary>取字形几何（首次访问时构建并冻结）。</summary>
    public static Geometry Get(IslandGlyph glyph)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(glyph, out var cached))
            {
                return cached;
            }

            var built = Build(glyph);
            Cache[glyph] = built;
            return built;
        }
    }

    /// <summary>该字形是否以填充绘制（其余以描边绘制：描边图标即使在细笔宽下也不会糊成一团）。</summary>
    public static bool IsFilled(IslandGlyph glyph) => glyph == IslandGlyph.Dot;

    private static Geometry Build(IslandGlyph glyph)
    {
        // 显式"不画字形"：空闲/休眠/收薄过程用（返回空几何，绘制调用是安全空操作）
        if (glyph == IslandGlyph.None)
        {
            return Geometry.Empty;
        }

        if (glyph == IslandGlyph.Dot)
        {
            var dot = new EllipseGeometry(new Point(9.0, 9.0), 4.6, 4.6);
            dot.Freeze();
            return dot;
        }

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            switch (glyph)
            {
                case IslandGlyph.Clipboard:
                    // 夹板：外框 + 顶部夹子 + 两条内容线
                    Stroke(ctx, (5.0, 6.0), (13.0, 6.0), (13.0, 15.0), (5.0, 15.0), (5.0, 6.0));
                    Stroke(ctx, (7.0, 4.0), (11.0, 4.0), (11.0, 6.6), (7.0, 6.6), (7.0, 4.0));
                    Stroke(ctx, (7.6, 9.4), (10.4, 9.4));
                    Stroke(ctx, (7.6, 12.0), (10.4, 12.0));
                    break;

                case IslandGlyph.Media:
                    // 音符：符头（圆） + 符干 + 符尾
                    Circle(ctx, new Point(6.6, 12.6), 2.8);
                    Stroke(ctx, (9.4, 12.6), (9.4, 4.6));
                    Stroke(ctx, (9.4, 4.6), (13.4, 5.9), (13.4, 8.8));
                    break;

                case IslandGlyph.Convert:
                    // 转换：下行箭头 + 托盘（"落盘/产出"语义）
                    Stroke(ctx, (9.0, 3.8), (9.0, 10.6));
                    Stroke(ctx, (6.0, 7.6), (9.0, 10.8), (12.0, 7.6));
                    Stroke(ctx, (5.0, 14.2), (13.0, 14.2));
                    break;

                case IslandGlyph.Check:
                    Stroke(ctx, (4.8, 9.6), (7.6, 12.4), (13.4, 5.8));
                    break;

                case IslandGlyph.Warning:
                    Stroke(ctx, (9.0, 4.2), (9.0, 10.4));
                    // "点"用等宽圆帽极短线实现：省一路填充几何，视觉上即一个圆点
                    Stroke(ctx, (9.0, 13.3), (9.0, 13.45));
                    break;

                default:
                    Circle(ctx, new Point(9.0, 9.0), 4.4);
                    break;
            }
        }

        geometry.Freeze();
        return geometry;
    }

    private static void Stroke(StreamGeometryContext ctx, params (double X, double Y)[] points)
    {
        ctx.BeginFigure(new Point(points[0].X, points[0].Y), isFilled: false, isClosed: false);
        for (var i = 1; i < points.Length; i++)
        {
            ctx.LineTo(new Point(points[i].X, points[i].Y), isStroked: true, isSmoothJoin: true);
        }
    }

    /// <summary>把整圆拆成四段 90° 折线（单线风格下足够圆，且不需要 ArcTo 的扫描方向推理）。</summary>
    private static void Circle(StreamGeometryContext ctx, Point center, double radius)
    {
        const int Segments = 12;
        ctx.BeginFigure(
            new Point(center.X + radius, center.Y),
            isFilled: false,
            isClosed: true);
        for (var i = 1; i <= Segments; i++)
        {
            var angle = i * 2.0 * Math.PI / Segments;
            ctx.LineTo(
                new Point(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle)),
                isStroked: true,
                isSmoothJoin: true);
        }
    }
}
