using System;
using System.Collections.Generic;
using System.Linq;
using BetterDesktop.Shell.Dock.Models;
using BetterDesktop.Shell.Dock.Services;
using Xunit;

namespace BetterDesktop.Shell.Dock.Tests;

/// <summary>
/// 【S8 · 2026-09-14】固定项**多级重绑**回归。
/// <para>
/// 旧实现只按「名称精确匹配」反查注册表：更新后改了版本目录还能救，「改名 + 换目录」就断链
/// （被判为已卸载而让位）。这些用例逐级锁死：App Paths → 同名 → 同安装根 → Store AUMID，
/// 并锁死**保守边界**（Store 列表为空 = 未知，不得据此判孤）。
/// </para>
/// </summary>
public class PinnedRebindResolverTests
{
    private const string OldEdge = @"C:\Program Files (x86)\Microsoft\Edge\Application\151.0.4129.101\msedge.exe";
    private const string NewEdge = @"C:\Program Files (x86)\Microsoft\Edge\Application\152.0.4389.90\msedge.exe";

    // ------------------------------------------------------------- ② App Paths

    [Fact]
    public void Resolve_AppPathsHit_ReturnsRegisteredPath()
    {
        // exe 名未变、安装目录变了 → App Paths 是安装器注册的规范入口，优先救回
        var sources = Sources(appPaths: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["msedge.exe"] = NewEdge,
        });

