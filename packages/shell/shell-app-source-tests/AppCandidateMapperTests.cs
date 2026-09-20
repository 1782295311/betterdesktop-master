using BetterDesktop.Shell.AppSource.Models;
using BetterDesktop.Shell.AppSource.Services;
using BetterDesktop.Shell.IndexIpc;
using Xunit;

namespace BetterDesktop.Shell.AppSource.Tests;

/// <summary>
/// 【M2 · 2026-09-13】引擎候选 → <see cref="AppItem"/> 的**升格语义**回归。
/// <para>
/// 这些用例锁死两条与本地实现**逐条对齐**的规则（否则引擎路径与本地路径会产出不同结果集，
/// 用户在开始菜单/应用提取器里看到条数变化）：
/// ① 程序盘点候选：Id 与本地同源（<c>AppSourceService.CreateStableId</c>，2026-09-14 由 <c>"all:"+path</c> 改）、
/// TargetPath 空则回退自身路径、**不跑**过滤链；
/// ② 开始菜单候选：**复用**本地 <c>ResolveFromPath</c>（过滤链语义不变）。
/// </para>
/// </summary>
public class AppCandidateMapperTests
{
    [Fact]
    public void ProgramFilesCandidate_UsesStableIdAndSelfTargetFallback()
    {
        var candidate = new AppCandidate
        {
            Path = @"D:\Apps\Tool\tool.exe",
            // 引擎把 targetPath 也填成自身路径（不解析 lnk）；此处给空值以验证回退分支
            TargetPath = string.Empty,
            NameHint = "tool",
            Source = AppCandidateMapper.EngineSourceProgramFiles,
        };

        var item = AppCandidateMapper.FromProgramFilesCandidate(candidate);

        Assert.NotNull(item);
        // .exe 的 Resolve 目标即自身 → 稳定 Id = 路径本身（**不再带 "all:" 前缀**，见 StableAppIdTests）
        Assert.Equal(candidate.Path, item!.Id.Value);
        Assert.Equal(candidate.Path, item.ShortcutPath);
        Assert.False(string.IsNullOrWhiteSpace(item.TargetPath), "TargetPath 不得为空（应回退自身路径或解析结果）");
        Assert.False(string.IsNullOrWhiteSpace(item.Name));
    }

    [Fact]
    public void ProgramFilesCandidate_EmptyPath_ReturnsNull()
    {
        var candidate = new AppCandidate { Path = "   ", Source = AppCandidateMapper.EngineSourceProgramFiles };
        Assert.Null(AppCandidateMapper.FromProgramFilesCandidate(candidate));
    }

    /// <summary>开始菜单候选必须**走本地升格委托**（过滤链）—— 不得自行构造 AppItem。</summary>
    [Fact]
    public void StartMenuCandidate_DelegatesToLocalResolve()
    {
        var candidate = new AppCandidate
        {
            Path = @"C:\Menu\App.lnk",
            Source = AppCandidateMapper.EngineSourceStartMenu,
        };

        string? seen = null;
        // 全限定：测试命名空间 `...AppSource.Tests` 会把 `AppSource` 解析成命名空间段（CS0234）
        var expected = new AppItem
        {
            Id = new AppItemId("x"),
            Name = "App",
            Source = BetterDesktop.Shell.AppSource.Models.AppSource.StartMenu,
        };

        var item = AppCandidateMapper.FromStartMenuCandidate(candidate, path =>
        {
            seen = path;
            return expected;
        });

        Assert.Same(expected, item);
        Assert.Equal(candidate.Path, seen);
    }

    /// <summary>被本地过滤链拒收（返回 null）时，引擎候选同样应被丢弃（不产生"幽灵应用"）。</summary>
    [Fact]
    public void StartMenuCandidate_FilteredByLocalResolve_ReturnsNull()
    {
        var candidate = new AppCandidate
        {
            Path = @"C:\Menu\readme.lnk",
            Source = AppCandidateMapper.EngineSourceStartMenu,
        };

        Assert.Null(AppCandidateMapper.FromStartMenuCandidate(candidate, _ => null));
    }

    [Fact]
    public void StartMenuCandidate_EmptyPath_SkipsResolve()
    {
        var candidate = new AppCandidate { Path = string.Empty, Source = AppCandidateMapper.EngineSourceStartMenu };

        var called = false;
        var item = AppCandidateMapper.FromStartMenuCandidate(candidate, _ =>
        {
            called = true;
            return null;
        });

        Assert.Null(item);
        Assert.False(called, "空路径不应触发本地解析");
    }

    /// <summary>边界：空参数不得抛（引擎数据来自外部进程，必须按"不可信输入"处理）。</summary>
    [Fact]
    public void Mapper_RejectsNullCandidate()
    {
        Assert.Throws<ArgumentNullException>(() => AppCandidateMapper.FromProgramFilesCandidate(null!));
        Assert.Throws<ArgumentNullException>(() => AppCandidateMapper.FromStartMenuCandidate(null!, _ => null));
    }
}
