using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using BetterDesktop.Kernel.Core;

namespace BetterDesktop.Shell.Convert.Services.Engines;

/// <summary>下载规格（dependency-on-demand：多镜像 + 大小 + SHA256 + 解压方式 + 受管目录）。</summary>
public sealed record EngineDownloadSpec(
    string Name,
    string[] MirrorUrls,
    long ExpectedBytes,
    string ExpectedSha256,
    string RelativeDir,
    string EntryRelativePath,
    bool IsZip);

/// <summary>
/// 引擎按需下载器（P3，toolknit dependency-on-demand 变体 A 五要素齐全）：
/// 多镜像回退；大小 + SHA-256 校验（不符即失败）；单实例门控；可取消 + 失败清半包；成功缓存。
/// 【诚实红线】校验和未配置（空串）→ 拒绝下载并显式报错：无校验不下载（已知坑 2）。
/// 校验和须从官方发布清单（pandoc releases / BtbN ffmpeg）取得后填入常量并落测试。
/// </summary>
public sealed class EngineDownloader
{
    public static readonly EngineDownloader Shared = new();

    private static readonly HttpClient SharedClient = new()
    {
        Timeout = TimeSpan.FromMinutes(10),
    };

    /// <summary>pandoc 规格（版本锁定 + 校验和待发布清单核对后填入；留空 = 下载禁用）。</summary>
    public static readonly EngineDownloadSpec PandocSpec = new(
        "pandoc",
        [
            "https://github.com/jgm/pandoc/releases/download/3.6.4/pandoc-3.6.4-windows-x86_64.zip",
            "https://gh-proxy.com/https://github.com/jgm/pandoc/releases/download/3.6.4/pandoc-3.6.4-windows-x86_64.zip",
        ],
        ExpectedBytes: 0,
        ExpectedSha256: string.Empty,
        RelativeDir: "engines/pandoc",
        EntryRelativePath: "pandoc.exe",
        IsZip: true);

    /// <summary>ffmpeg 规格（BtbN essentials zip；校验和待发布清单核对后填入）。</summary>
    public static readonly EngineDownloadSpec FfmpegSpec = new(
        "ffmpeg",
        [
            "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip",
            "https://gh-proxy.com/https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip",
        ],
        ExpectedBytes: 0,
        ExpectedSha256: string.Empty,
        RelativeDir: "engines/ffmpeg",
        EntryRelativePath: "ffmpeg.exe",
        IsZip: true);

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>校验和是否已配置（下载前判定；未配置 = 下载禁用，仅探测本机）。</summary>
    public static bool IsSpecConfigured(EngineDownloadSpec spec) =>
        spec.ExpectedBytes > 0 && !string.IsNullOrWhiteSpace(spec.ExpectedSha256);

    public async Task<string> DownloadAsync(EngineDownloadSpec spec, CancellationToken ct)
    {
        if (!IsSpecConfigured(spec))
        {
            throw new InvalidOperationException(
                $"引擎 {spec.Name} 下载规格未配置大小/SHA-256 校验和——无校验不下载（dependency-on-demand 红线），请先填入官方发布清单校验值");
        }

        // ③ 单实例门控：并发请求第二个进入即拒绝（防重复下载同一依赖）
        if (!await _gate.WaitAsync(0, ct))
        {
            throw new InvalidOperationException($"引擎 {spec.Name} 已在下载中（单实例门控）");
        }
        try
        {
            var baseDir = Path.Combine(AppContext.BaseDirectory,
                spec.RelativeDir.Replace('/', Path.DirectorySeparatorChar));
            var finalPath = Path.Combine(baseDir, Path.GetFileName(spec.EntryRelativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (File.Exists(finalPath))
            {
                return finalPath; // 已就绪（缓存）
            }

            Directory.CreateDirectory(baseDir);
            var partialPath = finalPath + ".partial";
            try
            {
                await FetchWithMirrorsAsync(spec, partialPath, ct);
                Verify(partialPath, spec);
                ExtractOrMove(partialPath, spec, finalPath, baseDir);
            }
            finally
            {
                // ⑤ 半包清理（失败/取消/解压残留）——禁止取消时残留半包
                try { if (File.Exists(partialPath)) File.Delete(partialPath); } catch { /* 不阻断 */ }
            }
            DiagnosticLog.Trace("shell-convert", $"引擎 {spec.Name} 下载完成: {finalPath}");
            return finalPath;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task FetchWithMirrorsAsync(EngineDownloadSpec spec, string partialPath, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var url in spec.MirrorUrls)
        {
            try
            {
                using var response = await SharedClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var target = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await source.CopyToAsync(target, ct);
                await target.FlushAsync(ct);
                return;
            }
            catch (OperationCanceledException)
            {
                throw; // 取消不换镜像，直接上抛（半包由调用方 finally 清理）
            }
            catch (Exception ex)
            {
                last = ex;
                DiagnosticLog.Trace("shell-convert", $"镜像失败，尝试下一源: {url} ({ex.Message})");
            }
        }
        throw new InvalidOperationException($"引擎 {spec.Name} 全部镜像不可达: {last?.Message}", last);
    }

    /// <summary>② 大小 + SHA-256 校验（先字节后哈希；不符即失败并清半包）。</summary>
    private static void Verify(string partialPath, EngineDownloadSpec spec)
    {
        var bytes = new FileInfo(partialPath).Length;
        if (bytes != spec.ExpectedBytes)
        {
            throw new InvalidOperationException($"引擎 {spec.Name} 下载大小不符（{bytes} ≠ {spec.ExpectedBytes}）");
        }
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(partialPath);
        var hex = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
        if (!string.Equals(hex, spec.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"引擎 {spec.Name} SHA-256 不符（{hex[..16]}… ≠ {spec.ExpectedSha256[..16]}…）");
        }
    }

    private static void ExtractOrMove(string partialPath, EngineDownloadSpec spec, string finalPath, string baseDir)
    {
        if (!spec.IsZip)
        {
            File.Move(partialPath, finalPath);
            return;
        }
        // zip：解压到临时目录 → 平移入口文件（pandoc zip 带一层目录/ffmpeg bin 子目录）
        var extractDir = baseDir + ".extract";
        try
        {
            System.IO.Compression.ZipFile.ExtractToDirectory(partialPath, extractDir, overwriteFiles: true);
            var entry = Directory.EnumerateFiles(extractDir, Path.GetFileName(spec.EntryRelativePath.Replace('/', Path.DirectorySeparatorChar)),
                SearchOption.AllDirectories).FirstOrDefault()
                ?? throw new InvalidOperationException($"引擎 {spec.Name} 压缩包内未找到入口 {spec.EntryRelativePath}");
            File.Move(entry, finalPath);
        }
        finally
        {
            try { if (Directory.Exists(extractDir)) Directory.Delete(extractDir, recursive: true); }
            catch { /* 不阻断 */ }
        }
    }
}
