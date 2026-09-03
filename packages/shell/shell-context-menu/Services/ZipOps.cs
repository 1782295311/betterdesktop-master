// BetterDesktop.Shell.ContextMenus — ZIP 压缩 / 解压（.NET 内置，零新包）
// 消费 FileCapabilities.Extract 能力位；压缩对文件与文件夹都开放。
// 重名规则与 DesktopBrowser.UniquePath 同款（"副本(i)"），避免覆盖既有文件。

using System.IO;
using System.IO.Compression;

namespace BetterDesktop.Shell.ContextMenus.Services;

/// <summary>ZIP 压缩/解压（内置 System.IO.Compression）。</summary>
public static class ZipOps
{
    /// <summary>把多个文件/文件夹压缩到一个 zip（目标 zip 路径）。</summary>
    public static bool Compress(IReadOnlyList<string> sources, string zipPath)
    {
        if (sources.Count == 0 || string.IsNullOrWhiteSpace(zipPath))
        {
            return false;
        }

        var target = UniquePath(zipPath);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var archive = ZipFile.Open(target, ZipArchiveMode.Create);
            foreach (var source in sources)
            {
                if (Directory.Exists(source))
                {
                    AddDirectory(archive, source);
                }
                else if (File.Exists(source))
                {
                    archive.CreateEntryFromFile(source, Path.GetFileName(source), CompressionLevel.Optimal);
                }
            }
            return true;
        }
        catch
        {
            TryDelete(target);
            return false;
        }
    }

    /// <summary>解压 zip 到目标目录（自动创建同名子目录，避免污染目标文件夹）。</summary>
    public static bool Extract(string zipPath, string? destination = null)
    {
        if (!File.Exists(zipPath))
        {
            return false;
        }

        try
        {
            var dir = destination ?? Path.Combine(
                Path.GetDirectoryName(zipPath)!,
                Path.GetFileNameWithoutExtension(zipPath));
            dir = UniquePath(dir);
            ZipFile.ExtractToDirectory(zipPath, dir, overwriteFiles: false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void AddDirectory(ZipArchive archive, string root)
    {
        var rootName = new DirectoryInfo(root).Name;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file);
            archive.CreateEntryFromFile(file, Path.Combine(rootName, relative), CompressionLevel.Optimal);
        }
    }

    /// <summary>重名自增：a → a(2) → a(3)（与系统"副本"规则同型）。</summary>
    public static string UniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return path;
        }

        var dir = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; i < 999; i++)
        {
            var candidate = Path.Combine(dir, $"{name}({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 清理失败静默（M10）
        }
    }
}
