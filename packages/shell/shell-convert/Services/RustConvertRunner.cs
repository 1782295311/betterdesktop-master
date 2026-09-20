// BetterDesktop.Shell.Convert — Rust 转换引擎门面（S9：ConversionService 执行核心切 Rust）
//
// 职责：spawn `convert-engine run`（stdin JSON 请求）→ 流式读 NDJSON（progress 阶段行 + result 末行）
//   → 转发 IEventBus convert/progress（Percent 真值来自 Rust，无精确值时 Rust 不发 percent 字段）
//   → 映射 ConversionResult（错误码六分类与 Rust ConvertError 同名枚举对齐）。
// 红线（local-engine-orchestration / pdf-edit-safe-output 移植）：
//   - 密码经 stdin JSON 传入，绝不进 argv（进程参数可见性）；
//   - exe 定位：env BETTERDESKTOP_CONVERT_ENGINE → AppContext.BaseDirectory\convert-engine.exe；
//   - 超时/杀进程树由 Rust exec.rs 负责（TIMEOUT_MS/MEDIA_TIMEOUT_MS + taskkill /T /F），C# 不重复实现；
//   - 临时目录同卷/原子发布/输出≠输入/自清理全部由 Rust service.rs 承担，C# 门面不再触碰。

using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.Convert.Contracts;
using BetterDesktop.Shell.Convert.Services.Engines;

namespace BetterDesktop.Shell.Convert.Services;

/// <summary>Rust 转换引擎进程门面（一次调用一个进程，非常驻 IPC——保持故障域隔离）。</summary>
internal sealed class RustConvertRunner : IRustConvertRunner
{
    private readonly IEventBus? _events;

    public RustConvertRunner(IEventBus? events)
    {
        _events = events;
    }

