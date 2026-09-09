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

    /// <summary>(path,target) 去重（沿用旧 InFlight，键扩展为目标维度）。</summary>
    private static readonly HashSet<string> InFlight = new(StringComparer.OrdinalIgnoreCase);

    public ConversionService(EngineRegistry registry, IEventBus? events = null, ISettingsService? settings = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _events = events;
        _settings = settings;
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

        // 拆分（单 pdf 每页一文件；多产物单作业）
        if (targetFormat == "pdf-split")
        {
            var split = await RunMultiInputAsync(paths, ConversionMatrix.SplitPdf, ct);
            await EmitBatchAsync(1, split.Success ? 1 : 0, split.Success ? 0 : 1, "pdf", startedAt);
            return [split];
        }

        // 多输入操作（合并/合成；单输出单事件）
        if (paths.Count > 1 && targetFormat == "pdf")
        {
            if (ConversionMatrix.AllPdf(paths))
            {
                var result = await RunMultiInputAsync(paths, ConversionMatrix.MergePdf, ct);
                await EmitBatchAsync(1, result.Success ? 1 : 0, result.Success ? 0 : 1, "pdf", startedAt);
                return [result];
            }
            if (ConversionMatrix.AllImages(paths))
            {
                var result = await RunMultiInputAsync(paths, ConversionMatrix.ComposePdf, ct);
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
        var target = isEncrypt ? ConversionMatrix.EncryptPdf : ConversionMatrix.DecryptPdf;
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

            var engine = new Services.Engines.PdfSecurityEngine(password);
            var directory = Path.GetDirectoryName(input)!;
            var tempDir = Path.Combine(directory, ConvertServiceConstants.TempDirPrefix
                + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var job = new ConversionJob([input], target, tempDir);
                var products = await RunVerifiedAsync(engine, job, ct);
                // 2026-09-07：加密/解密就地替换（用户口径：不产生新文件）。
                // 产物在 TempDir 完整生成后覆盖原文件；任一环节失败（含目标被占用）
                // 原文件保持不动（TempDir 由 finally 清理）。
                var product = products[0];
                File.Move(product, input, overwrite: true);

                var elapsed = ElapsedMs(startedAt);
                DiagnosticLog.Trace("shell-convert", $"convert result=ok input={input} target=in-place engine={engine.Name} elapsed={elapsed}ms");
                await EmitAsync("convert/finished", input, engine.Name, input, elapsed);
                return [new ConversionResult(input, true, input, ConvertError.None, engine.Name, elapsed)];
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); }
                catch { /* 清理失败不阻断（M10） */ }
            }
        }
        catch (Exception ex)
        {
            var elapsed = ElapsedMs(startedAt);
            DiagnosticLog.Trace("shell-convert", $"convert result=failed input={input} error={ex.Message} elapsed={elapsed}ms");
            await EmitAsync("convert/failed", input, "pdf-security", ex.Message, elapsed);
            return [new ConversionResult(input, false, null,
                ex is ConvertException ce ? ce.Error : ConvertError.ConversionFailed,
                "pdf-security", elapsed, ex.Message)];
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

        var target = ConversionMatrix.Find(Path.GetExtension(input), format);
        var engineName = target?.Prefer.ToString() ?? "-";
        try
        {
            await EmitAsync("convert/started", input, engineName, null, 0);
            return await ExecuteAsync(input, target!, engineName, startedAt, ct);
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

    private async Task<ConversionResult> ExecuteAsync(
        string input, ConversionTarget? target, string engineName, long startedAt, CancellationToken ct)
    {
        // 输入校验（pdf-edit-safe-output 红线 1：非空/无\0/存在/矩阵登记）
        ValidateInput(input);
        if (target is null)
        {
            throw new ConvertException(ConvertError.InputInvalid,
                $"不支持的类型或目标: {Path.GetExtension(input)} → {engineName}");
        }

        var engine = await ResolveWithDownloadAsync([input], target, ct)
            ?? throw new ConvertException(ConvertError.EngineMissing,
                "内置引擎未就绪（未找到可用的转换引擎）——这不是文件错误");

        var directory = Path.GetDirectoryName(input)!;
        var tempDir = Path.Combine(directory, ConvertServiceConstants.TempDirPrefix
            + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var job = new ConversionJob([input], target, tempDir);
            var products = await RunVerifiedAsync(engine, job, ct);
            var outputs = PublishAll(products, Path.GetFileNameWithoutExtension(input), target.Format, [input]);

            var elapsed = ElapsedMs(startedAt);
            DiagnosticLog.Trace("shell-convert", $"convert result=ok input={input} target={outputs[0]} engine={engine.Name} elapsed={elapsed}ms");
            await EmitAsync("convert/finished", input, engine.Name, outputs[0], elapsed);

            // P1-D 操作记忆：按源扩展记录上次目标（子菜单置顶）
            try
            {
                _settings?.Set(ConvertServiceConstants.LastTargetKey(Path.GetExtension(input)), target.Format);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Trace("shell-convert", $"操作记忆写入失败(忽略): {ex.Message}");
            }

            return new ConversionResult(input, true, outputs[0], ConvertError.None, engine.Name, elapsed);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch { /* 清理失败不阻断（M10） */ }
        }
    }

    /// <summary>多输入作业（合并/合成）：单输出；源集校验 + 输出≠任一输入。</summary>
    private async Task<ConversionResult> RunMultiInputAsync(IReadOnlyList<string> paths, ConversionTarget target, CancellationToken ct)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var key = string.Join("|", paths) + "|" + target.Filter;
        if (!InFlight.Add(key))
        {
            return new ConversionResult(paths[0], false, null, ConvertError.InputInvalid, "-", 0, "已在转换中");
        }

        try
        {
            await EmitAsync("convert/started", paths[0], target.Prefer.ToString(), null, 0);
            foreach (var path in paths)
            {
                ValidateInput(path);
            }
            var engine = await ResolveWithDownloadAsync(paths, target, ct)
                ?? throw new ConvertException(ConvertError.EngineMissing, "内置引擎未就绪（未找到可用的转换引擎）——这不是文件错误");

            var directory = Path.GetDirectoryName(paths[0])!;
            var tempDir = Path.Combine(directory, ConvertServiceConstants.TempDirPrefix
                + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var job = new ConversionJob(paths, target, tempDir);
                var products = await RunVerifiedAsync(engine, job, ct);
                var stem = Path.GetFileNameWithoutExtension(paths[0]);
                var suffix = target.Filter switch
                {
                    ConversionTarget.MergePdfMarker => "（合并）",
                    ConversionTarget.ComposePdfMarker => "（合成）",
                    _ => string.Empty, // 拆分：多产物经 PublishAll 自动 stem-N 命名
                };
                var outputs = PublishAll(products, stem + suffix, target.Format, paths);

                var elapsed = ElapsedMs(startedAt);
                DiagnosticLog.Trace("shell-convert", $"convert result=ok sources={paths.Count} target={outputs[0]} engine={engine.Name} elapsed={elapsed}ms");
                await EmitAsync("convert/finished", paths[0], engine.Name, outputs[0], elapsed);
                return new ConversionResult(paths[0], true, outputs[0], ConvertError.None, engine.Name, elapsed);
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); }
                catch { /* 清理失败不阻断（M10） */ }
            }
        }
        catch (Exception ex)
        {
            var elapsed = ElapsedMs(startedAt);
            await EmitAsync("convert/failed", paths[0], target.Prefer.ToString(), ex.Message, elapsed);
            return new ConversionResult(paths[0], false, null, ex is ConvertException ce ? ce.Error : ConvertError.ConversionFailed,
                target.Prefer.ToString(), elapsed, ex.Message);
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

    /// <summary>
    /// 解析引擎；Pandoc/FFmpeg 缺失时尝试一次按需下载（P3 dependency-on-demand；
    /// 校验和未配置时下载器显式拒绝——诚实不静默），下载成功重新解析。
    /// </summary>
    private async Task<IConversionEngine?> ResolveWithDownloadAsync(
        IReadOnlyList<string> sources, ConversionTarget target, CancellationToken ct)
    {
        var engine = _registry.Resolve(sources, target);
        if (engine is not null)
        {
            return engine;
        }
        foreach (var kind in EngineRegistry.CandidatesOf(target))
        {
            if (kind is not (EngineKind.Pandoc or EngineKind.Ffmpeg))
            {
                continue;
            }
            if (_registry.FindEngine(kind) is not IDownloadableEngine downloadable)
            {
                continue;
            }
            if (await downloadable.EnsureDownloadedAsync(ct))
            {
                return _registry.Resolve(sources, target);
            }
        }
        return null;
    }

    /// <summary>执行 + 回读验证（红线 12：产物存在且非空；失败恰好重试一次，再失败按 ConversionFailed 上报）。</summary>
    private static async Task<IReadOnlyList<string>> RunVerifiedAsync(IConversionEngine engine, ConversionJob job, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var products = await engine.RunAsync(job, ct);
            if (products.Count > 0 && products.All(p => File.Exists(p) && new FileInfo(p).Length > 0))
            {
                return products;
            }
            if (attempt >= 2)
            {
                throw new ConvertException(ConvertError.ConversionFailed,
                    $"{engine.Name} 产物验证失败（重试一次后仍无效）");
            }
            DiagnosticLog.Trace("shell-convert", $"{engine.Name} 产物验证失败，自动重试一次");
        }
    }

    /// <summary>
    /// 原子发布（红线 3/13）：临时产物 → 同卷 Move；单产物 = 原名+新扩展名；
    /// 多产物 = 原名-1..N；已存在自动 (2)(3) 序号，永不覆盖；输出不得等于任一输入。
    /// </summary>
    internal static IReadOnlyList<string> PublishAll(
        IReadOnlyList<string> products, string baseName, string targetFormat, IReadOnlyList<string> inputs)
    {
        var directory = Path.GetDirectoryName(inputs[0])!;
        var outputs = new List<string>(products.Count);
        var inputSet = new HashSet<string>(
            inputs.Select(p => Path.GetFullPath(p)), StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < products.Count; i++)
        {
            var name = products.Count > 1 ? $"{baseName}-{i + 1}" : baseName;
            var target = UniqueTarget(directory, name, targetFormat);
            if (inputSet.Contains(Path.GetFullPath(target)))
            {
                throw new ConvertException(ConvertError.OutputFailed, "输出路径与输入文件冲突，已拒绝发布");
            }
            File.Move(products[i], target); // 同卷 Move = 原子发布
            outputs.Add(target);
        }
        return outputs;
    }

    /// <summary>重名序号（不覆盖，用户拍板）：name.ext → "name (2).ext"、"name (3).ext"…</summary>
    internal static string UniqueTarget(string directory, string name, string extension)
    {
        var target = Path.Combine(directory, name + "." + extension);
        for (var i = 2; File.Exists(target); i++)
        {
            target = Path.Combine(directory, $"{name} ({i}).{extension}");
        }
        return target;
    }
}
