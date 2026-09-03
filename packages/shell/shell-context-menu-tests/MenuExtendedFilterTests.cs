// Shift 扩展项过滤（计划 §8 MenuExtendedFilterTests）。
// BuildAsync 全链路需渲染宿主（重）；此处经反射钉 StripExtended 纯函数语义——
// Extended 过滤的唯一执行体（MenuService.BuildAsync L235-243 按设置键/Shift 决定是否调用它）。

using System.Reflection;
using BetterDesktop.Shell.ContextMenus.Contracts;
using BetterDesktop.Shell.ContextMenus.Services;
using Xunit;

namespace BetterDesktop.Shell.ContextMenu.Tests;

public class MenuExtendedFilterTests
{
    private static List<MenuItemDef> Strip(List<MenuItemDef> items)
    {
        var method = typeof(MenuService).GetMethod("StripExtended",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsType<List<MenuItemDef>>(method!.Invoke(null, [items]));
    }

    private static MenuItemDef Item(string id, bool extended = false, List<MenuItemDef>? children = null) => new()
    {
        Id = id, Text = id, Extended = extended, Children = children,
    };

    [Fact]
    public void ExtendedItems_Stripped_NonExtended_Kept()
    {
        var stripped = Strip([Item("a"), Item("ext", extended: true), Item("b")]);

        Assert.Equal(["a", "b"], stripped.Select(i => i.Id));
    }

    [Fact]
    public void ExtendedSubmenu_ChildrenRemoved_NoOrphans()
    {
        var stripped = Strip(
        [
            Item("keep"),
            Item("ext-parent", extended: true, children: [Item("child-1"), Item("child-2")]),
            Item("normal-parent", children: [Item("normal-child")]),
        ]);

        var ids = stripped.Select(i => i.Id).ToList();
        Assert.Equal(["keep", "normal-parent"], ids);
        Assert.DoesNotContain(stripped, i => i.Children?.Any(c => c.Id == "child-1") == true);
    }
}