    /// <summary>定位 convert-engine.exe（env 覆盖 → 宿主输出根）。</summary>
    public static string? LocateEngine()
    {
        var env = Environment.GetEnvironmentVariable("BETTERDESKTOP_CONVERT_ENGINE");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            return env;
        }
        var local = Path.Combine(AppContext.BaseDirectory, "convert-engine.exe");
        return File.Exists(local) ? local : null;
    }

    /// <summary>
    /// 执行一次转换：stdin JSON 请求 → NDJSON 事件流 → ConversionResult。
    /// startedAt 用于进度事件 elapsed；fallbackEngine 用于 started 事件引擎名（Rust result.engine 为准）。
    /// </summary>
    public async Task<ConversionResult> RunAsync(
        IReadOnlyList<string> paths,
        string targetFormat,
        string? password,
        string fallbackEngine,
        long startedAt,
        CancellationToken ct)
    {
        var source = paths[0];
        var exe = LocateEngine()
            ?? throw new ConvertException(ConvertError.EngineMissing,
                "Rust 转换引擎未部署（convert-engine.exe 缺失）——请运行发布脚本或检查安装");

        var psi = new ProcessStartInfo(exe, "run")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi)
            ?? throw new ConvertException(ConvertError.EngineCrashed, "转换引擎进程启动失败");

        // 密码经 stdin JSON（红线：绝不拼进 argv）。
        var request = new RustRunRequest(1, targetFormat, paths.ToArray(), password);
        await proc.StandardInput.WriteAsync(JsonSerializer.Serialize(request).AsMemory(), ct);
        proc.StandardInput.Close();

        // stderr 异步读（防 64KB 管道死锁）；stdout 逐行解析 NDJSON。
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        string? resultJson = null;
        try
        {
            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync(ct)) is not null)
            {
                if (line.Length == 0 || line[0] != '{')
                {
                    continue;
                }
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var kindProp))
                {
                    continue;
                }
                switch (kindProp.GetString())
                {
                    case "progress":
                        await ForwardProgressAsync(root, startedAt);
                        break;
                    case "result":
                        resultJson = line;
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* 取消清理 */ }
            throw;
        }

        // Rust 正常退出但没给 result 行 = 引擎异常崩溃（契约：result 末行必有）。
        if (resultJson is null)
        {
            await proc.WaitForExitAsync(ct);
            var stderr = await stderrTask;
            DiagnosticLog.Trace("shell-convert", $"convert-engine 无 result 行（exit={proc.ExitCode}）stderr={stderr}");
            throw new ConvertException(ConvertError.EngineCrashed,
                string.IsNullOrWhiteSpace(stderr) ? "转换引擎异常退出（无结果）" : stderr.Trim());
        }

        await proc.WaitForExitAsync(ct);
        _ = await stderrTask;

        return MapResult(resultJson, source, fallbackEngine, startedAt);
    }

    /// <summary>result 行 → ConversionResult + finished/failed 事件（错误码六分类与 Rust 同名枚举对齐）。</summary>
    private ConversionResult MapResult(string resultJson, string source, string fallbackEngine, long startedAt)
    {
        using var doc = JsonDocument.Parse(resultJson);
        var root = doc.RootElement;
        var ok = root.GetProperty("ok").GetBoolean();
        var engine = root.TryGetProperty("engine", out var eProp) ? eProp.GetString() ?? fallbackEngine : fallbackEngine;
        var elapsed = root.TryGetProperty("elapsed_ms", out var msProp) ? msProp.GetInt64() : ElapsedMs(startedAt);
        var message = root.TryGetProperty("message", out var mProp) ? mProp.GetString() : null;
        var errorCode = root.TryGetProperty("error", out var cProp) ? cProp.GetString() : null;
        var outputs = root.TryGetProperty("outputs", out var oProp)
            ? oProp.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Where(x => x.Length > 0).ToArray()
            : [];

        if (!ok)
        {
            var code = ParseErrorCode(errorCode, message);
            var finalMessage = message ?? (errorCode is null ? "转换失败" : $"转换失败（{errorCode}）");
            DiagnosticLog.Trace("shell-convert", $"convert result=failed input={source} error={code} {finalMessage} elapsed={elapsed}ms");
            _ = EmitAsync("convert/failed", source, engine, finalMessage, elapsed);
            return new ConversionResult(source, false, null, code, engine, elapsed, finalMessage);
        }

        var output = outputs.Length > 0 ? outputs[0] : null;
        DiagnosticLog.Trace("shell-convert", $"convert result=ok input={source} target={output ?? "?"} engine={engine} elapsed={elapsed}ms");
        _ = EmitAsync("convert/finished", source, engine, output, elapsed);
        return new ConversionResult(source, true, output, ConvertError.None, engine, elapsed);
    }

    /// <summary>progress 行 → convert/progress 事件（Percent 真值；无 percent 字段 = 阶段推进，不编造）。</summary>
    private Task ForwardProgressAsync(JsonElement root, long startedAt)
    {
        try
        {
            if (_events is null)
            {
                return Task.CompletedTask;
            }
            var source = root.TryGetProperty("source", out var sProp) ? sProp.GetString() ?? string.Empty : string.Empty;
            var target = root.TryGetProperty("target", out var tProp) ? tProp.GetString() : null;
            var engine = root.TryGetProperty("engine", out var eProp) ? eProp.GetString() ?? "-" : "-";
            var phase = root.TryGetProperty("phase", out var pProp) ? pProp.GetString() ?? "running" : "running";
            int? percent = root.TryGetProperty("percent", out var pcProp) ? pcProp.GetInt32() : null;
            var elapsed = root.TryGetProperty("elapsed_ms", out var msProp) ? msProp.GetInt64() : ElapsedMs(startedAt);
            return _events.EmitAsync("convert/progress",
                new ConvertProgressEventPayload(source, target, engine, percent, phase, elapsed));
        }
        catch (Exception ex)
        {
            // 进度通知失败不阻断转换（M10）
            DiagnosticLog.Trace("shell-convert", $"事件 convert/progress 转发失败: {ex.Message}");
            return Task.CompletedTask;
        }
    }

    private static long ElapsedMs(long timestamp) => Stopwatch.GetElapsedTime(timestamp).Milliseconds;

    /// <summary>Rust 错误码名（InputInvalid/EngineMissing/…）→ C# ConvertError；未知码按转换失败。</summary>
    private static ConvertError ParseErrorCode(string? code, string? message)
    {
        if (!string.IsNullOrWhiteSpace(code) && Enum.TryParse<ConvertError>(code, ignoreCase: false, out var parsed))
        {
            return parsed;
        }
        // 引擎进程自身启动失败（exe 存在但无法运行）在 Rust 契约里报 EngineCrashed
        return ConvertError.ConversionFailed;
    }

    private async Task EmitAsync(string name, string source, string engine, string? target, long elapsed)
    {
        try
        {
            if (_events is not null)
            {
                await _events.EmitAsync(name, new ConvertEventPayload(source, target, engine, null, elapsed));
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell-convert", $"事件 {name} 发布失败: {ex.Message}");
        }
    }

    /// <summary>stdin 请求体（与 Rust RunRequest 字段一一对应；version=1 对齐 BATCH_PROTOCOL_VERSION）。
    /// 属性名显式小写——System.Text.Json 默认 PascalCase，Rust serde 契约要求 version/target/paths/password。</summary>
    private sealed record RustRunRequest(
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("target")] string Target,
        [property: JsonPropertyName("paths")] string[] Paths,
        [property: JsonPropertyName("password"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Password);
}
