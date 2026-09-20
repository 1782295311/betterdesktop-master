using System;
using System.Collections.Generic;
using System.Linq;
using BetterDesktop.Shell.AppSource.Models;
using BetterDesktop.Shell.ContextMenus.Contracts;
using Xunit;

namespace BetterDesktop.Shell.Dock.Tests;

/// <summary>
/// 【S3/S4 · 2026-09-14】应用条目菜单**项集矩阵**回归。
/// <para>
/// 构建器是「哪些项在什么条件下出现」的唯一判定点（应用提取器 + 菜单栏搜索共用）。
/// 每个条件单独立锁：路径 / 路径扩展名（提权与终端）/ 固定态 / 固定服务可用性 /
/// 卸载命令与动作 / 分组数据。顺序也在断言内——顺序即展示顺序，改动即用户可见变更。
/// </para>
/// <para>分组与系统级动作默认开启，需要模拟「缺能力」时用对应开关关掉。</para>
/// </summary>
public class AppEntryMenuBuilderTests
{
    // ------------------------------------------------------------- 基本项集

    [Fact]
    public void Build_UnpinnedExe_ProducesFullSetExceptPinningAndUninstall()
    {
        var items = AppEntryMenuBuilder.Build(Context());

        Assert.Equal(
            new[] { "grab.launch", "grab.admin", "grab.pin", "grab.dir", "grab.copy-path", "grab.terminal", "grab.properties" },
            Ids(items));
    }

    [Fact]
    public void Build_Pinned_SwapsPinForRemove()
    {
        var items = AppEntryMenuBuilder.Build(Context(isPinned: true));

        Assert.Equal(
            new[] { "grab.launch", "grab.admin", "grab.remove", "grab.dir", "grab.copy-path", "grab.terminal", "grab.properties" },
            Ids(items));

        // 已固定时**不得**同时出现「固定到 Dock」（否则菜单自相矛盾）
        Assert.DoesNotContain(items, i => i.Id == "grab.pin");
    }

    // ------------------------------------------------------------- 边界：缺事实

    [Fact]
    public void Build_WithoutPath_LeavesOnlyLaunchAndPinning()
    {
        var items = AppEntryMenuBuilder.Build(Context(path: null));

        Assert.Equal(new[] { "grab.launch", "grab.pin" }, Ids(items));
    }

    [Fact]
    public void Build_WithoutPinningService_OmitsPinAndRemove()
    {
        var items = AppEntryMenuBuilder.Build(Context(withPinning: false));

        Assert.Equal(
            new[] { "grab.launch", "grab.admin", "grab.dir", "grab.copy-path", "grab.terminal", "grab.properties" },
            Ids(items));
    }

    [Fact]
    public void Build_PinnedWithoutPinningService_OmitsRemove()
    {
        // 服务不可用 + 已固定：不能因为 IsPinned 就显示一个点了没反应的「移除」
        var items = AppEntryMenuBuilder.Build(Context(isPinned: true, withPinning: false));

        Assert.DoesNotContain(items, i => i.Id is "grab.pin" or "grab.remove");
    }

    // ------------------------------------------------------------- 提权 / 终端：按扩展名判定，不按「有路径」

    [Fact]
    public void Build_NonExecutablePath_OmitsAdminAndTerminalButKeepsLocation()
    {
        // .lnk 有真实路径（可定位/复制/看属性），但提权与开终端无意义
        var items = AppEntryMenuBuilder.Build(Context(path: @"D:\Apps\a\shortcut.lnk"));

        Assert.Equal(
            new[] { "grab.launch", "grab.pin", "grab.dir", "grab.copy-path", "grab.properties" },
            Ids(items));
    }

    [Theory]
    [InlineData(@"D:\a\tool.exe")]
    [InlineData(@"D:\a\tool.BAT")]
    [InlineData(@"D:\a\tool.cmd")]
    [InlineData(@"D:\a\tool.com")]
    [InlineData(@"D:\a\tool.msc")]
    public void Build_ElevatableExtensions_GetAdminAndTerminal(string path)
    {
        var items = AppEntryMenuBuilder.Build(Context(path: path));

        Assert.Contains(items, i => i.Id == "grab.admin");
        Assert.Contains(items, i => i.Id == "grab.terminal");
    }

    [Fact]
    public void Build_ShellActionsUnavailable_OmitsAdminTerminalProperties()
    {
        // 界面未提供这些动作实现时（如某表面暂未接入）整项省略，不留空动作项
        var items = AppEntryMenuBuilder.Build(Context(withShellActions: false));

        Assert.DoesNotContain(items, i => i.Id is "grab.admin" or "grab.terminal" or "grab.properties");
    }

    // ------------------------------------------------------------- 卸载：命令 + 动作缺一不可

    [Fact]
    public void Build_UninstallCommandWithAction_ShowsUninstallLast_AfterProperties()
    {
        var items = AppEntryMenuBuilder.Build(Context(
            uninstallCommand: @"MsiExec.exe /X{GUID}",
            withUninstallAction: true));

        Assert.Equal("grab.uninstall", items[^1].Id);
        Assert.Equal("grab.properties", items[^2].Id);
    }

    [Fact]
    public void Build_UninstallCommandWithoutAction_HidesUninstall()
    {
        // 有卸载命令但没有动作实现 → 整项不出现，不留空动作项
        var items = AppEntryMenuBuilder.Build(Context(
            uninstallCommand: @"MsiExec.exe /X{GUID}",
            withUninstallAction: false));

        Assert.DoesNotContain(items, i => i.Id == "grab.uninstall");
    }

