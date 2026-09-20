using System;
using System.Collections.Generic;
using System.Linq;
using BetterDesktop.Shell.Core.Hotkeys;
using BetterDesktop.Shell.Hotkeys.Contracts;
using BetterDesktop.Shell.Settings.Contracts;
using Xunit;

namespace BetterDesktop.Shell.Core.Tests.Hotkeys;

/// <summary>
/// 声明条目（外部热键）测试：Declare 只展示不注册（不双占 0x581 红线）、冲突校验链与 Register 一致、
/// 改键/停用/恢复对声明条目不触碰中心窗口与钩子、持久化照常。
/// </summary>
public class HotkeyRegistryServiceDeclareTests
{
    private sealed class FakeHotkeyWindow : IHotkeyWindow
    {
        public readonly List<(int Id, int Modifiers, uint Vk)> Registered = new();
        public readonly List<int> Unregistered = new();

        public bool Register(int id, int modifiers, uint vk)
        {
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
        HotkeySource source = HotkeySource.SystemHotkey)
        => new(
            id, new HotkeyChord(chord), scope, "测试 " + id, source,
            new HotkeyChord(chord), "external");

    private static (HotkeyRegistryService Service, FakeHotkeyWindow Window, MemorySettings Settings)
        CreateService()
    {
        var settings = new MemorySettings();
        var window = new FakeHotkeyWindow();
        var service = new HotkeyRegistryService(settings, window);
        return (service, window, settings);
    }

    [Fact]
    public void Declare_global_binding_appears_in_active_without_registration()
    {
        var (svc, window, _) = CreateService();
        var r = svc.Declare(Binding("capture.toggle", "Win+Shift+B", HotkeyScope.Global));
        Assert.True(r.Ok);

        // 不双注册红线：声明条目绝不触碰中心窗口
        Assert.Empty(window.Registered);

        var active = svc.GetActive();
        Assert.Single(active);
        Assert.Equal("capture.toggle", active[0].Binding.Id);
        Assert.False(active[0].OsConflict);
    }

    [Fact]
    public void Declare_duplicate_id_rejected()
    {
        var (svc, _, _) = CreateService();
        Assert.True(svc.Declare(Binding("a", "Ctrl+Shift+K", HotkeyScope.Global)).Ok);
        var r = svc.Declare(Binding("a", "Ctrl+Shift+J", HotkeyScope.Global));
        Assert.False(r.Ok);
        Assert.Contains("已存在", r.Reason);
    }

    [Fact]
    public void Declare_invalid_chord_rejected()
    {
        var (svc, _, _) = CreateService();
        var r = svc.Declare(Binding("a", "K", HotkeyScope.Global)); // 无修饰键
        Assert.False(r.Ok);
        Assert.Contains("格式非法", r.Reason);
    }

    [Fact]
    public void Declare_builtin_chord_allowed()
    {
        // 声明迁移（HotkeyDeclarations.Build）要把引擎内置键（Ctrl+Shift+V/P/Backspace 等）登记进
        // 注册表供侧板/设置中心展示——Declare 不注册 OS 键（键位由声明方进程自注册，0x581），
        // 故放行内置键；内置键保护只在 Register（真注册）/ Rebind（用户改键）处生效。
        var (svc, _, _) = CreateService();
        var r = svc.Declare(Binding("a", "Ctrl+Shift+V", HotkeyScope.Global)); // 内置键
        Assert.True(r.Ok);
    }

    [Fact]
    public void Declare_same_scope_same_chord_conflicts_with_registered()
    {
        var (svc, _, _) = CreateService();
        Assert.True(svc.Register(Binding("mine", "Ctrl+Shift+K", HotkeyScope.Global)).Ok);
        var r = svc.Declare(Binding("external", "Ctrl+Shift+K", HotkeyScope.Global));
        Assert.False(r.Ok);
        Assert.Equal("mine", r.ConflictWithId);
    }

