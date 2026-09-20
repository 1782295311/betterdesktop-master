using System;
using BetterDesktop.Shell.Island.Rendering;
using Xunit;

namespace BetterDesktop.Shell.Island.Tests.Rendering;

/// <summary>
/// 动效内核机检（计划 T1）：临界阻尼不超调、欠阻尼回弹 ≤6%、固定子步下的数值稳定性、
/// 空闲（两弹簧静止）必须可判定——这是"空闲零重绘"的唯一依据。
/// </summary>
public class IslandMotionTests
{
    private const double Frame = 1.0 / 60.0;

    private static IslandSize Collapsed => new(160.0, 26.0);

    private static IslandSize Expanded => new(280.0, 60.0);

    private static void Run(IslandMotionController motion, double seconds)
    {
        var frames = (int)Math.Ceiling(seconds / Frame);
        for (var i = 0; i < frames; i++)
        {
            motion.Advance(Frame);
        }
    }

    [Fact]
    public void Critical_damping_does_not_overshoot()
    {
        var spring = new IslandSpring(22.0, 1.0) { Target = 1.0 };
        var max = 0.0;
        for (var i = 0; i < 240; i++)
        {
            spring.Advance(Frame);
            max = Math.Max(max, spring.Value);
        }

        Assert.True(max <= 1.0 + 0.005, $"临界阻尼出现超调：{max}");
        Assert.True(spring.Settled, "临界阻尼应在 4 s 内静止");
    }

    [Fact]
    public void Underdamped_overshoot_within_six_percent()
    {
        var spring = new IslandSpring(18.0, 0.78) { Target = 1.0 };
        var max = 0.0;
        for (var i = 0; i < 240; i++)
        {
            spring.Advance(Frame);
            max = Math.Max(max, spring.Value);
        }

        var overshoot = max - 1.0;
        Assert.True(overshoot > 0.0, "欠阻尼应当有回弹（否则就不是'灵动'）");
        Assert.True(overshoot <= 0.06, $"回弹超过 6% 上限：{overshoot:P1}");
    }

    [Fact]
    public void Fixed_substeps_make_trajectory_frame_rate_independent()
    {
        // 掉帧（一帧 50 ms）与稳定 60 Hz 走同样时长，末值必须接近（否则表现成"卡了就抽一下"）
        var coarse = new IslandSpring(22.0, 1.0) { Target = 1.0 };
        coarse.Advance(0.05);

        var fine = new IslandSpring(22.0, 1.0) { Target = 1.0 };
        for (var i = 0; i < 3; i++)
        {
            fine.Advance(0.0166);
        }

        Assert.InRange(Math.Abs(coarse.Value - fine.Value), 0.0, 0.02);
    }

    [Fact]
    public void Huge_frame_time_is_clamped()
    {
        // 睡眠/挂起后的巨量 dt 不得把弹簧一步推飞（钳到 50 ms → 只走等效 50 ms 的行程且不超调）
        var spring = new IslandSpring(22.0, 1.0) { Target = 1.0 };
        spring.Advance(10.0);
        Assert.InRange(spring.Value, 0.0, 0.5);
        Assert.True(spring.Value <= 1.0, "任何情况下都不得越过目标值（越过去就是'抽一下'）");
    }

    [Fact]
    public void Visible_state_grows_pose_and_settles_into_idle()
    {
        var motion = new IslandMotionController(IslandMotionTier.Full);
        motion.SetVisible(true);
        Run(motion, 2.0);

        var pose = motion.ComputePose(attachY: 20.0, canvasWidth: 560.0, Collapsed, Expanded);
        Assert.True(pose.HasShape);
        Assert.InRange(pose.Width, Collapsed.Width - 1.0, Collapsed.Width + 1.0); // 未展开 = 收起态宽度
        Assert.InRange(pose.Height, Collapsed.Height - 1.0, Collapsed.Height + 1.0);
        Assert.InRange(pose.Gap, 0.0, 0.5);                                      // 已吸附到菜单栏下沿
        Assert.InRange(pose.ContentOpacity, 0.99, 1.0);                          // 内容已完全显现
        Assert.False(motion.IsAnimating);                                        // 静止 → 渲染层退订帧时钟
    }

    [Fact]
    public void Hidden_pose_is_shapeless_and_not_hit_testable()
    {
        var motion = new IslandMotionController(IslandMotionTier.Full);
        motion.SetVisible(false);
        Run(motion, 1.0);

        var pose = motion.ComputePose(20.0, 560.0, Collapsed, Expanded);
        Assert.False(pose.HasShape);
        Assert.False(motion.IsAnimating);
    }

    [Fact]
    public void Expand_requires_detail_content()
    {
        var motion = new IslandMotionController(IslandMotionTier.Full);
        motion.SetVisible(true);
        Run(motion, 1.0);

        motion.HasDetail = false;
        motion.SetExpanded(true);
        Run(motion, 1.0);
        var flat = motion.ComputePose(20.0, 560.0, Collapsed, Expanded);
        Assert.InRange(flat.Height, Collapsed.Height - 1.0, Collapsed.Height + 1.0); // 没细节就绝不变形

        motion.HasDetail = true;
        motion.SetExpanded(true);
        Run(motion, 1.0);
        var grown = motion.ComputePose(20.0, 560.0, Collapsed, Expanded);
        Assert.InRange(grown.Height, Expanded.Height - 1.0, Expanded.Height + 1.0);
        Assert.InRange(grown.Width, Expanded.Width - 1.0, Expanded.Width + 1.0);
    }

    [Fact]
    public void Off_tier_snaps_without_intermediate_frames()
    {
        var motion = new IslandMotionController(IslandMotionTier.Off);
        motion.SetVisible(true);

        var pose = motion.ComputePose(20.0, 560.0, Collapsed, Expanded);
        Assert.Equal(1.0, pose.Reveal, 3);
        Assert.True(pose.HasShape);
        Assert.False(motion.IsAnimating);
    }

    [Fact]
    public void Content_fades_in_only_after_geometry_can_host_it()
    {
        // 显形最初几帧高度还不满头部行：内容必须不可见，避免文字溢出轮廓（规格 §9 红线）
        var motion = new IslandMotionController(IslandMotionTier.Full);
        motion.SetVisible(true);
        motion.Advance(Frame);

        var early = motion.ComputePose(20.0, 560.0, Collapsed, Expanded);
        Assert.InRange(early.ContentOpacity, 0.0, 0.05);
    }

    [Fact]
    public void Pulse_only_active_when_requested_and_visible()
    {
        var motion = new IslandMotionController(IslandMotionTier.Full);
        motion.PulseActive = true;
        motion.SetVisible(true);
        Run(motion, 1.0);

        Assert.True(motion.IsAnimating, "有进度在走时必须保持重绘（脉冲是持续动画）");

        motion.PulseActive = false;
        Run(motion, 1.0);
        Assert.False(motion.IsAnimating, "进度结束后必须回到零重绘");
    }
}
