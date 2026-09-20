// BetterDesktop — 诊断包导出（共享源文件，与 DiagnosticLogger 同属一套）。
//
// 用途：把日志分发给他人实地测试时，出问题需要"把现场拿回来"。
// 本类把 日志目录 + 环境信息 打包成一个 zip（默认落在桌面），测试同学双击导出后直接回传即可。
// 约束：限量打包（文件数 + 总量上限），绝不因为用户攒了几个月日志而生成几百 MB 的包。
//
// 【S5-4 新增：导出时脱敏的钩子】`redact` 参数把"换成占位符"的能力注入进来 ——
// 为什么钩子在这里而不是写日志时脱敏：写日志有 N 个组件（host / tray / CLI / 各引擎），
// 导出只有这一处；一处做比 N 处做成本低一个量级，而且内部调试仍然能看到真实路径。
// 为什么要钩子而不是在这里实现脱敏：本文件被 host / tray / CLI 共同编译，
// 脱敏规则（用户名 / 机器名 / SID）属于"谁在导出、导给谁"的策略，不该塞进共享的打包器。

using System.IO.Compression;
using System.Text;

namespace BetterDesktop.Diagnostics;

/// <summary>诊断包导出器。</summary>
public static class DiagnosticBundle
{
    private const int MaxLogFiles = 20;
    private const long MaxLogBytes = 32L * 1024 * 1024;

    /// <summary>
    /// 导出诊断包到 <paramref name="targetDirectory"/>（默认桌面），返回 zip 路径。
    /// 导出前会先刷盘，确保最后几秒（往往就是崩溃前）的日志被包含。
    /// </summary>
    public static string Export(
        DiagnosticLogger? logger,
        string component,
        string? targetDirectory = null)
        => Export(logger, component, targetDirectory, redact: null, nameSuffix: null);

    /// <summary>
    /// 带"导出时脱敏 + 文件名后缀"的重载。
    /// </summary>
    /// <param name="redact">
    /// 内容变换（输入整份字节、返回变换后的字节）。<c>null</c> = **逐字节原样复制**，
    /// 既有调用方的产物一个字节都不变。
    /// </param>
    /// <param name="nameSuffix">
    /// 文件名后缀（如 <c>-RAW</c>）：用来让"没脱敏的那一份"从文件名上就能认出来。
    /// </param>
    /// <remarks>
    /// 【为什么变换的粒度是"整份字节"而不是"每行"】调用方（CLI）做的是**字节级**匹配替换 ——
    /// 它必须看到完整字节流才能安全地只替换命中段、不动其余字节（某些引擎日志不是 UTF-8，
    /// 按行按字符串处理会损坏证据）。本方法只负责"读出来 / 交给它 / 写回去"。
    /// </remarks>
    public static string Export(
        DiagnosticLogger? logger,
        string component,
        string? targetDirectory,
        Func<byte[], byte[]>? redact,
        string? nameSuffix)
    {
        // 关键：异步队列里可能还压着崩溃前最后几条日志。
        logger?.Flush();

        var outputDirectory = targetDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        Directory.CreateDirectory(outputDirectory);

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var zipPath = Path.Combine(
            outputDirectory,
            $"BetterDesktop-诊断包-{component}-{stamp}{nameSuffix ?? string.Empty}.zip");

        try
        {
            using var file = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var archive = new ZipArchive(file, ZipArchiveMode.Create);

            WriteSystemInfo(archive, component, logger, redact);
            WriteLogs(archive, redact);
        }
        catch
        {
            // 半成品包比没有包更危险：用户会把它当完整证据发出去（zip 能打开、内容是空的）。
            // 删掉再抛 —— 让失败是一次**显式失败**。
            try
            {
                File.Delete(zipPath);
            }
            catch
            {
                // 删不掉也必须继续抛
            }

            throw;
        }

        return zipPath;
    }

    private static void WriteSystemInfo(
        ZipArchive archive,
        string component,
        DiagnosticLogger? logger,
        Func<byte[], byte[]>? redact)
    {
        var entry = archive.CreateEntry("system-info.txt", CompressionLevel.Fastest);

        var builder = new StringBuilder();
        builder.AppendLine($"导出时间 : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"组件     : {component}");
        builder.AppendLine($"写入行数 : {logger?.WrittenCount ?? 0}");
        builder.AppendLine($"丢弃行数 : {logger?.DroppedCount ?? 0}");
        builder.AppendLine();
        foreach (var line in DiagnosticLogger.EnvironmentInfo())
        {
            builder.AppendLine(line);
        }

        // 与日志走**同一条**脱敏路径（同一份 text → bytes → redact）：这样 system-info 里的
        // "目录 / 日志 / 命令行"三行与日志正文不会一个漏一个不落 —— 那正是最容易漏的地方。
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(builder.ToString());
        using var stream = entry.Open();
        stream.Write(redact is null ? bytes : redact(bytes));
    }

    private static void WriteLogs(ZipArchive archive, Func<byte[], byte[]>? redact)
    {
        var logsDirectory = DiagnosticLogger.DefaultDirectory;
        if (!Directory.Exists(logsDirectory))
        {
            return;
        }

        var files = new DirectoryInfo(logsDirectory)
            .GetFiles("*.log")
            .OrderByDescending(f => f.LastWriteTime)
            .Take(MaxLogFiles)
            .ToList();

        long total = 0;
        foreach (var file in files)
        {
            if (total + file.Length > MaxLogBytes)
            {
                break;
            }

            total += file.Length;
            var entry = archive.CreateEntry("logs/" + file.Name, CompressionLevel.Fastest);
            using var source = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var destination = entry.Open();

            if (redact is null)
            {
                // 原样：**逐字节**复制。刻意不做任何重编码 —— 某些引擎日志不是 UTF-8，
                // 把它 Decode/Encode 一遍会把非法字节换成 U+FFFD，等于在打包时损坏证据。
                source.CopyTo(destination);
            }
            else
            {
                using var buffer = new MemoryStream();
                source.CopyTo(buffer);
                destination.Write(redact(buffer.ToArray()));
            }
        }
    }
}