    [Fact]
    public void Declare_then_rebind_updates_memory_but_does_not_persist_chord()
    {
        // P1-2：声明条目的键位**唯一真相源在外部消费方配置**——Rebind 更新内存但不落盘 chord，
        // 否则"设置中心改配置键 → 重启 ApplyPersistedOverrides 用旧 override 覆盖配置值"会长期显示旧键。
        var (svc, window, settings) = CreateService();
        Assert.True(svc.Declare(Binding("capture.toggle", "Win+Shift+B", HotkeyScope.Global)).Ok);

        var r = svc.Rebind("capture.toggle", new HotkeyChord("Win+Shift+C"));
        Assert.True(r.Ok);
        Assert.Empty(window.Registered);
        Assert.Empty(window.Unregistered);

        var active = svc.GetActive();
        Assert.Single(active);
        Assert.Equal("Win+Shift+C", active[0].Binding.Chord.Spec); // 内存键位已更新

        // 重建服务（同一 settings）后重新声明：chord 不被持久化覆盖 → 回到声明值（配置键 = 真相源）
        // （若 chord 被持久化，此处会是 Win+Shift+C——即"设置中心改配置键被旧 override 覆盖"的老 bug）
        using var win2 = new FakeHotkeyWindow();
        using var svc2 = new HotkeyRegistryService(settings, win2);
        Assert.True(svc2.Declare(Binding("capture.toggle", "Win+Shift+B", HotkeyScope.Global)).Ok);
        Assert.Equal("Win+Shift+B", svc2.GetActive().Single().Binding.Chord.Spec);
    }

    [Fact]
    public void Declare_set_enabled_false_hides_from_active_without_window_ops()
    {
        var (svc, window, _) = CreateService();
        Assert.True(svc.Declare(Binding("a", "Ctrl+Shift+K", HotkeyScope.Global)).Ok);

        svc.SetEnabled("a", false);
        Assert.Empty(svc.GetActive());
        Assert.Empty(window.Registered);
        Assert.Empty(window.Unregistered);

        svc.SetEnabled("a", true);
        Assert.Single(svc.GetActive());
        Assert.Empty(window.Registered); // 声明条目启用也不注册
    }

    [Fact]
    public void Declare_set_visible_false_hides_but_keeps_registration_semantics()
    {
        var (svc, window, _) = CreateService();
        Assert.True(svc.Declare(Binding("a", "Ctrl+Shift+K", HotkeyScope.Global)).Ok);

        svc.SetVisible("a", false);
        Assert.Empty(svc.GetActive()); // 隐藏 = 侧板不显示
        Assert.Empty(window.Registered);

        // 隐藏 ≠ 停用：GetAll 仍可见 Enabled 状态
        var all = svc.GetAll();
        Assert.Single(all);
        Assert.True(all[0].Enabled);
        Assert.False(all[0].Visible);
    }

    [Fact]
    public void Declare_reset_to_default_restores_chord_without_window_ops()
    {
        var (svc, window, _) = CreateService();
        Assert.True(svc.Declare(Binding("a", "Ctrl+Shift+K", HotkeyScope.Global)).Ok);
        Assert.True(svc.Rebind("a", new HotkeyChord("Ctrl+Shift+J")).Ok);

        svc.ResetToDefault("a");
        Assert.Empty(window.Registered);
        Assert.Empty(window.Unregistered);
        Assert.Equal("Ctrl+Shift+K", svc.GetActive().Single().Binding.Chord.Spec);
    }

    [Fact]
    public void Declared_and_registered_entries_coexist_in_active()
    {
        var (svc, window, _) = CreateService();
        Assert.True(svc.Declare(Binding("ext", "Win+Shift+B", HotkeyScope.Global)).Ok);
        Assert.True(svc.Register(Binding("mine", "Ctrl+Shift+K", HotkeyScope.Global)).Ok);

        Assert.Equal(2, svc.GetActive().Count);
        Assert.Single(window.Registered); // 只有 Register 的条目进了中心窗口
    }
}
