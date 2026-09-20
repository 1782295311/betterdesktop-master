using System.Diagnostics;
using System.IO;
using BetterDesktop.Shell.Convert.Contracts;

namespace BetterDesktop.Shell.Convert.Services.Engines;

/// <summary>
/// FFmpeg 引擎（P3）：音视频容器/编码互转、视频抽音轨、视频转 GIF。
/// 探测链：env BETTERDESKTOP_FFMPEG_PATH → Program Files\ffmpeg\bin → 受管 engines\ffmpeg；
/// 真实 -version 校验（dependency-on-demand 红线 2）。音视频转换超时按红线 2 单独放宽（MediaTimeoutMs）。
/// </summary>
public sealed class FfmpegEngine : IConversionEngine, IDownloadableEngine
{
    public const string EnvVar = "BETTERDESKTOP_FFMPEG_PATH";

    private static readonly HashSet<string> VideoSources = new(StringComparer.OrdinalIgnoreCase)
    { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v" };

    private static readonly HashSet<string> AudioSources = new(StringComparer.OrdinalIgnoreCase)
    { ".mp3", ".wav", ".flac", ".m4a", ".ogg", ".ape", ".aac", ".opus", ".wma" };

    /// <summary>图片源（2026-09-07 补全：tga 互转 + 图片→视频）。ico 解码 ffmpeg 亦可，但矩阵指派 Image 引擎。</summary>
    private static readonly HashSet<string> ImageSources = new(StringComparer.OrdinalIgnoreCase)
    { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".tga" };

    /// <summary>ffmpeg 可写的图片目标（tga 互转用）。</summary>
    private static readonly HashSet<string> ImageTargets = new(StringComparer.OrdinalIgnoreCase)
    { "png", "jpg", "bmp", "gif", "tif", "tiff", "webp", "tga", "ico" };

    private static volatile EngineAvailability? _probeCache;

    public EngineKind Kind => EngineKind.Ffmpeg;

    public string Name => "ffmpeg";

    public bool CanHandle(IReadOnlyList<string> sources, ConversionTarget target) =>
        sources.Count == 1
        && (target.Prefer == EngineKind.Ffmpeg || target.Fallback == EngineKind.Ffmpeg)
        && IsSupported(Path.GetExtension(sources[0]).ToLowerInvariant(), target.Format);

    internal static bool IsSupported(string srcExt, string format) =>
        (VideoSources.Contains(srcExt) && format is "mp4" or "mkv" or "webm" or "gif" or "mp3" or "mov")
        || (AudioSources.Contains(srcExt) && format is "mp3" or "wav" or "flac" or "m4a" or "aac" or "opus" or "wma")
        // 2026-09-07 补全：图片→视频（mp4/webm）+ 图片→AVIF（libaom-av1）+ tga 源/目标图片互转
        || (ImageSources.Contains(srcExt) && format is "mp4" or "webm" or "avif")
        || (srcExt == ".tga" && ImageTargets.Contains(format))
        || (format == "tga" && ImageSources.Contains(srcExt));

    public EngineAvailability Probe() => _probeCache ?? EnsureProbed();

    /// <summary>真实执行 ffmpeg -version 校验并缓存（dependency-on-demand 红线 2）。</summary>
    public static EngineAvailability EnsureProbed()
    {
        var ffmpeg = LocateFfmpeg();
        if (ffmpeg is null)
        {
            _probeCache = EngineAvailability.Missing;
            return _probeCache;
        }
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = ffmpeg,
                UseShellExecute = false,
                CreateNoWindow = true,
                // 红线 6：探测输出必须重定向——否则 ReadToEnd 抛 InvalidOperationException（内置前从未运行未暴露）
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            process.Start();
            // 探测超时：ffmpeg 静态构建 ~157MB exe 冷启动慢（内置后实测 3s 不够），放宽到 10s
            var exited = process.WaitForExitAsync().Wait(10_000);
            var stdout = exited ? process.StandardOutput.ReadToEnd() : string.Empty;
            var stderr = exited ? process.StandardError.ReadToEnd() : string.Empty;
            // BtbN 静态构建在宿主无控制台环境将 version 输出到 stderr 且退出码 1（实测），
            // 判定放宽：任一流含版本标识即命中（stderr 尾部实测为 configure 行，非错误）。
            var versionText = stdout + stderr;
            var ok = exited && process.HasExited
                && versionText.Contains("ffmpeg version", StringComparison.Ordinal);
            if (!ok)
            {
                BetterDesktop.Kernel.Core.DiagnosticLog.Trace("shell-convert",
                    $"ffmpeg 探测未通过: path={ffmpeg} exited={exited} code={(exited ? process.ExitCode : -1)} stdoutLen={stdout.Length} stderrLen={stderr.Length} errTail='{(stderr.Length > 500 ? stderr[^500..] : stderr)}'");
            }
            _probeCache = ok ? EngineAvailability.Ok(versionText.Split('\n')[0].Trim()) : EngineAvailability.Missing;
        }
        catch (Exception ex)
        {
            _probeCache = new EngineAvailability(false, null, $"ffmpeg 探测失败: {ex.Message}");
        }
        return _probeCache;
    }

    public static void ResetProbeCache() => _probeCache = null;

