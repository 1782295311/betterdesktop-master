using System.Diagnostics;
using System.IO;
using BetterDesktop.Shell.Convert.Contracts;

namespace BetterDesktop.Shell.Convert.Services.Engines;

/// <summary>
/// LibreOffice soffice 引擎（从旧 DocumentConversionService 等价搬迁，filter 参数化）：
/// UseShellExecute=false + ArgumentList 逐个传参（execFile 等价，禁止 shell 拼串，红线 1）；
/// 120s 超时 + Kill 进程树（红线 2）；产物只写 TempDir，原子发布由服务层负责（红线 3）。
/// </summary>
public sealed class SofficeEngine : IConversionEngine
{
    /// <summary>探测超时（ms；--version 真实执行校验，dependency-on-demand 红线 2：文件存在不算）。</summary>
    internal const int ProbeTimeoutMs = 3_000;

    public EngineKind Kind => EngineKind.Soffice;

    public string Name => "soffice";

    public bool CanHandle(IReadOnlyList<string> sources, ConversionTarget target) =>
        sources.Count == 1
        && (target.Prefer == EngineKind.Soffice || target.Fallback == EngineKind.Soffice);

    public EngineAvailability Probe() => LocateSoffice() is not null
        ? EngineAvailability.Ok(_probeCache?.Version)
        : EngineAvailability.Missing;

    // —— 真实探测（P2：--version 执行校验） ——

    private static volatile EngineAvailability? _probeCache;

    /// <summary>
    /// 实时执行 soffice --version 校验并缓存（ConvertPlugin 启动静默预热；
    /// 菜单只读缓存快照零阻塞，未预热时回落 LocateSoffice 文件判定）。
    /// </summary>
    public static EngineAvailability EnsureProbed()
    {
        var cached = _probeCache;
        if (cached is not null)
        {
            return cached;
        }

        var path = LocateSoffice();
        if (path is null)
        {
            _probeCache = EngineAvailability.Missing;
            return _probeCache;
        }

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            process.StartInfo.ArgumentList.Add("--version");
            process.Start();
            using var cts = new CancellationTokenSource(ProbeTimeoutMs);
            var exited = process.WaitForExitAsync(cts.Token).Wait(ProbeTimeoutMs + 500);
            var version = exited ? process.StandardOutput.ReadToEnd().Trim() : string.Empty;
            var ok = exited && process.HasExited && process.ExitCode == 0
                     && version.Contains("LibreOffice", StringComparison.OrdinalIgnoreCase);
            _probeCache = ok
                ? EngineAvailability.Ok(version)
                : new EngineAvailability(false, null, exited ? "soffice --version 未通过" : "soffice --version 探测超时");
        }
        catch (Exception ex)
        {
            _probeCache = new EngineAvailability(false, null, $"soffice --version 探测失败: {ex.Message}");
        }
        return _probeCache;
    }

    /// <summary>重置探测缓存（引擎安装/卸载后显式调用；dependency-on-demand 缓存失效钩子）。</summary>
    public static void ResetProbeCache() => _probeCache = null;

    /// <summary>定位 soffice.exe（env 覆盖 → Program Files → 便携运行时；进程级缓存）。</summary>
    public static string? LocateSoffice() => ConvertEngineLocator.LocateSoffice();

    public async Task<IReadOnlyList<string>> RunAsync(ConversionJob job, CancellationToken ct)
    {
        var soffice = LocateSoffice()
            ?? throw new ConvertException(ConvertError.EngineMissing, "内置引擎未就绪（未找到 LibreOffice）——这不是文件错误");
        var product = await ConvertAsync(soffice, job.PrimarySource, ResolveFilter(job), job.TempDir, ct);
        return [product];
    }

    /// <summary>filter 解析：矩阵显式 filter 优先，否则目标短名（红线 6：编码类 filter 已在矩阵锁常量）。</summary>
    internal static string ResolveFilter(ConversionJob job) => job.Target.Filter ?? job.Target.Format;

    /// <summary>
    /// soffice 子进程转换核心（TwoHop 引擎复用；与旧 RunSofficeAsync 行为等价）。
    /// 返回产物路径（TempDir 下 输入名.filter短名）。
    /// </summary>
    internal static async Task<string> ConvertAsync(
        string soffice, string input, string filter, string tempDir, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(input)!;
        var ext = filter.Split(':')[0]; // "csv:Text - txt - csv (StarCalc):44,34,76" 的短名 = csv
        var tempProduct = Path.Combine(tempDir, Path.GetFileNameWithoutExtension(input) + "." + ext);

        Directory.CreateDirectory(tempDir);
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = soffice,
            UseShellExecute = false,          // 不经 shell；参数数组直传（execFile 等价，红线 1）
            CreateNoWindow = true,
            WorkingDirectory = directory,
        };
        foreach (var arg in BuildArguments(filter, tempDir, input))
        {
            process.StartInfo.ArgumentList.Add(arg);
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
            Kill(process);
            throw new ConvertException(ConvertError.Timeout,
                $"soffice 超时（{ConvertServiceConstants.TimeoutMs}ms，已强杀进程树）");
        }

        if (process.ExitCode != 0 || !File.Exists(tempProduct))
        {
            throw new ConvertException(ConvertError.ConversionFailed,
                $"soffice 退出码 {process.ExitCode}，产物缺失");
        }

        return tempProduct;
        // 临时目录由服务层 finally 统一清理（目录内可能还有两跳中间产物，此处不删）
    }

    /// <summary>
    /// 命令参数序列（纯函数，单测锁定顺序与编码 filter 常量）：
    /// --headless --norestore --convert-to &lt;filter&gt; --outdir &lt;dir&gt; &lt;input&gt;。
    /// ArgumentList 直传：中文/空格/特殊字符路径不串参（红线 1）。
    /// </summary>
    internal static IReadOnlyList<string> BuildArguments(string filter, string outDir, string input) =>
    [
        "--headless",
        "--norestore",
        "--convert-to",
        filter,
        "--outdir",
        outDir,
        input,
    ];

    private static void Kill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch { /* 进程已退出 */ }
    }
}