        Assert.Equal(NewEdge, PinnedRebindResolver.Resolve(App("Microsoft Edge", OldEdge), sources));
    }

    [Fact]
    public void Resolve_AppPathsHitButFileMissing_FallsThrough()
    {
        var sources = Sources(
            installed: new[] { App("Microsoft Edge", NewEdge) },
            appPaths: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["msedge.exe"] = @"C:\Stale\msedge.exe",
            },
            exists: path => path != @"C:\Stale\msedge.exe");

        Assert.Equal(NewEdge, PinnedRebindResolver.Resolve(App("Microsoft Edge", OldEdge), sources));
    }

    // ------------------------------------------------------------- ③ 同名

    [Fact]
    public void Resolve_SameNameInInstalled_ReturnsItsPath()
    {
        var sources = Sources(installed: new[] { App("原神", @"D:\Genshin\GenshinImpact.exe") });

        Assert.Equal(@"D:\Genshin\GenshinImpact.exe", PinnedRebindResolver.Resolve(App("原神", @"D:\Old\GenshinImpact.exe"), sources));
    }

    // ------------------------------------------------------------- ④ 同安装根（本项新增能力）

    [Fact]
    public void Resolve_SameInstallRootWithDifferentName_StillMatches()
    {
        // 关键用例：更新后**显示名也变了**，只有安装根目录一致（…\Edge\Application）
        var sources = Sources(installed: new[] { App("Microsoft Edge (new)", NewEdge) });

        Assert.Equal(NewEdge, PinnedRebindResolver.Resolve(App("Microsoft Edge (old)", OldEdge), sources));
    }

    [Fact]
    public void Resolve_AmbiguousSameNameAcrossRoots_DoesNotGuess()
    {
        // 两个「原神」（正式服 / B 服）同名不同根：猜错等于静默启动错误的程序 → 不猜，判失效
        var sources = Sources(installed: new[]
        {
            App("原神", @"D:\GenshinB\GenshinImpact.exe"),
            App("原神", @"D:\GenshinC\GenshinImpact.exe"),
        });

        Assert.Null(PinnedRebindResolver.Resolve(App("原神", @"D:\GenshinA\GenshinImpact.exe"), sources));
    }

    [Fact]
    public void Resolve_UniqueSameNameDifferentRoot_Rebinds()
    {
        // 唯一同名候选（应用重装到别处、只此一个）→ 仍绑回，避免用户莫名丢掉固定项
        var sources = Sources(installed: new[] { App("原神", @"E:\Games\Genshin\GenshinImpact.exe") });

        Assert.Equal(@"E:\Games\Genshin\GenshinImpact.exe",
            PinnedRebindResolver.Resolve(App("原神", @"D:\GenshinA\GenshinImpact.exe"), sources));
    }

    [Fact]
    public void Resolve_ShallowDirectoriesOnSameDrive_DoNotShareRoot()
    {
        // 盘根不算根：D:\Gone 与 D:\Other 的「父目录的父目录」都是 D:\，不得据此误绑
        var sources = Sources(installed: new[] { App("别的应用", @"D:\Other\other.exe") });

        Assert.Null(PinnedRebindResolver.Resolve(App("被卸载的应用", @"D:\Gone\gone.exe"), sources));
    }

    // ------------------------------------------------------------- ① Store / AUMID

    [Fact]
    public void Resolve_StoreAppStillInstalled_ReturnsAumid()
    {
        var sources = Sources(storeIds: new[] { "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App" });

        var probe = App("计算器", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", DockAppType.Uwp,
            aumid: "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App");

        Assert.Equal("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", PinnedRebindResolver.Resolve(probe, sources));
    }

    [Fact]
    public void Resolve_StoreAppUninstalled_ReturnsNull()
    {
        // 旧实现对 UWP「一律保留」→ 卸载后永久占位；现在按 AUMID 存在性判定
        var sources = Sources(storeIds: new[] { "SomeOther.App_123!App" });

        var probe = App("计算器", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", DockAppType.Uwp,
            aumid: "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App");

        Assert.Null(PinnedRebindResolver.Resolve(probe, sources));
    }

    [Fact]
    public void Resolve_StoreListUnknown_KeepsItem()
    {
        // Store 列表为空 = 未知（AppsFolderSource 不可用）→ **不得判孤**（保守纪律）
        var sources = Sources(storeIds: Array.Empty<string>());

        var probe = App("计算器", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", DockAppType.Uwp,
            aumid: "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App");

        Assert.Equal("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", PinnedRebindResolver.Resolve(probe, sources));
    }

    // ------------------------------------------------------------- 边界

    [Fact]
    public void Resolve_NothingMatches_ReturnsNull()
    {
        // 全失配 → 调用方判 Orphaned（让位但保留快照）
        var sources = Sources(installed: new[] { App("别的应用", @"C:\Program Files\Other\other.exe") });

        Assert.Null(PinnedRebindResolver.Resolve(App("被卸载的应用", @"D:\Gone\gone.exe"), sources));
    }

    [Fact]
    public void Resolve_FallsBackToShortcutPathWhenTargetMissing()
    {
        var sources = Sources(
            installed: new[] { App("工具", @"D:\Gone\tool.exe") with { ShortcutPath = @"D:\Menu\tool.lnk" } },
            exists: path => path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(@"D:\Menu\tool.lnk", PinnedRebindResolver.Resolve(App("工具", @"D:\Old\tool.exe"), sources));
    }

    [Fact]
    public void Resolve_ProbeWithoutPath_DoesNotThrow()
    {
        var sources = Sources();

        Assert.Null(PinnedRebindResolver.Resolve(
            App("无路径", target: null) with { ShortcutPath = string.Empty, TargetPath = string.Empty },
            sources));
    }

    [Fact]
    public void Resolve_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => PinnedRebindResolver.Resolve(null!, Sources()));
        Assert.Throws<ArgumentNullException>(() => PinnedRebindResolver.Resolve(App("a", @"D:\a.exe"), null!));
    }

    // ------------------------------------------------------------- 共享容器根（T4 · 2026-09-14 真机回归）

    /// <summary>
    /// 【真机事故回归】第 ④ 级「同安装根」**不得跨共享容器根**认亲。
    ///
    /// <para>事故现场：`%LocalAppData%\Programs` 下的 <c>WorkBuddy.exe</c>（旧路径已不存在）被第 ④ 级
    /// 错指到同根下的 <c>CodeBuddy CN.exe</c>（**完全不同的程序**，用户当场发现固定区出现两个一样的图标）；
    /// 另一起：`Steam\steamapps\common` 下的小黑盒加速器被错指到 <c>BongoCat.exe</c>。</para>
    ///
    /// <para>根因：这些根目录下面并排放着几十个互不相干的应用，单看「同根」必然误绑 ——
    /// 与「盘根不算根」是同一条道理，只是低了一层。**判失效（交给用户手动指认）远好于猜错**。</para>
    /// </summary>
    [Fact]
    public void Resolve_DoesNotRebindAcrossSharedContainerRoot()
    {
        var probe = App("WorkBuddy", @"C:\Users\u\AppData\Local\Programs\WorkBuddy\WorkBuddy.exe");
        var sibling = App("CodeBuddy CN", @"C:\Users\u\AppData\Local\Programs\CodeBuddy CN\CodeBuddy CN.exe");

        var hit = PinnedRebindResolver.Resolve(probe, Sources(installed: new[] { sibling }));

        Assert.Null(hit); // 必须判 Orphaned（让位 + 保留快照），不得跨共享容器绑定
    }

    /// <summary>
    /// 反向对照：**专属**根（应用自己的目录树）**仍应**能救回 —— 上面那条修复不得顺手把合法场景杀掉。
    /// （版本化目录更新后改了显示名/版本目录，正是第 ④ 级存在的理由。）
    /// </summary>
    [Fact]
    public void Resolve_StillRebindsWithinDedicatedRoot()
    {
        var probe = App("Blender", @"D:\Program Files\Blender Foundation\Blender 3.6\blender.exe");
        var updated = App("Blender", @"D:\Program Files\Blender Foundation\Blender 4.0\blender.exe");

        var hit = PinnedRebindResolver.Resolve(probe, Sources(installed: new[] { updated }));

        Assert.Equal(updated.TargetPath, hit);
    }

    // ------------------------------------------------------------- helpers

    private static PinnedRebindSources Sources(
        IReadOnlyList<DockItemData>? installed = null,
        Dictionary<string, string>? appPaths = null,
        string[]? storeIds = null,
        Func<string, bool>? exists = null) => new()
    {
        Installed = installed ?? Array.Empty<DockItemData>(),
        AppPaths = appPaths ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        StoreAppIds = (storeIds ?? Array.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase),
        FileExists = exists ?? (_ => true),
    };

    private static DockItemData App(
        string name,
        string? target,
        DockAppType type = DockAppType.Win32,
        string? aumid = null) => new()
    {
        Id = new DockItemId(target ?? name),
        Name = name,
        ShortcutPath = target ?? string.Empty,
        TargetPath = target ?? string.Empty,
        AppType = type,
        AppUserModelId = aumid,
    };
}
