// BetterDesktop.Shell.Convert — MOBI/AZW 电子书源引擎（2026-09-07 集成，对齐 flyingmouse textInput 的 mobi）
// calibre ebook-convert 子进程（免费开源，用户可选装）：→ epub/pdf/docx/txt（目标由输出扩展名决定）。
// 探测：文件存在 + 真实 --version（输出 "ebook-convert (calibre x.y)"）。缺则置灰 + 安装指引。

using System.Diagnostics;
using System.IO;
using BetterDesktop.Shell.Convert.Contracts;

namespace BetterDesktop.Shell.Convert.Services.Engines;

/// <summary>
/// MOBI 电子书引擎：mobi/azw/azw3/prc → epub/pdf/docx/txt。依赖 calibre（ebook-convert.exe）。
/// 诚实边界：不自研 MOBI 解析器（无成熟托管库）；装 calibre 即接入。
/// </summary>
public sealed class CalibreEngine : IConversionEngine
{
    public const string EnvVar = "BETTERDESKTOP_CALIBRE_PATH";

    /// <summary>MOBI 家族源扩展（Kindle 旧格式）。</summary>
    public static readonly IReadOnlySet<string> MobiExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mobi", ".azw", ".azw3", ".prc",
    };

    private static volatile EngineAvailability? _probeCache;

    public EngineKind Kind => EngineKind.Calibre;

    public string Name => "calibre";

    public bool CanHandle(IReadOnlyList<string> sources, ConversionTarget target) =>
        sources.Count == 1
        && MobiExtensions.Contains(Path.GetExtension(sources[0]))
        && (target.Prefer == EngineKind.Calibre || target.Fallback == EngineKind.Calibre)
        && target.Format is "epub" or "pdf" or "docx" or "txt";

    public EngineAvailability Probe() => _probeCache ?? EnsureProbed();

    /// <summary>真实执行 ebook-convert --version 校验并缓存（红线 2）。</summary>
    public static EngineAvailability EnsureProbed()
    {
        var ebookConvert = LocateEbookConvert();
        if (ebookConvert is null)
        {
            // 诚实边界：受管引擎存在但结构残缺（无 launcher.dll 亦无 app\bin 核心）→ 明确告知原因而非笼统"未安装"
            var bundledExe = Path.Combine(AppContext.BaseDirectory, "engines", "calibre", "ebook-convert.exe");
            if (File.Exists(bundledExe)
                && !File.Exists(Path.Combine(Path.GetDirectoryName(bundledExe)!, "calibre-launcher.dll"))
                && !Directory.Exists(Path.Combine(Path.GetDirectoryName(bundledExe)!, "app", "bin")))
            {
                _probeCache = new EngineAvailability(false, null,
                    "内置 calibre 引擎不完整（缺 calibre-launcher.dll 且缺 app\\bin 核心），视为未就绪——请安装完整 calibre 或移除 engines\\calibre 目录");
                return _probeCache;
            }
            _probeCache = EngineAvailability.Missing;
            return _probeCache;
        }
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = ebookConvert,
                UseShellExecute = false,
                CreateNoWindow = true,
                // 红线 6：探测输出必须重定向——否则 ReadToEnd 抛 InvalidOperationException（内置前从未运行未暴露）
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            process.StartInfo.ArgumentList.Add("--version");
            process.Start();
            var exited = process.WaitForExitAsync().Wait(5_000);
            var stdout = exited ? process.StandardOutput.ReadToEnd() : string.Empty;
            var ok = exited && process.HasExited
                && stdout.Contains("ebook-convert", StringComparison.OrdinalIgnoreCase)
                && stdout.Contains("calibre", StringComparison.OrdinalIgnoreCase);
            _probeCache = ok ? EngineAvailability.Ok(stdout.Trim()) : EngineAvailability.Missing;
        }
        catch (Exception ex)
        {
            _probeCache = new EngineAvailability(false, null, $"ebook-convert 探测失败: {ex.Message}");
        }
        return _probeCache;
    }

    /// <summary>
    /// 定位 ebook-convert.exe（env → 受管 engines\calibre → Program Files\Calibre2）。
    /// 完整性防御（2026-09-16 根因修复）：ebook-convert 需同目录 calibre-launcher.dll 才能启动；
    /// 找到 exe 但缺该 DLL（残缺内置引擎）→ 视为未安装（返回 null），**绝不运行残缺引擎**——
    /// 否则子进程 LoadLibrary("calibre-launcher.dll") 失败会弹"找不到指定的模块(126)"错误框（Agent 自启初始化实测复现）。
    /// </summary>
    public static string? LocateEbookConvert()
    {
        var fromEnv = Environment.GetEnvironmentVariable(EnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv) && IsPlausibleEngine(fromEnv))
        {
            return fromEnv;
        }
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var candidates = new[]
        {
            Path.Combine(programFiles, "Calibre2", "ebook-convert.exe"),
            Path.Combine(AppContext.BaseDirectory, "engines", "calibre", "ebook-convert.exe"),
        };
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate) && IsPlausibleEngine(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>
    /// 引擎完整性检查（2026-09-16 修订：兼容新旧两代结构）：
    /// - 旧版 calibre（&lt;=8.x）：顶层 exe 是启动器，依赖同目录 calibre-launcher.dll；
    /// - 新版 calibre（9.x+）：无 launcher.dll，核心在 app\bin（自包含 exe 直接运行，实测 9.14.0 可运行）。
    /// 两者任一存在即视为"结构完整、可安全运行"；否则（exe 外壳 + 无核心）视为残缺，绝不运行（避免 LoadLibrary 126 弹窗）。
    /// </summary>
    private static bool IsPlausibleEngine(string ebookConvertPath)
    {
        var dir = Path.GetDirectoryName(ebookConvertPath);
        if (dir is null) return false;
        return File.Exists(Path.Combine(dir, "calibre-launcher.dll"))
            || Directory.Exists(Path.Combine(dir, "app", "bin"));
    }

    public async Task<IReadOnlyList<string>> RunAsync(ConversionJob job, CancellationToken ct)
    {
        var ebookConvert = LocateEbookConvert()
            ?? throw new ConvertException(ConvertError.EngineMissing,
                "内置引擎未就绪（未找到 ebook-convert.exe）。安装 calibre（https://calibre-ebook.com）后重试——这不是文件错误");
        var input = job.PrimarySource;
        var format = job.Target.Format;
        var product = Path.Combine(job.TempDir,
            Path.GetFileNameWithoutExtension(input) + "." + format);

        // 目标格式由输出扩展名决定（ebook-convert 惯例）
        var (code, _, stderr) = await RunCaptureAsync(ebookConvert, [input, product], ct);
        if (code != 0 || !File.Exists(product))
        {
            throw new ConvertException(ConvertError.ConversionFailed,
                $"ebook-convert 退出码 {code} {Truncate(stderr)}");
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
            // 红线 6：子进程输出必须重定向——ReadToEndAsync 未重定向抛 InvalidOperationException（内置前从未运行未暴露）
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }
        process.Start();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ConvertServiceConstants.MediaTimeoutMs);
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
            throw new ConvertException(ConvertError.Timeout, $"ebook-convert 超时（{ConvertServiceConstants.MediaTimeoutMs}ms）");
        }
    }
}
