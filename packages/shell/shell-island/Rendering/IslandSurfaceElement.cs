// BetterDesktop.Shell.Island — 岛的形状层（自绘：液体轮廓 + 水面高光 + 来源字形）
//
// 职责边界：本元素只画"形状与字形"，不画文字（文字走 WPF TextBlock，见 IslandWindow 的内容层）。
// 这样文本能沿用全局字号缩放/主题前景传导/无障碍读出，而形状保持逐帧自绘。
//
// 【融合观感 · 2026-09-16 用户反馈后重做】用户要求"上半与菜单栏融合、继续往下才有独立感"。
// 做法是给填充与描边都套一条**纵向渐变**（相对坐标 = 形状自身的包围盒，任何高度都自适应）：
//   · 填充：顶部偏透（透出下层菜单栏材质）→ 22% 处转为实心 → 下半是独立的实体；
//   · 描边：顶部与肩部**完全透明**（这条边就是"接缝"，画出来必然像贴上去的）→ 向下渐显到满强度。
// 于是"融合→独立"不是两套画法，而是同一条渐变的连续结果；矮到 8 DIP 的休眠胶囊自然只剩一枚淡淡的小凸起。

using System;
using System.Windows;
using System.Windows.Media;

namespace BetterDesktop.Shell.Island.Rendering;

/// <summary>岛的形状层（命中测试也在这里：唯一依据是当前轮廓本身）。</summary>
internal sealed class IslandSurfaceElement : FrameworkElement
{
    /// <summary>字形在头部行内的左边距。</summary>
    private const double GlyphLeftPadding = 11.0;

    /// <summary>头部行高度（与窗口的内容布局常量保持一致）。</summary>
    private const double HeaderHeight = 26.0;

    /// <summary>融合区：占形状高度的比例。这一段内描边从 0 渐显、填充从半透转实心。</summary>
    private const double FusionZone = 0.22;

    /// <summary>矮于此高度视为"休眠胶囊"：描边整体减弱，避免一枚静止小凸起反而最扎眼。</summary>
    private const double SoftStrokeMaxHeight = 12.0;

    /// <summary>低于此高度不画顶部高光（否则会变成悬浮在透明填充上的白线）。</summary>
    private const double HighlightMinHeight = 20.0;

    private readonly IslandOutline _outline = new();
    private readonly TranslateTransform _glyphTransform = new();
    private readonly ScaleTransform _glyphScale = new();

    /// <summary>字形笔（可变但只在主题/内容变化时重建，不逐帧）。</summary>
    private readonly Pen _glyphPen = new(Brushes.White, 1.5)
    {
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Round,
        LineJoin = PenLineJoin.Round,
    };

    /// <summary>顶部高光渐变（水面反光；主题变化时重建）。</summary>
    private LinearGradientBrush _highlight = BuildHighlight(Colors.White);

