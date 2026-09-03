// DockItem 模板（M3 计划 §3）：Target 缺失返回空 + 项集完整 + 能力门控。

using BetterDesktop.Shell.ContextMenus.Contracts;
using BetterDesktop.Shell.Dock.Models;
using BetterDesktop.Shell.Dock.Services;
using Xunit;

namespace BetterDesktop.Shell.Dock.Tests;

public class DockItemTemplateTests
{
    private static MenuRequest Request(DockItemData? item, FileIdentity? identity)
        => new(MenuScope.DockItem, item, default, File: identity);

    private static DockItemData MakeItem() => new()
    {
        Id = new DockItemId("test-1"),
        Name = "Test",
        AppType = DockAppType.Win32,
        ShortcutPath = "C:\\Windows\\System32\\notepad.exe",
        TargetPath = "C:\\Windows\\System32\\notepad.exe",
    };

    [Fact]
    public void NonDockItemTarget_ReturnsEmpty()
    {
        var template = new DockItemTemplate(apps: null!);
        var builder = new CollectBuilder();
        template.Build(builder, Request(item: null, identity: null));
        Assert.Empty(builder.Items);
    }

    [Fact]
    public void CoreItems_Present()
    {
        var template = new DockItemTemplate(apps: null!);
        var builder = new CollectBuilder();
        template.Build(builder, Request(MakeItem(), identity: null));

        var ids = builder.Items.Select(i => i.Id).ToList();
        Assert.Contains("dockitem.launch", ids);
        Assert.Contains("dockitem.remove", ids);
        Assert.Contains("dockitem.startmenu", ids);
        Assert.Contains("dockitem.appgrabber", ids);
    }

    [Fact]
    public void OpenAs_Item_RequiresOpenWithCap()
    {
        // 能力过滤本身在 MenuService（模板只声明 RequiredCapability）——钉住门控声明存在
        var template = new DockItemTemplate(apps: null!);
        var builder = new CollectBuilder();
        template.Build(builder, Request(MakeItem(), new FileIdentity(
            FileKind.Executable, FileCapabilities.Open | FileCapabilities.OpenWith, false, false,
            "C:\\Windows\\System32\\notepad.exe")));

        var openAs = Assert.Single(builder.Items, i => i.Id == "dockitem.openas");
        Assert.Equal(FileCapabilities.OpenWith, openAs.RequiredCapability);
    }

    private sealed class CollectBuilder : IMenuTemplateBuilder
    {
        public List<MenuItemDef> Items { get; } = [];

        public void AddItem(MenuItemDef item) => Items.Add(item);

        public void AddSeparator(MenuGroup group) => Items.Add(new MenuItemDef
        { Id = $"sep-{group}-{Items.Count}", Text = string.Empty, Kind = MenuItemKind.Separator });
    }
}
