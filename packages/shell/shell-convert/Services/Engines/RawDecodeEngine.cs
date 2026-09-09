// BetterDesktop.Shell.Convert — 相机 RAW 输入引擎（2026-09-07 集成，对齐 flyingmouse rawInput 19 种）
// dcraw（LibRaw）子进程：-T 输出 TIFF（-q 3 高质量去马赛克、-o 0 相机白平衡）→ GDI Bitmap →
// ImageTargetWriter 保存。探测链：env BETTERDESKTOP_DCRAW_PATH → 受管 engines\dcraw → Program Files\dcraw。

using System.Diagnostics;
using System.Drawing;
using System.IO;
using BetterDesktop.Shell.Convert.Contracts;

namespace BetterDesktop.Shell.Convert.Services.Engines;

/// <summary>
/// 相机 RAW 解码引擎（dcraw/LibRaw，19 种厂商 RAW：cr2/cr3/crw/nef/arw/dng/raf/rw2/orf/pef/srw/3fr/erf/fff/iiq/kdc/mef/mrw/x3f）。
/// dcraw 是自包含单 exe（LibRaw 官方构建），文件存在即视为可用；缺则置灰（可下载 dcraw.exe 放入 engines\dcraw）。
/// </summary>
public sealed class RawDecodeEngine : IConversionEngine
{
    public const string EnvVar = "BETTERDESKTOP_DCRAW_PATH";

    /// <summary>19 种 RAW 源扩展（飞鼠 rawInput 权威表，2026-09-07 同步）。</summary>
    public static readonly IReadOnlySet<string> RawExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".cr2", ".cr3", ".crw", ".nef", ".arw", ".dng", ".raf", ".rw2", ".orf", ".pef",
        ".srw", ".3fr", ".erf", ".fff", ".iiq", ".kdc", ".mef", ".mrw", ".x3f",
    };

    public EngineKind Kind => EngineKind.Raw;

    public string Name => "raw";

    public bool CanHandle(IReadOnlyList<string> sources, ConversionTarget target) =>
        sources.Count == 1
        && RawExtensions.Contains(Path.GetExtension(sources[0]))
        && (target.Prefer == EngineKind.Raw || target.Fallback == EngineKind.Raw);

    public EngineAvailability Probe() =>
        LocateDcraw() is not null
            ? EngineAvailability.Ok("dcraw/LibRaw")
            : EngineAvailability.Missing;

    /// <summary>定位 dcraw.exe（env → 受管 engines\dcraw → Program Files\dcraw）。</summary>
    public static string? LocateDcraw()
    {
        var fromEnv = Environment.GetEnvironmentVariable(EnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
        {
            return fromEnv;
        }
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "engines", "dcraw", "dcraw.exe"),
            Path.Combine(programFiles, "dcraw", "dcraw.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    public async Task<IReadOnlyList<string>> RunAsync(ConversionJob job, CancellationToken ct)
    {
        var dcraw = LocateDcraw()
            ?? throw new ConvertException(ConvertError.EngineMissing,
                "内置引擎未就绪（未找到 dcraw.exe）。下载 LibRaw 官方 dcraw 放入 engines\\dcraw 或设 "
                + EnvVar + " 环境变量后重试——这不是文件错误");
        var input = job.PrimarySource;
        var format = job.Target.Format;
        var name = Path.GetFileNameWithoutExtension(input);
        var product = Path.Combine(job.TempDir, name + "." + format);

        // dcraw：-T TIFF 输出；-q 3 高质量去马赛克；-o 0 相机白平衡；-O 指定输出文件
        var tiff = Path.Combine(job.TempDir, name + "-raw.tiff");
        var args = new[] { "-T", "-q", "3", "-o", "0", "-O", tiff, input };
        var (code, _, stderr) = await RunCaptureAsync(dcraw, args, ct);
        if (code != 0 || !File.Exists(tiff))
        {
            throw new ConvertException(ConvertError.ConversionFailed,
                $"dcraw 退出码 {code} {Truncate(stderr)}（RAW 可能不受支持或文件损坏）");
        }

        using var image = Image.FromFile(tiff);
        using var bitmap = new Bitmap(image);
        ImageTargetWriter.Save(bitmap, product, format);
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
            throw new ConvertException(ConvertError.Timeout, $"dcraw 超时（{ConvertServiceConstants.MediaTimeoutMs}ms）");
        }
    }
}
