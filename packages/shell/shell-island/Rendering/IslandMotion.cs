// BetterDesktop.Shell.Island — 灵动岛动效内核（弹簧物理 + 状态机 + 姿态计算）
//
// 【为什么不用 WPF Storyboard】动效规格 §4 明确：动画一律由渲染层自己的时钟驱动。
// 原因：① Storyboard 在分层窗口下走软件路径；② 它只能驱动依赖属性，驱动不了"几何参数"
// （肩部半径/颈部宽度/弹簧回弹）这类连续量；③ 无法在"两弹簧都静止"时自动退订时钟，
// 做不到规格 §8 要求的"空闲零重绘"。本文件是纯数学（可单测），不引用任何 WPF 类型。

using System;

namespace BetterDesktop.Shell.Island.Rendering;

/// <summary>动效强度三档（规格 §7）。默认按系统"允许动画"设置取值。</summary>
public enum IslandMotionTier
{
    /// <summary>关闭：状态瞬切，不产生中间帧。</summary>
    Off = 0,

    /// <summary>精简：保留形变但时长更短、无呼吸脉冲。</summary>
    Lite = 1,

    /// <summary>完整：液体形变 + 脉冲 + 步进呼吸。</summary>
    Full = 2,
}

/// <summary>岛的尺寸目标（宽 × 高，DIP）。</summary>
public readonly record struct IslandSize(double Width, double Height);

/// <summary>
/// 一帧的姿态：把"状态机进度"翻译成渲染层需要的全部标量（纯数据，渲染与逻辑分界）。
/// </summary>
public readonly record struct IslandPose(
    double AttachY,
    double Left,
    double Top,
    double Width,
    double Height,
    double CornerRadius,
    double Shoulder,
    double Gap,
    double Reveal,
    double Expand,
    double ContentOpacity,
    double DetailOpacity,
    double Pulse)
{
    /// <summary>右边界（DIP）。</summary>
    public double Right => Left + Width;

    /// <summary>下边界（DIP）。</summary>
    public double Bottom => Top + Height;

    /// <summary>中心横坐标（DIP）。</summary>
    public double CenterX => Left + Width / 2;

    /// <summary>是否已成形（可绘制/可命中）；隐藏态为 false。</summary>
    public bool HasShape => Reveal > 0.01 && Width > 1 && Height > 1;

    /// <summary>隐藏姿态（画布居中、零尺寸、不可命中）。</summary>
    public static IslandPose Hidden(double attachY, double canvasWidth) => new(
        AttachY: attachY,
        Left: canvasWidth / 2,
        Top: attachY,
        Width: 0,
        Height: 0,
        CornerRadius: 0,
        Shoulder: 0,
        Gap: 0,
        Reveal: 0,
        Expand: 0,
        ContentOpacity: 0,
        DetailOpacity: 0,
        Pulse: 0);
}

/// <summary>
/// 一维弹簧积分器：半隐式欧拉 + 固定子步（1/240 s）。
/// <para>
/// 【为什么固定子步】变步长积分在掉帧（dt=50ms）时会让欠阻尼弹簧发散，表现为"回弹变成抽搐"。
/// 固定子步保证数值稳定，且与帧率解耦——60 Hz 与 120 Hz 下轨迹一致。
/// </para>
/// <para>参数按规格 §4：ω（频率）与 ζ（阻尼比），收入/收出 ζ=1.0（临界），展开 ζ=0.7~0.8（轻微回弹）。</para>
/// </summary>
public sealed class IslandSpring
{
    private const double FixedStep = 1.0 / 240.0;
    private const int MaxSubSteps = 40;
    private const double MaxFrameSeconds = 0.05;

    /// <param name="omega">角频率（越大越快）。</param>
    /// <param name="zeta">阻尼比（1.0 = 临界阻尼，无超调；&lt;1 欠阻尼有回弹）。</param>
    public IslandSpring(double omega, double zeta)
    {
        Omega = omega;
        Zeta = zeta;
    }

