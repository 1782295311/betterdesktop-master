using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BetterDesktop.Shell.Search.Contracts;
using BetterDesktop.Shell.Search.Services;
using Xunit;

namespace BetterDesktop.Shell.Search.Tests;

/// <summary>
/// 聚合服务分组排序测试（2026-09-17 分组展示改造）：
/// ①App 固定第一组（程序优先级最前）；②其余类别按组内命中数升序（结果少的类别放前，
/// 同数 Settings 在 File 前）；③组内 Score 降序；④全量返回不截断（>30 条不丢）。
/// </summary>
public class StartMenuSearchServiceGroupingTests
{
    private sealed class FakeProvider : ISearchResultProvider
    {
        private readonly IReadOnlyList<SearchResult> _results;

        public FakeProvider(string name, IReadOnlyList<SearchResult> results)
        {
            Name = name;
            _results = results;
        }

        public string Name { get; }

        public IReadOnlyList<SearchResult> Search(string query, CancellationToken ct) => _results;
    }

    private static SearchResult Make(string category, string title, int score) => new()
    {
        Title = title,
        Category = category,
        Score = score
    };

    private static async System.Threading.Tasks.Task<IReadOnlyList<SearchResult>> SearchAsync(ISearchResultProvider provider)
    {
        var service = new StartMenuSearchService();
        service.RegisterProvider(provider);
        return await service.SearchAsync("q", CancellationToken.None);
    }

    [Fact]
    public async System.Threading.Tasks.Task AppGroup_AlwaysFirst_EvenWhenFileHasMoreHits()
    {
        // App 10 条 vs File 20 条 → App 组仍第一（程序优先级 > 结果少类别排序）
        var results = new List<SearchResult>();
        for (var i = 0; i < 10; i++) results.Add(Make("App", $"App{i}", 100 - i));
        for (var i = 0; i < 20; i++) results.Add(Make("File", $"F{i}", 90 - i));

        var ranked = await SearchAsync(new FakeProvider("mix", results));

        Assert.Equal(30, ranked.Count);
        Assert.All(ranked.Take(10), r => Assert.Equal("App", r.Category, ignoreCase: true));
        Assert.All(ranked.Skip(10), r => Assert.Equal("File", r.Category, ignoreCase: true));
    }

    [Fact]
    public async System.Threading.Tasks.Task FewerHitsCategory_ComesBefore_MoreHitsCategory()
    {
        // 无 App；File 2 条 < Settings 6 条 → File 组在前（结果少的类别放前）
        var results = new List<SearchResult>
        {
            Make("Settings", "S1", 50),
            Make("Settings", "S2", 50),
            Make("Settings", "S3", 50),
            Make("Settings", "S4", 50),
            Make("Settings", "S5", 50),
            Make("Settings", "S6", 50),
            Make("File", "F1", 99),
            Make("File", "F2", 99)
        };

        var ranked = await SearchAsync(new FakeProvider("mix", results));

        Assert.Equal(8, ranked.Count);
        Assert.Equal("File", ranked[0].Category, ignoreCase: true);
        Assert.Equal("File", ranked[1].Category, ignoreCase: true);
        Assert.All(ranked.Skip(2), r => Assert.Equal("Settings", r.Category, ignoreCase: true));
    }

    [Fact]
    public async System.Threading.Tasks.Task SameCountCategories_SettingsBeforeFile_StableOrder()
    {
        var results = new List<SearchResult>();
        for (var i = 0; i < 3; i++) results.Add(Make("File", $"F{i}", 60));
        for (var i = 0; i < 3; i++) results.Add(Make("Settings", $"S{i}", 60));

        var ranked = await SearchAsync(new FakeProvider("mix", results));

        Assert.All(ranked.Take(3), r => Assert.Equal("Settings", r.Category, ignoreCase: true));
        Assert.All(ranked.Skip(3), r => Assert.Equal("File", r.Category, ignoreCase: true));
    }

    [Fact]
    public async System.Threading.Tasks.Task WithinGroup_ScoreDescending()
    {
        var results = new List<SearchResult>
        {
            Make("App", "low", 10),
            Make("App", "high", 90),
            Make("App", "mid", 50)
        };

        var ranked = await SearchAsync(new FakeProvider("apps", results));

        Assert.Equal(new[] { "high", "mid", "low" }, ranked.Select(r => r.Title));
    }

    [Fact]
    public async System.Threading.Tasks.Task NoTruncation_AllResultsReturned()
    {
        // 40 条（> 旧 Take(30) 上限）→ 全量返回，一条不丢
        var results = new List<SearchResult>();
        for (var i = 0; i < 40; i++) results.Add(Make("File", $"F{i}", 30));

        var ranked = await SearchAsync(new FakeProvider("many", results));

        Assert.Equal(40, ranked.Count);
    }

    [Fact]
    public async System.Threading.Tasks.Task EmptyQuery_ReturnsEmpty()
    {
        var service = new StartMenuSearchService();
        service.RegisterProvider(new FakeProvider("any", new List<SearchResult> { Make("App", "A", 10) }));

        var ranked = await service.SearchAsync("   ", CancellationToken.None);

        Assert.Empty(ranked);
    }
}
