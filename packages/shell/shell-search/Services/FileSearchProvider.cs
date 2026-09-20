using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.IndexIpc;
using BetterDesktop.Shell.Search.Contracts;

namespace BetterDesktop.Shell.Search.Services;

/// <summary>
/// 文件搜索。三个来源，按可用性降级：
/// <list type="number">
/// <item><b>常驻索引引擎</b>（M3b）：覆盖「用户 下载/桌面/文档 + 各固定盘顶层目录」（深度 3、剪枝），
/// 内存子串匹配——取代每次查询的磁盘扫描（M0 基线：兜底扫描 105ms/次，是**常态开销**而非降级路径）。</item>
/// <item><b>Windows Search 索引</b>：COM（SearchManager → SystemIndex → ISearchQueryHelper → ADODB）；
/// 引擎可用时仍作为**补充来源**（索引深度与根集之外的文件它可能命中）。</item>
/// <item><b>文件系统兜底扫描</b>：仅在引擎不可用 / 构建中 / 降级时启用，与 Windows Search **并行**执行。</item>
/// </list>
/// 全部 COM / IPC 调用 try-catch：任一来源不可用都不崩、不冒泡（M10）。
/// </summary>
public sealed class FileSearchProvider : ISearchResultProvider
{
    // CLSID_SearchManager
    private const string SearchManagerClsid = "7D096C5F-AC08-4F1F-BEB7-5C22C517CE39";
    private const string QuerySelectColumns = "System.FileName,System.ItemPathDisplay";
    /// <summary>Windows Search / 兜底扫描的返回上限：全量档（2026-09-17 分组展示改造：
    /// 引擎/各来源不做概率截断，全量返回后由 UI 分组折叠 + 筛选，用户主动减少显示）。</summary>
    private const int MaxResults = 100;
    /// <summary>引擎 IPC 单次返回上限：只为防 JSON 体积爆炸（命中超限的极端词仍截尾，
    /// 属安全上限而非概率桶）。UI 分组折叠消费全量，不再依赖此值做候选截断。</summary>
    private const int EnginePageLimit = 1000;

    private readonly IKernelLogger _logger;

    /// <summary>
    /// 索引引擎客户端（可空 = 未装配 → 逐字走原「Windows Search + 兜底扫描」路径）。
    /// 装配失败绝不阻断搜索：文件搜索必须在任何情况下可用。
    /// </summary>
    private readonly IndexIpcClient? _indexClient;

    public string Name => "files";

    public FileSearchProvider(IKernelLogger logger, IndexIpcClient? indexClient = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _indexClient = indexClient;
    }

    public IReadOnlyList<SearchResult> Search(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<SearchResult>();
        }

        // 【M3b · 2026-09-14】引擎优先：常驻索引的根集与剪枝和下方兜底扫描**同源**
        //（用户 下载/桌面/文档 + 各固定盘顶层目录，深度 3），故引擎可用时它以同等覆盖面**取代**磁盘扫描——
        //「磁盘扫描是常态开销而非降级路径」正是立项动机（M0 基线：兜底扫描 105ms/次）。
        if (TryEngineSearch(query, ct) is { } engineResults)
        {
            var fromEngine = new List<SearchResult>(engineResults);
            // Windows Search 仍作补充来源：索引深度（3）与根集之外的文件它可能命中
            CollectWindowsSearch(fromEngine, query, ct);
            return fromEngine;
        }

        // —— 以下为引擎不可用 / 构建中 / 降级时的**原行为**（逐字保留）——
        // 兜底扫描与索引查询**并行**执行。不能只在索引空结果时兜底：
        // 索引会命中同名子串的无关文件（搜 maa 命中 Python 库的 CMAA），占坑导致兜底永不触发，
        // 而 D:\迅雷下载 这类未索引目录的文件只能靠文件系统扫描找到。
        var fallbackTask = Task.Run(() => FallbackFileSystemScan(query, ct), ct);

        var results = new List<SearchResult>();
        CollectWindowsSearch(results, query, ct);