    /// <summary>角频率。</summary>
    public double Omega { get; }

    /// <summary>阻尼比（可运行期改写：显现与消失用不同手感）。</summary>
    public double Zeta { get; set; }

    /// <summary>当前值。</summary>
    public double Value { get; private set; }

    /// <summary>当前速度（单位/秒）。</summary>
    public double Velocity { get; private set; }

    /// <summary>目标值。</summary>
    public double Target { get; set; }

    /// <summary>近静止判定：帧时钟据此自动退订（空闲零重绘）。</summary>
    public bool Settled => Math.Abs(Target - Value) < 0.0015 && Math.Abs(Velocity) < 0.02;

    /// <summary>直接置位（不产生动画；瞬切档与初始化用）。</summary>
    public void Reset(double value)
    {
        Value = value;
        Velocity = 0;
        Target = value;
    }

    /// <summary>注入一次速度脉冲（步进呼吸）。</summary>
    public void Nudge(double velocity) => Velocity += velocity;

    /// <summary>推进 <paramref name="dt"/> 秒（内部拆固定子步）。</summary>
    public void Advance(double dt)
    {
        if (dt <= 0)
        {
            return;
        }

        if (dt > MaxFrameSeconds)
        {
            dt = MaxFrameSeconds; // 掉帧/卡顿不为动画补课，避免一帧跳很远
        }

        var remaining = dt;
        var steps = 0;
        while (remaining > 1e-6 && steps < MaxSubSteps)
        {
            var h = Math.Min(FixedStep, remaining);
            var acceleration = Omega * Omega * (Target - Value) - 2.0 * Zeta * Omega * Velocity;
            Velocity += acceleration * h;
            Value += Velocity * h;
            remaining -= h;
            steps++;
        }
    }
}

/// <summary>
/// 岛的状态机与姿态计算：显隐（<c>reveal</c>）× 展开（<c>expand</c>）× 呼吸（<c>breathe</c>）三条弹簧。
/// <para>
/// 状态语义：`Hidden`（无形）→ `Capsule`（附着于菜单栏下沿的胶囊）→ `Expanded`（悬停/点击后长出细节）。
/// 展开只在"有细节内容"时可用（否则悬停不应有任何形变）。
/// </para>
/// </summary>
public sealed class IslandMotionController
{
    /// <summary>脉冲频率（Hz）：与进度解耦，保证"进度慢时也有生命感"（规格 §5）。</summary>
    private const double PulseHz = 1.5;

    /// <summary>显现过程中胶囊与菜单栏下沿的最大脱离距离（DIP）：水滴先落下再吸附。</summary>
    private const double RevealTravel = 7.0;

    /// <summary>精简档的时长倍率（时间轴加速 → 时长约减半）。</summary>
    private const double LiteTimeScale = 1.8;

    private readonly IslandSpring _reveal = new(22.0, 1.0);
    private readonly IslandSpring _expand = new(18.0, 0.78);
    private readonly IslandSpring _breathe = new(30.0, 0.35);

    /// <summary>尺寸弹簧：换内容 / 收成休眠时宽度高度是"长"过去的，不是瞬间跳过去的。</summary>
    private readonly IslandSpring _width = new(20.0, 0.9);
    private readonly IslandSpring _height = new(22.0, 0.95);

    /// <summary>尺寸是"状态"（内容变化时才改），由 ComputePose 记下来、Advance 推进弹簧。</summary>
    private IslandSize _sizeCollapsed = new(160.0, 26.0);
    private IslandSize _sizeExpanded = new(160.0, 26.0);

    private bool _sizeKnown;
    private bool _sizeReady;
    private bool _retracting;

    /// <param name="tier">动效强度档位。</param>
    public IslandMotionController(IslandMotionTier tier = IslandMotionTier.Full) => Tier = tier;

    /// <summary>动效强度档位。</summary>
    public IslandMotionTier Tier { get; set; }

    /// <summary>当前活动是否存在"展开后才有"的细节（副标题/动作按钮）。</summary>
    public bool HasDetail { get; set; }

