// shell-core-tests —「桌面控制」优先级规则单测
//
// 【钉住什么】用户 2026-09-17 拍板："桌面控制的优先级高于其他功能的默认隐藏效果"。
// 现实中唯一存在的冲突是「dock 启用即默认隐藏原生任务栏」（2026-09-02 定稿的联动）。
// 旧实现只有单向优先级（显式隐藏压过 dock，显式显示却被 dock 压掉）→ 用户实测错位：
// 菜单显示"未勾（没隐藏）"而任务栏其实被 dock 藏起来了，再点只会更隐藏（像没反应）。
// 这组用例把新的双向对称规则固定下来，防止再被"简化"回单向。

using System.Linq;
using BetterDesktop.Shell.Core.DesktopControl;
using Xunit;

namespace BetterDesktop.Shell.Core.Tests;

public sealed class DesktopControlRulesTests
{
    // ---- 任务栏：桌面控制显式选择 > dock 默认隐藏 ----

    [Fact]
    public void NeverTouched_FollowsDockDefaultHide()
    {
        // 用户从没动过任务栏开关（wintaskbar 默认 true、留痕键 false）→ 沿用 dock 联动 =
        // **既有观感不变**（dock 开着任务栏就是隐藏的）。这条是"别把默认行为改掉"的护栏。
        Assert.False(DesktopControlRules.ShouldShowNativeTaskbar(
            wintaskbar: true, dock: true, explicitVisible: false));
    }

    [Fact]
    public void ExplicitVisible_BeatsDockDefaultHide()
    {
        // 本次需求的核心：桌面控制里显式要求"显示任务栏"→ 压过 dock 的默认隐藏。
        Assert.True(DesktopControlRules.ShouldShowNativeTaskbar(
            wintaskbar: true, dock: true, explicitVisible: true));
    }

    [Fact]
    public void ExplicitHide_Wins()
    {
        // 显式隐藏：dock 关着也隐藏；留痕键残留 true 也不该让任务栏跳出来（意图键优先）。
        Assert.False(DesktopControlRules.ShouldShowNativeTaskbar(wintaskbar: false, dock: false, explicitVisible: true));
        Assert.False(DesktopControlRules.ShouldShowNativeTaskbar(wintaskbar: false, dock: true, explicitVisible: false));
    }

    [Fact]
    public void DockOff_TaskbarFollowsIntent()
    {
        // dock 关 → 不再有"默认隐藏"，一切按意图（含从未动过的默认 true）。
        Assert.True(DesktopControlRules.ShouldShowNativeTaskbar(true, dock: false, explicitVisible: false));
    }

    // ---- 留痕键：只有存在"默认隐藏冲突"的开关才需要 ----

    [Fact]
    public void ExplicitOverrides_OnlyTaskbarAndMirrorsValue()
    {
        Assert.True(DesktopToggleCatalog.TryGet(DesktopToggleCatalog.Taskbar, out var taskbar));
        var shown = DesktopToggleCatalog.ExplicitOverrides(taskbar, newValue: true);
        Assert.Single(shown);
        Assert.Equal(DesktopToggleCatalog.TaskbarExplicitVisibleKey, shown[0].Key);
        Assert.True(shown[0].Value);

        // 翻到"隐藏" → 留痕复位为 false = 回到"从未显式要求"，dock 联动照旧
        var hidden = DesktopToggleCatalog.ExplicitOverrides(taskbar, newValue: false);
        Assert.Single(hidden);
        Assert.False(hidden[0].Value);

        // 图标 / 菜单栏 / Dock / 热键侧板：没有"其它功能的默认隐藏"冲突 → 不留痕（不该凭空长出新键）
        Assert.True(DesktopToggleCatalog.TryGet(DesktopToggleCatalog.Icons, out var icons));
        Assert.Empty(DesktopToggleCatalog.ExplicitOverrides(icons, newValue: true));
    }

    [Fact]
    public void DockFlip_ResetsTaskbarExplicitLatch()
    {
        // 用户 2026-09-17 拍板：关掉 / 重开 Dock → 清掉留痕，回到 dock 默认隐藏（**两个方向都复位**）。
        Assert.True(DesktopToggleCatalog.TryGet(DesktopToggleCatalog.Dock, out var dock));
        foreach (var newDockValue in new[] { true, false })
        {
            var reset = DesktopToggleCatalog
                .ExplicitOverrides(dock, newValue: newDockValue)
                .Single(o => o.Key == DesktopToggleCatalog.TaskbarExplicitVisibleKey);
            Assert.False(reset.Value);

            // 复位后 = "从未显式要求" → dock 默认隐藏重新生效（这就是"回到 dock 默认隐藏"的可测含义）
            Assert.False(DesktopControlRules.ShouldShowNativeTaskbar(
                wintaskbar: true, dock: true, explicitVisible: reset.Value));
        }

        // Dock 自己不属于"显式可见"型开关 → 不给自身留痕（只有默认值恰为"可见"的开关才需要）
        Assert.DoesNotContain(
            DesktopToggleCatalog.ExplicitOverrides(dock, newValue: true),
            o => o.Key == DesktopToggleCatalog.DockKey);
    }

    // ---- 目录本身：跨进程契约（命令名）不得漂移 ----

    [Theory]
    [InlineData("icons", "desktop.iconsHidden")]
    [InlineData("taskbar", "components.wintaskbar")]
    [InlineData("doubleclick", "desktop.doubleClickHideIcons")]
    [InlineData("menubar", "components.menubar")]
    [InlineData("dock", "components.dock")]
    [InlineData("hotkey-panel", "hotkeys-panel.enabled")]
    public void Catalog_MapsCommandNameToSettingsKey(string name, string expectedKey)
    {
        Assert.True(DesktopToggleCatalog.TryGet(name, out var toggle));
        Assert.Equal(expectedKey, toggle.SettingsKey);
    }

    [Fact]
    public void Catalog_IsCaseInsensitive_AndRejectsUnknown()
    {
        // 命令名大小写混用是常态（命令桥 path / CLI 参数 / 手工调试）→ 必须大小写不敏感
        Assert.True(DesktopToggleCatalog.TryGet("TaskBar", out var upper));
        Assert.Equal(DesktopToggleCatalog.TaskbarKey, upper.SettingsKey);
        Assert.False(DesktopToggleCatalog.TryGet("no-such-toggle", out _));
    }

    [Fact]
    public void Catalog_HostRequiredFlagsMatchAgreedBoundary()
    {
        // 用户 2026-09-17 边界：无宿主时「图标 / 任务栏」当场生效；「双击 / 菜单栏 / Dock / 热键侧板」属宿主组件。
        Assert.True(DesktopToggleCatalog.TryGet(DesktopToggleCatalog.Icons, out var icons));
        Assert.True(DesktopToggleCatalog.TryGet(DesktopToggleCatalog.Taskbar, out var taskbar));
        Assert.False(icons.RequiresHost);
        Assert.False(taskbar.RequiresHost);
        Assert.True(icons.NativeEffective);
        Assert.True(taskbar.NativeEffective);

        foreach (var name in new[]
                 {
                     DesktopToggleCatalog.DoubleClick, DesktopToggleCatalog.MenuBar,
                     DesktopToggleCatalog.Dock, DesktopToggleCatalog.HotkeyPanel,
                 })
        {
            Assert.True(DesktopToggleCatalog.TryGet(name, out var spec));
            Assert.True(spec.RequiresHost, $"{name} 应标记为宿主组件（无宿主时置灰）");
            Assert.False(spec.NativeEffective, $"{name} 不该声称能免宿主原生生效");
        }
    }
}
