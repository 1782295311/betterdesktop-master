using System.Windows.Input;
using Xunit;

namespace BetterDesktop.Shell.HotkeyPanel.Tests;

/// <summary>录键校验（复制自设置中心模式）：修饰键要求 / 裸键拒绝 / 内置键冲突 / 组合构建。</summary>
public class HotkeyCaptureKeysTests
{
    [Fact]
    public void Bare_key_without_modifier_rejected()
    {
        Assert.False(HotkeyCaptureKeys.TryBuild(ModifierKeys.None, Key.K, out _, out var reason));
        Assert.Contains("Ctrl/Alt/Shift", reason);
    }

    [Fact]
    public void Modifier_key_alone_rejected()
    {
        Assert.False(HotkeyCaptureKeys.TryBuild(ModifierKeys.Control, Key.LeftCtrl, out _, out var reason));
        Assert.Contains("再按一个主键", reason);
    }

    [Fact]
    public void Builtin_engine_key_conflict_rejected()
    {
        Assert.False(HotkeyCaptureKeys.TryBuild(
            ModifierKeys.Control | ModifierKeys.Shift, Key.V, out _, out var reason));
        Assert.Contains("全局热键冲突", reason);
    }

    [Fact]
    public void Valid_combo_builds_canonical_spec()
    {
        Assert.True(HotkeyCaptureKeys.TryBuild(
            ModifierKeys.Control | ModifierKeys.Shift, Key.K, out var spec, out _));
        Assert.Equal("Ctrl+Shift+K", spec);
    }

    [Fact]
    public void Describe_empty_shows_unset()
    {
        Assert.Equal("（未设置）", HotkeyCaptureKeys.Describe(null));
        Assert.Equal("（未设置）", HotkeyCaptureKeys.Describe("  "));
        Assert.Equal("Ctrl+Shift+K", HotkeyCaptureKeys.Describe("Ctrl+Shift+K"));
    }
}
