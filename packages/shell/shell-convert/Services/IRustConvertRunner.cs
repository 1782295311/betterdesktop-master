// BetterDesktop.Shell.Convert — Rust 转换引擎接口（测试可打桩）
using BetterDesktop.Shell.Convert.Contracts;

namespace BetterDesktop.Shell.Convert.Services;

/// <summary>
/// Rust 转换引擎接口（S9 后执行核心全在 Rust 侧；接口化仅为测试可打桩，
/// 生产实现永远是 RustConvertRunner，一次调用一个进程）。
/// </summary>
public interface IRustConvertRunner
{
    /// <summary>
    /// 执行一次转换：stdin JSON 请求 → NDJSON 事件流 → ConversionResult。
    /// </summary>
    Task<ConversionResult> RunAsync(
        IReadOnlyList<string> paths,
        string targetFormat,
        string? password,
        string fallbackEngine,
        long startedAt,
        CancellationToken ct);
}
