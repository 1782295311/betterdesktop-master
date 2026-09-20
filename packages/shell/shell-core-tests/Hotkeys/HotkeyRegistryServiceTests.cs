using System;
using System.Collections.Generic;
using System.Linq;
using BetterDesktop.Shell.Core.Hotkeys;
using BetterDesktop.Shell.Hotkeys.Contracts;
using BetterDesktop.Shell.Settings.Contracts;
using Xunit;

namespace BetterDesktop.Shell.Core.Tests.Hotkeys;

/// <summary>
/// 热键注册表服务测试（热键计划 T1-T5：冲突/作用域/键表/settings 往返/注册失败路径）。
/// 全部用假中心窗口 + 内存设置驱动，不装真实钩子（键表消费直接调内部方法）。
/// </summary>
public class HotkeyRegistryServiceTests
{
    private sealed class FakeHotkeyWindow : IHotkeyWindow
    {
        public readonly List<(int Id, int Modifiers, uint Vk)> Registered = new();
        public readonly List<int> Unregistered = new();
        public bool FailRegistration;

        public bool Register(int id, int modifiers, uint vk)
        {
            if (FailRegistration)
            {
                return false;
            }

            Registered.Add((id, modifiers, vk));
            return true;
        }

        public bool Unregister(int id)
        {
            Unregistered.Add(id);
            return true;
        }

        public void Dispose()
        {
        }
    }

    private sealed class MemorySettings : ISettingsService
    {
        private readonly Dictionary<string, object?> _store = new(StringComparer.Ordinal);

        public T? Get<T>(string key, T? defaultValue = default)
            => _store.TryGetValue(key, out var v) && v is T t ? t : defaultValue;

        public void Set<T>(string key, T value) => _store[key] = value;
    }

    private static HotkeyBinding Binding(
        string id,
        string chord,
        HotkeyScope scope,
        HotkeySource source = HotkeySource.SystemHotkey,
        string? shadows = null)
        => new(
            id, new HotkeyChord(chord), scope, "测试 " + id, source,
            new HotkeyChord(chord), "tests", Shadows: shadows);

    private static (HotkeyRegistryService Service, FakeHotkeyWindow Window, MemorySettings Settings)
        CreateService()
    {
        var settings = new MemorySettings();
        var window = new FakeHotkeyWindow();
        var service = new HotkeyRegistryService(settings, window);
        return (service, window, settings);
    }

    // ---- 注册与冲突（T1）----

    [Fact]
    public void Register_global_binding_appears_in_active()
    {
        var (svc, _, _) = CreateService();
        var r = svc.Register(Binding("a", "Ctrl+Shift+K", HotkeyScope.Global));
        Assert.True(r.Ok);

        var active = svc.GetActive();
        Assert.Single(active);
        Assert.Equal("a", active[0].Binding.Id);
        Assert.True(active[0].Visible);
        Assert.False(active[0].ShadowedBy);
        Assert.False(active[0].OsConflict);
    }

    [Fact]
    public void Register_duplicate_id_rejected()
    {
        var (svc, _, _) = CreateService();
        svc.Register(Binding("a", "Ctrl+Shift+K", HotkeyScope.Global));
        var r = svc.Register(Binding("a", "Ctrl+Shift+J", HotkeyScope.Global));
        Assert.False(r.Ok);
        Assert.Contains("已存在", r.Reason);
    }

    [Fact]
    public void Register_same_scope_same_chord_rejected_with_owner()
    {
        var (svc, _, _) = CreateService();
        Assert.True(svc.Register(Binding("a", "Ctrl+Shift+K", HotkeyScope.Global)).Ok);
        var r = svc.Register(Binding("b", "Ctrl+Shift+K", HotkeyScope.Global));
        Assert.False(r.Ok);
        Assert.Equal("a", r.ConflictWithId);
    }

