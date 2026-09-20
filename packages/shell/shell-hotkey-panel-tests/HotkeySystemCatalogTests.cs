using System.Collections.Generic;
using System.Linq;
using BetterDesktop.Shell.Clipboard.Ipc;
using BetterDesktop.Shell.HotkeyPanel.Sections;
using BetterDesktop.Shell.Hotkeys.Contracts;
using Xunit;

namespace BetterDesktop.Shell.HotkeyPanel.Tests;

/// <summary>
/// 系统热键目录（Windows 全局热键清单）与"检测快捷键"逻辑：
/// 清单完整性（非空/Id 唯一/键位合法且唯一/子集包含）、与自家声明不冲突、
/// AnalyzeHotkey 的三条识别路径（系统库 → 自家注册表 → RegisterHotKey 试注册）。
/// </summary>
public class HotkeySystemCatalogTests
{
    [Fact]
    public void System_catalog_is_nonempty_and_large_enough()
    {
        Assert.True(HotkeyDeclarations.SystemHotkeys.Count >= 20,
            $"系统热键清单应不少于 20 条，实际 {HotkeyDeclarations.SystemHotkeys.Count}");
    }

    [Fact]
    public void System_catalog_covers_all_major_groups()
    {
        // 用户反馈"系统热键显示不全"→ 清单已从 21 条扩到 45+ 条并按主题分组；
        // 这条测试把"覆盖度 + 分组完整性"钉住，防止后续被改回精简版。
        Assert.True(HotkeyDeclarations.SystemHotkeys.Count >= 90,
            $"系统热键 + 通用键清单应不少于 90 条，实际 {HotkeyDeclarations.SystemHotkeys.Count}");
        Assert.True(HotkeyDeclarations.SystemHotkeyGroups.Count >= 6,
            $"系统热键分组应不少于 6 组，实际 {HotkeyDeclarations.SystemHotkeyGroups.Count}");
        // 用户要求补上 Ctrl+C 这类通用编辑键（应用内生效，非全局）
        Assert.Contains(HotkeyDeclarations.SystemHotkeyGroups, g => g.Title.Contains("编辑"));
        Assert.Contains(HotkeyDeclarations.SystemHotkeys, b => b.Chord.Spec == "Ctrl+C");
        Assert.All(HotkeyDeclarations.SystemHotkeyGroups, g =>
        {
            Assert.False(string.IsNullOrWhiteSpace(g.Title));
            Assert.NotEmpty(g.Items);
        });
        // UI 只依赖分组渲染 → 分组扁平化后必须与全量清单逐条一致（不能漏也不能重）
        Assert.Equal(
            HotkeyDeclarations.SystemHotkeys.Count,
            HotkeyDeclarations.SystemHotkeyGroups.Sum(g => g.Items.Count));
    }

