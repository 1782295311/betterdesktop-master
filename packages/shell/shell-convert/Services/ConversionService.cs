using System.Diagnostics;
using System.IO;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.Convert.Contracts;
using BetterDesktop.Shell.Convert.Services.Engines;
using BetterDesktop.Shell.Settings.Contracts;

namespace BetterDesktop.Shell.Convert.Services;

/// <summary>
/// 泛化转换服务（替代 DocumentConversionService；安全输出照 pdf-edit-safe-output）：
/// 输入校验 → 引擎写同卷临时目录 → 回读验证（失败恰好重试一次，红线 12）→ File.Move 原子发布
/// → 目标已存在自动 (2)(3) 序号永不覆盖 → 输出路径绝不等于任一输入（红线 3）。
/// 多选批量逐文件出结果事件，末尾 convert/batch-finished 带计数（红线 13：部分成功不得当全成功）。
/// </summary>
public sealed class ConversionService
{
    private readonly EngineRegistry _registry;
    private readonly IEventBus? _events;
    private readonly ISettingsService? _settings;
    private readonly IRustConvertRunner _rustRunner;

    /// <summary>(path,target) 去重（沿用旧 InFlight，键扩展为目标维度）。</summary>
    private static readonly HashSet<string> InFlight = new(StringComparer.OrdinalIgnoreCase);

    public ConversionService(EngineRegistry registry, IEventBus? events = null, ISettingsService? settings = null, IRustConvertRunner? rustRunner = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _events = events;
        _settings = settings;
        _rustRunner = rustRunner ?? new RustConvertRunner(events);
    }

    /// <summary>
    /// 转换主入口：paths.Count &gt; 1 且全 pdf/全图片且目标 pdf 时走合并/合成多输入作业；
    /// 其余逐文件批量（每文件独立结果，一项失败不影响其他）。
    /// </summary>
    public async Task<IReadOnlyList<ConversionResult>> ConvertAsync(
        IReadOnlyList<string> paths, string targetFormat, CancellationToken ct = default)
    {
        if (paths is not { Count: > 0 })
        {
            return [new ConversionResult(string.Empty, false, null, ConvertError.InputInvalid, "-", 0, "输入为空")];
        }

        var startedAt = Stopwatch.GetTimestamp();

        // 拆分（单 pdf 每页一文件；多产物单作业）——S9：交 Rust run（特殊目标 pdf-split）
        if (targetFormat == "pdf-split")
        {
            var split = await RunRustAsync(paths, targetFormat, startedAt, ct);
            await EmitBatchAsync(1, split.Success ? 1 : 0, split.Success ? 0 : 1, "pdf", startedAt);
            return [split];
        }

        // 多输入操作（合并/合成；单输出单事件）——S9：交 Rust run（特殊目标 pdf-merge/pdf-compose）
        if (paths.Count > 1 && targetFormat == "pdf")
        {
            if (ConversionMatrix.AllPdf(paths))
            {
                var result = await RunRustAsync(paths, "pdf-merge", startedAt, ct);
                await EmitBatchAsync(1, result.Success ? 1 : 0, result.Success ? 0 : 1, "pdf", startedAt);
                return [result];
            }
            if (ConversionMatrix.AllImages(paths))
            {
                var result = await RunRustAsync(paths, "pdf-compose", startedAt, ct);
                await EmitBatchAsync(1, result.Success ? 1 : 0, result.Success ? 0 : 1, "pdf", startedAt);
                return [result];
            }
        }

        // 逐文件批量
        var results = new List<ConversionResult>(paths.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (!seen.Add(path))
            {
                continue; // 批量内同文件不重复入队（计划 §9 并发）
            }
            results.Add(await ConvertOneAsync(path, targetFormat, ct));
        }
        await EmitBatchAsync(results.Count, results.Count(r => r.Success), results.Count(r => !r.Success), targetFormat, startedAt);
        return results;
    }

    /// <summary>多输入 Rust 作业门面：started 事件 + Rust run + 结果（失败事件由 runner 按 result 行发）。</summary>
    private async Task<ConversionResult> RunRustAsync(IReadOnlyList<string> paths, string targetFormat, long startedAt, CancellationToken ct)
    {
        var engineName = ConversionMatrix.Find(Path.GetExtension(paths[0]), targetFormat)?.Prefer.ToString() ?? "-";
        await EmitAsync("convert/started", paths[0], engineName, null, 0);
        return await _rustRunner.RunAsync(paths, targetFormat, null, engineName, startedAt, ct);
    }

