using System.Threading.Tasks;

namespace BetterDesktop.Shell.Convert.Contracts;

/// <summary>压缩/解压结果（Success=false 时 Message 含用户可读原因）。</summary>
public sealed class ArchiveResult
{
    public bool Success { get; init; }

    public string Message { get; init; } = string.Empty;

    /// <summary>产出路径（压缩产物 / 解压目录），失败时为空。</summary>
    public string? Output { get; init; }
}

/// <summary>
/// 归档服务（2026-09-07）：zip 用 .NET 内置 System.IO.Compression（零依赖、永远可用）；
/// rar/7z 解压走 WinRAR 命令行引擎（UnRAR.exe / WinRAR.exe，静态探测，缺失时菜单置灰）。
/// 所有重活内部 Task.Run 后台执行，不阻塞 UI。
/// </summary>
public interface IArchiveService
{
    /// <summary>压缩为 .zip（多选支持：文件+文件夹混合，条目保留顶层名称；重名自动追加 (1)(2)）。</summary>
    Task<ArchiveResult> CompressZipAsync(IReadOnlyList<string> paths);

    /// <summary>压缩为 .7z（7-Zip 引擎，<see cref="SevenZipAvailable"/> 缺失时置灰；多选/重名语义同 zip）。</summary>
    Task<ArchiveResult> Compress7zAsync(IReadOnlyList<string> paths);

    /// <summary>压缩为 .rar（WinRAR Rar.exe 引擎，<see cref="RarAvailable"/> 缺失时置灰；多选/重名语义同 zip）。</summary>
    Task<ArchiveResult> CompressRarAsync(IReadOnlyList<string> paths);

    /// <summary>
    /// 解压归档：zip 内置；7z 优先 7-Zip（缺失回退 WinRAR）；rar 走 UnRAR。
    /// toNamedFolder=true → 解压到同目录下「文件名\」子文件夹；false → 解压到归档所在目录（当前文件夹）。
    /// </summary>
    Task<ArchiveResult> ExtractAsync(string archivePath, bool toNamedFolder);

    /// <summary>WinRAR 引擎是否可用（rar 解压 / WinRAR 回退 7z 解压的置灰依据；zip 不依赖此）。</summary>
    bool WinRarAvailable { get; }

    /// <summary>7-Zip 引擎是否可用（7z 压缩/解压的置灰依据）。</summary>
    bool SevenZipAvailable { get; }

    /// <summary>Rar.exe 是否可用（rar 压缩的置灰依据）。</summary>
    bool RarAvailable { get; }
}