    private Pen _strokePen = new(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), 1.0);
    private Pen _strokePenSoft = new(new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)), 1.0);
    private Brush _fillBrush = new SolidColorBrush(Color.FromArgb(0xCC, 0x1C, 0x1C, 0x1E));

    private IslandPose _pose;
    private IslandGlyph _glyph = IslandGlyph.Dot;

    /// <summary>当前姿态（供窗口做点击/命中判定与诊断）。</summary>
    public IslandPose Pose => _pose;

    /// <summary>换主题令牌（填充/描边/字形/高光）：填充与描边在这里转成"融合渐变"。</summary>
    public void SetPalette(Brush fill, Brush stroke, Brush glyph)
    {
        var fillColor = (fill as SolidColorBrush)?.Color ?? Color.FromArgb(0xE6, 0x1C, 0x1C, 0x1E);
        var strokeColor = (stroke as SolidColorBrush)?.Color ?? Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF);

        // 填充：顶边几乎全透（读作"菜单栏还在继续"）→ 融合区末端接近实心 → 下半是独立的实体。
        // 这正是用户要的"上半融合、继续往下才有独立感"：融合不是靠画一条线，而是靠**颜色连续**。
        _fillBrush = BuildVerticalFade(
            WithAlpha(fillColor, Scale(fillColor.A, 0.18)),
            WithAlpha(fillColor, Scale(fillColor.A, 0.85)),
            fillColor,
            FusionZone);

        // 描边：顶部全透明（不画接缝）→ 融合区末端约半强度 → 底部满强度
        _strokePen = new Pen(
            BuildVerticalFade(
                WithAlpha(strokeColor, 0),
                WithAlpha(strokeColor, Scale(strokeColor.A, 0.55)),
                strokeColor,
                FusionZone),
            1.0);

        // 休眠胶囊用：同一条渐变但整体更淡
        _strokePenSoft = new Pen(
            BuildVerticalFade(
                WithAlpha(strokeColor, 0),
                WithAlpha(strokeColor, Scale(strokeColor.A, 0.18)),
                WithAlpha(strokeColor, Scale(strokeColor.A, 0.42)),
                FusionZone),
            1.0);

        _glyphPen.Brush = glyph;
        _highlight = BuildHighlight((glyph as SolidColorBrush)?.Color ?? Colors.White);
        InvalidateVisual();
    }

    /// <summary>换姿态（逐帧调用；内部零分配）。</summary>
    public void SetPose(in IslandPose pose, IslandGlyph glyph)
    {
        _pose = pose;
        _glyph = glyph;
        InvalidateVisual();
    }

    /// <summary>点是否落在岛内（空白处返回 false → 主窗口返回 HTTRANSPARENT 放行点击）。</summary>
    public bool HitTestIsland(Point point) => _pose.HasShape && _outline.Contains(point);

    /// <inheritdoc />
    protected override void OnRender(DrawingContext drawingContext)
    {
        if (drawingContext is null || !_pose.HasShape)
        {
            return;
        }

        // 轮廓：与命中测试共用同一份几何（"看得见点得到"由结构保证，不靠两处各算一遍）。
        // 描边用"融合渐变"笔：顶部与肩部的 alpha 为 0，所以顶边虽然画了但看不见 → 没有接缝。
        _outline.Update(_pose);
        var pen = _pose.Height <= SoftStrokeMaxHeight ? _strokePenSoft : _strokePen;
        drawingContext.DrawGeometry(_fillBrush, pen, _outline.Geometry);

        // 顶部高光：裁到轮廓内，只在头部行上沿画一条渐变细带（模拟水面反光），强度随显现进度上升。
        // 矮态（休眠胶囊/收薄过程）不画：那里填充本就近乎全透，一条高光会变成悬浮的白线。
        if (_pose.Reveal > 0.85 && _pose.Height >= HighlightMinHeight)
        {
            drawingContext.PushClip(_outline.Geometry);
            drawingContext.DrawRectangle(
                _highlight,
                null,
                new Rect(_pose.Left, _pose.Top, Math.Max(0, _pose.Width), 2.4));
            drawingContext.Pop();
        }

        DrawGlyph(drawingContext);
    }

    private void DrawGlyph(DrawingContext dc)
    {
        if (_pose.ContentOpacity <= 0.01)
        {
            return;
        }

        // 字形垂直居中于头部行（头部行锚在胶囊顶边，形变时高度向下生长，故不会溢出）。
        var box = Math.Min(IslandGlyphs.DesignSize, Math.Max(10.0, _pose.Height - 8.0));
        var scale = box / IslandGlyphs.DesignSize;
        var x = _pose.Left + GlyphLeftPadding;
        var y = _pose.Top + (Math.Min(HeaderHeight, _pose.Height) - box) / 2.0;

        dc.PushOpacity(_pose.ContentOpacity);
        _glyphTransform.X = x;
        _glyphTransform.Y = y;
        _glyphScale.ScaleX = scale;
        _glyphScale.ScaleY = scale;
        dc.PushTransform(_glyphScale);
        dc.PushTransform(_glyphTransform);

        var geometry = IslandGlyphs.Get(_glyph);
        dc.DrawGeometry(IslandGlyphs.IsFilled(_glyph) ? _glyphPen.Brush : null, _glyphPen, geometry);

        dc.Pop();
        dc.Pop();
        dc.Pop();
    }

    /// <inheritdoc />
    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters)
    {
        if (hitTestParameters is null)
        {
            return null;
        }

        return HitTestIsland(hitTestParameters.HitPoint)
            ? new PointHitTestResult(this, hitTestParameters.HitPoint)
            : null;
    }

    private static LinearGradientBrush BuildHighlight(Color color)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x3A, color.R, color.G, color.B), 0.0),
                new GradientStop(Color.FromArgb(0x12, color.R, color.G, color.B), 0.6),
                new GradientStop(Color.FromArgb(0x00, color.R, color.G, color.B), 1.0),
            },
        };
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// 纵向三段渐变（相对坐标 = 形状包围盒 → 任意高度自适应，逐帧零重建）。
    /// 起点在形状顶边（0）、终点在底边（1），<paramref name="midAt"/> 处由 <paramref name="mid"/> 转到 <paramref name="bottom"/>。
    /// </summary>
    private static LinearGradientBrush BuildVerticalFade(Color top, Color mid, Color bottom, double midAt)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
            GradientStops =
            {
                new GradientStop(top, 0.0),
                new GradientStop(mid, midAt),
                new GradientStop(bottom, 1.0),
            },
        };
        brush.Freeze();
        return brush;
    }

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    private static byte Scale(byte value, double factor)
    {
        var scaled = Math.Round(value * factor, MidpointRounding.AwayFromZero);
        return (byte)Math.Clamp(scaled, 0.0, 255.0);
    }
}
