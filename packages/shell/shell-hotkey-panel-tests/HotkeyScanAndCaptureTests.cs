using System.Linq;
using System.Text.Json.Nodes;
using BetterDesktop.Shell.Clipboard.Ipc;
using BetterDesktop.Shell.HotkeyPanel.Sections;
using Xunit;

namespace BetterDesktop.Shell.HotkeyPanel.Tests;

/// <summary>
/// 2026-09-16 用户反馈批次的回归测试：
/// ① capture 配置必须**读-改-写**（禁止整份覆写丢 <c>stickerTopmost</c>）；
/// ② 改键生效状态如实三态（重启成功 / 未运行下次生效 / 重启失败）；
/// ③ 第三方热键目录完整性；④ 扫描器可用性与归类。
/// </summary>
public class HotkeyScanAndCaptureTests
{
    // ---- ① capture 配置读-改-写 ----

    [Fact]
    public void PatchCaptureJson_preserves_other_settings()
    {
        // 真机 capture/settings.json 实际内容：改键不得抹掉 stickerTopmost。
        const string existing = """
        { "hotkey": { "enabled": true, "modifiers": "Win+Shift", "key": "B" }, "stickerTopmost": true }
        """;

        var json = HotkeyDeclarations.PatchCaptureJson(existing, enabled: true, modifiers: "Ctrl+Alt", key: "K");
        var root = JsonNode.Parse(json)!.AsObject();

        Assert.True(root["stickerTopmost"]!.GetValue<bool>(), "改键不能丢失其它设置字段");
        Assert.Equal("Ctrl+Alt", root["hotkey"]!["modifiers"]!.GetValue<string>());
        Assert.Equal("K", root["hotkey"]!["key"]!.GetValue<string>());
        Assert.True(root["hotkey"]!["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public void PatchCaptureJson_updates_only_requested_fields()
    {
        const string existing = """
        { "hotkey": { "enabled": true, "modifiers": "Win+Shift", "key": "B" } }
        """;

        // 停用：只动 enabled，键位必须保留（否则用户"停用再启用"会丢原键位）
        var json = HotkeyDeclarations.PatchCaptureJson(existing, enabled: false, modifiers: null, key: null);
        var hotkey = JsonNode.Parse(json)!["hotkey"]!.AsObject();

        Assert.False(hotkey["enabled"]!.GetValue<bool>());
        Assert.Equal("Win+Shift", hotkey["modifiers"]!.GetValue<string>());
        Assert.Equal("B", hotkey["key"]!.GetValue<string>());
    }

    [Fact]
    public void PatchCaptureJson_creates_missing_hotkey_section()
    {
        var json = HotkeyDeclarations.PatchCaptureJson("{}", enabled: true, modifiers: "Win+Shift", key: "K");
        var hotkey = JsonNode.Parse(json)!["hotkey"]!.AsObject();

        Assert.Equal("Win+Shift", hotkey["modifiers"]!.GetValue<string>());
        Assert.Equal("K", hotkey["key"]!.GetValue<string>());
    }

    [Fact]
    public void PatchCaptureJson_handles_empty_file()
    {
        var json = HotkeyDeclarations.PatchCaptureJson(null, enabled: true, modifiers: "Win+Shift", key: "J");
        Assert.NotNull(JsonNode.Parse(json)!["hotkey"]);
    }

    // ---- ② 改键生效状态三态（不假装生效） ----

    [Fact]
    public void DescribeRestart_reports_restarted()
    {
        var r = HotkeySettingsSection.DescribeRestart(
            ClipboardEngineLauncher.ExternalProcessRestart.Restarted, "引擎");
        Assert.True(r.Ok);
        Assert.Contains("立即生效", r.Message);
    }

    [Fact]
    public void DescribeRestart_reports_not_running_as_saved()
    {
        var r = HotkeySettingsSection.DescribeRestart(
            ClipboardEngineLauncher.ExternalProcessRestart.NotRunning, "截图进程");
        Assert.True(r.Ok);
        Assert.Contains("下次启动生效", r.Message);
        Assert.Contains("未运行", r.Message);
    }

    [Fact]
    public void DescribeRestart_reports_failure_honestly()
    {
        var r = HotkeySettingsSection.DescribeRestart(
            ClipboardEngineLauncher.ExternalProcessRestart.Failed, "引擎");
        Assert.False(r.Ok);
        Assert.Contains("失败", r.Message);
    }

    // ---- ③ 第三方热键目录 ----

    [Fact]
    public void ThirdParty_catalog_is_nonempty_and_well_formed()
    {
        Assert.NotEmpty(ThirdPartyHotkeyCatalog.All);
        Assert.All(ThirdPartyHotkeyCatalog.All, app =>
        {
            Assert.False(string.IsNullOrWhiteSpace(app.App));
            Assert.NotEmpty(app.ProcessNames);
            // 键位与备注不能同时为空（否则这一条对用户毫无信息）
            Assert.True(app.Bindings.Count > 0 || app.Notes.Count > 0, $"{app.App} 既无键位也无备注");
        });
    }

    [Fact]
    public void ThirdParty_running_is_subset_of_all()
    {
        var running = ThirdPartyHotkeyCatalog.Running();
        Assert.All(running, app => Assert.Contains(app, ThirdPartyHotkeyCatalog.All));
    }

    [Fact]
    public void ThirdParty_match_by_chord_normalizes_spelling()
    {
        // 大小写/顺序归一后应命中 NVIDIA 的 Alt+Z（"归属推断"的核心能力）
        var hit = ThirdPartyHotkeyCatalog.MatchByChord("alt+z");
        Assert.NotNull(hit);
        Assert.Contains("NVIDIA", hit!.Value.App.App);
    }

    [Fact]
    public void ThirdParty_match_by_chord_returns_null_for_unknown_chord()
    {
        // 查不出来就必须如实返回 null（UI 走"归属未知"分支，不许编造归属）
        Assert.Null(ThirdPartyHotkeyCatalog.MatchByChord("Ctrl+Alt+Shift+Win+F13"));
    }

    // ---- ④ 扫描器 ----

    [Fact]
    public void IsGloballyTaken_returns_false_for_unparseable_spec()
    {
        Assert.False(HotkeyScanner.IsGloballyTaken("K")); // 无修饰键 → 解析失败 → 不误判为被占用
        Assert.False(HotkeyScanner.IsGloballyTaken(""));
    }

    [Fact]
    public void Scan_probes_candidates_and_classifies_system_hotkeys()
    {
        var result = HotkeyScanner.Scan(registry: null);

        Assert.True(result.ProbedCount > 500, $"候选组合太少：{result.ProbedCount}");
        Assert.True(result.ElapsedMs >= 0);
        // Windows 自带热键（Win+E / Win+L 等）必然被系统占用 → 至少能识别出若干条，
        // 这是"扫描确实生效"的最小证据（不依赖本机装了哪些第三方软件）。
        Assert.Contains(result.Entries, e => e.Kind == HotkeyScanKind.System);
        Assert.All(result.Entries, e => Assert.True(HotkeySpec.TryParse(e.Spec, out _, out _, out _)));
    }

    [Fact]
    public void Scan_does_not_report_system_reserved_win_combinations_as_third_party()
    {
        // 【2026-09-16 真机校准实证】Win+任意 / Ctrl+Win+任意 / Ctrl+Shift+Win+任意 是
        // Windows 自己保留的修饰键组合（连 F24 都注册不上，错误码与"被占用"同为 1409）。
        // 未校准前它们被大量误报成"其他程序占用"——这正是用户看到的"大量第三方热键来源未确认"。
        var result = HotkeyScanner.Scan(registry: null);

        Assert.DoesNotContain(result.ThirdParty, e =>
            e.Spec.StartsWith("Win+", StringComparison.OrdinalIgnoreCase)
            || e.Spec.StartsWith("Ctrl+Win+", StringComparison.OrdinalIgnoreCase)
            || e.Spec.StartsWith("Ctrl+Shift+Win+", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ThirdParty_catalog_exposes_settings_path_for_known_apps()
    {
        // "能改"的现实路径：归属确定时告诉用户去哪改（我们无权改他人配置）
        var wechat = ThirdPartyHotkeyCatalog.All.First(a => a.App == "微信");
        Assert.False(string.IsNullOrWhiteSpace(wechat.SettingsPath));
    }
}
