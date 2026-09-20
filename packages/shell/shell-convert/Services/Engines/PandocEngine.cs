using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
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
/// 真实 --version 校验（dependency-on-demand 红线 2），首行格式兼容旧 "pandoc.exe 2.0.1.1" 与新 "pandoc 3.6.4"。
/// pptx 能力门槛：pandoc 3.0+（2.x 早期 pptx writer 实测失败，2.0.1.1 exit=1 无产物——高亮即失败违反"高亮=成功"契约）。
/// md 图片相对路径传 --resource-path（红线 7）。
/// </summary>
public sealed class PandocEngine : IConversionEngine, IDownloadableEngine
{
    public const string EnvVar = "BETTERDESKTOP_PANDOC_PATH";

    internal const int ProbeTimeoutMs = 2_500;

    /// <summary>pptx writer 能力门槛：pandoc ≥ 3.0（2.0.1.1 实测 -t pptx exit=1 无产物）。</summary>
    internal static readonly Version PptxRequiredVersion = new(3, 0);

    /// <summary>
    /// pandoc 可读源扩展（2026-09-10 扩展：md/txt/log/html/htm/epub/docx/odt/rtf 均 pandoc 原生 reader；
    /// 2026-09-10 追加 .pptx：pandoc 3.x 原生 pptx reader，门槛同 SupportsPptx（3.0+），CanHandle 单独校验）。
    /// </summary>
    internal static readonly IReadOnlySet<string> ReadableSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown", ".txt", ".log", ".html", ".htm", ".epub", ".docx", ".odt", ".rtf", ".pptx",
    };

    /// <summary>
    /// pandoc 可写目标格式（-o 扩展名自动选 writer；pptx 走 SupportsPptx 版本门槛单独判定）。
    /// 2026-09-10 扩展：rtf/odt/tex/rst/org/wiki/adoc/textile/ipynb/db/man/context/texi/opendocument/plain。
    /// </summary>
    internal static readonly IReadOnlySet<string> WritableTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "md", "html", "txt", "docx", "epub", "rtf", "odt", "tex", "rst", "org", "wiki", "adoc",
        "textile", "ipynb", "db", "man", "context", "texi", "opendocument", "plain",
    };

    /// <summary>
    /// 产物扩展名 ≠ pandoc writer 名的映射（§12 解除：-o 推断不可靠的目标显式 -t；其余靠 -o 扩展名自动选 writer）。
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> WriterNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["tex"] = "latex",
            ["wiki"] = "mediawiki",
            ["adoc"] = "asciidoc",
            ["db"] = "docbook",
            ["texi"] = "texinfo",
            ["opendocument"] = "opendocument",
        };

    private static volatile EngineAvailability? _probeCache;

    /// <summary>pptx 能力（由最近一次探测的版本决定；未探测/未知 → false 保守置灰——能力诚实显隐，隐藏优先）。</summary>
    internal static volatile bool SupportsPptx;

    public EngineKind Kind => EngineKind.Pandoc;

    public string Name => "pandoc";

    public bool CanHandle(IReadOnlyList<string> sources, ConversionTarget target)
    {
        var ext = sources.Count == 1 ? Path.GetExtension(sources[0]).ToLowerInvariant() : null;
        return ext is not null
            && (target.Prefer == EngineKind.Pandoc || target.Fallback == EngineKind.Pandoc)
            && ReadableSources.Contains(ext)
            && (WritableTargets.Contains(target.Format) || (target.Format == "pptx" && SupportsPptx))
            && (ext != ".pptx" || SupportsPptx); // pptx reader 门槛 pandoc ≥ 3.0（2.x 无 pptx reader）
    }

    public EngineAvailability Probe() => _probeCache ?? EnsureProbed();

    public static EngineAvailability EnsureProbed()
    {
        var pandoc = LocatePandoc();
        if (pandoc is null)
        {
            SupportsPptx = false;
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
                RedirectStandardOutput = true, // 红线：校验输出必须重定向，否则 ReadToEnd 抛 InvalidOperationException
            };
            process.StartInfo.ArgumentList.Add("--version");
            process.Start();
            var exited = process.WaitForExitAsync().Wait(ProbeTimeoutMs + 500);
            var stdout = exited ? process.StandardOutput.ReadToEnd() : string.Empty;
            var versionLine = stdout.Split('\n')[0].Trim();
            var parsed = TryParseVersion(versionLine, out var version);
            var ok = exited && process.HasExited && parsed;
            if (parsed && exited && process.HasExited)
            {
                SupportsPptx = version >= PptxRequiredVersion;
                _probeCache = EngineAvailability.Ok(versionLine);
            }
            else
            {
                SupportsPptx = false;
                _probeCache = EngineAvailability.Missing;
            }
        }
        catch (Exception ex)
        {
            SupportsPptx = false;
            _probeCache = new EngineAvailability(false, null, $"pandoc 探测失败: {ex.Message}");
        }
        return _probeCache;
    }

    /// <summary>解析 --version 首行：兼容旧格式 "pandoc.exe 2.0.1.1" 与新格式 "pandoc 3.6.4"。</summary>
    internal static bool TryParseVersion(string firstLine, out Version version)
    {
        version = new Version();
        if (string.IsNullOrWhiteSpace(firstLine))
        {
            return false;
        }
        var m = Regex.Match(
            firstLine,
            @"^pandoc(?:\.exe)?\s+v?(\d+)\.(\d+)(?:\.(\d+)(?:\.(\d+))?)?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!m.Success)
        {
            return false;
        }
        version = new Version(
            int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),
            m.Groups[3].Success ? int.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture) : 0,
            m.Groups[4].Success ? int.Parse(m.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture) : 0);
        return true;
    }

    public static void ResetProbeCache()
    {
        _probeCache = null;
        SupportsPptx = false;
    }

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
            if (format == "md")
            {
                process.StartInfo.ArgumentList.Add("-t");
                process.StartInfo.ArgumentList.Add("gfm"); // docx→md 高质量（GitHub flavored）
            }
        }
        // 2026-09-10：产物扩展名 ≠ writer 名的目标显式 -t（tex→latex 等；其余靠 -o 扩展名自动选 writer）
        if (WriterNames.TryGetValue(format, out var writer))
        {
            process.StartInfo.ArgumentList.Add("-t");
            process.StartInfo.ArgumentList.Add(writer);
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
