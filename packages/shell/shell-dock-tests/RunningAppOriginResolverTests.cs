using System;
using System.Collections.Generic;
using BetterDesktop.Shell.Dock.Services;
using Xunit;

namespace BetterDesktop.Shell.Dock.Tests;

/// <summary>
/// 【T3 · 2026-09-14】运行项「溯源到应用本体」判定回归。
///
/// 锁死三件事（都来自真机教训）：
/// ① **由近及远取第一个已知应用** —— helper 子进程 → 取到主应用本体；
/// ② **不猜** —— 链上全未知时返回 null（调用方退回原路径 + 记日志），
///    绝不按"路径相似 / 同安装根"去认亲（S8 第 ④ 级就是这样误绑的：WorkBuddy → CodeBuddy CN）；
/// ③ 空项/空白项按未知跳过（进程链上有取不到路径的进程是常态）。
/// </summary>
public sealed class RunningAppOriginResolverTests
{
    private static Func<string, bool> Known(params string[] known)
    {
        var set = new HashSet<string>(known, StringComparer.OrdinalIgnoreCase);
        return path => set.Contains(path);
    }

    /// <summary>运行 exe 本身就在应用索引里 → 取它自己（= 现状行为，不得改变）。</summary>
    [Fact]
    public void Resolve_PrefersSelf_WhenSelfIsKnown()
    {
        var ancestors = new[] { @"C:\Apps\Code.exe", @"C:\Apps\launcher.exe" };

        var origin = RunningAppOriginResolver.Resolve(
            ancestors,
            Known(@"C:\Apps\Code.exe", @"C:\Apps\launcher.exe"));

        Assert.Equal(@"C:\Apps\Code.exe", origin);
    }

    /// <summary>运行进程未登记（helper/子进程）→ 向上一层取到已知的父应用（本体）。</summary>
    [Fact]
    public void Resolve_WalksUpToParent_WhenSelfIsUnknown()
    {
        var ancestors = new[] { @"C:\Apps\Code Helper.exe", @"C:\Apps\Code.exe" };

        var origin = RunningAppOriginResolver.Resolve(ancestors, Known(@"C:\Apps\Code.exe"));

        Assert.Equal(@"C:\Apps\Code.exe", origin);
    }

    /// <summary>链上全未知 → null（调用方退回原路径 + 记日志；**不许猜**）。</summary>
    [Fact]
    public void Resolve_ReturnsNull_WhenNothingOnChainIsKnown()
    {
        var ancestors = new[] { @"D:\Games\Game.exe", @"D:\Games\launcher.exe" };

        Assert.Null(RunningAppOriginResolver.Resolve(ancestors, Known(@"C:\SomethingElse.exe")));
    }

    /// <summary>取"最近"的已知者，而不是最外层：游戏本体已知时就不该被替换成启动器。</summary>
    [Fact]
    public void Resolve_TakesNearestKnown_NotOutermost()
    {
        var ancestors = new[] { @"D:\Games\Game.exe", @"D:\Games\launcher.exe" };

        var origin = RunningAppOriginResolver.Resolve(
            ancestors,
            Known(@"D:\Games\Game.exe", @"D:\Games\launcher.exe"));

        Assert.Equal(@"D:\Games\Game.exe", origin);
    }

    /// <summary>空白项按未知跳过（链上取不到路径的进程是常态，不得据此短路）。</summary>
    [Fact]
    public void Resolve_SkipsBlankEntries()
    {
        var ancestors = new[] { "  ", string.Empty, @"C:\Apps\Code.exe" };

        Assert.Equal(
            @"C:\Apps\Code.exe",
            RunningAppOriginResolver.Resolve(ancestors, Known(@"C:\Apps\Code.exe")));
    }

    /// <summary>空链 → null（不抛）。</summary>
    [Fact]
    public void Resolve_EmptyChain_ReturnsNull()
    {
        Assert.Null(RunningAppOriginResolver.Resolve(Array.Empty<string>(), Known(@"C:\Apps\Code.exe")));
    }
}
