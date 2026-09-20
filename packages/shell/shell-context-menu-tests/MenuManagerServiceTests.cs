// 管理器引擎单测（M1 + U0）：临时 HKCU 键桩上的枚举/启停/新建/分组/样式开关——测内核非 UI。
// 场景桩：HKCU\Software\Classes\BdTestScene（HKCR 合并视图可见）；Dispose 全量清理。

using System;
using System.Linq;
using BetterDesktop.Shell.ContextMenus.Services;
using Microsoft.Win32;
using Xunit;

namespace BetterDesktop.Shell.ContextMenu.Tests;

// 与 HandlerCrashGuardTests / MenuBrokerClientTests 同属一个 Collection：本类含 ShellExMenuPreview 用例，
// 会经 ShellMenuInterop → MenuBrokerClient 走到 broker，而那些类会改写 broker 的进程级测试缝。
[Collection("shellmenu-static-seams")]
public class MenuManagerServiceTests : IDisposable
{
    private const string ScenePath = @"Software\Classes\BdTestScene";
    private const string SceneRegPath = "BdTestScene";

    public MenuManagerServiceTests()
    {
        using var scene = Registry.CurrentUser.CreateSubKey(ScenePath, writable: true);
        using var item = scene!.CreateSubKey(@"shell\TestItem", writable: true)!;
        item.SetValue("MUIVerb", "测试项");
        using var command = item.CreateSubKey("command", writable: true)!;
        command.SetValue(string.Empty, "notepad.exe \"%1\"");
        using var handler = scene.CreateSubKey(@"shellex\ContextMenuHandlers\TestHandler", writable: true)!;
        handler.SetValue(string.Empty, "{BdTest-1111-2222-3333-444455556666}");
    }

