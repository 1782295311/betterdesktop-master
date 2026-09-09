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
