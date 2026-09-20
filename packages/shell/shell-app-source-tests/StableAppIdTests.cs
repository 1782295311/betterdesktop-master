using System;
using System.Collections.Generic;
using System.IO;
using BetterDesktop.Shell.AppSource.Models;
using BetterDesktop.Shell.AppSource.Services;
using BetterDesktop.Shell.IndexIpc;
using Xunit;
using AppSourceKind = BetterDesktop.Shell.AppSource.Models.AppSource;

namespace BetterDesktop.Shell.AppSource.Tests;

/// <summary>
/// 【S1 · 2026-09-14】稳定 Id 统一性回归 ——「同一个程序在两种模式与固定库里必须是同一个 Id」。
/// <para>
/// 背景：全程序模式曾自造 <c>"all:" + 路径</c> 前缀，而干净模式与固定库用
/// <c>AppSourceService.CreateStableId</c>（真实目标路径）。后果是固定态在两种模式间分裂：
/// 全程序模式右键永远只有「固定到 Dock」、绿点不显示、「已固定」筛选恒空、新装提醒错乱。
/// </para>
/// <para>
/// 本文件同时锁死**引擎路径与本地路径的平价**：M2 建立的「条目数一致」若 Id 不一致即失效
/// （固定态、去重、固定集合匹配全部依赖 Id）。
/// </para>
/// </summary>
public class StableAppIdTests
{
    // ------------------------------------------------------------- Id 契约

    /// <summary>非 Store：优先真实目标路径（.lnk 指向谁就是谁）；目标缺失才回退快捷方式自身。</summary>
    [Theory]
    [InlineData(@"D:\Apps\a\a.exe", @"D:\Apps\a\a.exe", @"D:\Apps\a\a.exe")]
    [InlineData(@"C:\Menu\a.lnk", @"D:\Apps\a\a.exe", @"D:\Apps\a\a.exe")]
    [InlineData(@"C:\Menu\a.lnk", "", @"C:\Menu\a.lnk")]
    public void CreateStableId_NonStore_PrefersTargetPath(string shortcut, string target, string expected)
    {
        var id = AppSourceService.CreateStableId(AppSourceKind.Installed, shortcut, target);

        Assert.Equal(expected, id.Value);
    }

    /// <summary>Store 以 AUMID（承载在 targetPath）为主键；AUMID 缺失时回退路径，**不得产出空 Id**。</summary>
    [Theory]
    [InlineData("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App")]
    [InlineData("", @"C:\Menu\store.lnk")]
    [InlineData("   ", @"C:\Menu\store.lnk")]
    public void CreateStableId_Store_UsesAumid_WithPathFallback(string target, string expected)
    {
        var id = AppSourceService.CreateStableId(AppSourceKind.Store, @"C:\Menu\store.lnk", target);

        Assert.Equal(expected, id.Value);
        Assert.False(id.IsEmpty, "任何来源都不得产出空 Id（空 Id 会让固定集合里的项互相碰撞）");
    }

    // ------------------------------------------------------------- 两条路径一致（本缺陷的核心）

    /// <summary>
    /// 同一个 .exe：引擎升格路径（<see cref="AppCandidateMapper"/>）与本地解析路径
    /// （<c>ResolveFromPath</c>）必须产出**同一个 Id** —— 这正是「固定态在两种模式间通用」的前提。
    /// </summary>
    [Fact]
    public void EngineCandidate_AndLocalResolve_ProduceSameId()
    {
        var dir = CreateTempDir();
        try
        {
            var exe = Path.Combine(dir, "contoso-app.exe");
            File.WriteAllBytes(exe, Array.Empty<byte>());

            using var service = new AppSourceService(dataDirectory: dir);
            var local = service.ResolveFromPath(exe);
            Assert.NotNull(local);

            var engine = AppCandidateMapper.FromProgramFilesCandidate(new AppCandidate
            {
                Path = exe,
                TargetPath = exe,
                NameHint = "contoso-app",
                Source = AppCandidateMapper.EngineSourceProgramFiles,
            });
            Assert.NotNull(engine);

            Assert.Equal(local!.Id, engine!.Id);

            // 回归护栏：若 "all:" 前缀被重新引入，本断言立即失败（那就是缺陷重现）。
            Assert.DoesNotContain("all:", engine.Id.Value, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    /// <summary>
    /// 固定集合的匹配语义：固定库里的 Id（<c>AddByPath</c> → <c>ResolveFromPath</c>）
    /// 必须能被全程序模式的条目命中，且大小写不敏感（<c>AppItemId</c> 比较为 OrdinalIgnoreCase）。
    /// </summary>
    [Fact]
    public void PinnedId_MatchesAllProgramsEntry_CaseInsensitively()
    {
        var pinnedDir = @"D:\Apps\Demo";
        var pinnedId = AppSourceService.CreateStableId(
            AppSourceKind.Installed, Path.Combine(pinnedDir, "demo.lnk"), Path.Combine(pinnedDir, "demo.exe"));

        var entryId = AppSourceService.CreateStableId(
            AppSourceKind.Installed, Path.Combine(pinnedDir, "demo.exe"), Path.Combine(pinnedDir, "demo.exe"));

        Assert.Equal(pinnedId, entryId);

        // 固定集合是 HashSet<DockItemId>/HashSet<AppItemId>：大小写不同也必须命中（OrdinalIgnoreCase）。
        var pinnedSet = new HashSet<AppItemId> { pinnedId };
        Assert.Contains(new AppItemId(pinnedId.Value.ToUpperInvariant()), pinnedSet);
    }

    // ------------------------------------------------------------- 去重

    /// <summary>
    /// 同一 Id 的多个文件只保留**首次**出现：下游 <c>DockItemData.Id</c> 是字典键
    /// （<c>AppGrabberWindow._containers</c>），重复键会让容器/角标/批量编号互相覆盖。
    /// </summary>
    [Fact]
    public void DedupByStableId_KeepsFirstOccurrence()
    {
        var items = new List<AppItem>
        {
            MakeItem(@"D:\Apps\a\a.exe", "A（先）"),
            MakeItem(@"D:\Apps\b\b.exe", "B"),
            MakeItem(@"D:\APPS\A\A.EXE", "A（后，仅大小写不同）"),
        };

        var result = AppSourceService.DedupByStableId(items);

        Assert.Equal(2, result.Count);
        Assert.Equal("A（先）", result[0].Name);
        Assert.Equal("B", result[1].Name);
    }

    /// <summary>无重复时顺序与内容不得变化（去重是恒等变换，不是重排）。</summary>
    [Fact]
    public void DedupByStableId_NoDuplicates_PreservesOrder()
    {
        var items = new List<AppItem>
        {
            MakeItem(@"D:\Apps\a\a.exe", "A"),
            MakeItem(@"D:\Apps\b\b.exe", "B"),
            MakeItem(@"D:\Apps\c\c.exe", "C"),
        };

        var result = AppSourceService.DedupByStableId(items);

        Assert.Equal(3, result.Count);
        Assert.Equal(new[] { "A", "B", "C" }, new[] { result[0].Name, result[1].Name, result[2].Name });
    }

    [Fact]
    public void DedupByStableId_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(AppSourceService.DedupByStableId(new List<AppItem>()));
    }

    // ------------------------------------------------------------- helpers

    private static AppItem MakeItem(string id, string name) => new()
    {
        Id = new AppItemId(id),
        Name = name,
        ShortcutPath = id,
        TargetPath = id,
        Source = AppSourceKind.Installed,
    };

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bd-stableid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响断言（Windows 上偶发文件句柄延迟释放）。
        }
    }
}
