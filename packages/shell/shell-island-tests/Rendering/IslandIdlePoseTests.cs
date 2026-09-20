// BetterDesktop.Shell.Island.Tests — 休眠形态的姿态不变量（2026-09-16 用户反馈后新增）
//
// 背景：菜单栏没有刘海，空闲时若岛完全不可见，用户会以为功能不存在。因此无活动时保留一枚休眠矮胶囊。
// 本组测试锁定它的三条不变量：
//   ① 有形状（看得见）；② 内容不淡入（只画形状，不显示文字/字形）；③ 静止后不产帧（零重绘口径对休眠胶囊同样成立）。

using BetterDesktop.Shell.Island.Rendering;
using Xunit;

namespace BetterDesktop.Shell.Island.Tests.Rendering;

/// <summary>休眠胶囊（无活动常驻形态）的姿态与零重绘不变量。</summary>
public sealed class IslandIdlePoseTests
{
    private const double AttachY = 20.0;
    private const double Canvas = 560.0;

    /// <summary>与 IslandWindow 的 IdleWidth / IdleHeight 保持一致（改窗口常量必须同步这里）。</summary>
    private static readonly IslandSize Idle = new(76.0, 20.0);

    /// <summary>休眠尺寸：可见，但内容层完全不淡入。</summary>
    [Fact]
    public void IdleSize_ShowsShapeWithoutContent()
    {
        var motion = new IslandMotionController(IslandMotionTier.Full)
        {
            HasDetail = false,
        };
        motion.SetVisible(true);
        Settle(motion);

        var pose = motion.ComputePose(AttachY, Canvas, Idle, Idle);

        Assert.True(pose.HasShape);
        Assert.True(pose.ContentOpacity < 0.01, $"休眠胶囊不该淡入内容（实际 {pose.ContentOpacity}）");
        Assert.Equal(0.0, pose.Expand);
        Assert.Equal(Idle.Width, pose.Width, 1.0);
        Assert.Equal(Idle.Height, pose.Height, 1.0);
        Assert.Equal(Canvas / 2.0, pose.CenterX, 0.5);   // 与菜单栏中置对齐
        Assert.Equal(AttachY, pose.Top, 0.5);            // 贴住菜单栏下沿（不脱离）
    }

    /// <summary>休眠态静止后不再产帧：常驻可见与"空闲零重绘"不冲突。</summary>
    [Fact]
    public void IdleSize_SettlesAndStopsAnimating()
    {
        var motion = new IslandMotionController(IslandMotionTier.Full)
        {
            HasDetail = false,
            PulseActive = false,
        };
        motion.SetVisible(true);
        Settle(motion);

        // ComputePose 会把尺寸弹簧的目标设为休眠尺寸；再推进一段仍应保持静止。
        motion.ComputePose(AttachY, Canvas, Idle, Idle);
        motion.Advance(1.0 / 60.0);
        Settle(motion);
        motion.ComputePose(AttachY, Canvas, Idle, Idle);

        Assert.False(motion.IsAnimating);
    }

    /// <summary>从活动胶囊回到休眠：宽度是收缩过去的（尺寸弹簧），而不是瞬间跳变。</summary>
    [Fact]
    public void FromActivity_ShrinksSmoothlyBackToIdle()
    {
        var active = new IslandSize(240.0, 26.0);
        var motion = new IslandMotionController(IslandMotionTier.Full)
        {
            HasDetail = true,
        };
        motion.SetVisible(true);
        Settle(motion);
        var activePose = motion.ComputePose(AttachY, Canvas, active, active);
        Assert.Equal(active.Width, activePose.Width, 1.0);
        Assert.True(activePose.ContentOpacity > 0.95);

        // 活动结束：目标尺寸改回休眠，仅推进一帧——宽度必须已经在收缩，但还没到 76。
        motion.HasDetail = false;
        motion.ComputePose(AttachY, Canvas, Idle, Idle);
        motion.Advance(1.0 / 60.0);
        var shrinking = motion.ComputePose(AttachY, Canvas, Idle, Idle);
        Assert.True(shrinking.Width < active.Width, $"第一帧就应变窄，实际 {shrinking.Width}");
        Assert.True(shrinking.Width > Idle.Width, $"第一帧不应直接跳到休眠宽度，实际 {shrinking.Width}");

        Settle(motion);
        var idlePose = motion.ComputePose(AttachY, Canvas, Idle, Idle);
        Assert.Equal(Idle.Width, idlePose.Width, 1.0);
        Assert.True(idlePose.ContentOpacity < 0.01, $"收成休眠后内容应完全不可见（实际 {idlePose.ContentOpacity}）");
    }

    /// <summary>推进到静止（上限 1200 帧足够，超过即视为不收敛 → 由断言暴露）。</summary>
    private static void Settle(IslandMotionController motion)
    {
        for (var i = 0; i < 1200 && motion.IsAnimating; i++)
        {
            motion.Advance(1.0 / 60.0);
        }
    }
}
