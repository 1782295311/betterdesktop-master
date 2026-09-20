using System.Collections.Generic;
using BetterDesktop.Shell.Core.Hotkeys;
using BetterDesktop.Shell.Hotkeys.Contracts;
using Xunit;

namespace BetterDesktop.Shell.Core.Tests.Hotkeys;

/// <summary>
/// 冲突判定与作用域过滤纯函数测试（热键计划 T1/T2）。
/// </summary>
public class HotkeyConflictPolicyTests
{
    private static HotkeyBinding Binding(
        string id,
        string chord,
        HotkeyScope scope,
        string? shadows = null,
        HotkeySource source = HotkeySource.SystemHotkey)
        => new(
            id, new HotkeyChord(chord), scope, "测试绑定", source,
            new HotkeyChord(chord), "tests", Shadows: shadows);

    // ---- 键位等价 ----

    [Fact]
    public void ChordEquals_normalizes_order_and_case()
    {
        Assert.True(HotkeyConflictPolicy.ChordEquals(
            new HotkeyChord("Ctrl+Shift+V"), new HotkeyChord("Shift+Ctrl+V")));
        Assert.True(HotkeyConflictPolicy.ChordEquals(
            new HotkeyChord("ctrl+shift+v"), new HotkeyChord("Ctrl+Shift+V")));
        Assert.True(HotkeyConflictPolicy.ChordEquals(
            new HotkeyChord("Ctrl+Alt+F5"), new HotkeyChord("Alt+Ctrl+F5")));
        Assert.False(HotkeyConflictPolicy.ChordEquals(
            new HotkeyChord("Ctrl+Shift+V"), new HotkeyChord("Ctrl+Shift+P")));
    }

    [Fact]
    public void ChordEquals_falls_back_to_raw_when_unparseable()
    {
        Assert.True(HotkeyConflictPolicy.ChordEquals(
            new HotkeyChord("  Ctrl+A  "), new HotkeyChord("Ctrl+A")));
        // 非法串（无修饰键）：按原串比较，宁可判冲突也不静默放行
        Assert.False(HotkeyConflictPolicy.ChordEquals(
            new HotkeyChord("F5"), new HotkeyChord("Ctrl+F5")));
    }

    // ---- 同作用域冲突 ----

    [Fact]
    public void SameScopeDuplicate_detects_same_chord()
    {
        var a = Binding("a", "Ctrl+Shift+K", HotkeyScope.Global);
        var b = Binding("b", "Ctrl+Shift+K", HotkeyScope.Global);
        Assert.True(HotkeyConflictPolicy.IsSameScopeDuplicate(a, b));
        Assert.True(HotkeyConflictPolicy.IsSameScopeDuplicate(b, a)); // 对称
    }

    [Fact]
    public void SameScopeDuplicate_ignores_self()
    {
        var a = Binding("a", "Ctrl+Shift+K", HotkeyScope.Global);
        Assert.False(HotkeyConflictPolicy.IsSameScopeDuplicate(a, a));
    }

    [Fact]
    public void SameScope_different_chords_not_conflict()
    {
        var a = Binding("a", "Ctrl+Shift+K", HotkeyScope.Global);
        var b = Binding("b", "Ctrl+Shift+J", HotkeyScope.Global);
        Assert.False(HotkeyConflictPolicy.IsSameScopeDuplicate(a, b));
    }

    [Fact]
    public void SameScope_different_scopes_not_same_scope_conflict()
    {
        var a = Binding("a", "Ctrl+Shift+K", HotkeyScope.Global);
        var b = Binding("b", "Ctrl+Shift+K", HotkeyScope.Surface("ClipboardPanel"));
        Assert.False(HotkeyConflictPolicy.IsSameScopeDuplicate(a, b));
    }

    // ---- 跨作用域 Shadows ----

    [Fact]
    public void CrossScope_undeclared_is_conflict()
    {
        var global = Binding("g", "Ctrl+V", HotkeyScope.Global);
        var surface = Binding("s", "Ctrl+V", HotkeyScope.Surface("ClipboardPanel")); // 未声明 Shadows
        Assert.True(HotkeyConflictPolicy.IsUndeclaredCrossScopeConflict(surface, global));
        // 纯函数对称：任何未声明 Shadows 的跨作用域同键位都是冲突；"先注册者"语义由服务层 Register 保证
        Assert.True(HotkeyConflictPolicy.IsUndeclaredCrossScopeConflict(global, surface));
    }

