using System.Collections.Concurrent;
using System.IO;
using BetterDesktop.Kernel.Core;

namespace BetterDesktop.Shell.Convert.Services;

/// <summary>
/// 引擎路径定位（local-engine-orchestration 变体 A：
/// 环境变量覆盖 → 多候选路径 → 进程级缓存）。
/// 【红线适配】execFile → Process.Start + ArgumentList（不经过 shell，参数数组直传）在引擎层实施；
/// 本类只做定位与 COM ProgID 探测。
/// </summary>
public static class ConvertEngineLocator
{
    /// <summary>soffice 路径环境变量覆盖（BETTERDESKTOP_SOFFICE_PATH）。</summary>
    public const string SofficeEnvVar = "BETTERDESKTOP_SOFFICE_PATH";

    // Office/WPS COM ProgID，按优先级探测（Office > WPS，用户拍板）
    private static readonly (FileCategory Category, string[] ProgIds)[] OfficeProgIds =
    [
        (FileCategory.Word, ["Word.Application", "KWPS.Application"]),
        (FileCategory.Spreadsheet, ["Excel.Application", "KET.Application"]),
        (FileCategory.Presentation, ["PowerPoint.Application", "KWpp.Application"]),
    ];

    private static string? _sofficePath;
    private static readonly ConcurrentDictionary<string, bool> ProgIdCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>文档类别（决定 COM 分支）。</summary>
    public enum FileCategory { Word, Spreadsheet, Presentation }

    /// <summary>定位 soffice.exe（Win 专属；进程级缓存）。找不到返回 null。</summary>
    public static string? LocateSoffice()
    {
        if (_sofficePath is not null)
        {
            return File.Exists(_sofficePath) ? _sofficePath : null;
        }

        // 环境变量覆盖优先（测试/打包路径漂移注入）
        var candidates = new List<string>();
        var fromEnv = Environment.GetEnvironmentVariable(SofficeEnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            candidates.Add(fromEnv);
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        // 2026-09-10 弹窗根因修复：soffice.exe PE 子系统=GUI，GUI 父进程（宿主）启动它执行 --version 时会
        // 主动 AllocConsole 弹控制台窗（重定向拦不住）——统一改用 soffice.com（PE=Console，重定向后不建控制台）。
        candidates.Add(Path.Combine(programFiles, "LibreOffice", "program", "soffice.com"));
        candidates.Add(Path.Combine(programFilesX86, "LibreOffice", "program", "soffice.com"));
        // 受管内置（2026-09-10：LibreOffice 26.8.0 MSI 管理安装点提取，标准结构；pandoc/poppler 同款三链内置）
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "engines", "libreoffice", "program", "soffice.com"));
        // 便携运行时（dependency-on-demand 约定的受管目录）
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "engines", "libreoffice", "LibreOfficePortable",
            "App", "libreoffice", "program", "soffice.com"));

        _sofficePath = candidates.FirstOrDefault(File.Exists) ?? string.Empty;
        if (_sofficePath.Length == 0)
        {
            DiagnosticLog.Trace("shell-convert", "soffice 未找到（检查安装目录/便携目录/env 覆盖）");
            return null;
        }
        DiagnosticLog.Trace("shell-convert", $"soffice 已定位: {_sofficePath}");
        return _sofficePath;
    }

    /// <summary>指定类别的 COM 引擎 ProgID 是否可用（Office 优先，回退 WPS；进程级缓存）。</summary>
    public static string? LocateComProgId(FileCategory category)
    {
        var entry = OfficeProgIds.First(e => e.Category == category);
        return entry.ProgIds.FirstOrDefault(id =>
            ProgIdCache.GetOrAdd(id, static pid => Type.GetTypeFromProgID(pid) is not null));
    }

    /// <summary>COM（Office/WPS）引擎是否可用（决定 pdf 兜底链显隐，隐藏优先）。</summary>
    public static bool HasComEngine() =>
        OfficeProgIds.Any(e => LocateComProgId(e.Category) is not null);

    /// <summary>扩展名 → 类别（COM 分支支持集）。</summary>
    public static FileCategory? CategoryOf(string extension) => extension.ToLowerInvariant() switch
    {
        ".doc" or ".docx" or ".docm" or ".rtf" or ".odt" or ".wps" => FileCategory.Word,
        ".xls" or ".xlsx" or ".xlsm" or ".et" => FileCategory.Spreadsheet,
        ".ppt" or ".pptx" or ".pps" or ".dps" => FileCategory.Presentation,
        _ => null,
    };
}
