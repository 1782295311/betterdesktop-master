// BetterDesktop.Shell.Island — 脉冲进度环（规格 §5）
//
// 【规格要点】① 弧长按进度线性映射，未完成段保留低亮度"轨迹"（科技感）；
// ② 脉冲频率与进度解耦（1.5 Hz 由动效内核驱动），进度慢时也保持生命感；
// ③ 不确定进度改"旋转扫描 + 双脉冲"，**不显示假百分比**（与项目"禁止假精确"纪律一致）；
// ④ 终态：完成→绿色一次爆发，失败→警示红单次抖动（不做循环闪烁）。
//
// 【零分配纪律】弧段与路径图构建一次，逐帧只改起止点与半径；光晕用的是**可变**画刷/笔
// （冻结对象逐帧改色会抛 InvalidOperationException——这是本文件唯一需要小心的地方）。

using System;
using System.Windows;
using System.Windows.Media;

namespace BetterDesktop.Shell.Island.Rendering;

/// <summary>脉冲进度环（自绘；直径由宿主给定，通常 18 DIP）。</summary>
public sealed class PulseRing : FrameworkElement
{
    private readonly PathFigure _arcFigure = new();
    private readonly ArcSegment _arc = new();
    private readonly PathGeometry _arcGeometry = new();

    /// <summary>光晕画刷必须可变：逐帧按相位改 alpha（冻结画刷改不了）。</summary>
    private readonly SolidColorBrush _haloBrush = new(Colors.Transparent);
    private readonly Pen _haloPen;

    private Color _accent = Color.FromRgb(0x0A, 0x84, 0xFF);
    private Color _danger = Color.FromRgb(0xFF, 0x5F, 0x57);

    private Pen _trackPen = CreateFrozenPen(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF), 2.2);
    private Pen _valuePen = CreateFrozenPen(Color.FromRgb(0x0A, 0x84, 0xFF), 2.4);

    /// <summary>构造一次弧段（此后只改点）。</summary>
    public PulseRing()
    {
        _arcFigure.Segments.Add(_arc);
        _arcGeometry.Figures.Add(_arcFigure);
        _arc.SweepDirection = SweepDirection.Clockwise;
        _arc.IsLargeArc = false;
        _haloPen = new Pen(_haloBrush, 3.4)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };

        IsHitTestVisible = false; // 环不参与命中：点击落在胶囊本体上
    }

    /// <summary>进度 0..1（不确定进度时忽略）。</summary>
    public double Progress { get; private set; }

    /// <summary>是否不确定进度（旋转扫描 + 双脉冲，不显示百分比）。</summary>
    public bool Indeterminate { get; private set; }

    /// <summary>是否失败终态（警示色）。</summary>
    public bool Failed { get; private set; }

    private double _pulse;

    /// <summary>脉冲相位 0..1（由动效内核的 PulsePhase 驱动；写入即重绘）。</summary>
    public double Pulse
    {
        get => _pulse;
        set
        {
            _pulse = value;
            InvalidateVisual();
        }
    }

    /// <summary>是否显示脉冲光晕（完整档；精简/关闭档关掉以省重绘）。</summary>
    public bool PulseEnabled { get; set; } = true;

    /// <summary>换主题令牌（强调色/轨道色/警示色）：只在主题变化时调用，不逐帧。</summary>
    public void SetPalette(Color accent, Color track, Color danger)
    {
        _accent = accent;
        _danger = danger;
        _trackPen = CreateFrozenPen(track, 2.2);
        ApplyValuePen();
        InvalidateVisual();
    }

    /// <summary>内容状态变化（进度/不确定/失败）：只重造进度笔，不逐帧调用。</summary>
    public void SetState(double progress, bool indeterminate, bool failed)
    {
        Progress = Math.Clamp(progress, 0.0, 1.0);
        Indeterminate = indeterminate;
        Failed = failed;
        ApplyValuePen();
    }

    /// <inheritdoc />
    protected override void OnRender(DrawingContext drawingContext)
    {
        if (drawingContext is null || ActualWidth <= 4 || ActualHeight <= 4)
        {
            return;
        }

        var center = new Point(ActualWidth / 2.0, ActualHeight / 2.0);
        var baseRadius = Math.Max(1.0, Math.Min(ActualWidth, ActualHeight) / 2.0 - 2.4);

        // 轨道（未完成段的"轨迹"）
        drawingContext.DrawEllipse(null, _trackPen, center, baseRadius, baseRadius);

        if (Indeterminate)
        {
            DrawIndeterminate(drawingContext, center, baseRadius);
            return;
        }

        if (Progress <= 0.001)
        {
            return;
        }

        // 脉冲光晕：半径 ±1.5% 呼吸 + 亮度随相位起伏（与进度解耦）。
        if (PulseEnabled)
        {
            var phase = Pulse * 2.0 * Math.PI;
            var haloRadius = (baseRadius + 1.2) * (1.0 + 0.015 * Math.Sin(phase));
            var alpha = (byte)Math.Clamp((int)Math.Round(70 + 110 * (0.5 + 0.5 * Math.Sin(phase))), 0, 255);
            var haloColor = Failed ? _danger : _accent;
            _haloBrush.Color = Color.FromArgb(alpha, haloColor.R, haloColor.G, haloColor.B);
            drawingContext.DrawEllipse(null, _haloPen, center, haloRadius, haloRadius);
        }

        // 进度弧：12 点方向起，顺时针扫过 progress × 360°
        SetArc(center, baseRadius, 0.0, Progress * 360.0);
        drawingContext.DrawGeometry(null, _valuePen, _arcGeometry);
    }

    private void ApplyValuePen()
    {
        // 未确定进度用强调色；失败终态用警示色（成功沿用强调色，终态"爆发"由来源侧一次性给出）。
        _valuePen = CreateFrozenPen(Failed ? _danger : _accent, 2.4);
    }

    private void DrawIndeterminate(DrawingContext dc, Point center, double radius)
    {
        // 旋转扫描：96° 前导弧 + 对侧 40° 短弧（双脉冲感），整圈由 Pulse 相位驱动。
        var head = Pulse * 360.0;
        SetArc(center, radius, head, head + 96.0);
        dc.DrawGeometry(null, _valuePen, _arcGeometry);

        var tailStart = head + 180.0;
        SetArc(center, radius, tailStart, tailStart + 40.0);
        dc.DrawGeometry(null, _valuePen, _arcGeometry);
    }

    private void SetArc(Point center, double radius, double startDegrees, double endDegrees)
    {
        _arcFigure.StartPoint = PointOnCircle(center, radius, startDegrees);
        _arc.Point = PointOnCircle(center, radius, endDegrees);
        _arc.Size = new Size(radius, radius);
        _arc.IsLargeArc = endDegrees - startDegrees > 180.0;
    }

    /// <summary>0° = 12 点方向，角度增大 = 顺时针（与 WPF Clockwise 扫描方向一致）。</summary>
    private static Point PointOnCircle(Point center, double radius, double degrees)
    {
        var radians = (degrees - 90.0) * Math.PI / 180.0;
        return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }

    private static Pen CreateFrozenPen(Color color, double thickness)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var pen = new Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        pen.Freeze();
        return pen;
    }
}