    // ------------------------------------------------------------- 分组

    [Fact]
    public void Build_WithGroups_AddsMoveToSubmenu_ExcludingCurrentGroup()
    {
        var items = AppEntryMenuBuilder.Build(Context(
            withGrouping: true,
            groups: new[] { "办公", "游戏" },
            currentGroup: "办公"));

        Assert.Equal(
            new[]
            {
                "grab.launch", "grab.admin", "grab.pin", "grab.dir", "grab.copy-path", "grab.terminal",
                "grab.ungroup", "grab.moveto", "grab.properties",
            },
            Ids(items));

        var moveTo = items.Single(i => i.Id == "grab.moveto");
        Assert.Equal(MenuItemKind.Submenu, moveTo.Kind);
        Assert.NotNull(moveTo.Children);
        // 当前所在分组不再出现在「移动到分组」里（移到自己无意义）
        Assert.Equal(
            new[] { "grab.moveto.游戏", "grab.newgroup" },
            moveTo.Children!.Select(c => c.Id).ToArray());

        Assert.Equal("从「办公」移出", items.Single(i => i.Id == "grab.ungroup").Text);
    }

    [Fact]
    public void Build_NotInAnyGroup_ShowsMoveToSubmenuWithoutUngroup()
    {
        var items = AppEntryMenuBuilder.Build(Context(
            withGrouping: true,
            groups: new[] { "办公" },
            currentGroup: null));

        Assert.DoesNotContain(items, i => i.Id == "grab.ungroup");

        var moveTo = items.Single(i => i.Id == "grab.moveto");
        Assert.Equal(
            new[] { "grab.moveto.办公", "grab.newgroup" },
            moveTo.Children!.Select(c => c.Id).ToArray());
    }

    [Fact]
    public void Build_NoGroupsButNewGroupEntry_StillShowsMoveToSubmenu()
    {
        // 应用提取器在零分组时仍要能「新建分组」——故数据为空但 NewGroup 有动作时保留该子菜单
        var items = AppEntryMenuBuilder.Build(Context(withGrouping: true));

        var moveTo = items.SingleOrDefault(i => i.Id == "grab.moveto");
        Assert.NotNull(moveTo);
        Assert.Equal(new[] { "grab.newgroup" }, moveTo!.Children!.Select(c => c.Id).ToArray());
    }

    [Fact]
    public void Build_NoGroupingCapability_OmitsGroupingItems()
    {
        // 搜索面板：既不传分组，也没有新建分组入口 → 分组项整体不出现
        var items = AppEntryMenuBuilder.Build(Context());

        Assert.DoesNotContain(items, i => i.Id is "grab.ungroup" or "grab.moveto");
    }

    // ------------------------------------------------------------- 呈现差异与异常

    [Fact]
    public void Build_PassesThroughLaunchTextAndDefault()
    {
        var items = AppEntryMenuBuilder.Build(Context(launchText: "打开", launchIsDefault: true));

        var launch = items[0];
        Assert.Equal("打开", launch.Text);
        Assert.True(launch.IsDefault);
        // 命令行界面的搜索面板用「打开」而非「启动」
        Assert.Equal("grab.launch", launch.Id);
    }

    [Fact]
    public void Build_NoActions_ReturnsEmpty()
    {
        // 无任何可用动作 → 空列表（调用方据此不弹菜单，而不是弹一个空菜单）
        var items = AppEntryMenuBuilder.Build(new AppEntryMenuContext { Path = @"D:\a.exe" });

        Assert.Empty(items);
    }

    [Fact]
    public void Build_NullContext_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => AppEntryMenuBuilder.Build(null!));
    }

    // ------------------------------------------------------------- helpers

    private static string[] Ids(IReadOnlyList<MenuItemDef> items) => items.Select(i => i.Id).ToArray();

    private static AppEntryMenuContext Context(
        string? path = @"D:\Apps\a\a.exe",
        bool isPinned = false,
        bool withPinning = true,
        bool withShellActions = true,
        string? uninstallCommand = null,
        bool withUninstallAction = false,
        bool withGrouping = false,
        IReadOnlyList<string>? groups = null,
        string? currentGroup = null,
        string launchText = "启动",
        bool launchIsDefault = false) => new()
    {
        Path = path,
        IsPinned = isPinned,
        UninstallCommand = uninstallCommand,
        Groups = groups ?? Array.Empty<string>(),
        CurrentGroup = currentGroup,
        LaunchText = launchText,
        LaunchIsDefault = launchIsDefault,
        Actions = new AppEntryMenuActions
        {
            Launch = () => { },
            Pin = withPinning ? () => { } : null,
            Unpin = withPinning ? () => { } : null,
            RevealInExplorer = () => { },
            CopyPath = () => { },
            RunAsAdmin = withShellActions ? () => { } : null,
            OpenInTerminal = withShellActions ? () => { } : null,
            ShowProperties = withShellActions ? () => { } : null,
            RemoveFromGroup = withGrouping ? () => { } : null,
            MoveToGroup = withGrouping ? _ => { } : null,
            NewGroup = withGrouping ? () => { } : null,
            Uninstall = withUninstallAction ? () => { } : null,
        },
    };
}
