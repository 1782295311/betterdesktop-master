namespace BetterDesktop.Shell.Convert.Contracts;

// 转换事件载荷（IEventBus convert/started、finished、failed；字段与旧契约保持兼容，仅扩 batch）。
public sealed record ConvertEventPayload(
    string Source,
    string? Target,
    string Engine,
    string? Error,
    long ElapsedMs);

// 批量转换汇总事件载荷（IEventBus convert/batch-finished；红线 13：部分成功不得当全成功）。
public sealed record ConvertBatchEventPayload(
    int Total,
    int Succeeded,
    int Failed,
    string Target,
    long ElapsedMs);

// 转换进度事件载荷（IEventBus convert/progress；同一契约供给两个消费对象：
//   通知中心 = shell-menu-bar NotificationCenterWindow（活动区 + 阶段徽标）；
//   灵动岛 = 组件按本契约接入（计划 §12 O7：本次只供给契约，不建组件）。
// Phase 序列：running → verifying → publishing → finalizing（与 convert-engine NDJSON 运行契约一致）；
// Percent = null 表示阶段推进（禁止假精确——引擎无真实百分比时不得编造数字）。
public sealed record ConvertProgressEventPayload(
    string Source,
    string? Target,
    string Engine,
    int? Percent,
    string Phase,
    long ElapsedMs);
