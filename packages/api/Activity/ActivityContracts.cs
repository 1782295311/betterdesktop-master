using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace BetterDesktop.Activity.Contracts;

/// <summary>活动优先级（仲裁唯一依据；数值越大越优先）。</summary>
public enum ActivityPriority
{
    Background = 0,
    Progress = 1,
    Notice = 2,
    Clipboard = 3,
    Media = 4,
    Attention = 5,
}

/// <summary>活动生命周期类型。</summary>
public enum ActivityKind
{
    /// <summary>到时自动收起（TTL 生效）。</summary>
    Transient,

    /// <summary>常驻直到显式 Complete/Dismiss（如正在播放）。</summary>
    Sticky,

    /// <summary>进度型（同 Id 再次 Post = 更新进度；TTL 生效）。</summary>
    Progress,
}

/// <summary>活动附带动作（胶囊上的按钮，如"打开" / "重试"）。</summary>
public sealed record ActivityAction(string Id, string Label, Func<Task>? Invoke);

/// <summary>
/// 活动条目（"程序的所有消息"统一进入胶囊的载体）。
/// </summary>
/// <param name="Id">稳定标识（同 Id 再次 Post = 更新，进度用）。</param>
/// <param name="Source">来源（剪贴板 / 转换 / 通知 …；同源合并按它折叠）。</param>
/// <param name="Kind">生命周期类型。</param>
/// <param name="Priority">优先级。</param>
/// <param name="Title">标题。</param>
/// <param name="Body">正文（可空）。</param>
/// <param name="IconPath">图标（可空）。</param>
/// <param name="Progress">进度 0..1（可空）。</param>
/// <param name="CreatedAt">创建时间（仲裁排序用）。</param>
/// <param name="TimeToLive">Transient/Progress 的自动收起时限。</param>
/// <param name="Actions">动作列表。</param>
/// <param name="MergeCount">
/// 同源合并计数（默认 1）：同 <c>Source</c> + 同 <c>Title</c> 的连续 Transient 事件折叠进同一条并 +1，
/// 用于"5 项转换完成"这类累计展示。契约面新增字段（岛文档 §3 记录为草图，合并规则要求计数有家）。
/// </param>
/// <param name="Failed">
/// 终态是否为失败（成功/失败都是"终态提示"，但呈现要区分：失败走警示色 + 单次抖动，不循环闪烁）。
/// 契约可加性：新增带默认值的字段，旧构造点零影响；呈现层据此选择字形与配色（不靠解析标题文本猜）。
/// </param>
public sealed record ActivityItem(
    string Id,
    string Source,
    ActivityKind Kind,
    ActivityPriority Priority,
    string Title,
    string? Body,
    string? IconPath,
    double? Progress,
    DateTimeOffset CreatedAt,
    TimeSpan TimeToLive,
    IReadOnlyList<ActivityAction> Actions,
    int MergeCount = 1,
    bool Failed = false);

/// <summary>
/// 活动服务（岛的"程序所有消息"统一入口 + 仲裁器）。
/// <para>
/// 仲裁规则（岛文档 §3）：优先级 <c>Attention &gt; Media &gt; Clipboard &gt; Notice &gt; Progress &gt; Background</c>，
/// 同优先级 <c>CreatedAt</c> 后到先显示；高优先级抢占低优先级，被抢占项回队列不丢失、优先级回落后续播；
/// <c>Sticky</c> 常驻直到显式 Complete/Dismiss；<c>Transient/Progress</c> 到 TTL 自动收起；
/// 同源连续事件折叠计数；全屏/游戏/DND 抑制时不弹出（进队列，退出后按优先级补播）。
/// </para>
/// <para>
/// 【变更通知与 ADR-002 D4】接口**不声明**跨程序集裸 C# event（D4 禁止）；
/// 变更通知由具体实现（shell-core `ActivityService`）的类级 <c>Changed</c> 事件提供，
/// 跨包消费（岛渲染 P3）届时走内核 IEventBus 桥接。Changed 节流：只在"当前活动"或队列顺序变化时触发；
/// 同 Id 的进度/内容更新**不触发**（订阅方按帧轮询 <see cref="Current"/>，避免高频刷新）；
/// 全量订阅者为零时零额外开销。
/// </para>
/// </summary>
public interface IActivityService
{
    /// <summary>发布/更新活动（同 Id = 更新）。</summary>
    void Post(ActivityItem item);

    /// <summary>完成（终态提示 resultText 由订阅方消费；当前项完成 → 从队列续播下一个）。</summary>
    void Complete(string id, string? resultText = null);

    /// <summary>忽略（与 Complete 同路径，无终态文本）。</summary>
    void Dismiss(string id);

    /// <summary>
    /// 抑制开关（全屏应用/游戏/DND）：开启时当前项退入队列不弹出，关闭后按优先级补播。
    /// </summary>
    void SetSuppressed(bool suppressed);

    /// <summary>当前占据胶囊的活动（仲裁结果）。</summary>
    ActivityItem? Current { get; }

    /// <summary>等待队列快照（优先级降序、同优先级 CreatedAt 降序；含被抢占项）。</summary>
    IReadOnlyList<ActivityItem> Queue { get; }
}

/// <summary>
/// 活动变更广播载荷（IEventBus 事件 <c>shell.activity/changed</c>；供岛等跨包表面消费）。
/// <para>
/// 用 record 而不是直接发 <see cref="ActivityItem"/>：<c>Current</c> 可为 null（无活动），
/// 而 IEventBus 的载荷约束是 <c>notnull</c>；同时把队列长度一并带上，订阅方不必回读服务。
/// </para>
/// </summary>
/// <param name="Current">当前占据胶囊的活动（无活动为 null）。</param>
/// <param name="QueueCount">等待队列长度（被抢占项 + 待播项）。</param>
public sealed record ActivityChangedNotice(ActivityItem? Current, int QueueCount);
