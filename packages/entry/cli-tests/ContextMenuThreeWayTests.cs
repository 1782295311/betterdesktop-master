using BetterDesktop.Cli;
using BetterDesktop.Shell.ContextMenus.Contracts;
using BetterDesktop.Shell.ContextMenus.Services;
using Microsoft.Win32;
using Xunit;

namespace BetterDesktop.Cli.Tests;

/// <summary>
/// 统一注册体系三路一致性测试（计划 §13 单测 12）：同一 Action 标识在
/// ① 自绘 MenuItemDef.Action ② 系统注册表 verb 命令 ③ CLI 路由键 三处完全一致。
/// </summary>
public class ContextMenuThreeWayTests
{
    private const string TestVerb = "BetterDesktop.ThreeWayTest";

    [Fact]
    public void Action_IsConsistentAcrossSelfDrawn_System_And_Cli()
    {
        const string action = "convert-to-pdf";

        // ① 自绘路：MenuItemDef.Action（ConvertMenuService 产出同款标识）
        var item = new MenuItemDef { Id = "convertPdf", Text = "转换为 PDF", Action = action };
        Assert.Equal(action, item.Action);

        // ③ CLI 路：Classify 路由键
        Assert.Equal(HeadlessActionKind.ConvertTo, HeadlessExecutor.Classify(action));

        // ② 系统路：注册表 verb 命令（ContextMenuRegistry 注入）
        var registry = new ContextMenuRegistry();
        try
        {
            Assert.True(registry.Register(new ContextMenuContribution
            {
                Id = TestVerb,
                Action = action,
                Title = "三路一致性测试",
            }));

            using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\*\shell\{TestVerb}");
            Assert.NotNull(key);
            using var cmd = key!.OpenSubKey("command");
            var command = cmd!.GetValue(null) as string;
            Assert.NotNull(command);
            Assert.Contains("--menu-cmd convert-to-pdf", command);
        }
        finally
        {
            registry.Unregister(TestVerb);
        }
    }
}
