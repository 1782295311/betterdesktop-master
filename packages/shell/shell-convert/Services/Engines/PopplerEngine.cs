using System.Diagnostics;
using System.IO;
using BetterDesktop.Shell.Convert.Contracts;

namespace BetterDesktop.Shell.Convert.Services.Engines;

/// <summary>
/// Poppler 引擎（P2）：pdftoppm（PDF→png/jpg，多页多产物）+ pdftotext（PDF→txt，UTF-8）。
/// 探测照 dependency-on-demand 红线 2：真实执行 -v 校验（版本号在 stderr），文件存在不算命中。
/// </summary>
public sealed class PopplerEngine : IConversionEngine
{
    /// <summary>pdftoppm 路径环境变量覆盖（BETTERDESKTOP_POPPLER_PATH）。</summary>
    public const string EnvVar = "BETTERDESKTOP_POPPLER_PATH";

    internal const int ProbeTimeoutMs = 2_500;

    private static volatile EngineAvailability? _probeCache;

    public EngineKind Kind => EngineKind.Poppler;

    public string Name => "poppler";

    public bool CanHandle(IReadOnlyList<string> sources, ConversionTarget target) =>
        sources.Count == 1
        && Path.GetExtension(sources[0]).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
        && (target.Prefer == EngineKind.Poppler || target.Fallback == EngineKind.Poppler);

    public EngineAvailability Probe() => _probeCache ?? EnsureProbed();

    /// <summary>真实执行 pdftoppm -v 校验并缓存（dependency-on-demand 缓存失效 = ResetProbeCache）。</summary>
    public static EngineAvailability EnsureProbed()
    {
        var pdftoppm = LocatePdftoppm();
        if (pdftoppm is null)
        {
            _probeCache = EngineAvailability.Missing;
            return _probeCache;
        }
        try
        {
            var (code, stdout, stderr) = RunCapture(pdftoppm, ["-v"], ProbeTimeoutMs);
            var version = (stdout + stderr).Trim();
            var ok = code == 0 || version.Contains("pdftoppm version", StringComparison.Ordinal); // pdftoppm 版本走 stderr
            _probeCache = ok
                ? EngineAvailability.Ok(version.Split('\n')[0].Trim())
                : new EngineAvailability(false, null, "pdftoppm -v 未通过");
        }
        catch (Exception ex)
        {
            _probeCache = new EngineAvailability(false, null, $"pdftoppm 探测失败: {ex.Message}");
        }
        return _probeCache;
    }

    public static void ResetProbeCache() => _probeCache = null;

    /// <summary>定位 pdftotext（Poppler 套件内；TextPdfEngine 复用同一套件，缺则引擎缺失）。</summary>
    public static string? LocatePdftotext()
    {
        var pdftoppm = LocatePdftoppm();
        if (pdftoppm is null)
        {
            return null;
        }
        var candidate = Path.Combine(Path.GetDirectoryName(pdftoppm)!, "pdftotext.exe");
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>定位 pdftoppm（env → Program Files → 受管 engines 目录）。</summary>
    public static string? LocatePdftoppm()
    {
        var fromEnv = Environment.GetEnvironmentVariable(EnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
        {
            return fromEnv;
        }
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var candidates = new[]
        {
            Path.Combine(programFiles, "poppler", "Library", "bin", "pdftoppm.exe"),
            Path.Combine(AppContext.BaseDirectory, "engines", "poppler", "Library", "bin", "pdftoppm.exe"),
            Path.Combine(AppContext.BaseDirectory, "engines", "poppler", "poppler", "Library", "bin", "pdftoppm.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    public async Task<IReadOnlyList<string>> RunAsync(ConversionJob job, CancellationToken ct)
    {
        var pdftoppm = LocatePdftoppm()
            ?? throw new ConvertException(ConvertError.EngineMissing,
                "内置引擎未就绪（未找到 Poppler）——这不是文件错误");
        var input = job.PrimarySource;
        var format = job.Target.Format;
        var name = Path.GetFileNameWithoutExtension(input);

        if (format is "png" or "jpg" or "tiff")
        {
            var outRoot = Path.Combine(job.TempDir, name);
            var fmtArg = format switch
            {
                "png" => "-png",
                "jpg" => "-jpeg",
                _ => "-tiff",
            };
            var (code, _, stderr) = await RunCaptureAsync(pdftoppm, ["-r", "150", fmtArg, input, outRoot], ct);
            // tiff 产物扩展名为 .tif（Poppler 约定）；用 tif* 通配兼容 .tif/.tiff
            var pattern = Path.GetFileNameWithoutExtension(outRoot) + "-*." + (format == "tiff" ? "tif*" : format);
            var products = Directory.Exists(job.TempDir)
                ? Directory.GetFiles(job.TempDir, pattern).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList()
                : [];
            if (code != 0 || products.Count == 0)
            {
                throw new ConvertException(ConvertError.ConversionFailed,
                    $"pdftoppm 退出码 {code}，产物缺失 {Truncate(stderr)}");
            }
            return products;
        }

        if (format == "txt")
        {
            var pdftotext = LocatePdftotext()
                ?? throw new ConvertException(ConvertError.EngineMissing, "未找到 pdftotext.exe（Poppler 套件不完整）");
            var product = Path.Combine(job.TempDir, name + ".txt");
            var (code, _, stderr) = await RunCaptureAsync(pdftotext, ["-enc", "UTF-8", input, product], ct);
            if (code != 0 || !File.Exists(product))
            {
                throw new ConvertException(ConvertError.ConversionFailed, $"pdftotext 退出码 {code} {Truncate(stderr)}");
            }
            return [product];
        }

        throw new ConvertException(ConvertError.InputInvalid, $"Poppler 引擎不支持的转换目标: {format}");
    }

    private static string Truncate(string text) => text.Length > 200 ? text[..200] : text;

    private static (int Code, string Stdout, string Stderr) RunCapture(string exe, string[] args, int timeoutMs)
    {
        using var process = Build(exe, args);
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            process.WaitForExitAsync(cts.Token).Wait(timeoutMs + 500);
            Task.WaitAll([stdout, stderr], TimeSpan.FromSeconds(2));
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
        }
        catch (AggregateException)
        {
            // 读流超时（探测阶段不影响主流程）
        }
        return (process.HasExited ? process.ExitCode : -1,
            stdout.IsCompleted ? stdout.Result : string.Empty,
            stderr.IsCompleted ? stderr.Result : string.Empty);
    }

    private static async Task<(int Code, string Stdout, string Stderr)> RunCaptureAsync(
        string exe, string[] args, CancellationToken ct)
    {
        using var process = Build(exe, args);
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
            TryKill(process);
            throw new ConvertException(ConvertError.Timeout, $"Poppler 超时（{ConvertServiceConstants.TimeoutMs}ms）");
        }
    }

    private static Process Build(string exe, string[] args)
    {
        var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false, // 红线 1：参数数组直传
            CreateNoWindow = true,
            RedirectStandardOutput = true, // 2026-09-10：读流必须重定向（缺则 ReadToEndAsync 抛 InvalidOperationException，探测/转换全失败）
            RedirectStandardError = true,
        };
        foreach (var arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }
        return process;
    }

    private static void TryKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
    }
}