    [Fact]
    public void Register_same_scope_different_chord_allowed()
    {
        var (svc, _, _) = CreateService();
        Assert.True(svc.Register(Binding("a", "Ctrl+Shift+K", HotkeyScope.Global)).Ok);
        Assert.True(svc.Register(Binding("b", "Ctrl+Shift+J", HotkeyScope.Global)).Ok);
        Assert.Equal(2, svc.GetActive().Count);
    }

    [Fact]
    public void Register_cross_scope_undeclared_rejected()
    {
        var (svc, _, _) = CreateService();
        Assert.True(svc.Register(Binding("g", "Ctrl+V", HotkeyScope.Global)).Ok);
        var r = svc.Register(Binding("s", "Ctrl+V", HotkeyScope.Surface("ClipboardPanel")));
        Assert.False(r.Ok);
        Assert.Equal("g", r.ConflictWithId);
    }

    [Fact]
    public void Register_cross_scope_with_shadows_accepted_and_marks_takeover()
    {
        var (svc, _, _) = CreateService();
        Assert.True(svc.Register(Binding("clipboard.paste", "Ctrl+V", HotkeyScope.Global)).Ok);
        var r = svc.Register(Binding("clipboard.paste-next", "Ctrl+V", HotkeyScope.Surface("ClipboardPanel"),
            shadows: "clipboard.paste"));
        Assert.True(r.Ok);

        // 被接管方在 GetActive 里可见且标 ShadowedBy
        var active = svc.GetActive();
        var global = active.Single(v => v.Binding.Id == "clipboard.paste");
        Assert.True(global.ShadowedBy);

        var conflicts = svc.GetConflicts();
        var c = conflicts.Single(x => x.Id == "clipboard.paste");
        Assert.Equal("clipboard.paste-next", c.OtherId);
    }

    [Fact]
    public void Register_builtin_key_rejected()
    {
        var (svc, _, _) = CreateService();
        var r = svc.Register(Binding("x", "Ctrl+Shift+V", HotkeyScope.Global));
        Assert.False(r.Ok);
        Assert.Contains("内置热键", r.Reason);
    }

    [Fact]
    public void Register_invalid_chord_rejected()
    {
        var (svc, _, _) = CreateService();
        Assert.False(svc.Register(Binding("x", "F5", HotkeyScope.Global)).Ok); // 无修饰键
        Assert.False(svc.Register(Binding("x", "", HotkeyScope.Global)).Ok);
    }

    // ---- 作用域（T2）----

    [Fact]
    public void Surface_binding_hidden_when_scope_inactive()
    {
        var (svc, _, _) = CreateService();
        svc.Register(Binding("g", "Ctrl+Shift+K", HotkeyScope.Global));
        svc.Register(Binding("s", "Ctrl+Shift+J", HotkeyScope.Surface("ClipboardPanel")));
        svc.SetActiveScopes(Array.Empty<string>());

        var active = svc.GetActive();
        Assert.Single(active);
        Assert.Equal("g", active[0].Binding.Id);

        svc.SetActiveScopes(new[] { "Surface.ClipboardPanel" });
        Assert.Equal(2, svc.GetActive().Count);
    }

    // ---- 改键（D3 机制）----

    [Fact]
    public void Rebind_releases_old_and_registers_new()
    {
        var (svc, win, _) = CreateService();
        Assert.True(svc.Register(Binding("a", "Ctrl+Shift+K", HotkeyScope.Global)).Ok);
        var oldId = win.Registered.Single().Id;

        var r = svc.Rebind("a", new HotkeyChord("Ctrl+Shift+J"));
        Assert.True(r.Ok);

        Assert.Contains(oldId, win.Unregistered);          // 旧键释放
        Assert.Equal(2, win.Registered.Count);             // 新旧各一次
        Assert.Equal("Ctrl+Shift+J", svc.GetActive()[0].Binding.Chord.Spec);
    }

