// BetterDesktop.Shell.Island — 岛的液态轮廓几何（规格 §3 的落地实现）
//
// 【形态意图】岛不是"贴在菜单栏下面的一个圆角矩形"，而是**从菜单栏里长出来**的：
//   左右两条边与菜单栏下沿之间用凹角（cove fillet）过渡；当胶囊离开菜单栏（显现/收缩过程）
//   凹角随脱离距离变宽 → 颈部变细，形成水滴的"表面张力拉丝"，脱离即断裂（收出末段的吸附手感）。
//
// 【与规格 §3 的差异（如实声明）】规格给的是 SDF metaball + smin 的像素级方案，需要像素着色器；
//   本实现用**解析几何**直接构造同一条轮廓：附着时等价于凹角 fillet，脱离时等价于变宽颈部。
//   形态语义一致、无着色器依赖、零分配、且在任意 DPI 下都是矢量的（无锯齿）。真正的 SDF 版本
//   待自渲染层（DComp/HLSL）拍板后再替换——替换面只有"谁把本几何画成像素"。
//
// 【零分配纪律】路径图与各段一次性建好，逐帧只改点/半径（规格 §9：动画路径上不做分配与布局）。

using System;
using System.Windows;
using System.Windows.Media;

namespace BetterDesktop.Shell.Island.Rendering;

/// <summary>岛的轮廓路径：由 <see cref="IslandPose"/> 驱动，逐帧就地更新（不重新分配几何）。</summary>
public sealed class IslandOutline
{
    /// <summary>四分之一圆的三次贝塞尔逼近系数（0.5523 = 4/3·tan(π/8) 的标准取值）。</summary>
    private const double BezierK = 0.5523;

    private readonly PathFigure _figure = new() { IsClosed = true };
    private readonly BezierSegment _leftShoulder = new();
    private readonly LineSegment _leftEdge = new();
    private readonly ArcSegment _bottomLeft = new();
    private readonly LineSegment _bottomEdge = new();
    private readonly ArcSegment _bottomRight = new();
    private readonly LineSegment _rightEdge = new();
    private readonly BezierSegment _rightShoulder = new();

    /// <summary>构造一次路径图（此后只改点）。</summary>
    public IslandOutline()
    {
        // 顺序即轮廓方向：起点在菜单栏下沿 → 左肩 → 左边 → 左下圆角 → 底边 → 右下圆角 → 右边 → 右肩 → 闭合。
        _figure.Segments.Add(_leftShoulder);
        _figure.Segments.Add(_leftEdge);
        _figure.Segments.Add(_bottomLeft);
        _figure.Segments.Add(_bottomEdge);
        _figure.Segments.Add(_bottomRight);
        _figure.Segments.Add(_rightEdge);
        _figure.Segments.Add(_rightShoulder);

        var geometry = new PathGeometry();
        geometry.Figures.Add(_figure);
        Geometry = geometry;
    }

    /// <summary>轮廓几何（渲染与命中测试共用同一份，保证"看得见点得到"）。</summary>
    public PathGeometry Geometry { get; }

    /// <summary>按当前姿态就地更新轮廓。</summary>
    public void Update(in IslandPose pose)
    {
        var left = pose.Left;
        var right = pose.Right;
        var top = pose.Top;
        var bottom = pose.Bottom;
        var attachY = pose.AttachY;
        var height = Math.Max(0.0, bottom - top);
        var width = Math.Max(0.0, right - left);

        // 圆角不超过半高（胶囊）；肩部不超过半宽（否则左右颈线相交）。
        var radius = Math.Clamp(pose.CornerRadius, 0.0, height / 2.0);
        var shoulder = Math.Clamp(pose.Shoulder, 0.0, width / 2.0);
        // 肩部落点：正常落在 left/right 边的 top+shoulder 处；矮态被压到 "bottom-radius" 以上不倒挂。
        var shoulderEnd = Math.Min(top + shoulder, Math.Max(top, bottom - radius));
        var k = BezierK;

        _figure.StartPoint = new Point(left + shoulder, attachY);

        // 左肩：水平切出（贴着菜单栏下沿）→ 垂直切下（贴着胶囊左边）。
        _leftShoulder.Point1 = new Point(left + shoulder * (1.0 - k), attachY);
        _leftShoulder.Point2 = new Point(left, shoulderEnd - shoulder * k);
        _leftShoulder.Point3 = new Point(left, shoulderEnd);

        _leftEdge.Point = new Point(left, bottom - radius);
        SetCorner(_bottomLeft, new Point(left + radius, bottom), radius);
        _bottomEdge.Point = new Point(right - radius, bottom);
        SetCorner(_bottomRight, new Point(right, bottom - radius), radius);
        _rightEdge.Point = new Point(right, shoulderEnd);

        // 右肩：镜像（垂直切线 → 水平切线）。
        _rightShoulder.Point1 = new Point(right, shoulderEnd - shoulder * k);
        _rightShoulder.Point2 = new Point(right - shoulder * (1.0 - k), attachY);
        _rightShoulder.Point3 = new Point(right - shoulder, attachY);
    }

    /// <summary>点是否落在岛内（命中测试唯一依据；空白处返回 false → 交给下层窗口）。</summary>
    public bool Contains(Point point) => Geometry.FillContains(point);

    /// <summary>底部圆角段（逆时针短弧，与轮廓方向一致）。</summary>
    private static void SetCorner(ArcSegment segment, Point endPoint, double radius)
    {
        segment.Point = endPoint;
        segment.Size = new Size(radius, radius);
        segment.RotationAngle = 0;
        segment.IsLargeArc = false;
        segment.SweepDirection = SweepDirection.Counterclockwise;
    }
}
