using System.Diagnostics;
using System.IO;
using BetterDesktop.Shell.Convert.Contracts;

namespace BetterDesktop.Shell.Convert.Services.Engines;

/// <summary>
/// 可按需下载引擎钩子（EngineRegistry 在 EngineMissing 时尝试一次；五要素见 EngineDownloader）。
/// </summary>
public interface IDownloadableEngine
{
    /// <summary>确保引擎就绪（本机缺失 → 下载-校验-解压）；false = 拒绝/失败（校验和未配置等）。</summary>
    Task<bool> EnsureDownloadedAsync(CancellationToken ct);
}

/// <summary>
/// pandoc 引擎（P3）：docx→md（gfm 高质量）、md→docx/epub/pptx。
/// 探测链：env BETTERDESKTOP_PANDOC_PATH → Program Files\Pandoc → 受管 engines\pandoc；
/// 真实 --version 校验（dependency-on-demand 红线 2）。md 图片相对路径传 --resource-path（红线 7）。
/// </summary>
public sealed class PandocEngine : IConversionEngine, IDownloadableEngine
{
    public const string EnvVar = "BETTERDESKTOP_PANDOC_PATH";

    internal const int ProbeTimeoutMs = 2_500;

    private static volatile EngineAvailability? _probeCache;

    public EngineKind Kind => EngineKind.Pandoc;

    public string Name => "pandoc";

    public bool CanHandle(IReadOnlyList<string> sources, ConversionTarget target) =>
        sources.Count == 1
        && (target.Prefer == EngineKind.Pandoc || target.Fallback == EngineKind.Pandoc)
        && (Path.GetExtension(sources[0]).ToLowerInvariant(), target.Format) switch
        {
            (".docx", "md") => true,
            (".md", "docx") or (".md", "epub") or (".md", "pptx") => true,
            _ => false,
        };

    public EngineAvailability Probe() => _probeCache ?? EnsureProbed();

    public static EngineAvailability EnsureProbed()
    {
        var pandoc = LocatePandoc();
        if (pandoc is null)
        {
            _probeCache = EngineAvailability.Missing;
            return _probeCache;
        }
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = pandoc,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            process.StartInfo.ArgumentList.Add("--version");
            process.Start();
            var exited = process.WaitForExitAsync().Wait(ProbeTimeoutMs + 500);
            var stdout = exited ? process.StandardOutput.ReadToEnd() : string.Empty;
            var ok = exited && process.HasExited && stdout.Contains("pandoc.exe", StringComparison.Ordinal);
            _probeCache = ok ? EngineAvailability.Ok(stdout.Split('\n')[0].Trim()) : EngineAvailability.Missing;
        }
        catch (Exception ex)
        {
            _probeCache = new EngineAvailability(false, null, $"pandoc 探测失败: {ex.Message}");
        }
        return _probeCache;
    }

    public static void ResetProbeCache() => _probeCache = null;

    public static string? LocatePandoc()
    {
        var fromEnv = Environment.GetEnvironmentVariable(EnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
        {
            return fromEnv;
        }
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var candidates = new[]
        {
            Path.Combine(programFiles, "Pandoc", "pandoc.exe"),
            Path.Combine(AppContext.BaseDirectory, "engines", "pandoc", "pandoc.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>本机缺失 → EngineDownloader 按需下载（校验和未配置则拒绝，诚实不静默）。</summary>
    public async Task<bool> EnsureDownloadedAsync(CancellationToken ct)
    {
        if (Probe().Available)
        {
            return true;
        }
        try
        {
            await EngineDownloader.Shared.DownloadAsync(EngineDownloader.PandocSpec, ct);
            ResetProbeCache();
            return Probe().Available;
        }
        catch (Exception ex)
        {
            BetterDesktop.Kernel.Core.DiagnosticLog.Trace("shell-convert", $"pandoc 按需下载未完成: {ex.Message}");
            return false;
        }
    }

    public async Task<IReadOnlyList<string>> RunAsync(ConversionJob job, CancellationToken ct)
    {
        var pandoc = LocatePandoc()
            ?? throw new ConvertException(ConvertError.EngineMissing,
                "内置引擎未就绪（未找到 pandoc）——这不是文件错误");
        var input = job.PrimarySource;
        var format = job.Target.Format;
        var product = Path.Combine(job.TempDir, Path.GetFileNameWithoutExtension(input) + "." + format);

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = pandoc,
            UseShellExecute = false, // 红线 1：参数数组直传
            CreateNoWindow = true,
        };
        if (Path.GetExtension(input).Equals(".docx", StringComparison.OrdinalIgnoreCase))
        {
            process.StartInfo.ArgumentList.Add("-f");
            process.StartInfo.ArgumentList.Add("docx");
            process.StartInfo.ArgumentList.Add("-t");
            process.StartInfo.ArgumentList.Add("gfm"); // docx→md 高质量（GitHub flavored）
        }
        process.StartInfo.ArgumentList.Add(input);
        process.StartInfo.ArgumentList.Add("-o");
        process.StartInfo.ArgumentList.Add(product);
        if (Path.GetExtension(input).Equals(".md", StringComparison.OrdinalIgnoreCase))
        {
            // 红线 7：md 相对图片按源目录解析
            process.StartInfo.ArgumentList.Add("--resource-path=" + Path.GetDirectoryName(input));
        }

        process.Start();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ConvertServiceConstants.TimeoutMs);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
            throw new ConvertException(ConvertError.Timeout, $"pandoc 超时（{ConvertServiceConstants.TimeoutMs}ms）");
        }
        if (process.ExitCode != 0 || !File.Exists(product))
        {
            throw new ConvertException(ConvertError.ConversionFailed, $"pandoc 退出码 {process.ExitCode}，产物缺失");
        }
        return [product];
    }
}
