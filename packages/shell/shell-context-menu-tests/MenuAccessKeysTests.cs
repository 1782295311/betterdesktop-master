// 菜单助记分配器（计划 §8 MenuAccessKeysTests）：同层唯一、显式标注优先、中文不抛错。

using BetterDesktop.Shell.ContextMenus.Services;
using Xunit;

namespace BetterDesktop.Shell.ContextMenu.Tests;

public class MenuAccessKeysTests
{
    [Fact]
    public void EnglishItems_GetUniqueKeys()
    {
        var result = MenuAccessKeys.Assign(["Open", "Copy", "Cut"]);

        Assert.Equal(3, result.Count);
        var marked = result.Where(r => r is not null).ToList();
        Assert.Equal(3, marked.Count); // 英文标题都有可分配字符
        // 各项 & 插入位置的助记字符互不相同
        var keys = result.Select(r => r![r!.IndexOf('&') + 1]).ToHashSet();
        Assert.Equal(marked.Count, keys.Count);
    }

    [Fact]
    public void ExplicitMarker_Wins_AndBlocksSameKey()
    {
        var result = MenuAccessKeys.Assign(["编辑(_E)", "Exit"]);

        Assert.Equal("编辑&E", result[0]); // "(_E)" 被替换为 &E
        Assert.NotNull(result[1]);
        // E 被显式项占用 → "Exit" 顺延取首个未占用字符（x）
        var assigned = result[1]![result[1]!.IndexOf('&') + 1];
        Assert.Equal('x', assigned);
    }

    [Fact]
    public void ChineseItems_NoAutoAssign_NoThrow()
    {
        var result = MenuAccessKeys.Assign(["中文一", "中文二"]);

        Assert.All(result, Assert.Null); // 第一版不引拼音库：中文不自动分配（OQ7）
    }

    [Fact]
    public void Conflict_FallsThroughToNextFreeChar()
    {
        // 两项都以 S 开头：第一项取 S，第二项顺延取下一个可用字符
        var result = MenuAccessKeys.Assign(["Save", "Send"]);

        Assert.Equal("&Save", result[0]);
        Assert.NotNull(result[1]);
        Assert.Equal("S", result[0]![result[0]!.IndexOf('&') + 1].ToString());
        Assert.NotEqual("S", result[1]![result[1]!.IndexOf('&') + 1].ToString());
    }
}