    [Fact]
    public void System_catalog_ids_are_unique()
    {
        var ids = HotkeyDeclarations.SystemHotkeys.Select(b => b.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(HotkeyDeclarations.SystemHotkeys, b => Assert.True(HotkeyDeclarations.IsSystem(b.Id)));
    }

    [Fact]
    public void System_catalog_chords_are_valid_and_unique()
    {
        var canonicals = HotkeyDeclarations.SystemHotkeys
            .Select(b =>
            {
                Assert.True(HotkeySpec.TryParse(b.Chord.Spec, out _, out _, out var canonical),
                    $"系统热键 {b.Id} 的键位不合法：{b.Chord.Spec}");
                return canonical;
            })
            .ToList();
        Assert.Equal(canonicals.Count, canonicals.Distinct().Count());
    }

    [Fact]
    public void Sidebar_subset_is_inside_full_catalog()
    {
        var allIds = HotkeyDeclarations.SystemHotkeys.Select(b => b.Id).ToHashSet();
        Assert.NotEmpty(HotkeyDeclarations.SystemSidebarIds);
        Assert.All(HotkeyDeclarations.SystemSidebarIds, id => Assert.Contains(id, allIds));
    }

    [Fact]
    public void System_catalog_does_not_conflict_with_own_declarations()
    {
        // 自家声明（含 paste-back 已配置态）与系统热键在 Global 作用域不能撞键位。
        var own = HotkeyDeclarations.Build(null)
            .Where(b => !HotkeyDeclarations.IsSystem(b.Id))
            .Select(b => Canonical(b.Chord.Spec))
            .Where(c => c.Length > 0)
            .ToHashSet();

        foreach (var sys in HotkeyDeclarations.SystemHotkeys)
        {
            var c = Canonical(sys.Chord.Spec);
            Assert.DoesNotContain(c, own);
        }
    }

    [Fact]
    public void FindSystemBySpec_matches_canonical_spelling()
    {
        Assert.Equal("system.win-e", HotkeyDeclarations.FindSystemBySpec("Win+E")?.Id);
        // 别名/乱序归一：win+e、E+Win 都该命中
        Assert.Equal("system.win-e", HotkeyDeclarations.FindSystemBySpec("win+e")?.Id);
    }

    [Fact]
    public void FindSystemBySpec_returns_null_for_unknown()
    {
        Assert.Null(HotkeyDeclarations.FindSystemBySpec("Ctrl+Shift+K"));
        Assert.Null(HotkeyDeclarations.FindSystemBySpec("K")); // 无修饰键，解析失败
    }

    [Fact]
    public void Analyze_identifies_system_hotkey()
    {
        var result = HotkeySettingsSection.AnalyzeHotkey(new FakeRegistry(), "Win+E");
        Assert.Contains("系统热键", result);
        Assert.Contains("资源管理器", result);
    }

    [Fact]
    public void Analyze_identifies_own_registry_hit()
    {
        var registry = new FakeRegistry(new HotkeyBinding(
            "clipboard.paste-back", new HotkeyChord("Ctrl+Shift+K"), HotkeyScope.Global,
            "粘贴回原窗口", HotkeySource.SystemHotkey, new HotkeyChord("Ctrl+Shift+K"), "panel"));
        var result = HotkeySettingsSection.AnalyzeHotkey(registry, "Ctrl+Shift+K");
        Assert.Contains("BetterDesktop", result);
        Assert.Contains("粘贴回原窗口", result);
    }

    [Fact]
    public void Analyze_unparseable_reports_error()
    {
        var result = HotkeySettingsSection.AnalyzeHotkey(new FakeRegistry(), "K");
        Assert.Contains("无法解析", result);
    }

    [Fact]
    public void WriteCaptureHotkey_rejects_multi_char_key()
    {
        // capture 的 HotKeyManager 只认单字符主键——F1 这类组合必须拒绝（不假装改成功、不落盘）。
        var err = HotkeyDeclarations.WriteCaptureHotkey("Ctrl+Shift+F1");
        Assert.NotNull(err);
        Assert.Contains("单键", err);
    }

    [Fact]
    public void WriteCaptureHotkey_rejects_bare_key()
    {
        var err = HotkeyDeclarations.WriteCaptureHotkey("K");
        Assert.NotNull(err);
        Assert.Contains("无法解析", err);
    }

    private static string Canonical(string spec)
        => HotkeySpec.TryParse(spec, out _, out _, out var canonical) ? canonical : string.Empty;

    /// <summary>最小注册表替身（AnalyzeHotkey 只用 GetAll）。</summary>
    private sealed class FakeRegistry : IHotkeyRegistryService
    {
        private readonly List<HotkeyView> _all;

        public FakeRegistry(params HotkeyBinding[] bindings)
            => _all = bindings.Select(b => new HotkeyView(b, Enabled: true, Visible: true, ShadowedBy: false, OsConflict: false)).ToList();

        public IReadOnlyList<HotkeyView> GetAll() => _all;
        public IReadOnlyList<HotkeyView> GetActive() => _all;
        public IReadOnlyList<HotkeyConflict> GetConflicts() => new List<HotkeyConflict>();
        public RegistrationResult Register(HotkeyBinding binding, System.Action<HotkeyBinding>? onTrigger = null)
            => new(true, null, null);
        public RegistrationResult Declare(HotkeyBinding binding) => new(true, null, null);
        public void Unregister(string id) { }
        public RegistrationResult Rebind(string id, HotkeyChord chord) => new(true, null, null);
        public void SetEnabled(string id, bool enabled) { }
        public void SetVisible(string id, bool visible) { }
        public void ResetToDefault(string id) { }
        public void SetActiveScopes(IReadOnlyList<string> scopeIds) { }
    }
}
