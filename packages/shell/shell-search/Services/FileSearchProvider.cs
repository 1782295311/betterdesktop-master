using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.Search.Contracts;

namespace BetterDesktop.Shell.Search.Services;

/// <summary>
/// 文件搜索：通过 Windows Search 索引查询文件（SearchManager → SystemIndex → ISearchQueryHelper，
/// ADODB 连接 Search.CollatorDSO 执行 SQL）。
/// 全部 COM 调用 try-catch：Windows Search 服务禁用 / 未建索引 / 组件缺失时降级为空列表（M10），不崩溃。
/// 使用 dynamic 后期绑定，避免引入 COM 互操作程序集；查询用 AQS 文件名子串匹配 + 全文。
/// </summary>
public sealed class FileSearchProvider : ISearchResultProvider
{
    // CLSID_SearchManager
    private const string SearchManagerClsid = "7D096C5F-AC08-4F1F-BEB7-5C22C517CE39";
    private const string QuerySelectColumns = "System.FileName,System.ItemPathDisplay";
    private const int MaxResults = 20;

    private readonly IKernelLogger _logger;

    public string Name => "files";

    public FileSearchProvider(IKernelLogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public IReadOnlyList<SearchResult> Search(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<SearchResult>();
        }

        // 兜底扫描与索引查询**并行**执行。不能只在索引空结果时兜底：
        // 索引会命中同名子串的无关文件（搜 maa 命中 Python 库的 CMAA），占坑导致兜底永不触发，
        // 而 D:\迅雷下载 这类未索引目录的文件只能靠文件系统扫描找到。
        var fallbackTask = Task.Run(() => FallbackFileSystemScan(query, ct), ct);

        var results = new List<SearchResult>();
        try
        {
            ct.ThrowIfCancellationRequested();

            var managerType = Type.GetTypeFromCLSID(new Guid(SearchManagerClsid));
            if (managerType is not null)
            {
                dynamic manager = Activator.CreateInstance(managerType)!;
                dynamic catalog = manager.GetCatalog("SystemIndex");
                dynamic helper = catalog.GetQueryHelper();
                // QueryContentProperties 是"无属性名时搜索哪些属性"的**字符串列表**；
                // 此前误赋 int(1)，语义错误且会干扰自由文本分支，已删除（走系统默认全文属性）。
                helper.QuerySelectColumns = QuerySelectColumns;
                helper.QueryWhereRestrictions = BuildAqs(query);
                helper.MaxColumns = 32;

                var connectionString = (string)helper.ConnectionString;
                var commandText = (string)helper.GenerateSQLFromUserQuery(null);

                dynamic? connection = null;
                dynamic? recordset = null;
                try
                {
                    connection = CreateConnection(connectionString);
                    recordset = connection.Execute(commandText);

                    var count = 0;
                    while (!recordset.EOF && count < MaxResults)
                    {
                        ct.ThrowIfCancellationRequested();
                        var fileName = (string)recordset.Fields["System.FileName"].Value;
                        var path = (string)recordset.Fields["System.ItemPathDisplay"].Value;
                        if (!string.IsNullOrWhiteSpace(fileName))
                        {
                            results.Add(new SearchResult
                            {
                                Title = fileName,
                                Subtitle = path,
                                Category = "File",
                                LaunchPath = path,
                                IconPath = path,
                                Score = ScoreFileName(fileName, query)
                            });
                            count++;
                        }

                        recordset.MoveNext();
                    }
                }
                finally
                {
                    if (recordset is not null)
                    {
                        try { recordset.Close(); } catch { /* 关闭失败忽略 */ }
                        (recordset as IDisposable)?.Dispose();
                    }

                    if (connection is not null)
                    {
                        (connection as IDisposable)?.Dispose();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 取消：仍尝试返回已收集 + 兜底结果（下方合并）
        }
        catch (Exception ex)
        {
            // 索引不可用（服务禁用/未建索引/组件缺失）：文件结果完全由兜底扫描提供（M10）
            _logger.Warn($"[Search:files] Windows Search 不可用，降级为文件系统扫描：{ex.Message}");
        }

        // 总是合并兜底扫描结果（索引结果优先，按 LaunchPath 去重，总数仍受 MaxResults 保护）
        MergeFallbackResults(results, fallbackTask, ct);
        return results;
    }

    /// <summary>合并兜底扫描结果：按 LaunchPath 去重（索引结果优先），总结果数受 MaxResults 上限保护。</summary>
    private static void MergeFallbackResults(
        List<SearchResult> results, Task<IReadOnlyList<SearchResult>> fallbackTask, CancellationToken ct)
    {
        try
        {
            fallbackTask.Wait(ct);
            var seen = new HashSet<string>(
                results.Select(r => r.LaunchPath ?? string.Empty),
                StringComparer.OrdinalIgnoreCase);
            foreach (var item in fallbackTask.Result)
            {
                if (results.Count >= MaxResults) break;
                if (seen.Add(item.LaunchPath ?? string.Empty))
                {
                    results.Add(item);
                }
            }
        }
        catch (OperationCanceledException) { /* 取消/超时：保留已收集结果 */ }
        catch { /* 兜底失败不影响索引结果（M10） */ }
    }

    private static string BuildAqs(string query)
    {
        // 双引号转义避免 AQS 注入。
        var escaped = query.Replace("\"", "\"\"");
        // ~~ = contains（任意子串匹配）：中文无空格分词，`~`（整词匹配）对中文文件名
        // 极不可靠（"项目报告" 按 ~ 匹配 "报告" 常落空），~~ 按子串匹配稳定命中。
        // 全文分支同样用引号包裹的裸词（走系统默认全文属性）。
        return $"filename:~~\"{escaped}\" OR \"{escaped}\"";
    }

    /// <summary>
    /// 按匹配位置打分（聚合层按 Score 降序排序）。量纲与 ProgramSearchProvider.Score 对齐：
    /// 前缀命中（如 "maa" → "MaaEnd-x.zip"）= 55（与应用前缀命中同级，压过散落子序列噪声）；
    /// 中间包含（如 "C-MAA.py"）= 30（高于应用弱子序列 ~6-15，低于各类强信号）。
    /// 索引命中与兜底扫描共用同一规则，保证两类结果排序一致。
    /// </summary>
    private static int ScoreFileName(string fileName, string query)
    {
        return fileName.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 55 : 30;
    }

    // ======== 索引空结果兜底：文件系统实时扫描 ========

    /// <summary>兜底扫描兜底超时（毫秒）：超时返回已收集结果，绝不拖死 UI（上层防抖后仍在后台 Task 执行）。</summary>
    private const int FallbackScanTimeoutMs = 1500;

    /// <summary>兜底扫描的递归深度：盘根一级目录 + 其下两层，覆盖"盘根\中文目录\程序名"这类下载路径。</summary>
    private const int FallbackMaxRecursionDepth = 2;

    private static readonly EnumerationOptions FallbackScanOptions = new()
    {
        MatchCasing = MatchCasing.CaseInsensitive,
        IgnoreInaccessible = true,
        RecurseSubdirectories = true,
        MaxRecursionDepth = FallbackMaxRecursionDepth,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint
    };

    /// <summary>
    /// 文件系统实时兜底扫描：Windows Search 索引不覆盖自定义目录（如 D:\迅雷下载），
    /// 索引查空时按子串通配枚举 常见用户目录 + 各盘符根一级目录，补齐索引盲区。
    /// 三重保护：1.5s 超时 / 递归深度 2 / 结果上限 MaxResults——索引命中时本方法零调用。
    /// </summary>
    private static IReadOnlyList<SearchResult> FallbackFileSystemScan(string query, CancellationToken ct)
    {
        var results = new List<SearchResult>();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(FallbackScanTimeoutMs);
            var pattern = $"*{query}*";

            foreach (var root in GetScanRoots())
            {
                if (timeout.IsCancellationRequested || results.Count >= MaxResults) break;
                try
                {
                    foreach (var path in Directory.EnumerateFiles(root, pattern, FallbackScanOptions))
                    {
                        timeout.Token.ThrowIfCancellationRequested();
                        var fileName = Path.GetFileName(path);
                        results.Add(new SearchResult
                        {
                            Title = fileName,
                            Subtitle = path,
                            Category = "File",
                            LaunchPath = path,
                            IconPath = path,
                            Score = ScoreFileName(fileName, query)
                        });
                        if (results.Count >= MaxResults) break;
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { /* 单个根枚举失败（权限/盘不可用）忽略，继续下一个 */ }
            }
        }
        catch (OperationCanceledException) { /* 超时/取消：返回已收集结果 */ }
        catch { /* 兜底失败不影响主流程（M10） */ }
        return results;
    }

    /// <summary>
    /// 盘根一级目录中跳过的系统大目录（Windows/Program Files/Users 等要么已被索引覆盖、
    /// 要么不应被用户搜索，剪枝后扫描开销大幅下降，能更快到达 D:\迅雷下载 这类自定义目录）。
    /// </summary>
    private static readonly HashSet<string> SkippedRootDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "windows", "windows.old", "program files", "program files (x86)", "programdata",
        "users", "perflogs", "$recycle.bin", "system volume information",
        "recovery", "intel", "amd", "nvidia", "drivers", "dell", "swsetup"
    };

    /// <summary>兜底扫描根：用户下载/桌面/文档 + 各固定盘符根的一级子目录（覆盖中文名目录如"迅雷下载"）。</summary>
    private static IEnumerable<string> GetScanRoots()
    {
        // 1) 用户常见目录（Downloads 无 SpecialFolder 枚举项，按 Known Folder 惯例拼接）
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var knownDirs = new[]
        {
            Path.Combine(profile, "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        foreach (var dir in knownDirs)
        {
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir)) yield return dir;
        }

        // 2) 各固定盘根的一级子目录（忽略盘级异常；一层枚举开销小，
        //    把"D:\迅雷下载"这类中文目录整体纳入递归扫描范围）
        // 注意：yield return 不能出现在带 catch 的 try 块内（CS1626），先物化再返回。
        var driveRootDirs = new List<string>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                driveRootDirs.AddRange(Directory.EnumerateDirectories(drive.RootDirectory.FullName));
            }
            catch { /* 盘级失败忽略 */ }
        }
        foreach (var dir in driveRootDirs)
        {
            var name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (SkippedRootDirNames.Contains(name)) continue;
            yield return dir;
        }
    }

    private static dynamic CreateConnection(string connectionString)
    {
        var connType = Type.GetTypeFromProgID("ADODB.Connection");
        if (connType is null)
        {
            throw new InvalidOperationException("ADODB 组件不可用");
        }

        dynamic conn = Activator.CreateInstance(connType)!;
        conn.ConnectionString = connectionString;
        conn.Open();
        return conn;
    }
}
