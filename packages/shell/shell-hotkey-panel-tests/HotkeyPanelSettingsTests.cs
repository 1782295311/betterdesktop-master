// 侧板显隐设置的纯逻辑单测（2026-09-17 用户需求"增加侧板的启动和关闭"）。
//
// 钉住三条不变量：
//   ① 默认**显示**（保持既有行为：装好即常驻，不想看再关）；
//   ② 关掉是持久化意图（写进设置键，跨重启保持）；
//   ③ 设置服务缺失（降级）时读回默认值 —— 不静默改变既有行为，也不抛。

using System;
using System.Collections.Generic;
using BetterDesktop.Shell.Settings.Contracts;
using Xunit;

namespace BetterDesktop.Shell.HotkeyPanel.Tests;

public class HotkeyPanelSettingsTests
{
    private sealed class MemSettings : ISettingsService
    {
        public Dictionary<string, object?> Store { get; } = new(StringComparer.Ordinal);

        public T? Get<T>(string key, T? defaultValue = default)
            => Store.TryGetValue(key, out var v) && v is T t ? t : defaultValue;

        public void Set<T>(string key, T value) => Store[key] = value;
    }

    [Fact]
    public void Default_IsVisible_WhenNotConfigured()
    {
        var settings = new MemSettings();

        Assert.True(HotkeyPanelSettings.IsEnabled(settings));
    }

    [Fact]
    public void SetEnabled_PersistsUnderDocumentedKey()
    {
        var settings = new MemSettings();

        HotkeyPanelSettings.SetEnabled(settings, false);

        Assert.False(HotkeyPanelSettings.IsEnabled(settings));
        Assert.True(settings.Store.ContainsKey("hotkeys-panel.enabled"));
        Assert.Equal(false, settings.Store["hotkeys-panel.enabled"]);

        HotkeyPanelSettings.SetEnabled(settings, true);
        Assert.True(HotkeyPanelSettings.IsEnabled(settings));
    }

    [Fact]
    public void MissingSettingsService_FallsBackToDefault_AndDoesNotThrow()
    {
        // 降级（设置服务未注册）：读回默认、写不抛
        Assert.True(HotkeyPanelSettings.IsEnabled(null));
        HotkeyPanelSettings.SetEnabled(null, false);
    }

    [Fact]
    public void ToggleHotkey_DefaultChord_ParsesWithModifier()
    {
        // 切换热键必须真能被注册表解析（HotkeySpec 是唯一解析来源），否则 Register 直接 fail-closed 失效
        var ok = BetterDesktop.Shell.Clipboard.Ipc.HotkeySpec.TryParse(
            HotkeyPanelSettings.ToggleHotkeyDefault, out var modifiers, out var mainKey, out var canonical);

        Assert.True(ok, "默认切换热键必须是 HotkeySpec 认得出的规范串");
        // 必须带修饰键（裸键极易与其它程序抢占，也不该由我们独占）：
        // MOD_ALT = 0x0001 / MOD_CONTROL = 0x0002（winuser.h）
        Assert.True((modifiers & 0x0001) != 0, "缺 Alt 修饰键");
        Assert.True((modifiers & 0x0002) != 0, "缺 Ctrl 修饰键");
        Assert.Equal("H", mainKey);
        Assert.Equal(HotkeyPanelSettings.ToggleHotkeyDefault, canonical);
    }
}