    /// <summary>是否处于脉冲态（有进度在走）：脉冲需要持续重绘。</summary>
    public bool PulseActive { get; set; }

    /// <summary>是否可见（有活动占据胶囊）。</summary>
    public bool Visible { get; private set; }

    /// <summary>是否已请求展开。</summary>
    public bool Expanded { get; private set; }

    /// <summary>脉冲相位（0..1，周期 1/1.5 s）。</summary>
    public double PulsePhase { get; private set; }

    /// <summary>
    /// 是否仍在动画中。false 时渲染层必须退订帧时钟（规格 §8：稳态空闲零重绘）。
    /// </summary>
    public bool IsAnimating =>
        !_reveal.Settled
        || !_expand.Settled
        || !_breathe.Settled
        || !_width.Settled
        || !_height.Settled
        || (PulseActive && Visible && Tier == IslandMotionTier.Full && _reveal.Value > 0.05);

    /// <summary>
    /// 显隐切换（收入：完整档轻微回弹；收出：ζ=1.0 临界）。
    /// <para>
    /// 【收纳方向 · 2026-09-16 用户反馈】收出时<see cref="_retracting"/>置真 —— 形态**不再向下脱离**，
    /// 而是顶边钉在菜单栏下沿、原地收薄（读作"被菜单栏吸回去"）。用户的原话是"向上融合收纳在不用的时候"，
    /// 与规格 §4 原本的"脱离时颈部拉丝"相反；以用户反馈为准，规格侧已同步说明。
    /// </para>
    /// </summary>
    public void SetVisible(bool visible)
    {
        Visible = visible;
        _retracting = !visible;
        if (!visible)
        {
            Expanded = false;
            _expand.Target = 0;
        }

        if (Tier == IslandMotionTier.Off)
        {
            _reveal.Zeta = 1.0;
            _reveal.Reset(visible ? 1 : 0);
            if (!visible)
            {
                _expand.Reset(0);
                PulsePhase = 0;
            }

            return;
        }

        _reveal.Zeta = visible && Tier == IslandMotionTier.Full ? 0.72 : 1.0;
        _reveal.Target = visible ? 1 : 0;
    }

    /// <summary>展开/收起（无细节内容时强制收起，避免"悬停却什么都不长"）。</summary>
    public void SetExpanded(bool expanded)
    {
        var want = expanded && HasDetail;
        if (want == Expanded)
        {
            return;
        }

        Expanded = want;
        if (Tier == IslandMotionTier.Off)
        {
            _expand.Reset(want ? 1 : 0);
            return;
        }

        _expand.Zeta = want ? 0.78 : 0.95;
        _expand.Target = want ? 1 : 0;
    }

    /// <summary>步进呼吸（按序粘贴/进度步进的一次脉冲；精简与关闭档不做）。</summary>
    public void Breathe()
    {
        if (Tier == IslandMotionTier.Full)
        {
            _breathe.Nudge(1.2);
        }
    }

    /// <summary>推进一帧。</summary>
    public void Advance(double dt)
    {
        if (dt <= 0)
        {
            return;
        }

        var scale = Tier == IslandMotionTier.Lite ? LiteTimeScale : 1.0;
        _reveal.Advance(dt * scale);
        _expand.Advance(dt * scale);
        _breathe.Advance(dt * scale);

        // 尺寸目标按"当前 expand 的插值"推进：尺寸变化因此也是有弹簧的（不是瞬间跳变）。
        if (_sizeKnown)
        {
            SyncSizeTargets(instant: Tier == IslandMotionTier.Off);
        }

        _width.Advance(dt * scale);
        _height.Advance(dt * scale);

        if (Tier == IslandMotionTier.Full && Visible && _reveal.Value > 0.5)
        {
            PulsePhase += dt * PulseHz;
            if (PulsePhase >= 1.0)
            {
                PulsePhase -= Math.Floor(PulsePhase);
            }
        }
    }

