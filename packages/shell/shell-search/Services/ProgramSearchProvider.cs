using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.AppSource.Contracts;
using BetterDesktop.Shell.AppSource.Models;
using BetterDesktop.Shell.Search.Contracts;

namespace BetterDesktop.Shell.Search.Services;

/// <summary>
/// 程序搜索：对 IAppSourceService 的缓存列表（开始菜单 + 已安装注册表，按 Id 去重）做子序列模糊匹配。
/// 参考 Open-Shell SearchManager：查询字符按顺序出现在名称中即匹配；
/// 词首（开头 / 分隔符后 / 大写字母）命中加分，连续匹配加分。
/// 语料不纳入 ScanAllPrograms（磁盘深度遍历、无缓存，逐键调用会卡输入）。
/// </summary>
public sealed class ProgramSearchProvider : ISearchResultProvider
{
    private readonly IAppSourceService _appSource;
    private readonly IKernelLogger _logger;

    public string Name => "programs";

    public ProgramSearchProvider(IAppSourceService appSource, IKernelLogger logger)
    {
        _appSource = appSource ?? throw new ArgumentNullException(nameof(appSource));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public IReadOnlyList<SearchResult> Search(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<SearchResult>();
        }

        var results = new List<SearchResult>();
        var seen = new HashSet<AppItemId>();

        try
        {
            foreach (var app in _appSource.ScanStartMenu().Concat(_appSource.ScanInstalledApps()))
            {
                ct.ThrowIfCancellationRequested();
                if (!seen.Add(app.Id))
                {
                    continue;
                }

                var score = Score(query, app.Name);
                if (score <= 0)
                {
                    continue;
                }

                results.Add(new SearchResult
                {
                    Title = app.Name,
                    Subtitle = app.ShortcutPath,
                    Category = "App",
                    AppItem = app,
                    IconPath = !string.IsNullOrWhiteSpace(app.IconCacheKey) ? app.IconCacheKey : app.TargetPath,
                    Score = score
                });
            }
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<SearchResult>();
        }
        catch (Exception ex)
        {
            _logger.Error($"[Search:programs] 搜索失败：{ex.Message}");
            return Array.Empty<SearchResult>();
        }

        // 全量返回（2026-09-17 分组展示改造）：不做 Take 截断——所有适配结果都交给
        // 聚合层分组排序 + UI 筛选/折叠，由用户主动减少显示，而不是在这里概率截断。
        return results
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 子序列匹配打分：query 字符按顺序出现在 name 中则得分；完整匹配才有分。
    /// 词首命中 +3（开头 / 大写 / 分隔符后）、连续匹配 +2、每字符基础 +1。
    /// 例：查询 "not" 能匹配 "Notepad"（10 分）；"gc" 能匹配 "Google Chrome"（双词首 8 分）。
    /// 【强信号加分，统一跨 Provider 量纲】（FileSearchProvider.ScoreFileName 同一套基准）：
    ///   - 名字以 query 开头 +50：前缀命中是最高置信信号（"MAA" 对 "maa"、"Notepad" 对 "not"），
    ///     否则会被散落子序列噪声压过（搜 maa 时 "Microsoft Visual..."（M..a..a = 6 分）
    ///     曾压过文件 "MAA.exe" 前缀命中）。
    ///   - query 作为完整词出现 +40：词边界精确命中（"Google Chrome" 对 "chrome"）。
    /// </summary>
    internal static int Score(string query, string name)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrEmpty(name))
        {
            return 0;
        }

        var qi = 0;
        var score = 0;
        var run = 0;
        var wordStart = true;

        for (var ni = 0; ni < name.Length && qi < query.Length; ni++)
        {
            if (char.ToLowerInvariant(name[ni]) != char.ToLowerInvariant(query[qi]))
            {
                run = 0;
                wordStart = ni == 0
                    || char.IsUpper(name[ni])
                    || name[ni] is ' ' or '-' or '_' or '.' or '(' or '（' or '/' or '\\';
                continue;
            }

            qi++;
            score += 1;          // 基础分
            if (wordStart)
            {
                score += 3;      // 词首 / 首字母命中
            }

            if (run > 0)
            {
                score += 2;      // 连续匹配
            }

            run++;
            wordStart = false;
        }

        if (qi < query.Length)
        {
            return 0;
        }

        // 强信号加分（在子序列累计之上）：
        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            score += 50;         // 前缀命中：最高置信
        }
        else if (ContainsWholeWord(name, query))
        {
            score += 40;         // 完整词命中：词边界精确（如 "Google Chrome" 对 "chrome"）
        }

        return score;
    }

    /// <summary>query 是否作为完整词出现在 name 中（按空白/常用分隔符切词后任一词与 query 相等，忽略大小写）。</summary>
    private static bool ContainsWholeWord(string name, string query)
    {
        var separators = new[] { ' ', '-', '_', '.', '(', '（', ')', '）', '/', '\\', '+' };
        foreach (var word in name.Split(separators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(word, query, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
