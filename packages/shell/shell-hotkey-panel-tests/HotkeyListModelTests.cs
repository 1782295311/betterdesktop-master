using System.Collections.Generic;
using System.Linq;
using BetterDesktop.Shell.Hotkeys.Contracts;
using Xunit;

namespace BetterDesktop.Shell.HotkeyPanel.Tests;

/// <summary>侧板列表模型：冲突置顶排序 / 隐藏过滤 / 忽略计数 / 接线状态。</summary>
public class HotkeyListModelTests
{
    private static HotkeyView View(string id, string chord, bool visible = true, bool osConflict = false, bool shadowed = false)
    {
        var binding = new HotkeyBinding(
            id, new HotkeyChord(chord), HotkeyScope.Global, "功能 " + id,
            HotkeySource.SystemHotkey, new HotkeyChord(chord), "tests");
        return new HotkeyView(binding, Enabled: true, Visible: visible, ShadowedBy: shadowed, OsConflict: osConflict);
    }

    [Fact]
    public void Conflict_rows_pinned_to_top()
    {
        var conflicts = new Dictionary<string, HotkeyConflict>
        {
            ["a"] = new("a", null, "Win+Shift+B", "被其他程序占用，热键未生效（请改键）"),
        };
        var rows = HotkeyListModel.BuildRows(
            new List<HotkeyView> { View("b", "Ctrl+Shift+K"), View("a", "Win+Shift+B", osConflict: true) },
            conflicts);

        Assert.Equal(new[] { "a", "b" }, rows.Select(r => r.Id).ToArray());
        Assert.True(rows[0].Conflict);
        Assert.NotNull(rows[0].ConflictReason);
        Assert.False(rows[1].Conflict);
    }

    [Fact]
    public void Shadowed_rows_pinned_with_reason()
    {
        var conflicts = new Dictionary<string, HotkeyConflict>
        {
            ["global"] = new("global", "panel", "Ctrl+V", "被「面板」接管（Surface.ClipboardPanel）"),
        };
        var rows = HotkeyListModel.BuildRows(
            new List<HotkeyView> { View("global", "Ctrl+V", shadowed: true) },
            conflicts);

        Assert.True(rows[0].Conflict);
        Assert.Contains("接管", rows[0].ConflictReason);
    }

    [Fact]
    public void Hidden_rows_are_excluded_from_list()
    {
        var rows = HotkeyListModel.BuildRows(
            new List<HotkeyView> { View("a", "Ctrl+Shift+K", visible: false), View("b", "Ctrl+Shift+J") },
            new Dictionary<string, HotkeyConflict>());

        Assert.Equal(new[] { "b" }, rows.Select(r => r.Id).ToArray());
    }

    [Fact]
    public void Hidden_count_excludes_forced_conflict_visibility()
    {
        var all = new List<HotkeyView>
        {
            View("a", "Ctrl+Shift+K", visible: false),
            View("b", "Ctrl+Shift+J", visible: false, osConflict: true), // 冲突项强制显示，不计入"已忽略"
            View("c", "Ctrl+Shift+M"),
        };

        Assert.Equal(1, HotkeyListModel.HiddenCount(all));
    }

    [Fact]
    public void Wired_flag_marks_paste_back_only()
    {
        var rows = HotkeyListModel.BuildRows(
            new List<HotkeyView> { View("clipboard.paste-back", "Ctrl+Shift+K"), View("capture.toggle", "Win+Shift+B") },
            new Dictionary<string, HotkeyConflict>());

        Assert.True(rows.Single(r => r.Id == "clipboard.paste-back").Wired);
        Assert.False(rows.Single(r => r.Id == "capture.toggle").Wired);
    }

    [Fact]
    public void Rows_sorted_by_description_within_same_conflict_state()
    {
        var rows = HotkeyListModel.BuildRows(
            new List<HotkeyView> { View("z", "Ctrl+Shift+Z"), View("a", "Ctrl+Shift+A") },
            new Dictionary<string, HotkeyConflict>());

        Assert.Equal(new[] { "a", "z" }, rows.Select(r => r.Id).ToArray());
    }

