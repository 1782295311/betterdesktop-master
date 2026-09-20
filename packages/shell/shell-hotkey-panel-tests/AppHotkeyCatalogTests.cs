using System.Linq;
using Xunit;

namespace BetterDesktop.Shell.HotkeyPanel.Tests;

/// <summary>
/// 应用场景热键目录（"打开 Blender 就先显示它的功能键"）：
/// 结构完整性、前台匹配、以及**裸键表达能力**（这是它必须独立于注册表的原因）。
/// </summary>
public class AppHotkeyCatalogTests
{
    [Fact]
    public void Catalog_is_nonempty_and_well_formed()
    {
        Assert.NotEmpty(AppHotkeyCatalog.All);
        Assert.All(AppHotkeyCatalog.All, profile =>
        {
            Assert.False(string.IsNullOrWhiteSpace(profile.App));
            Assert.NotEmpty(profile.ProcessNames);
            Assert.NotEmpty(profile.Groups);
            Assert.True(profile.Count > 0);
            Assert.All(profile.Groups, group =>
            {
                Assert.False(string.IsNullOrWhiteSpace(group.Title));
                Assert.NotEmpty(group.Items);
                Assert.All(group.Items, item =>
                {
                    Assert.False(string.IsNullOrWhiteSpace(item.Chord));
                    Assert.False(string.IsNullOrWhiteSpace(item.Description));
                });
            });
        });
    }

    [Fact]
    public void Catalog_app_names_are_unique()
    {
        var names = AppHotkeyCatalog.All.Select(p => p.App).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void Match_hits_blender_case_insensitively()
    {
        Assert.Equal("Blender", AppHotkeyCatalog.Match("blender")?.App);
        Assert.Equal("Blender", AppHotkeyCatalog.Match("BLENDER")?.App);
    }

    [Fact]
    public void Match_returns_null_for_unknown_or_empty()
    {
        Assert.Null(AppHotkeyCatalog.Match("app-not-in-catalog"));
        Assert.Null(AppHotkeyCatalog.Match(null));
        Assert.Null(AppHotkeyCatalog.Match("   "));
    }

    [Fact]
    public void Catalog_supports_bare_keys_which_registry_model_cannot()
    {
        // Blender 的 G / R / S / Tab 都是裸键 —— 注册表模型（HotkeySpec 要求至少一个修饰键）
        // 表达不了它们，这正是"应用热键目录"必须独立成一条通道的原因。
        var blender = AppHotkeyCatalog.Match("blender");
        Assert.NotNull(blender);
        var chords = blender!.Groups.SelectMany(g => g.Items).Select(i => i.Chord).ToList();
        Assert.Contains("G", chords);
        Assert.Contains("Tab", chords);
    }
}
