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

        try
        {
            ct.ThrowIfCancellationRequested();

            var managerType = Type.GetTypeFromCLSID(new Guid(SearchManagerClsid));
            if (managerType is null)
            {
                return Array.Empty<SearchResult>();
            }

            dynamic manager = Activator.CreateInstance(managerType)!;
            dynamic catalog = manager.GetCatalog("SystemIndex");
            dynamic helper = catalog.GetQueryHelper();
            helper.QueryContentProperties = 1; // QUERY_CONTENT_PROPERTIES_ADVANCED_QUERY_SYNTAX
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

                var results = new List<SearchResult>();
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
                            Score = 1
                        });
                        count++;
                    }

                    recordset.MoveNext();
                }

                return results;
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
            return Array.Empty<SearchResult>();
        }
        catch (Exception ex)
        {
            _logger.Warn($"[Search:files] Windows Search 不可用，降级为空：{ex.Message}");
            return Array.Empty<SearchResult>();
        }
    }

    private static string BuildAqs(string query)
    {
        // 文件名子串匹配 + 全文；双引号转义避免 AQS 注入。
        var escaped = query.Replace("\"", "\"\"");
        return $"filename:~\"{escaped}\" OR {escaped}";
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
