using System.Collections.Generic;
using System.Linq;
using BetterDesktop.Shell.Core.Hotkeys;
using BetterDesktop.Shell.Hotkeys.Contracts;
using Xunit;

namespace BetterDesktop.Shell.Core.Tests.Hotkeys;

/// <summary>
/// 作用域聚合器测试：多表面并发活跃合并（注册表 SetActiveScopes 是覆盖语义）、去重、全量覆盖、空集。
/// </summary>
public class HotkeyScopeTrackerTests
{
    private sealed class RecordingRegistry : IHotkeyRegistryService
    {
        public readonly List<List<string>> Pushed = new();

        public void SetActiveScopes(IReadOnlyList<string> scopeIds)
            => Pushed.Add(scopeIds.ToList());

        public IReadOnlyList<HotkeyView> GetActive() => new List<HotkeyView>();
        public IReadOnlyList<HotkeyView> GetAll() => new List<HotkeyView>();
        public IReadOnlyList<HotkeyConflict> GetConflicts() => new List<HotkeyConflict>();
        public RegistrationResult Register(HotkeyBinding binding, System.Action<HotkeyBinding>? onTrigger = null)
            => new(false, null, "not used");
        public RegistrationResult Declare(HotkeyBinding binding) => new(false, null, "not used");
        public void Unregister(string id)
        {
        }

        public RegistrationResult Rebind(string id, HotkeyChord chord) => new(false, null, "not used");
        public void SetEnabled(string id, bool enabled)
        {
        }

        public void SetVisible(string id, bool visible)
        {
        }

        public void ResetToDefault(string id)
        {
        }
    }

    [Fact]
    public void Enter_two_scopes_pushes_merged_snapshot()
    {
        var reg = new RecordingRegistry();
        var tracker = new HotkeyScopeTracker(reg);

        tracker.EnterScope("Surface.ClipboardPanel");
        tracker.EnterScope("Surface.MenuBar");

        Assert.Equal(2, reg.Pushed.Count); // 每次变化各推一次
        Assert.Equal(
            new[] { "Surface.ClipboardPanel", "Surface.MenuBar" },
            reg.Pushed[^1].OrderBy(s => s).ToArray());
    }

    [Fact]
    public void Exit_one_scope_keeps_the_other()
    {
        var reg = new RecordingRegistry();
        var tracker = new HotkeyScopeTracker(reg);
        tracker.EnterScope("Surface.ClipboardPanel");
        tracker.EnterScope("Surface.MenuBar");

        tracker.ExitScope("Surface.ClipboardPanel");

        Assert.Equal(new[] { "Surface.MenuBar" }, reg.Pushed[^1]);
    }

    [Fact]
    public void Enter_same_scope_twice_is_idempotent()
    {
        var reg = new RecordingRegistry();
        var tracker = new HotkeyScopeTracker(reg);

        tracker.EnterScope("Surface.ClipboardPanel");
        tracker.EnterScope("Surface.ClipboardPanel");

        Assert.Single(reg.Pushed);
    }

    [Fact]
    public void Exit_last_scope_pushes_empty_set()
    {
        var reg = new RecordingRegistry();
        var tracker = new HotkeyScopeTracker(reg);
        tracker.EnterScope("Surface.ClipboardPanel");

        tracker.ExitScope("Surface.ClipboardPanel");

        Assert.Empty(reg.Pushed[^1]);
    }

    [Fact]
    public void SetScopes_replaces_whole_set_only_on_change()
    {
        var reg = new RecordingRegistry();
        var tracker = new HotkeyScopeTracker(reg);

        tracker.SetScopes(new[] { "Surface.A", "Surface.B" });
        tracker.SetScopes(new[] { "Surface.B", "Surface.A" }); // 同集合不推送
        tracker.SetScopes(new[] { "Surface.B" }); // 变化推送

        Assert.Equal(2, reg.Pushed.Count);
        Assert.Equal(new[] { "Surface.A", "Surface.B" }, reg.Pushed[0].OrderBy(s => s).ToArray());
        Assert.Equal(new[] { "Surface.B" }, reg.Pushed[1]);
    }

    [Fact]
    public void Null_or_blank_scope_ignored()
    {
        var reg = new RecordingRegistry();
        var tracker = new HotkeyScopeTracker(reg);

        tracker.EnterScope(null!);
        tracker.EnterScope("  ");

        Assert.Empty(reg.Pushed);
    }
}