        // 总是合并兜底扫描结果（索引结果优先，按 LaunchPath 去重，总数仍受 MaxResults 保护）
        MergeFallbackResults(results, fallbackTask, ct);
        return results;
    }

    /// <summary>
    /// 引擎文件搜索。失败 / 未装配 / **构建中** / **降级** 一律返回 <c>null</c> → 调用方回退本地实现。
    /// <para>为什么「构建中」也回退：空集会被用户读成「没有这个文件」，而真相是「索引还没建好」。</para>
    /// <para>同步接口内等待异步 IPC：客户端内部已把调用包在 <c>Task.Run</c>，故不会捕获 UI 上下文而死锁
    ///（与 <c>AppSourceService</c> 的引擎路径同一处理）。</para>
    /// </summary>
    private IReadOnlyList<SearchResult>? TryEngineSearch(string query, CancellationToken ct)
    {
        var client = _indexClient;
        if (client is null || !client.IsConnected)
        {
            return null;
        }

        try
        {
            // 全量取（EnginePageLimit=1000）：引擎三轮桶全量收集 + limit 只截返回体积，
            // 2026-09-17 分组展示改造后不再由本层做候选截断——排序与展示由聚合层/UI 分组折叠决定。
            var page = client.TrySearchFilesAsync(query, EnginePageLimit, ct).GetAwaiter().GetResult();
            if (page is null)
            {
                _logger.Warn("[Search:files] 索引引擎构建中或降级 → 回退 Windows Search + 文件系统扫描");
                return null;
            }

            var hits = new List<SearchResult>(page.Files.Count);
            foreach (var hit in page.Files)
            {
                if (string.IsNullOrWhiteSpace(hit.Name) || string.IsNullOrWhiteSpace(hit.Path))
                {
                    continue;
                }

                hits.Add(new SearchResult
                {
                    Title = hit.Name,
                    Subtitle = hit.Path,
                    Category = "File",
                    LaunchPath = hit.Path,
                    IconPath = hit.Path,
                    Score = ScoreFileName(hit.Name, hit.Path, query)
                });
            }

            return hits;
        }
        catch (Exception ex)
        {
            // 引擎不可用不是错误路径（可能只是没装/没启），记 Warn 后走本地实现
            _logger.Warn($"[Search:files] 引擎查询失败，回退本地实现：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Windows Search 索引查询（COM：SearchManager → SystemIndex → ISearchQueryHelper → ADODB），
    /// 结果**追加**到 <paramref name="sink"/>（按 <c>LaunchPath</c> 去重，总数受 <see cref="MaxResults"/> 保护）。
    /// <para>抽成方法是为了让「引擎路径」与「本地回退路径」共用同一实现——不复制那段易碎的 dynamic COM。
    /// 任何 COM 失败都只降级为「本来源无结果」，不影响其它来源（M10）。</para>
    /// </summary>
    private void CollectWindowsSearch(List<SearchResult> sink, string query, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            var managerType = Type.GetTypeFromCLSID(new Guid(SearchManagerClsid));
            if (managerType is null)
            {
                return;
            }

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

            // 已有结果（如引擎命中）参与去重：同一文件不得因两个来源而重复出现
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var existing in sink)
            {
                seen.Add(existing.LaunchPath ?? string.Empty);
            }

            dynamic? connection = null;
            dynamic? recordset = null;
            try
            {
                connection = CreateConnection(connectionString);
                recordset = connection.Execute(commandText);

                while (!recordset.EOF && sink.Count < MaxResults)
                {
                    ct.ThrowIfCancellationRequested();
                    var fileName = (string)recordset.Fields["System.FileName"].Value;
                    // 动态 COM 取值可能为 null → 归一为非空（SearchResult 的路径字段非空）
                    var path = (string)recordset.Fields["System.ItemPathDisplay"].Value ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(fileName) && seen.Add(path))
                    {
                        sink.Add(new SearchResult
                        {
                            Title = fileName,
                            Subtitle = path,
                            Category = "File",
                            LaunchPath = path,
                            IconPath = path,
                            Score = ScoreFileName(fileName, path, query)
                        });
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
        catch (OperationCanceledException)
        {
            // 取消：保留已收集结果
        }
        catch (Exception ex)
        {
            // 索引不可用（服务禁用/未建索引/组件缺失）：本来源无结果，其余来源照常（M10）
            _logger.Warn($"[Search:files] Windows Search 不可用，本来源无结果：{ex.Message}");
        }
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
    /// 中间包含（如 "C-MAA.py"）= 30（高于应用弱子序列 ~6-15，低于各类强信号）；
    /// 纯路径命中（如按目录名「迅雷下载」搜，仅完整路径包含）= 20——低于一切文件名命中，
    /// 引擎路径匹配（2026-09-17）新增档，不得压过文件名匹配。
    /// 索引命中与兜底扫描共用同一规则，保证两类结果排序一致。
    /// </summary>
    internal static int ScoreFileName(string fileName, string path, string query)
    {
        if (fileName.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 55;
        if (fileName.Contains(query, StringComparison.OrdinalIgnoreCase)) return 30;
        if (path is not null && path.Contains(query, StringComparison.OrdinalIgnoreCase)) return 20;
        return 30;
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
                            Score = ScoreFileName(fileName, path, query)
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