    // ---- P0-1：可操作态全量列表（含非活跃作用域，灰显 + 作用域徽标）----

    private static HotkeyView SurfaceView(string id, string scopeName)
    {
        var binding = new HotkeyBinding(
            id, new HotkeyChord("Ctrl+Shift+K"), HotkeyScope.Surface(scopeName), "功能 " + id,
            HotkeySource.SystemHotkey, new HotkeyChord("Ctrl+Shift+K"), "tests");
        return new HotkeyView(binding, Enabled: true, Visible: true, ShadowedBy: false, OsConflict: false);
    }

    [Fact]
    public void BuildAllRows_lists_surface_bindings_even_when_scope_inactive_with_badge()
    {
        var rows = HotkeyListModel.BuildAllRows(
            new List<HotkeyView> { SurfaceView("clipboard.paste-back", "ClipboardPanel"), View("capture.toggle", "Win+Shift+B") },
            new Dictionary<string, HotkeyConflict>(),
            activeScopes: System.Array.Empty<string>());

        var paste = rows.Single(r => r.Id == "clipboard.paste-back");
        Assert.True(paste.InactiveScope); // 面板未开 → 灰显
        Assert.Equal("ClipboardPanel", paste.ScopeBadge); // 作用域徽标
        Assert.False(rows.Single(r => r.Id == "capture.toggle").InactiveScope); // 全局键恒可用
    }

    [Fact]
    public void BuildAllRows_marks_surface_active_when_scope_reported()
    {
        var rows = HotkeyListModel.BuildAllRows(
            new List<HotkeyView> { SurfaceView("clipboard.paste-back", "ClipboardPanel") },
            new Dictionary<string, HotkeyConflict>(),
            activeScopes: new[] { "Surface.ClipboardPanel" });

        Assert.False(rows.Single().InactiveScope); // 面板已开 → 不灰显
        Assert.Null(rows.Single().ScopeBadge);
    }

    [Fact]
    public void BuildAllRows_sorts_inactive_after_conflicts_but_before_rest()
    {
        var rows = HotkeyListModel.BuildAllRows(
            new List<HotkeyView>
            {
                SurfaceView("paste", "ClipboardPanel"),
                View("conflict", "Win+Shift+B", osConflict: true),
                View("global", "Ctrl+Shift+M"),
            },
            new Dictionary<string, HotkeyConflict> { ["conflict"] = new("conflict", null, "Win+Shift+B", "被占用") },
            System.Array.Empty<string>());

        Assert.Equal(new[] { "conflict", "global", "paste" }, rows.Select(r => r.Id).ToArray());
    }

    // ---- P0-3：已忽略管理列表（可恢复，绝不静默丢失）----

    [Fact]
    public void BuildManageRows_lists_only_hidden_and_excludes_forced_conflicts()
    {
        var rows = HotkeyListModel.BuildManageRows(
            new List<HotkeyView>
            {
                View("hidden-a", "Ctrl+Shift+A", visible: false),
                View("forced", "Ctrl+Shift+F", visible: false, osConflict: true), // 冲突项不在此列
                View("shown", "Ctrl+Shift+S"),
            },
            new Dictionary<string, HotkeyConflict>());

        Assert.Equal(new[] { "hidden-a" }, rows.Select(r => r.Id).ToArray());
    }

    [Fact]
    public void BuildManageRows_sorts_by_description()
    {
        var rows = HotkeyListModel.BuildManageRows(
            new List<HotkeyView> { View("z", "Ctrl+Shift+Z", visible: false), View("a", "Ctrl+Shift+A", visible: false) },
            new Dictionary<string, HotkeyConflict>());

        Assert.Equal(new[] { "a", "z" }, rows.Select(r => r.Id).ToArray());
    }
}
