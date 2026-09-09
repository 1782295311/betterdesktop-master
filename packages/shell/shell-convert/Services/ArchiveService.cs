using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.Convert.Contracts;

namespace BetterDesktop.Shell.Convert.Services;

/// <summary>
/// 归档服务实现（2026-09-07）：
/// ① zip —— 内置 System.IO.Compression，无外部依赖，压缩/解压永远可用；
/// ② 7z —— 7-Zip 引擎（7z.exe/7zz.exe，含 7-Zip ZS 版；缺失回退 WinRAR 解压）；
/// ③ rar —— 解压走 UnRAR.exe，压缩走 Rar.exe（WinRAR 安装）。
/// 静态探测缓存，缺失 → 菜单置灰、系统右键不注册。重活统一 Task.Run；失败返回 ArchiveResult 不抛。
/// </summary>
public sealed class ArchiveService : IArchiveService
{
    private const string WinRarDirX64 = @"C:\Program Files\WinRAR";
    private const string WinRarDirX86 = @"C:\Program Files (x86)\WinRAR";
    private const string SevenZipZstdDir = @"C:\Program Files\7-Zip-Zstandard";
    private const string SevenZipDirX64 = @"C:\Program Files\7-Zip";
    private const string SevenZipDirX86 = @"C:\Program Files (x86)\7-Zip";

    private static bool? _winRarProbed;
    private static string? _unrarPath;
    private static string? _winrarPath;
    private static string? _rarPath;
    private static bool? _sevenZipProbed;
    private static string? _sevenZipPath;
    private static bool? _rarProbed;

    /// <inheritdoc />
    public bool WinRarAvailable => ProbeWinRar();

    /// <inheritdoc />
    public bool SevenZipAvailable => ProbeSevenZip();

    /// <inheritdoc />
    public bool RarAvailable => ProbeRar();

    /// <summary>WinRAR 引擎探测（静态缓存；UnRAR.exe 解 rar，WinRAR.exe 解 7z，Rar.exe 压 rar——同一安装目录）。</summary>
    public static bool ProbeWinRar()
    {
        if (_winRarProbed is not null)
        {
            return _winRarProbed.Value;
        }

        _unrarPath = null;
        _winrarPath = null;
        _rarPath = null;
        foreach (var dir in new[] { WinRarDirX64, WinRarDirX86 })
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            var unrar = Path.Combine(dir, "UnRAR.exe");
            var winrar = Path.Combine(dir, "WinRAR.exe");
            var rar = Path.Combine(dir, "Rar.exe");
            if (File.Exists(unrar))
            {
                _unrarPath = unrar;
            }
            if (File.Exists(winrar))
            {
                _winrarPath = winrar;
            }
            if (File.Exists(rar))
            {
                _rarPath = rar;
            }
            if (_unrarPath is not null || _winrarPath is not null || _rarPath is not null)
            {
                break;
            }
        }

