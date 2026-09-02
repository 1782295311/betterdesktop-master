using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using BetterDesktop.Kernel.Core;

namespace BetterDesktop.Shell.Convert.Services;

/// <summary>转换错误分类（照 local-engine-orchestration 契约：引擎缺失/崩溃/超时/转换失败 分开）。</summary>
public enum ConvertError
{
    None,
    /// <summary>引擎缺失（不是用户文件错误——提示语必须区分，禁止伪装成转换失败）。</summary>
    EngineMissing,
    /// <summary>引擎崩溃（进程非零退出且无产物）。</summary>
    EngineCrashed,
    /// <summary>超时（超时控制必须存在）。</summary>
    Timeout,
    /// <summary>转换失败（引擎正常退出但无输出/业务失败）。</summary>
    ConversionFailed,
    /// <summary>输入非法（空路径/\0/不存在/不支持类型）。</summary>
    InputInvalid,
    /// <summary>输出发布失败（写/移动失败）。</summary>
    OutputFailed,
}

/// <summary>
/// PDF 转换引擎定位与可用性探测（适配 local-engine-orchestration 变体 A：
/// 环境变量覆盖 → 多候选路径 → 进程级缓存）。
/// 【红线适配】错误分类保留；execFile→Process.Start+ArgumentList（不经过 shell，参数数组直传）。
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
        candidates.Add(Path.Combine(programFiles, "LibreOffice", "program", "soffice.exe"));
        candidates.Add(Path.Combine(programFilesX86, "LibreOffice", "program", "soffice.exe"));
        // 便携运行时（dependency-on-demand 约定的受管目录）
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "engines", "libreoffice", "LibreOfficePortable",
            "App", "libreoffice", "program", "soffice.exe"));

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

    /// <summary>PDF 引擎是否可用（任一途径；决定"转 PDF"菜单项显示，隐藏优先）。</summary>
    public static bool HasPdfEngine() => LocateSoffice() is not null || OfficeProgIds.Any(e => LocateComProgId(e.Category) is not null);

    /// <summary>扩展名 → 类别（convert 支持集）。</summary>
    public static FileCategory? CategoryOf(string extension) => extension.ToLowerInvariant() switch
    {
        ".doc" or ".docx" or ".docm" or ".rtf" or ".odt" or ".wps" => FileCategory.Word,
        ".xls" or ".xlsx" or ".xlsm" or ".et" => FileCategory.Spreadsheet,
        ".ppt" or ".pptx" or ".pps" or ".dps" => FileCategory.Presentation,
        _ => null,
    };
}

/// <summary>转换事件载荷（IEventBus convert/*）。</summary>
public sealed record ConvertEventPayload(
    string Source,
    string? Target,
    string Engine,
    string? Error,
    long ElapsedMs);
