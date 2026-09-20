using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BetterDesktop.Updater;

/// <summary>清单中的一个文件条目（由 scripts/publish.ps1 生成）。</summary>
internal sealed class ReleaseFile
{
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = string.Empty;
}

/// <summary>发布清单 manifest.json（版本/通道/文件哈希）。</summary>
internal sealed class ReleaseManifest
{
    [JsonPropertyName("product")] public string Product { get; set; } = string.Empty;
    [JsonPropertyName("version")] public string Version { get; set; } = string.Empty;
    [JsonPropertyName("build")] public string Build { get; set; } = string.Empty;
    [JsonPropertyName("channel")] public string Channel { get; set; } = string.Empty;
    [JsonPropertyName("publishedAt")] public string PublishedAt { get; set; } = string.Empty;
    [JsonPropertyName("minHost")] public string MinHost { get; set; } = string.Empty;
    [JsonPropertyName("notes")] public string Notes { get; set; } = string.Empty;
    [JsonPropertyName("fileCount")] public int FileCount { get; set; }
    [JsonPropertyName("files")] public List<ReleaseFile> Files { get; set; } = [];
}

/// <summary>本机版本描述 version.json（publish 生成）。</summary>
internal sealed class LocalVersion
{
    [JsonPropertyName("product")] public string Product { get; set; } = string.Empty;
    [JsonPropertyName("version")] public string Version { get; set; } = string.Empty;
    [JsonPropertyName("build")] public string Build { get; set; } = string.Empty;
    [JsonPropertyName("informational")] public string Informational { get; set; } = string.Empty;
    [JsonPropertyName("channel")] public string Channel { get; set; } = string.Empty;
    [JsonPropertyName("publishedAt")] public string PublishedAt { get; set; } = string.Empty;
}

/// <summary>更新结果状态（托盘据此弹气泡）。</summary>
internal sealed class UpdateStatus
{
    [JsonPropertyName("at")] public string At { get; set; } = string.Empty;
    [JsonPropertyName("command")] public string Command { get; set; } = string.Empty;
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("local")] public string Local { get; set; } = string.Empty;
    [JsonPropertyName("remote")] public string Remote { get; set; } = string.Empty;
    [JsonPropertyName("hasUpdate")] public bool HasUpdate { get; set; }
    [JsonPropertyName("staging")] public string Staging { get; set; } = string.Empty;
    [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
}

/// <summary>更新源配置（update.config.json，运维本地文件）。</summary>
internal sealed class UpdateConfig
{
    [JsonPropertyName("source")] public string Source { get; set; } = string.Empty;
    [JsonPropertyName("channel")] public string Channel { get; set; } = "stable";
}

internal static class ReleaseIo
{
    /// <summary>
    /// 把清单里的相对路径解析到指定根目录内，越界一律拒绝（返回 null）。
    ///
    /// 为什么必须做：**manifest 本身就是攻击面** —— SHA256 只能证明"文件没被篡改"，
    /// 防不了"清单里写 `..\..\Windows\System32\...` 这种恶意路径"。没有这层校验，
    /// 一个被劫持/伪造的更新源就能实现任意文件写（提权、持久化）。
    /// </summary>
    public static string? ResolveWithin(string baseDir, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            return null;
        }

        var segments = relativePath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || Array.Exists(segments, s => s is ".." or "."))
        {
            return null;
        }

        var root = Path.GetFullPath(baseDir);
        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static T? TryLoad<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), ReadOptions);
        }
        catch (Exception ex)
        {
            UpdaterLog.Write($"读取 JSON 失败（{path}）: {ex.Message}");
            return null;
        }
    }

    public static void SaveJson<T>(string path, T value)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(path, JsonSerializer.Serialize(value, WriteOptions));
        }
        catch (Exception ex)
        {
            UpdaterLog.Write($"写入 JSON 失败（{path}）: {ex.Message}");
        }
    }

    /// <summary>本机版本：优先 version.json；缺失时退回程序集信息版本（保证更新器单独跑也有版本）。</summary>
    public static LocalVersion LoadLocalVersion()
    {
        var fromFile = TryLoad<LocalVersion>(UpdaterPaths.LocalVersionFile);
        if (fromFile is not null && !string.IsNullOrEmpty(fromFile.Version))
        {
            return fromFile;
        }

        var asm = Assembly.GetExecutingAssembly();
        var informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        var build = informational.Contains('-', StringComparison.Ordinal)
            ? informational[(informational.IndexOf('-', StringComparison.Ordinal) + 1)..]
            : string.Empty;

        UpdaterLog.Write("警告：本机 version.json 缺失，改用程序集版本（发布产物应始终带 version.json）");
        return new LocalVersion
        {
            Product = "BetterDesktop",
            Version = asm.GetName().Version?.ToString(3) ?? "unknown",
            Build = build,
            Informational = informational,
            Channel = "unknown",
        };
    }
}