    /// <summary>
    /// 带密码转换入口（2026-09-07：PDF 加密/解密）。目标格式串 pdf-encrypt / pdf-decrypt；
    /// 其余委托 ConvertAsync。单文件约束；密码为空 → InputInvalid（不触碰文件）。
    /// </summary>
    public async Task<IReadOnlyList<ConversionResult>> ConvertWithPasswordAsync(
        IReadOnlyList<string> paths, string targetFormat, string password, CancellationToken ct = default)
    {
        if (targetFormat is not ("pdf-encrypt" or "pdf-decrypt"))
        {
            return await ConvertAsync(paths, targetFormat, ct);
        }

        if (paths is not { Count: 1 })
        {
            return [new ConversionResult(paths.FirstOrDefault() ?? string.Empty, false, null,
                ConvertError.InputInvalid, "-", 0, "加密/解密仅支持单个 PDF 文件")];
        }

        var startedAt = Stopwatch.GetTimestamp();
        var input = paths[0];
        var isEncrypt = targetFormat == "pdf-encrypt";
        var engineName = isEncrypt ? "pdf-encrypt" : "pdf-decrypt";
        try
        {
            ValidateInput(input);
            if (!Path.GetExtension(input).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                throw new ConvertException(ConvertError.InputInvalid, "加密/解密仅支持 PDF 文件");
            }
            if (string.IsNullOrWhiteSpace(password))
            {
                throw new ConvertException(ConvertError.InputInvalid, "密码为空（已取消或未输入）");
            }

            // S9：加密/解密执行交 Rust 引擎（密码经 stdin JSON，就地替换语义由 Rust service 承担）。
            await EmitAsync("convert/started", input, engineName, null, 0);
            return [await _rustRunner.RunAsync([input], targetFormat, password, engineName, startedAt, ct)];
        }
        catch (Exception ex)
        {
            var elapsed = ElapsedMs(startedAt);
            DiagnosticLog.Trace("shell-convert", $"convert result=failed input={input} error={ex.Message} elapsed={elapsed}ms");
            await EmitAsync("convert/failed", input, engineName, ex.Message, elapsed);
            return [new ConversionResult(input, false, null,
                ex is ConvertException ce ? ce.Error : ConvertError.ConversionFailed,
                engineName, elapsed, ex.Message)];
        }
    }

    private async Task EmitBatchAsync(int total, int succeeded, int failed, string target, long startedAt)
    {
        try
        {
            if (_events is not null)
            {
                await _events.EmitAsync("convert/batch-finished",
                    new ConvertBatchEventPayload(total, succeeded, failed, target, ElapsedMs(startedAt)));
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell-convert", $"事件 convert/batch-finished 发布失败: {ex.Message}");
        }
    }

    private async Task<ConversionResult> ConvertOneAsync(string path, string format, CancellationToken ct)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var input = string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFullPath(path);
        var key = input + "|" + format;
        if (!InFlight.Add(key))
        {
            DiagnosticLog.Trace("shell-convert", $"已在转换中，忽略重复请求: {input} → {format}");
            return new ConversionResult(input, false, null, ConvertError.InputInvalid, "-", 0, "已在转换中");
        }

        var engineName = ConversionMatrix.Find(Path.GetExtension(input), format)?.Prefer.ToString() ?? "-";
        // 输入校验（矩阵找不到 = 不支持的类型，直接拒绝，不调用 Rust）
        if (ConversionMatrix.Find(Path.GetExtension(input), format) is null)
        {
            InFlight.Remove(key);
            await EmitAsync("convert/failed", input, engineName, $"不支持的类型或目标: {Path.GetExtension(input)} → {format}", 0);
            return new ConversionResult(input, false, null, ConvertError.InputInvalid, engineName, 0,
                $"不支持的类型或目标: {Path.GetExtension(input)} → {format}");
        }
        try
        {
            // S9：执行核心交 Rust 引擎（矩阵/引擎定位/临时目录/原子发布/超时杀树全在 Rust 侧；事件契约不变）。
            await EmitAsync("convert/started", input, engineName, null, 0);
            return await _rustRunner.RunAsync([input], format, null, engineName, startedAt, ct);
        }
        catch (Exception ex)
        {
            var elapsed = ElapsedMs(startedAt);
            DiagnosticLog.Trace("shell-convert", $"convert result=failed input={input} error={ex.Message} elapsed={elapsed}ms");
            await EmitAsync("convert/failed", input, engineName, ex.Message, elapsed);
            return new ConversionResult(input, false, null, ex is ConvertException ce ? ce.Error : ConvertError.ConversionFailed,
                engineName, elapsed, ex.Message);
        }
        finally
        {
            InFlight.Remove(key);
        }
    }

    private static long ElapsedMs(long timestamp) => Stopwatch.GetElapsedTime(timestamp).Milliseconds;

    private async Task EmitAsync(string name, string source, string engine, string? target, long elapsed, string? error = null)
    {
        try
        {
            if (_events is not null)
            {
                await _events.EmitAsync(name, new ConvertEventPayload(source, target, engine, error, elapsed));
            }
        }
        catch (Exception ex)
        {
            // 事件通知失败不阻断转换结果（M10）
            DiagnosticLog.Trace("shell-convert", $"事件 {name} 发布失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 进度阶段事件（convert/progress，供给通知中心与灵动岛）。
    /// Phase 序列 running → verifying → publishing → finalizing（与 convert-engine NDJSON 运行契约一致）；
    /// percent 恒为 null：C# 侧引擎无真实进度源，阶段推进即进度（禁止假精确）。
    /// </summary>
    private async Task EmitProgressAsync(string phase, string source, string engine, string? target, long startedAt)
    {
        try
        {
            if (_events is not null)
            {
                await _events.EmitAsync("convert/progress",
                    new ConvertProgressEventPayload(source, target, engine, null, phase, ElapsedMs(startedAt)));
            }
        }
        catch (Exception ex)
        {
            // 进度通知失败不阻断转换结果（M10）
            DiagnosticLog.Trace("shell-convert", $"事件 convert/progress 发布失败: {ex.Message}");
        }
    }

    /// <summary>输入校验（pdf-edit-safe-output 红线适配：非空/无\0/存在/矩阵登记）。</summary>
    private static string ValidateInput(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0'))
        {
            throw new ConvertException(ConvertError.InputInvalid, "输入路径非法");
        }
        if (!File.Exists(path))
        {
            throw new ConvertException(ConvertError.InputInvalid, "输入文件不存在");
        }
        if (!ConversionMatrix.IsConvertible(Path.GetExtension(path)))
        {
            throw new ConvertException(ConvertError.InputInvalid, $"不支持的类型: {Path.GetExtension(path)}");
        }
        return Path.GetFullPath(path);
    }

}