    /// <summary>
    /// 计算当前帧姿态。
    /// </summary>
    /// <param name="attachY">菜单栏下沿（窗口内坐标，DIP）——岛的顶边永远贴它。</param>
    /// <param name="canvasWidth">窗口画布宽度（DIP）：用于水平居中，不含左偏移。</param>
    /// <param name="collapsed">收起态尺寸（实测文本宽 + 内边距）。</param>
    /// <param name="expanded">展开态尺寸（收起态 + 细节行高）。</param>
    public IslandPose ComputePose(double attachY, double canvasWidth, IslandSize collapsed, IslandSize expanded)
    {
        var reveal = Clamp01(_reveal.Value);
        var expand = Clamp01(_expand.Value);
        var eased = SmoothStep(reveal);
        var breathe = Math.Clamp(_breathe.Value, -1.0, 1.0);

        // 尺寸记下来当状态用（换内容 → 目标尺寸变了 → 由尺寸弹簧补间过去）。
        _sizeCollapsed = collapsed;
        _sizeExpanded = expanded;
        _sizeKnown = true;
        if (!_sizeReady)
        {
            SyncSizeTargets(instant: true); // 首次调用直接置位：不从 0 弹出来
        }

        // 显现：宽度先到、高度随显现进度生长，同时上浮到菜单栏下沿（水滴吸附过程）。
        // 宽度高度取尺寸弹簧当前值（而非目标值）——这一层补间让"换内容/收成休眠"平滑生长。
        var width = Math.Max(2.0, _width.Value * (0.88 + 0.12 * eased) + breathe * 2.0);
        var height = Math.Max(2.0, _height.Value * eased);
        // 显现：水滴从下方浮上来吸附到菜单栏下沿；收出：留在原地被吸薄（不向下掉）。
        var gap = _retracting ? 0.0 : (1.0 - eased) * RevealTravel;

        var left = (canvasWidth - width) / 2.0;
        var top = attachY + gap;
        var radius = Math.Min(height / 2.0, 13.0 + 3.0 * expand);

        // 肩部凹角：附着时 9（像从菜单栏里长出来）；脱离越大肩部越宽 → 颈部越细（表面张力）。
        var shoulder = Math.Clamp(9.0 + gap * 1.4, 9.0, width * 0.34);

        // 内容等到"高度已容得下头部行"才淡入：形变过程中不出现溢出轮廓的文字（规格 §9 红线）。
        // 判据用几何高度而不是时间进度——这样任何档位（含瞬切）下都不会出现文字露在形状外。
        const double contentStartHeight = 20.0;
        const double contentFullHeight = 26.0;
        var content = Clamp01((height - contentStartHeight) / (contentFullHeight - contentStartHeight));
        return new IslandPose(
            AttachY: attachY,
            Left: left,
            Top: top,
            Width: width,
            Height: height,
            CornerRadius: radius,
            Shoulder: shoulder,
            Gap: gap,
            Reveal: reveal,
            Expand: expand,
            ContentOpacity: content,
            DetailOpacity: Clamp01((expand - 0.25) / 0.5),
            Pulse: PulsePhase);
    }

    private static double Clamp01(double value) => value < 0 ? 0 : value > 1 ? 1 : value;

    /// <summary>
    /// 把尺寸弹簧的目标同步到"当前 expand 对应的插值尺寸"。
    /// <paramref name="instant"/> = true 时直接置位（首次初始化与"关闭动效"档）。
    /// </summary>
    private void SyncSizeTargets(bool instant)
    {
        var e = Clamp01(_expand.Value);
        var width = _sizeCollapsed.Width + (_sizeExpanded.Width - _sizeCollapsed.Width) * e;
        var height = _sizeCollapsed.Height + (_sizeExpanded.Height - _sizeCollapsed.Height) * e;

        if (instant || !_sizeReady)
        {
            _width.Reset(width);
            _height.Reset(height);
            _sizeReady = true;
            return;
        }

        _width.Target = width;
        _height.Target = height;
    }

    private static double SmoothStep(double t) => t * t * (3.0 - 2.0 * t);
}