    [Fact]
    public void Rebind_to_conflicting_chord_rejected_keeps_old()
    {
        var (svc, win, _) = CreateService();
        Assert.True(svc.Register(Binding("a", "Ctrl+Shift+K", HotkeyScope.Global)).Ok);
        Assert.True(svc.Register(Binding("b", "Ctrl+Shift+J", HotkeyScope.Global)).Ok);

        var r = svc.Rebind("a", new HotkeyChord("Ctrl+Shift+J"));
        Assert.False(r.Ok);
        Assert.Equal("b", r.ConflictWithId);
        Assert.Equal(2, win.Registered.Count); // a、b 各注册一次，改键被拒未新增
        Assert.Equal("Ctrl+Shift+K", svc.GetAll().Single(v => v.Binding.Id == "a").Binding.Chord.Spec);
    }

    // ---- 启停（D8 机制）----

    [Fact]
    public void SetEnabled_false_releases_key_true_re_registers()
    {
        var (svc, win, _) = CreateService();
        Assert.True(svc.Register(Binding("a", "Ctrl+Shift+K", HotkeyScope.Global)).Ok);
        var id = win.Registered.Single().Id;

        svc.SetEnabled("a", false);
        Assert.Contains(id, win.Unregistered);
        Assert.Empty(svc.GetActive()); // 停用后不再显示

        svc.SetEnabled("a", true);
        Assert.Equal(2, win.Registered.Count);
        Assert.Single(svc.GetActive());
    }

    // ---- 隐藏（红线：隐藏 ≠ 静音）----

    [Fact]
    public void SetVisible_false_hides_but_registration_untouched()
    {
        var (svc, win, _) = CreateService();
        Assert.True(svc.Register(Binding("a", "Ctrl+Shift+K", HotkeyScope.Global)).Ok);

        svc.SetVisible("a", false);
        Assert.Empty(svc.GetActive());       // 侧板不再显示
        Assert.Single(win.Registered);       // 键位注册未动
        Assert.Contains(svc.GetAll(), v => v.Binding.Id == "a" && !v.Visible); // 设置中心仍可见

        svc.SetVisible("a", true);
        Assert.Single(svc.GetActive());
    }

    [Fact]
    public void Hidden_os_conflict_still_shown()
    {
        var (svc, win, _) = CreateService();
        win.FailRegistration = true; // 模拟被其他程序占用
        Assert.True(svc.Register(Binding("a", "Ctrl+Shift+K", HotkeyScope.Global)).Ok);
        svc.SetVisible("a", false); // 用户隐藏了它

        var active = svc.GetActive();
        Assert.Single(active);                       // 隐藏 ≠ 静音：占用异常强制显示
        Assert.True(active[0].Visible);
        Assert.True(active[0].OsConflict);
    }

    // ---- 注册失败路径（T5）----

    [Fact]
    public void OsConflict_marked_and_listed_when_registration_fails()
    {
        var (svc, win, _) = CreateService();
        win.FailRegistration = true;
        Assert.True(svc.Register(Binding("a", "Ctrl+Shift+K", HotkeyScope.Global)).Ok); // 注册本身成功（条目入库）

        Assert.True(svc.GetActive()[0].OsConflict);
        var conflicts = svc.GetConflicts();
        var c = Assert.Single(conflicts);
        Assert.Equal("a", c.Id);
        Assert.Contains("占用", c.Reason);
    }

    // ---- settings 往返（T4）----

    [Fact]
    public void Persisted_visible_override_restored_on_new_instance()
    {
        var settings = new MemorySettings();
        using var w1 = new FakeHotkeyWindow();
        using (var svc1 = new HotkeyRegistryService(settings, w1))
        {
            Assert.True(svc1.Register(Binding("a", "Ctrl+Shift+K", HotkeyScope.Global)).Ok);
            svc1.SetVisible("a", false);
        }

        using var w2 = new FakeHotkeyWindow();
        using var svc2 = new HotkeyRegistryService(settings, w2);
        Assert.True(svc2.Register(Binding("a", "Ctrl+Shift+K", HotkeyScope.Global)).Ok);
        var view = svc2.GetAll().Single(v => v.Binding.Id == "a");
        Assert.False(view.Visible); // 重启后保持隐藏
    }