    [Fact]
    public void CrossScope_with_shadows_declared_not_conflict()
    {
        var global = Binding("clipboard.paste", "Ctrl+V", HotkeyScope.Global);
        var surface = Binding("clipboard.paste-next", "Ctrl+V", HotkeyScope.Surface("ClipboardPanel"),
            shadows: "clipboard.paste");
        Assert.False(HotkeyConflictPolicy.IsUndeclaredCrossScopeConflict(surface, global));
    }

    [Fact]
    public void CrossScope_different_chords_not_conflict()
    {
        var global = Binding("g", "Ctrl+V", HotkeyScope.Global);
        var surface = Binding("s", "Ctrl+Shift+V", HotkeyScope.Surface("ClipboardPanel"));
        Assert.False(HotkeyConflictPolicy.IsUndeclaredCrossScopeConflict(surface, global));
    }

    // ---- 内置键保护 ----

    [Fact]
    public void ConflictsWithBuiltIn_detects_engine_three_keys()
    {
        Assert.True(HotkeyConflictPolicy.ConflictsWithBuiltIn(Binding("x", "Ctrl+Shift+V", HotkeyScope.Global)));
        Assert.True(HotkeyConflictPolicy.ConflictsWithBuiltIn(Binding("x", "Ctrl+Shift+P", HotkeyScope.Global)));
        Assert.True(HotkeyConflictPolicy.ConflictsWithBuiltIn(Binding("x", "Ctrl+Shift+Backspace", HotkeyScope.Global)));
        Assert.False(HotkeyConflictPolicy.ConflictsWithBuiltIn(Binding("x", "Ctrl+Shift+K", HotkeyScope.Global)));
    }

    // ---- 作用域过滤 ----

    [Fact]
    public void IsActive_global_always_true()
    {
        var global = Binding("g", "Ctrl+Shift+K", HotkeyScope.Global);
        Assert.True(HotkeyConflictPolicy.IsActive(global, new HashSet<string>()));
        Assert.True(HotkeyConflictPolicy.IsActive(global, new HashSet<string> { "Surface.StartMenu" }));
    }

    [Fact]
    public void IsActive_surface_requires_stack()
    {
        var s = Binding("s", "Ctrl+Shift+K", HotkeyScope.Surface("ClipboardPanel"));
        Assert.False(HotkeyConflictPolicy.IsActive(s, new HashSet<string>()));
        Assert.True(HotkeyConflictPolicy.IsActive(s, new HashSet<string> { "Surface.ClipboardPanel" }));
        Assert.False(HotkeyConflictPolicy.IsActive(s, new HashSet<string> { "Surface.StartMenu" }));
    }

    [Fact]
    public void IsActive_any_surface_when_any_surface_active()
    {
        var any = Binding("a", "Ctrl+Shift+K", HotkeyScope.Any);
        Assert.False(HotkeyConflictPolicy.IsActive(any, new HashSet<string>()));
        Assert.True(HotkeyConflictPolicy.IsActive(any, new HashSet<string> { "Surface.ClipboardPanel" }));
        Assert.False(HotkeyConflictPolicy.IsActive(any, new HashSet<string> { "Global" })); // 仅 Global 不算表面
    }

    // ---- Shadows 接管判定 ----

    [Fact]
    public void FindShadowing_detects_explicit_id()
    {
        var global = Binding("clipboard.paste", "Ctrl+V", HotkeyScope.Global);
        var surface = Binding("clipboard.paste-next", "Ctrl+V", HotkeyScope.Surface("ClipboardPanel"),
            shadows: "clipboard.paste");
        Assert.Same(surface, HotkeyConflictPolicy.FindShadowing(global, new[] { global, surface }));
        Assert.Null(HotkeyConflictPolicy.FindShadowing(surface, new[] { global, surface })); // 接管方不被标
    }

    [Fact]
    public void FindShadowing_detects_global_takeover_by_surface()
    {
        var global = Binding("g", "Ctrl+Shift+K", HotkeyScope.Global);
        var surface = Binding("s", "Ctrl+Shift+K", HotkeyScope.Surface("Island"), shadows: "*");
        Assert.Same(surface, HotkeyConflictPolicy.FindShadowing(global, new[] { global, surface }));
    }

    [Fact]
    public void FindShadowing_requires_chord_match()
    {
        var global = Binding("g", "Ctrl+V", HotkeyScope.Global);
        var surface = Binding("s", "Ctrl+Shift+V", HotkeyScope.Surface("ClipboardPanel"), shadows: "g");
        Assert.Null(HotkeyConflictPolicy.FindShadowing(global, new[] { global, surface }));
    }
}
