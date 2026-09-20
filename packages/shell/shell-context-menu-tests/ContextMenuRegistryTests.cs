using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.ContextMenus.Contracts;
using BetterDesktop.Shell.ContextMenus.Services;
using Microsoft.Win32;
using Xunit;

namespace BetterDesktop.Shell.ContextMenu.Tests;

/// <summary>
/// ContextMenuRegistry 单测（计划 §13 单测 9/10）：HKCU verb 注入行为——
/// 契约校验拒绝 / 幂等重写 / 注销删树 / 场景路径映射。测试用独立 verb，测后清理，不污染真实菜单。
/// </summary>
public class ContextMenuRegistryTests
{
    private const string TestVerb = "BetterDesktop.TestVerb";

    [Fact]
    public void Register_FileScene_CreatesVerbPointingToCli()
    {
        var registry = new ContextMenuRegistry();
        try
        {
            var ok = registry.Register(new ContextMenuContribution
            {
                Id = TestVerb,
                Action = "convert-to-test",
                Title = "测试转换",
            });

            Assert.True(ok);
            using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\*\shell\{TestVerb}");
            Assert.NotNull(key);
            Assert.Equal("测试转换", key!.GetValue("MUIVerb"));
            using var cmd = key.OpenSubKey("command");
            var command = cmd!.GetValue(null) as string;
            // 命令 = MenuCommandPaths 单点解析的 CLI 路径 + --menu-cmd <Action> + 默认 %1 参数
            // （同源；不硬编码 exe 名，测试环境 GetCliPath 回退 testhost 亦成立）
            Assert.Equal($"\"{MenuCommandPaths.GetCliPath()}\" --menu-cmd convert-to-test \"%1\"", command);
        }
        finally
        {
            registry.Unregister(TestVerb);
        }
    }

    [Fact]
    public void Register_IsIdempotent_NoDuplicateKeys()
    {
        var registry = new ContextMenuRegistry();
        try
        {
            var contribution = new ContextMenuContribution
            {
                Id = TestVerb,
                Action = "convert-to-test",
                Title = "测试转换",
            };

            Assert.True(registry.Register(contribution));
            Assert.True(registry.Register(contribution)); // 重写同值，天然去重

            using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\*\shell\{TestVerb}");
            Assert.NotNull(key);
            Assert.Null(Registry.CurrentUser.OpenSubKey($@"Software\Classes\*\shell\{TestVerb} (2)"));
        }
        finally
        {
            registry.Unregister(TestVerb);
        }
    }

    [Fact]
    public void Unregister_RemovesVerbTree()
    {
        var registry = new ContextMenuRegistry();
        var contribution = new ContextMenuContribution
        {
            Id = TestVerb,
            Action = "convert-to-test",
            Title = "测试转换",
        };

        Assert.True(registry.Register(contribution));
        Assert.True(registry.IsRegistered(TestVerb));

        Assert.True(registry.Unregister(TestVerb));
        Assert.False(registry.IsRegistered(TestVerb));
        Assert.Null(Registry.CurrentUser.OpenSubKey($@"Software\Classes\*\shell\{TestVerb}"));
    }

    [Theory]
    [InlineData(MenuScene.File, @"Software\Classes\*\shell\")]
    [InlineData(MenuScene.Directory, @"Software\Classes\Directory\shell\")]
    [InlineData(MenuScene.Background, @"Software\Classes\Directory\Background\shell\")]
    public void Register_SceneMapsToRegistryPath(MenuScene scene, string expectedPrefix)
    {
        var registry = new ContextMenuRegistry();
        try
        {
            Assert.True(registry.Register(new ContextMenuContribution
            {
                Id = TestVerb,
                Action = "test-action",
                Title = "场景测试",
                Scene = scene,
            }));

            Assert.NotNull(Registry.CurrentUser.OpenSubKey(expectedPrefix + TestVerb));
        }
        finally
        {
            registry.Unregister(TestVerb);
        }
    }

    [Fact]
    public void Register_InvalidContribution_Rejected()
    {
        var registry = new ContextMenuRegistry();

        Assert.False(registry.Register(new ContextMenuContribution { Id = "", Action = "a", Title = "t" }));
        Assert.False(registry.Register(new ContextMenuContribution { Id = "v", Action = "", Title = "t" }));
        Assert.False(registry.Register(new ContextMenuContribution { Id = "v", Action = "a", Title = "" }));
    }
}