    [Fact]
    public void Persisted_rebind_restored_on_new_instance()
    {
        var settings = new MemorySettings();
        using var w1 = new FakeHotkeyWindow();
        using (var svc1 = new HotkeyRegistryService(settings, w1))
        {
            Assert.True(svc1.Register(Binding("a", "Ctrl+Shift+K", HotkeyScope.Global)).Ok);
            Assert.True(svc1.Rebind("a", new HotkeyChord("Ctrl+Shift+J")).Ok);
        }

        using var w2 = new FakeHotkeyWindow();
        using var svc2 = new HotkeyRegistryService(settings, w2);
        Assert.True(svc2.Register(Binding("a", "Ctrl+Shift+K", HotkeyScope.Global)).Ok);
        Assert.Equal("Ctrl+Shift+J", svc2.GetActive()[0].Binding.Chord.Spec); // 用户改键优先于默认
        Assert.Single(w2.Registered); // 新实例只按持久化后的键位注册一次
    }

    [Fact]
    public void ResetToDefault_clears_override_and_restores_default()
    {
        var (svc, win, _) = CreateService();
        Assert.True(svc.Register(Binding("a", "Ctrl+Shift+K", HotkeyScope.Global)).Ok);
        Assert.True(svc.Rebind("a", new HotkeyChord("Ctrl+Shift+J")).Ok);
        svc.SetVisible("a", false);

        svc.ResetToDefault("a");
        var view = svc.GetAll().Single(v => v.Binding.Id == "a");
        Assert.Equal("Ctrl+Shift+K", view.Binding.Chord.Spec);
        Assert.True(view.Visible);
        Assert.True(view.Enabled);
    }

    [Fact]
    public void Missing_settings_uses_defaults()
    {
        var (svc, _, _) = CreateService(); // 空设置
        Assert.True(svc.Register(Binding("a", "Ctrl+Shift+K", HotkeyScope.Global)).Ok);
        var view = svc.GetAll().Single();
        Assert.True(view.Visible);
        Assert.True(view.Enabled);
    }

    [Fact]
    public void Corrupt_settings_falls_back_to_default()
    {
        var settings = new MemorySettings();
        settings.Set("hotkeys.a", "not-a-dto-object"); // 类型不匹配 → Get 返回默认
        using var fakeWindow = new FakeHotkeyWindow();
        using var svc = new HotkeyRegistryService(settings, fakeWindow);
        Assert.True(svc.Register(Binding("a", "Ctrl+Shift+K", HotkeyScope.Global)).Ok);
        Assert.Equal("Ctrl+Shift+K", svc.GetAll().Single().Binding.Chord.Spec);
    }

    // ---- 键表查表（T3，假键事件驱动）----

    [Fact]
    public void LowLevelHook_consume_matching_key_invokes_callback()
    {
        var (svc, _, _) = CreateService();
        HotkeyBinding? fired = null;
        Assert.True(svc.Register(
            Binding("hook", "Ctrl+Shift+F10", HotkeyScope.Global, HotkeySource.LowLevelHook),
            b => fired = b).Ok);

        // Ctrl+Shift = ModControl(2)|ModShift(4) = 6；VK_V = 0x56
        Assert.True(svc.TryConsumeLowLevelKey(0x79, 6));
        Assert.NotNull(fired);
        Assert.Equal("hook", fired!.Id);
    }

    [Fact]
    public void LowLevelHook_consume_ignores_norepeat_bit()
    {
        var (svc, _, _) = CreateService();
        var fired = false;
        Assert.True(svc.Register(
            Binding("hook", "Ctrl+Shift+F10", HotkeyScope.Global, HotkeySource.LowLevelHook),
            _ => fired = true).Ok);
        // MOD_NOREPEAT(0x4000) 与修饰键一起传入：比较时忽略高位
        Assert.True(svc.TryConsumeLowLevelKey(0x79, 6));
        Assert.True(fired);
    }