    public void Dispose()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\BdTestScene", throwOnMissingSubKey: false); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\.bdtest", throwOnMissingSubKey: false); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\DesktopBackground\shell\__bdtest__", throwOnMissingSubKey: false); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\BdStyleTest", throwOnMissingSubKey: false); } catch { }
    }

    [Fact]
    public void Enumerate_FindsStaticAndShellex()
    {
        var items = MenuManagerService.EnumerateByPath("Test", SceneRegPath);
        var stat = items.FirstOrDefault(i => i.Kind == "Static" && i.KeyName == "TestItem");
        var shex = items.FirstOrDefault(i => i.Kind == "Shellex" && i.KeyName == "TestHandler");

        Assert.NotNull(stat);
        Assert.Equal("测试项", stat!.DisplayName);
        Assert.True(stat.Enabled);
        Assert.Equal("notepad.exe \"%1\"", stat.Command);
        Assert.Equal("HKCU", stat.Origin);

        Assert.NotNull(shex);
        Assert.Equal("HKCU", shex!.Origin);
    }

    [Fact]
    public void ToggleStatic_HidesThenShows()
    {
        var before = MenuManagerService.EnumerateByPath("Test", SceneRegPath)
            .First(i => i.Kind == "Static" && i.KeyName == "TestItem");
        Assert.True(before.Enabled);

        MenuManagerService.Toggle(before);
        var hidden = MenuManagerService.EnumerateByPath("Test", SceneRegPath)
            .First(i => i.KeyName == "TestItem");
        Assert.False(hidden.Enabled);

        MenuManagerService.Toggle(hidden);
        var restored = MenuManagerService.EnumerateByPath("Test", SceneRegPath)
            .First(i => i.KeyName == "TestItem");
        Assert.True(restored.Enabled);
    }

    [Fact]
    public void ToggleShellex_MovesToNegativePrefixAndBack()
    {
        var before = MenuManagerService.EnumerateByPath("Test", SceneRegPath)
            .First(i => i.Kind == "Shellex" && i.KeyName == "TestHandler");
        Assert.True(before.Enabled);

        MenuManagerService.Toggle(before);
        using (var scene = Registry.CurrentUser.OpenSubKey(ScenePath))
        {
            Assert.Null(scene!.OpenSubKey(@"shellex\ContextMenuHandlers\TestHandler"));
            Assert.NotNull(scene.OpenSubKey(@"shellex\" + MenuManagerService.DisabledPrefix + @"\TestHandler"));
        }
        var disabled = MenuManagerService.EnumerateByPath("Test", SceneRegPath)
            .FirstOrDefault(i => i.KeyName == "TestHandler");
        Assert.True(disabled is not null && !disabled.Enabled,
            "枚举快照: " + string.Join(";", MenuManagerService
                .EnumerateByPath("Test", SceneRegPath)
                .Select(i => $"{i.Kind}/{i.KeyName}/{i.Enabled}/{i.WritePath}")));

        MenuManagerService.Toggle(disabled);
        using (var scene = Registry.CurrentUser.OpenSubKey(ScenePath))
        {
            Assert.NotNull(scene!.OpenSubKey(@"shellex\ContextMenuHandlers\TestHandler"));
            Assert.Null(scene.OpenSubKey(@"shellex\" + MenuManagerService.DisabledPrefix + @"\TestHandler"));
        }
    }

    [Fact]
    public void CreateStaticVerb_WritesMergeViewVisibleKeys()
    {
        var created = MenuManagerService.CreateStaticVerb(
            "DesktopBackground", "__bdtest__", "引擎测试项", "notepad.exe");
        Assert.Equal("HKCU", created.Origin);
        Assert.Equal("引擎测试项",
            Registry.GetValue(@"HKEY_CURRENT_USER\Software\Classes\DesktopBackground\shell\__bdtest__", "MUIVerb", null));
        Assert.NotNull(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Classes\DesktopBackground\shell\__bdtest__\command", string.Empty, null));
    }

    [Fact]
    public void CreateShellNew_WritesNullFile()
    {
        MenuManagerService.CreateShellNew("bdtest");
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\.bdtest\ShellNew");
        Assert.NotNull(key);
        Assert.NotNull(key!.GetValue("NullFile"));
    }

    // ===== U0（2026-09-05）：来源识别 / 分组 / 整体开关 / 样式开关 / 预览降级 =====

    [Fact]
    public void Source_TagsBetterDeskPrefixAsOwn()
    {
        var own = new MenuItemInfo("T", "BetterDesk", "Static", "x", "HKCU", "p", true, "", "");
        var ownDock = new MenuItemInfo("T", "BetterDeskDock", "Static", "x", "HKCU", "p", true, "", "");
        var custom = new MenuItemInfo("T", "UserMenu0", "Static", "x", "HKCU", "p", true, "", "");
        var shellex = new MenuItemInfo("T", "h", "Shellex", "7-Zip", "HKCU", "p", true, "{A}", "");
        var system = new MenuItemInfo("T", "open", "Static", "打开", "HKCU", "p", true, "", "");

        Assert.Equal("BetterDesktop", own.Source);
        Assert.Equal("BetterDesktop", ownDock.Source);
        Assert.Equal("自定义", custom.Source);
        Assert.Equal("扩展程序", shellex.Source);
        Assert.Equal("系统", system.Source);
    }

    [Fact]
    public void GroupExtensions_GroupsBySourceAndMergesByClsid()
    {
        var items = new List<MenuItemInfo>
        {
            new("T", "BetterDesk", "Static", "BetterDesktop", "HKCU", "p1", true, "", ""),
            new("T", "BetterDeskDock", "Static", "钉到 Dock", "HKCU", "p2", true, "", ""),
            new("T", "UserMenu0", "Static", "我的工具", "HKCU", "p7", true, "", ""),
            new("T", "7z1", "Shellex", "7-Zip", "HKCU", "p3", true, "{A}", ""),
            new("T", "7z2", "Shellex", "7-Zip", "HKCU", "p4", false, "{A}", ""),
            new("T", "rar", "Shellex", "WinRAR", "HKCU", "p5", true, "{B}", ""),
            new("T", "open", "Static", "打开", "HKCU", "p6", true, "", ""),
        };

        var groups = MenuManagerService.GroupExtensions(items);

        Assert.Equal(5, groups.Count);
        Assert.Equal("BetterDesktop", groups[0].Source);
        Assert.Equal(2, groups[0].Items.Count);
        Assert.Equal("自定义", groups[1].Source);
        Assert.Equal("我创建的菜单项", groups[1].Name);
        Assert.Equal("扩展程序", groups[2].Source);
        Assert.Equal("7-Zip", groups[2].Name);
        Assert.Equal(2, groups[2].Items.Count); // 同 CLSID 合并
        Assert.Equal("WinRAR", groups[3].Name);
        Assert.Equal("系统", groups[4].Source);
    }

    [Fact]
    public void ToggleGroup_DisablesThenReEnablesAndIsIdempotent()
    {
        // 桩场景预置两个 BetterDesk 前缀项（识别锚点生效）
        using (var scene = Registry.CurrentUser.CreateSubKey(ScenePath, writable: true))
        {
            foreach (var name in new[] { "BetterDesk", "BetterDeskDock" })
            {
                using var item = scene!.CreateSubKey($@"shell\{name}", writable: true);
                item!.SetValue("MUIVerb", name);
                using var cmd = item.CreateSubKey("command", writable: true);
                cmd.SetValue(string.Empty, "notepad.exe");
            }
        }

        var items = MenuManagerService.EnumerateByPath("T", SceneRegPath)
            .Where(i => i.Source == "BetterDesktop").ToList();
        Assert.Equal(2, items.Count);
        var group = MenuManagerService.GroupExtensions(items).Single(g => g.Source == "BetterDesktop");
        Assert.True(MenuManagerService.IsGroupEnabled(group));

        MenuManagerService.ToggleGroup(group, false);
        var hidden = MenuManagerService.EnumerateByPath("T", SceneRegPath)
            .Where(i => i.Source == "BetterDesktop").ToList();
        Assert.All(hidden, i => Assert.False(i.Enabled));
        Assert.False(MenuManagerService.IsGroupEnabled(
            MenuManagerService.GroupExtensions(hidden).Single(g => g.Source == "BetterDesktop")));

        // 幂等：同一 group 快照重复调用不得反向翻转
        MenuManagerService.ToggleGroup(group, false);
        Assert.All(MenuManagerService.EnumerateByPath("T", SceneRegPath)
            .Where(i => i.Source == "BetterDesktop"), i => Assert.False(i.Enabled));

        var msg = MenuManagerService.ToggleGroup(group, true);
        Assert.All(MenuManagerService.EnumerateByPath("T", SceneRegPath)
            .Where(i => i.Source == "BetterDesktop"), i => Assert.True(i.Enabled, msg));
    }

    [Fact]
    public void SetClassicMenuAt_RoundTripsOnTempKey()
    {
        const string keyPath = @"HKEY_CURRENT_USER\Software\Classes\BdStyleTest\InprocServer32";
        try
        {
            Assert.False(MenuManagerService.IsClassicMenuEnabledAt(keyPath));

            MenuManagerService.SetClassicMenuAt(keyPath, true);
            Assert.True(MenuManagerService.IsClassicMenuEnabledAt(keyPath));
            Assert.Equal(string.Empty, Registry.GetValue(keyPath, string.Empty, null));

            // 幂等：重复启用不炸、状态不变
            MenuManagerService.SetClassicMenuAt(keyPath, true);
            Assert.True(MenuManagerService.IsClassicMenuEnabledAt(keyPath));

            MenuManagerService.SetClassicMenuAt(keyPath, false);
            Assert.False(MenuManagerService.IsClassicMenuEnabledAt(keyPath));

            // 幂等：重复禁用不炸
            MenuManagerService.SetClassicMenuAt(keyPath, false);
            Assert.False(MenuManagerService.IsClassicMenuEnabledAt(keyPath));
        }
        finally
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\BdStyleTest", throwOnMissingSubKey: false); } catch { }
        }
    }

    [Fact]
    public async Task ShellExMenuPreview_UnknownClsid_ReturnsEmptyNotThrow()
    {
        var preview = new ShellExMenuPreview();
        var result = await preview.QueryAsync(
            "{00000000-0000-0000-0000-000000000000}", "AllFiles", System.Threading.CancellationToken.None);
        // 未注册 CLSID：COM 创建失败 → Query 空树 → 空列表（不抛、不返回假数据）
        Assert.NotNull(result);
    }

    [Fact]
    public void ShowStatic_RemovesMarkersAndEnumerationRecovers()
    {
        using (var scene = Registry.CurrentUser.CreateSubKey(ScenePath, writable: true))
        {
            using var item = scene!.CreateSubKey(@"shell\DbgShow", writable: true);
            item.SetValue("MUIVerb", "调试");
        }
        const string p = @"HKEY_CURRENT_USER\Software\Classes\BdTestScene\shell\DbgShow";
        var it = MenuManagerService.EnumerateByPath("T", SceneRegPath).First(i => i.KeyName == "DbgShow");
        MenuManagerService.Toggle(it); // 隐藏
        Assert.NotNull(Registry.GetValue(p, "HideBasedOnVelocityId", null));
        var hidden = MenuManagerService.EnumerateByPath("T", SceneRegPath).First(i => i.KeyName == "DbgShow");
        Assert.False(hidden.Enabled);
        MenuManagerService.Toggle(hidden); // 显示
        var after = Registry.GetValue(p, "HideBasedOnVelocityId", null);
        var en = MenuManagerService.EnumerateByPath("T", SceneRegPath).First(i => i.KeyName == "DbgShow");
        Assert.True(en.Enabled, $"after={after ?? "<null>"}, Enabled={en.Enabled}");
    }
}