        _winRarProbed = _unrarPath is not null || _winrarPath is not null || _rarPath is not null;
        DiagnosticLog.Trace("shell-convert",
            $"WinRAR 引擎探测: 可用={_winRarProbed.Value} unrar={_unrarPath ?? "(无)"} winrar={_winrarPath ?? "(无)"} rar={_rarPath ?? "(无)"}");
        return _winRarProbed.Value;
    }

    /// <summary>7-Zip 引擎探测（静态缓存；优先 ZS 版目录，含标准安装与 PATH 中的 7z.exe/7zz.exe）。</summary>
    public static bool ProbeSevenZip()
    {
        if (_sevenZipProbed is not null)
        {
            return _sevenZipProbed.Value;
        }

        _sevenZipPath = null;
        foreach (var dir in new[] { SevenZipZstdDir, SevenZipDirX64, SevenZipDirX86 })
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (var name in new[] { "7z.exe", "7zz.exe" })
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate))
                {
                    _sevenZipPath = candidate;
                    break;
                }
            }

            if (_sevenZipPath is not null)
            {
                break;
            }
        }

        // PATH 兜底
        _sevenZipPath ??= FindOnPath("7z.exe") ?? FindOnPath("7zz.exe");
        _sevenZipProbed = _sevenZipPath is not null;
        DiagnosticLog.Trace("shell-convert",
            $"7-Zip 引擎探测: 可用={_sevenZipProbed.Value} path={_sevenZipPath ?? "(无)"}");
        return _sevenZipProbed.Value;
    }

    /// <summary>Rar.exe（rar 压缩）可用性：随 WinRAR 探测结果，独立缓存。</summary>
    public static bool ProbeRar()
    {
        if (_rarProbed is not null)
        {
            return _rarProbed.Value;
        }

        ProbeWinRar();
        _rarProbed = _rarPath is not null;
        return _rarProbed.Value;
    }

    /// <inheritdoc />
    public Task<ArchiveResult> CompressZipAsync(IReadOnlyList<string> paths)
    {
        if (paths is not { Count: > 0 })
        {
            return Task.FromResult(Fail("没有可压缩的项。"));
        }

        return Task.Run(() => CompressZipCore(paths));
    }

    /// <inheritdoc />
    public Task<ArchiveResult> Compress7zAsync(IReadOnlyList<string> paths)
    {
        if (paths is not { Count: > 0 })
        {
            return Task.FromResult(Fail("没有可压缩的项。"));
        }

        return Task.Run(() => CompressExternalCore(paths, ".7z",
            ProbeSevenZip() ? _sevenZipPath : null, "-t7z"));
    }

    /// <inheritdoc />
    public Task<ArchiveResult> CompressRarAsync(IReadOnlyList<string> paths)
    {
        if (paths is not { Count: > 0 })
        {
            return Task.FromResult(Fail("没有可压缩的项。"));
        }

        return Task.Run(() => CompressExternalCore(paths, ".rar",
            ProbeRar() ? _rarPath : null, null, excludeBasePath: true));
    }

    /// <inheritdoc />
    public Task<ArchiveResult> ExtractAsync(string archivePath, bool toNamedFolder)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
        {
            return Task.FromResult(Fail("归档文件不存在。"));
        }

        return Task.Run(() => ExtractCore(archivePath, toNamedFolder));
    }

    // ===== 压缩（zip 内置） =====

    private static ArchiveResult CompressZipCore(IReadOnlyList<string> paths)
    {
        try
        {
            // 校验输入：任一路径不存在 → 整批失败（不做部分成功）
            foreach (var p in paths)
            {
                if (!File.Exists(p) && !Directory.Exists(p))
                {
                    return Fail($"路径不存在：{p}");
                }
            }

            var first = paths[0];
            var parent = Path.GetDirectoryName(first);
            if (string.IsNullOrEmpty(parent))
            {
                parent = Directory.GetCurrentDirectory();
            }

            var baseName = Path.GetFileNameWithoutExtension(first);
            if (string.IsNullOrWhiteSpace(baseName))
            {
                baseName = Path.GetFileName(first.TrimEnd('\\', '/'));
            }

            var zipPath = UniquePath(Path.Combine(parent, baseName + ".zip"));
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                foreach (var p in paths)
                {
                    if (Directory.Exists(p))
                    {
                        var folderName = Path.GetFileName(p.TrimEnd('\\', '/'));
                        AddDirectoryToZip(zip, p, folderName);
                    }
                    else
                    {
                        zip.CreateEntryFromFile(p, Path.GetFileName(p), CompressionLevel.Optimal);
                    }
                }
            }

            return new ArchiveResult
            {
                Success = true,
                Output = zipPath,
                Message = $"压缩完成：{Path.GetFileName(zipPath)}",
            };
        }
        catch (Exception ex)
        {
            return Fail($"压缩失败：{ex.Message}");
        }
    }

    /// <summary>递归加入目录：条目 = folderName/相对路径（空目录也保留条目）。</summary>
    private static void AddDirectoryToZip(ZipArchive zip, string dir, string entryPrefix)
    {
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            var entryName = entryPrefix + "/" + Path.GetFileName(file);
            zip.CreateEntryFromFile(file, entryName, CompressionLevel.Optimal);
        }

        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            var subName = entryPrefix + "/" + Path.GetFileName(sub);
            zip.CreateEntry(subName + "/", CompressionLevel.Optimal);
            AddDirectoryToZip(zip, sub, subName);
        }
    }

    /// <summary>7z/rar 压缩（外部引擎）：输出名 = 首项名.扩展，重名自动 (1)；引擎缺失返回明确提示。</summary>
    private static ArchiveResult CompressExternalCore(
        IReadOnlyList<string> paths, string ext, string? engine, string? formatArg,
        bool excludeBasePath = false)
    {
        // 校验输入：任一路径不存在 → 整批失败（不做部分成功）
        foreach (var p in paths)
        {
            if (!File.Exists(p) && !Directory.Exists(p))
            {
                return Fail($"路径不存在：{p}");
            }
        }

        if (string.IsNullOrEmpty(engine))
        {
            return Fail(ext == ".7z" ? "未检测到 7-Zip，无法压缩为 7z。" : "未检测到 Rar.exe，无法压缩为 rar。");
        }

        var first = paths[0];
        var parent = Path.GetDirectoryName(first);
        if (string.IsNullOrEmpty(parent))
        {
            parent = Directory.GetCurrentDirectory();
        }

        var baseName = Path.GetFileNameWithoutExtension(first);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = Path.GetFileName(first.TrimEnd('\\', '/'));
        }

        var outputPath = UniquePath(Path.Combine(parent, baseName + ext));
        var args = new List<string> { "a" };
        if (formatArg is not null)
        {
            args.Add(formatArg);
        }
        if (excludeBasePath)
        {
            args.Add("-ep1"); // Rar.exe 默认存绝对路径，-ep1 排除基路径（只留最后一级）
        }
        args.Add("-y");
        args.Add(outputPath);
        foreach (var p in paths)
        {
            args.Add(p);
        }

        var code = RunProcess(engine, args);
        if (code != 0)
        {
            return Fail($"{Path.GetFileName(engine)} 压缩失败（退出码 {code}）。");
        }

        return new ArchiveResult
        {
            Success = true,
            Output = outputPath,
            Message = $"压缩完成：{Path.GetFileName(outputPath)}",
        };
    }

    // ===== 解压（zip 内置 / rar+7z 外部引擎） =====

    private static ArchiveResult ExtractCore(string archivePath, bool toNamedFolder)
    {
        try
        {
            var ext = Path.GetExtension(archivePath);
            var archiveDir = Path.GetDirectoryName(archivePath)
                ?? Directory.GetCurrentDirectory();
            var destDir = toNamedFolder
                ? UniquePath(Path.Combine(archiveDir, Path.GetFileNameWithoutExtension(archivePath)))
                : archiveDir;

            if (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                // 内置：不覆盖同名文件（冲突即报错，绝不静默覆盖用户文件）；.NET 内部校验防路径穿越（Zip Slip）。
                ZipFile.ExtractToDirectory(archivePath, destDir, overwriteFiles: false);
            }
            else if (ext.Equals(".rar", StringComparison.OrdinalIgnoreCase))
            {
                if (_unrarPath is null && !ProbeWinRar())
                {
                    return Fail("未检测到 WinRAR（UnRAR.exe），无法解压 .rar。");
                }
                if (_unrarPath is null)
                {
                    return Fail("WinRAR 安装不完整（缺少 UnRAR.exe），无法解压 .rar。");
                }
                // C2：解压前校验条目名，拒绝路径穿越（Zip Slip）
                if (HasUnsafeEntries(_unrarPath, archivePath, destDir, is7z: false))
                {
                    return Fail("压缩包含有越界路径条目（Zip Slip 风险），已拒绝解压。");
                }
                // x = 完整路径解压，-y 全确认，-o+ 覆盖已存在文件（WinRAR 命令行语义）
                var code = RunProcess(_unrarPath,
                    ["x", "-y", "-o+", archivePath, destDir.TrimEnd('\\', '/') + "\\"]);
                if (code != 0)
                {
                    return Fail($"UnRAR 解压失败（退出码 {code}）。");
                }
            }
            else if (ext.Equals(".7z", StringComparison.OrdinalIgnoreCase))
            {
                if (ProbeSevenZip() && _sevenZipPath is not null)
                {
                    // C2：解压前校验条目名，拒绝路径穿越（Zip Slip）
                    if (HasUnsafeEntries(_sevenZipPath, archivePath, destDir, is7z: true))
                    {
                        return Fail("压缩包含有越界路径条目（Zip Slip 风险），已拒绝解压。");
                    }
                    // 7-Zip 优先（含 ZS 版）：x = 完整路径解压，-y 全确认，-o<dir> 目标目录
                    var code = RunProcess(_sevenZipPath,
                        ["x", "-y", "-o" + destDir.TrimEnd('\\', '/'), archivePath]);
                    if (code != 0)
                    {
                        return Fail($"7-Zip 解压失败（退出码 {code}）。");
                    }
                }
                else if (_winrarPath is not null || ProbeWinRar())
                {
                    if (_winrarPath is null)
                    {
                        return Fail("WinRAR 安装不完整，无法解压 .7z。");
                    }

                    // C2：WinRAR 回退解压前同样校验条目名
                    if (HasUnsafeEntries(_winrarPath, archivePath, destDir, is7z: false))
                    {
                        return Fail("压缩包含有越界路径条目（Zip Slip 风险），已拒绝解压。");
                    }

                    // -ibck 后台运行（避免 GUI 窗口闪现）
                    var code = RunProcess(_winrarPath,
                        ["x", "-y", "-ibck", "-o+", archivePath, destDir.TrimEnd('\\', '/') + "\\"]);
                    if (code != 0)
                    {
                        return Fail($"WinRAR 解压失败（退出码 {code}）。");
                    }
                }
                else
                {
                    return Fail("未检测到 7-Zip 或 WinRAR，无法解压 .7z。");
                }
            }
            else
            {
                return Fail($"暂不支持解压 {ext} 格式。");
            }

            return new ArchiveResult
            {
                Success = true,
                Output = destDir,
                Message = $"解压完成：{Path.GetFileName(archivePath)} → {Path.GetFileName(destDir)}",
            };
        }
        catch (IOException ex)
        {
            // ExtractToDirectory overwriteFiles:false 的文件冲突路径
            return Fail($"解压失败：目标位置已存在同名文件（{ex.Message.Split('\n')[0]}）。请先删除或改名后再试。");
        }
        catch (Exception ex)
        {
            return Fail($"解压失败：{ex.Message}");
        }
    }

    // ===== 工具 =====

    /// <summary>同目录重名自动追加 " (1)"、" (2)"…</summary>
    private static string UniquePath(string candidate)
    {
        if (!File.Exists(candidate) && !Directory.Exists(candidate))
        {
            return candidate;
        }

        var dir = Path.GetDirectoryName(candidate) ?? "";
        var name = Path.GetFileNameWithoutExtension(candidate);
        var ext = Path.GetExtension(candidate);
        for (var i = 1; ; i++)
        {
            var next = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(next) && !Directory.Exists(next))
            {
                return next;
            }
        }
    }

    /// <summary>启动外部进程等待退出，返回退出码（控制台无窗口）。</summary>
    /// <summary>在 PATH 环境变量目录中查找可执行文件（7z/7zz 兜底探测）。</summary>
    private static string? FindOnPath(string fileName)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar))
        {
            return null;
        }

        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }

            try
            {
                var candidate = Path.Combine(dir.Trim('"'), fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // 非法路径项忽略
            }
        }

        return null;
    }

    /// <summary>启动外部进程等待退出，返回退出码（控制台无窗口）。
    /// C3 修复：默认 120s 超时，超时杀进程树，避免 7z/UnRAR/WinRAR 挂起导致任务永不完成、Task 泄漏。</summary>
    private static int RunProcess(string exe, IReadOnlyList<string> args, int timeoutSeconds = 120)
    {

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = Process.Start(psi);
        if (proc is null)
        {
            return -1;
        }

        if (!proc.WaitForExit(timeoutSeconds * 1000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* 进程已退出或无权限 */ }
            proc.WaitForExit(2000);
            return -1; // 超时
        }
        return proc.ExitCode;
    }

    /// <summary>启动外部进程并捕获 stdout，用于解压前的条目列表校验（C2 路径穿越防护）。</summary>
    private static (int exitCode, string stdout) RunProcessCapture(string exe, IReadOnlyList<string> args, int timeoutSeconds = 60)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = Process.Start(psi);
        if (proc is null) return (-1, "");
        var stdout = proc.StandardOutput.ReadToEnd();
        if (!proc.WaitForExit(timeoutSeconds * 1000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            proc.WaitForExit(2000);
            return (-1, stdout);
        }
        return (proc.ExitCode, stdout);
    }

    /// <summary>C2 修复：解压前用引擎 list 命令枚举条目名，拒绝含路径穿越（绝对路径或 ../ 越界）的恶意压缩包，防止 Zip Slip 越界写。
    /// list 失败时不阻断（降级为正常解压，引擎自身会报错），仅在明确检出越界条目时拒绝。</summary>
    private static bool HasUnsafeEntries(string enginePath, string archivePath, string destDir, bool is7z)
    {
        var args = is7z
            ? new[] { "l", "-ba", archivePath }
            : new[] { "lb", archivePath };
        var (code, stdout) = RunProcessCapture(enginePath, args);
        if (code != 0) return false; // 列表不可用：降级，不阻断

        var destFull = Path.GetFullPath(destDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var rawLine in stdout.Split('\n'))
        {
            var entry = rawLine.Trim('\r', ' ', '\t');
            if (string.IsNullOrEmpty(entry)) continue;
            // 拒绝绝对路径条目（如 C:\evil 或 /etc/passwd）
            if (Path.IsPathRooted(entry)) return true;
            // 解析组合后的完整路径，拒绝越出 destDir
            string combined;
            try { combined = Path.GetFullPath(Path.Combine(destFull, entry)); }
            catch { return true; } // 非法路径字符视为不安全
            if (!combined.StartsWith(destFull + Path.DirectorySeparatorChar, StringComparison.Ordinal) && combined != destFull)
                return true;
        }
        return false;
    }

    private static ArchiveResult Fail(string message) =>
        new() { Success = false, Message = message };
}