    [Fact]
    public void LowLevelHook_nonmatching_key_not_consumed()
    {
        var (svc, _, _) = CreateService();
        var fired = false;
        Assert.True(svc.Register(
            Binding("hook", "Ctrl+Shift+F10", HotkeyScope.Global, HotkeySource.LowLevelHook),
            _ => fired = true).Ok);

        Assert.False(svc.TryConsumeLowLevelKey(0x50, 6)); // 键不同
        Assert.False(svc.TryConsumeLowLevelKey(0x79, 2)); // 修饰键不同
        Assert.False(fired);
    }

    [Fact]
    public void LowLevelHook_disabled_not_consumed()
    {
        var (svc, _, _) = CreateService();
        var fired = false;
        Assert.True(svc.Register(
            Binding("hook", "Ctrl+Shift+F10", HotkeyScope.Global, HotkeySource.LowLevelHook),
            _ => fired = true).Ok);
        svc.SetEnabled("hook", false);

        Assert.False(svc.TryConsumeLowLevelKey(0x56, 6));
        Assert.False(fired);
    }

    [Fact]
    public void LowLevelHook_surface_scope_inactive_not_consumed()
    {
        var (svc, _, _) = CreateService();
        var fired = false;
        Assert.True(svc.Register(
            Binding("hook", "Ctrl+Shift+F10", HotkeyScope.Surface("ClipboardPanel"), HotkeySource.LowLevelHook),
            _ => fired = true).Ok);
        svc.SetActiveScopes(Array.Empty<string>());

        Assert.False(svc.TryConsumeLowLevelKey(0x56, 6));
        Assert.False(fired);

        svc.SetActiveScopes(new[] { "Surface.ClipboardPanel" });
        Assert.True(svc.TryConsumeLowLevelKey(0x79, 6));
        Assert.True(fired);
    }

    // ---- 系统热键分派（WM_HOTKEY）----

    [Fact]
    public void DispatchSystemHotkey_invokes_callback()
    {
        var (svc, win, _) = CreateService();
        HotkeyBinding? fired = null;
        Assert.True(svc.Register(
            Binding("sys", "Ctrl+Shift+K", HotkeyScope.Global),
            b => fired = b).Ok);
        var id = win.Registered.Single().Id;

        Assert.True(svc.DispatchSystemHotkey(id));
        Assert.NotNull(fired);
    }

    [Fact]
    public void DispatchSystemHotkey_scope_inactive_ignored()
    {
        var (svc, win, _) = CreateService();
        var fired = false;
        Assert.True(svc.Register(
            Binding("sys", "Ctrl+Shift+K", HotkeyScope.Surface("ClipboardPanel")),
            _ => fired = true).Ok);
        var id = win.Registered.Single().Id;
        svc.SetActiveScopes(Array.Empty<string>());

        Assert.False(svc.DispatchSystemHotkey(id));
        Assert.False(fired);
    }

    [Fact]
    public void DispatchSystemHotkey_unknown_id_not_handled()
    {
        var (svc, _, _) = CreateService();
        Assert.False(svc.DispatchSystemHotkey(9999));
    }

    // ---- 注销 ----

    [Fact]
    public void Unregister_releases_key()
    {
        var (svc, win, _) = CreateService();
        Assert.True(svc.Register(Binding("a", "Ctrl+Shift+K", HotkeyScope.Global)).Ok);
        var id = win.Registered.Single().Id;

        svc.Unregister("a");
        Assert.Contains(id, win.Unregistered);
        Assert.Empty(svc.GetAll());
    }

    [Fact]
    public void Changed_fires_on_mutations()
    {
        var (svc, _, _) = CreateService();
        var count = 0;
        svc.Changed += () => count++;

        svc.Register(Binding("a", "Ctrl+Shift+K", HotkeyScope.Global));
        svc.SetVisible("a", false);
        svc.SetActiveScopes(new[] { "Surface.X" });
        Assert.True(count >= 3);
    }
}
