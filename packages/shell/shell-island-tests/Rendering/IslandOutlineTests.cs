using System.Windows;
using BetterDesktop.Shell.Island.Rendering;
using Xunit;

namespace BetterDesktop.Shell.Island.Tests.Rendering;

/// <summary>
/// 液态轮廓机检（计划 T2）：附着态命中卡片本体、脱离态颈部变细、矮态不倒挂、
/// 以及"命中判据与绘制几何同源"（看得见点得到，不出现看不见的可点区域）。
/// </summary>
public class IslandOutlineTests
{
    private const double AttachY = 20.0;
    private const double Left = 200.0;
    private const double Width = 160.0;
    private const double Height = 26.0;

    private static IslandPose Pose(double gap, double shoulder)
    {
        var top = AttachY + gap;
        return new IslandPose(
            AttachY: AttachY,
            Left: Left,
            Top: top,
            Width: Width,
            Height: Height,
            CornerRadius: Height / 2.0,
            Shoulder: shoulder,
            Gap: gap,
            Reveal: 1.0,
            Expand: 0.0,
            ContentOpacity: 1.0,
            DetailOpacity: 0.0,
            Pulse: 0.0);
    }

    [Fact]
    public void Attached_pose_covers_card_body_and_neck_mouth()
    {
        var outline = new IslandOutline();
        outline.Update(Pose(gap: 0.0, shoulder: 9.0));

        Assert.True(outline.Contains(new Point(Left + (Width / 2.0), AttachY + (Height / 2.0))), "卡片中心必须在轮廓内");
        Assert.True(outline.Contains(new Point(Left + 12.0, AttachY + 0.5)), "附着时肩部以内、贴着菜单栏下沿的点应在岛上");
        Assert.False(outline.Contains(new Point(Left - 40.0, AttachY + 4.0)), "轮廓左侧之外不得命中");
        Assert.False(outline.Contains(new Point(Left + (Width / 2.0), AttachY + Height + 40.0)), "轮廓下边缘外不得命中");
    }

    [Fact]
    public void Detached_pose_narrows_the_neck()
    {
        var outline = new IslandOutline();
        // 同一颗胶囊：一边贴附着，一边脱离 7 DIP（肩部按动效内核的公式放大 → 颈部变细）
        outline.Update(Pose(gap: 0.0, shoulder: 9.0));
        var attached = outline.Contains(new Point(Left + 12.0, AttachY + 0.5));

        outline.Update(Pose(gap: 7.0, shoulder: 9.0 + (7.0 * 1.4)));
        var detached = outline.Contains(new Point(Left + 12.0, AttachY + 0.5));

        Assert.True(attached, "附着态该点在岛上（肩部以内）");
        Assert.False(detached, "脱离态该点已被收窄的颈部排到轮廓外");

        // 颈部中心仍在岛上：脱离不等于断开（拉丝状态）
        Assert.True(outline.Contains(new Point(Left + (Width / 2.0), AttachY + 0.5)));
    }

    [Fact]
    public void Update_is_in_place_and_never_stale()
    {
        var outline = new IslandOutline();
        outline.Update(Pose(0.0, 9.0));
        var before = outline.Geometry.Bounds;

        outline.Update(Pose(7.0, 18.0));
        var after = outline.Geometry.Bounds;

        // 顶边永远贴菜单栏下沿（attachY）；脱离 7 DIP 后底边必须同步下移 → 证明几何是就地更新的
        Assert.Equal(AttachY, after.Top, 1);
        Assert.InRange(after.Bottom - before.Bottom, 6.5, 7.5);
    }

    [Fact]
    public void Degenerate_pose_does_not_throw_and_misses_everything()
    {
        var outline = new IslandOutline();
        var hidden = IslandPose.Hidden(AttachY, 560.0);
        outline.Update(hidden);

        Assert.False(hidden.HasShape);
        Assert.False(outline.Contains(new Point(280.0, 30.0)));

        // 极小高度（显形第一帧）也不得抛；此时几何退化成贴着菜单栏的一条薄片
        outline.Update(Pose(0.0, 9.0) with { Height = 0.4, CornerRadius = 13.0 });
        Assert.True(outline.Contains(new Point(Left + 80.0, AttachY + 0.2)), "薄片范围内仍应算岛内（命中与绘制一致）");
        Assert.False(outline.Contains(new Point(Left + 80.0, AttachY + 3.0)), "薄片下方必须落空");
    }
}