    /// <summary>定位 ffmpeg（env → Program Files → 受管 engines 目录）。</summary>
    public static string? LocateFfmpeg()
    {
        var fromEnv = Environment.GetEnvironmentVariable(EnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
        {
            return fromEnv;
        }
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var candidates = new[]
        {
            Path.Combine(programFiles, "ffmpeg", "bin", "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "engines", "ffmpeg", "bin", "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "engines", "ffmpeg", "ffmpeg.exe"),
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
            await EngineDownloader.Shared.DownloadAsync(EngineDownloader.FfmpegSpec, ct);
            ResetProbeCache();
            return Probe().Available;
        }
        catch (Exception ex)
        {
            BetterDesktop.Kernel.Core.DiagnosticLog.Trace("shell-convert", $"ffmpeg 按需下载未完成: {ex.Message}");
            return false;
        }
    }

    public async Task<IReadOnlyList<string>> RunAsync(ConversionJob job, CancellationToken ct)
    {
        var ffmpeg = LocateFfmpeg()
            ?? throw new ConvertException(ConvertError.EngineMissing,
                "内置引擎未就绪（未找到 FFmpeg）——这不是文件错误");
        var input = job.PrimarySource;
        var product = Path.Combine(job.TempDir,
            Path.GetFileNameWithoutExtension(input) + "." + job.Target.Format);

        var srcExt = Path.GetExtension(input).ToLowerInvariant();
        var format = job.Target.Format;
        var marker = job.Target.Filter;
        var isVideo = VideoSources.Contains(srcExt);
        var isImageToVideo = ImageSources.Contains(srcExt) && format is "mp4" or "webm";

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false, // 红线 1：参数数组直传
            CreateNoWindow = true,
        };
        process.StartInfo.ArgumentList.Add("-y");
        if (isImageToVideo)
        {
            // 单图循环 3 秒成视频（2026-09-07 补全：图片→mp4/webm）
            process.StartInfo.ArgumentList.Add("-loop");
            process.StartInfo.ArgumentList.Add("1");
            process.StartInfo.ArgumentList.Add("-framerate");
            process.StartInfo.ArgumentList.Add("25");
        }
        process.StartInfo.ArgumentList.Add("-i");
        process.StartInfo.ArgumentList.Add(input);
        if (isImageToVideo)
        {
            process.StartInfo.ArgumentList.Add("-t");
            process.StartInfo.ArgumentList.Add("3");
            process.StartInfo.ArgumentList.Add("-pix_fmt");
            process.StartInfo.ArgumentList.Add("yuv420p"); // 兼容性（多数播放器不吃 4:2:0 之外的 RGB）
        }
        else if (isVideo)
        {
            // 2026-09-10：mkv/mov 目标 = 容器 remux（-c copy 不重编码，无损，级联可注册）；
            // 其余走编码选择（Filter 承载 marker）；webm 显式 VP9
            if (format is "mkv" or "mov")
            {
                process.StartInfo.ArgumentList.Add("-c");
                process.StartInfo.ArgumentList.Add("copy");
            }
            else
            {
                // MP4 编码选择（2026-09-07 补全：H.264/H.265/AV1，Filter 承载 marker）；webm 显式 VP9
                switch (marker)
                {
                    case ConversionTarget.VideoEncH265Marker:
                        AddEncoder(process, "libx265", "28");
                        break;
                    case ConversionTarget.VideoEncAv1Marker:
                        AddEncoder(process, "libaom-av1", "30");
                        process.StartInfo.ArgumentList.Add("-b:v");
                        process.StartInfo.ArgumentList.Add("0");
                        process.StartInfo.ArgumentList.Add("-cpu-used");
                        process.StartInfo.ArgumentList.Add("6");
                        break;
                    case ConversionTarget.VideoEncH264Marker:
                        AddEncoder(process, "libx264", "23");
                        break;
                    default:
                        if (format == "webm")
                        {
                            process.StartInfo.ArgumentList.Add("-c:v");
                            process.StartInfo.ArgumentList.Add("libvpx-vp9");
                            process.StartInfo.ArgumentList.Add("-crf");
                            process.StartInfo.ArgumentList.Add("32");
                            process.StartInfo.ArgumentList.Add("-b:v");
                            process.StartInfo.ArgumentList.Add("0");
                        }
                        break;
                }
            }
        }
        else if (format == "avif")
        {
            // 图片 → AVIF（libaom-av1；完整构建需 libaom，缺编码器时 ffmpeg 报错→ConversionFailed）
            process.StartInfo.ArgumentList.Add("-c:v");
            process.StartInfo.ArgumentList.Add("libaom-av1");
            process.StartInfo.ArgumentList.Add("-crf");
            process.StartInfo.ArgumentList.Add("30");
            process.StartInfo.ArgumentList.Add("-pix_fmt");
            process.StartInfo.ArgumentList.Add("yuv420p");
        }
        process.StartInfo.ArgumentList.Add(product);

        process.Start();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ConvertServiceConstants.MediaTimeoutMs);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
            throw new ConvertException(ConvertError.Timeout,
                $"FFmpeg 超时（{ConvertServiceConstants.MediaTimeoutMs}ms，已强杀进程树）");
        }
        if (process.ExitCode != 0 || !File.Exists(product))
        {
            throw new ConvertException(ConvertError.ConversionFailed, $"FFmpeg 退出码 {process.ExitCode}，产物缺失");
        }
        return [product];
    }

    private static void AddEncoder(Process process, string codec, string crf)
    {
        process.StartInfo.ArgumentList.Add("-c:v");
        process.StartInfo.ArgumentList.Add(codec);
        process.StartInfo.ArgumentList.Add("-crf");
        process.StartInfo.ArgumentList.Add(crf);
        process.StartInfo.ArgumentList.Add("-preset");
        process.StartInfo.ArgumentList.Add("medium");
    }
}
