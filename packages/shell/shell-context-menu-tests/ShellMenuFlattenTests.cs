// 第三方 COM 入口展开策略（用户拍板：两种模式都实现，开关 context-menu.com.flatten）。
// 收纳（默认）= handler 原生结构透传；展开 = 顶层子菜单整体上提一层 + 分隔线去重。

using BetterDesktop.Shell.ContextMenus.Contracts;
using BetterDesktop.Shell.ContextMenus.Services;
using Xunit;

namespace BetterDesktop.Shell.ContextMenu.Tests;

public class ShellMenuFlattenTests
{
    private static ShellVerbItem Cmd(string text) =>
        new(text, false, false, [], Invoke: () => { });

    private static ShellVerbItem Sep() => new(string.Empty, true, false, [], null);

    private static ShellVerbItem Sub(string text, params ShellVerbItem[] children) =>
        new(text, false, true, [.. children], null);

    private static List<MenuItemDef> Map(List<ShellVerbItem> tree, bool flatten)
        => ShellMenuContributor.MapShellItems(tree, ["C:\\a.txt"], "com:0", flatten).ToList();

    [Fact]
    public void Collapsed_VendorSubmenuStaysSubmenu()
    {
        var result = Map([Sub("7-Zip", Cmd("添加到压缩包"), Cmd("解压到"))], flatten: false);

        var sub = Assert.Single(result);
        Assert.Equal(MenuItemKind.Submenu, sub.Kind);
        Assert.Equal("7-Zip", sub.Text);
        Assert.Equal(2, sub.Children!.Count);
    }

    [Fact]
    public void Flattened_VendorSubmenuHoistedOneLevel()
    {
        var result = Map([Sub("7-Zip", Cmd("添加到压缩包"), Cmd("解压到"))], flatten: true);

        Assert.All(result, i => Assert.NotEqual(MenuItemKind.Submenu, i.Kind)); // 顶层无子菜单
        Assert.Equal(["添加到压缩包", "解压到"], result.Select(i => i.Text));
    }

    [Fact]
    public void Flattened_NestedLevels_StaySubmenus()
    {
        var result = Map([Sub("7-Zip", Sub("解压到", Cmd("解压到当前目录"), Cmd("解压到 xxx\\")))], flatten: true);

        var hoisted = Assert.Single(result);
        Assert.Equal(MenuItemKind.Submenu, hoisted.Kind); // 第二层不打散
        Assert.Equal("解压到", hoisted.Text);
    }

    [Fact]
    public void Flattened_MultipleRoots_AllHoisted()
    {
        var result = Map([Sub("7-Zip", Cmd("压缩")), Cmd("独立命令"), Sub("WinRAR", Cmd("解压"))], flatten: true);

        Assert.Equal(["压缩", "独立命令", "解压"], result.Select(i => i.Text));
    }

    [Fact]
    public void Flattened_Separators_Deduped_NoLeadingTrailingOrAdjacent()
    {
        var result = Map(
        [
            Sep(),
            Sub("7-Zip", Cmd("压缩"), Sep(), Sep(), Cmd("测试")),
            Sep(),
            Cmd("独立命令"),
        ], flatten: true);

        var separators = result.Where(i => i.Kind == MenuItemKind.Separator).ToList();
        Assert.Equal(2, separators.Count); // 厂商内 2 连分隔线折叠为 1 + 厂商边界 1
        Assert.NotEqual(MenuItemKind.Separator, result[0].Kind); // 无头部分隔线
        Assert.NotEqual(MenuItemKind.Separator, result[^1].Kind); // 无尾部分隔线
    }

    [Fact]
    public void Collapsed_Separators_Preserved()
    {
        var result = Map([Cmd("a"), Sep(), Cmd("b")], flatten: false);

        Assert.Equal(3, result.Count);
        Assert.Equal(MenuItemKind.Separator, result[1].Kind);
    }
}
