using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace BetterDesktop.Updater;

internal sealed class ManifestFetchResult
{
    public ReleaseManifest? Manifest { get; init; }
    public string Source { get; init; } = string.Empty;
    public string? Error { get; init; }
    public bool Ok => Manifest is not null;
}

/// <summary>
/// 更新源访问：解析源列表（支持多镜像，分号分隔，依次回退）→ 取 manifest.json → 下载文件并做 SHA256 校验。
/// 源可以是 HTTP(S) 目录，也可以是本地目录 / UNC 共享（内网分发最常见的两种）。
/// 复用了 shell-convert/EngineDownloader 的多镜像回退 + 大小/SHA256 校验骨架，但保持零包引用。
/// </summary>
internal static class UpdateSource
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    /// <summary>
    /// 清单解析选项。
    /// <para>
    /// 【C1 为什么必须限深】manifest.json 来自 HTTP(S) / UNC / 本地目录 —— 属**外部输入**。
    /// 不限深时深层嵌套的 JSON 会把解析器压成"栈深炸弹"（STJ 默认上限 64，够用但不该依赖默认值：
    /// 默认值是**库的行为**，显式值是**我们的契约**）。32 层远超任何真实清单的嵌套需求。
    /// </para>
    /// </summary>
    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        MaxDepth = 32,
    };

    /// <summary>解析更新源：--source 优先，其次 update.config.json；分号分隔可配镜像。</summary>
    public static List<string> ResolveSources(string? explicitSource)
    {
        var raw = explicitSource;
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = ReleaseIo.TryLoad<UpdateConfig>(UpdaterPaths.ConfigFile)?.Source;
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        return [.. raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    /// <summary>依次尝试各源取清单（第一个能给出有效清单的源胜出）。</summary>
    public static ManifestFetchResult FetchManifest(List<string> sources)
    {
        string? lastError = null;

        foreach (var source in sources)
        {
            try
            {
                var manifest = FetchManifestFrom(source);
                if (manifest is not null && manifest.Files.Count > 0)
                {
                    UpdaterLog.Write($"清单获取成功：源={source} 版本={manifest.Version} build={manifest.Build} 文件数={manifest.Files.Count}");
                    return new ManifestFetchResult { Manifest = manifest, Source = source };
                }

                lastError = $"{source}：清单为空或缺少 files";
            }
            catch (Exception ex)
            {
                lastError = $"{source}：{ex.Message}";
                UpdaterLog.Write($"清单获取失败（{source}）：{ex.Message}");
            }
        }

        return new ManifestFetchResult { Error = lastError ?? "未配置更新源（--source 或 update.config.json）" };
    }

    private static ReleaseManifest? FetchManifestFrom(string source)
    {
        if (IsHttp(source))
        {
            var url = source.TrimEnd('/') + "/manifest.json";
            var json = Http.GetStringAsync(url).GetAwaiter().GetResult();
            // HTTP 分支必须自己剥 BOM：JsonSerializer 不接受首字符 \uFEFF
            //（publish.ps1 现已写无 BOM，但外部/历史清单可能带 BOM，两边都防）
            return JsonSerializer.Deserialize<ReleaseManifest>(json.TrimStart('\uFEFF'), ManifestOptions);
        }

        return ReleaseIo.TryLoad<ReleaseManifest>(Path.Combine(source, "manifest.json"));
    }

    /// <summary>按清单文件列表下载到暂存目录并逐个校验；任一文件在所有源上都失败即整体失败。</summary>
    public static bool DownloadAll(ReleaseManifest manifest, List<string> sources, string stagingDir, out string error)
    {
        error = string.Empty;
        Directory.CreateDirectory(stagingDir);

        foreach (var file in manifest.Files)
        {
            // 路径越界校验：拒绝绝对路径与 `..`（防被劫持的更新源做任意文件写）
            var target = ReleaseIo.ResolveWithin(stagingDir, file.Path);
            if (target is null)
            {
                error = $"清单包含非法路径（拒绝写入）：{file.Path}";
                return false;
            }

            var dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var ok = false;
            foreach (var source in sources)
            {
                try
                {
                    FetchFile(source, file.Path, target);
                    var actual = Sha256Of(target);
                    if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        UpdaterLog.Write($"校验失败（{file.Path}）：期望 {file.Sha256[..Math.Min(12, file.Sha256.Length)]}… 实际 {actual[..Math.Min(12, actual.Length)]}…，换下一个源");
                        continue;
                    }

                    ok = true;
                    break;
                }
                catch (Exception ex)
                {
                    UpdaterLog.Write($"下载失败（{file.Path}）来自 {source}：{ex.Message}");
                }
            }

            if (!ok)
            {
                error = $"文件下载或校验失败：{file.Path}";
                return false;
            }
        }

        // 落地"安装计划"：apply 阶段只依赖暂存目录，不需要再联网
        ReleaseIo.SaveJson(Path.Combine(stagingDir, "manifest.json"), manifest);
        return true;
    }

    private static void FetchFile(string source, string relativePath, string target)
    {
        if (IsHttp(source))
        {
            var url = source.TrimEnd('/') + "/" + relativePath.Replace('\\', '/');
            using var resp = Http.GetAsync(url).GetAwaiter().GetResult();
            resp.EnsureSuccessStatusCode();
            using var stream = resp.Content.ReadAsStream();
            using var file = File.Create(target);
            stream.CopyTo(file);
            return;
        }

        File.Copy(Path.Combine(source, relativePath), target, overwrite: true);
    }

    public static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static bool IsHttp(string source)
        => source.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || source.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>版本是否相同：优先比 build 时间戳（同产品版本下的正确判据），退回比版本号。</summary>
    public static bool SameRelease(LocalVersion local, ReleaseManifest remote)
    {
        if (!string.IsNullOrEmpty(local.Build) && !string.IsNullOrEmpty(remote.Build))
        {
            return string.Equals(local.Build, remote.Build, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(local.Version, remote.Version, StringComparison.OrdinalIgnoreCase);
    }
}
