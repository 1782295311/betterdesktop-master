// BetterDesktop.Shell.Convert — Tesseract OCR 引擎（2026-09-07 补全，对齐 flyingmouse 的 OCR 能力）
// 图片 → 识别文本（txt）。真实 --version 探测（dependency-on-demand 红线 2）；chi_sim+eng 优先，缺语言包回落 eng。

using System.Diagnostics;
using System.IO;
using BetterDesktop.Shell.Convert.Contracts;

namespace BetterDesktop.Shell.Convert.Services.Engines;

/// <summary>
/// Tesseract OCR 引擎：图片→txt。探测链 env BETTERDESKTOP_TESSERACT_PATH → Program Files\Tesseract-OCR
/// → 受管 engines\tesseract。语言：先 chi_sim+eng，非零退出回落 eng（诚实，不静默失败）。
/// </summary>
public sealed class TesseractEngine : IConversionEngine
{
    public const string EnvVar = "BETTERDESKTOP_TESSERACT_PATH";

    private static volatile EngineAvailability? _probeCache;

    public EngineKind Kind => EngineKind.Tesseract;

    public string Name => "tesseract";

    public bool CanHandle(IReadOnlyList<string> sources, ConversionTarget target) =>
        sources.Count == 1
        && ConversionMatrix.ImageExtensions.Contains(Path.GetExtension(sources[0]))
        && target.Format == "txt"
        && (target.Prefer == EngineKind.Tesseract || target.Fallback == EngineKind.Tesseract);

    public EngineAvailability Probe() => _probeCache ?? EnsureProbed();

    /// <summary>真实执行 tesseract --version 校验并缓存（红线 2：文件存在不算命中）。</summary>
    public static EngineAvailability EnsureProbed()
    {
        var tesseract = LocateTesseract();
        if (tesseract is null)
        {
            _probeCache = EngineAvailability.Missing;
            return _probeCache;
        }
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = tesseract,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            process.StartInfo.ArgumentList.Add("--version");
            process.Start();
            var exited = process.WaitForExitAsync().Wait(3_000);
            var stdout = exited ? process.StandardOutput.ReadToEnd() : string.Empty;
            var ok = exited && process.HasExited
                && stdout.Contains("tesseract", StringComparison.OrdinalIgnoreCase);
            _probeCache = ok ? EngineAvailability.Ok(stdout.Split('\n')[0].Trim()) : EngineAvailability.Missing;
        }
        catch (Exception ex)
        {
            _probeCache = new EngineAvailability(false, null, $"tesseract 探测失败: {ex.Message}");
        }
        return _probeCache;
    }

    public static void ResetProbeCache() => _probeCache = null;

    /// <summary>定位 tesseract.exe（env → Program Files → 受管 engines 目录）。</summary>
    public static string? LocateTesseract()
    {
        var fromEnv = Environment.GetEnvironmentVariable(EnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
        {
            return fromEnv;
        }
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var candidates = new[]
        {
            Path.Combine(programFiles, "Tesseract-OCR", "tesseract.exe"),
            Path.Combine(AppContext.BaseDirectory, "engines", "tesseract", "tesseract.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    public async Task<IReadOnlyList<string>> RunAsync(ConversionJob job, CancellationToken ct)
    {
        var tesseract = LocateTesseract()
            ?? throw new ConvertException(ConvertError.EngineMissing,
                "内置引擎未就绪（未找到 Tesseract-OCR）——这不是文件错误");
        var input = job.PrimarySource;
        var name = Path.GetFileNameWithoutExtension(input);
        var outBase = Path.Combine(job.TempDir, name + "-ocr");

        var (code, _, stderr) = await RunCaptureAsync(tesseract,
            [input, outBase, "-l", "chi_sim+eng", "--psm", "6"], ct);
        if (code != 0)
        {
            // 缺 chi_sim 语言包 → 回落 eng（诚实重试，不静默）
            var (code2, _, stderr2) = await RunCaptureAsync(tesseract,
                [input, outBase, "-l", "eng", "--psm", "6"], ct);
            if (code2 != 0)
            {
                throw new ConvertException(ConvertError.ConversionFailed,
                    $"tesseract 退出码 {code2} {Truncate(stderr + stderr2)}");
            }
        }

        var product = outBase + ".txt";
        if (!File.Exists(product))
        {
            throw new ConvertException(ConvertError.ConversionFailed, "tesseract 产物缺失（.txt）");
        }
        return [product];
    }

    private static string Truncate(string text) => text.Length > 200 ? text[..200] : text;

    private static async Task<(int Code, string Stdout, string Stderr)> RunCaptureAsync(
        string exe, string[] args, CancellationToken ct)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false, // 红线 1：参数数组直传
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }
        process.Start();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ConvertServiceConstants.TimeoutMs);
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(timeoutCts.Token);
            return (process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
            throw new ConvertException(ConvertError.Timeout, $"tesseract 超时（{ConvertServiceConstants.TimeoutMs}ms）");
        }
    }
}
