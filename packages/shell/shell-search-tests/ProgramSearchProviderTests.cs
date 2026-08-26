using BetterDesktop.Shell.Search.Services;
using Xunit;

namespace BetterDesktop.Shell.Search.Tests;

/// <summary>
/// 程序搜索子序列匹配打分测试（对应步骤6验收：输入 "not" 能匹配 "Notepad"）。
/// </summary>
public class ProgramSearchProviderTests
{
    [Theory]
    [InlineData("not", "Notepad")]       // 验收项：顺序子序列命中
    [InlineData("gc", "Google Chrome")]  // 双词首命中
    [InlineData("no", "Notepad")]
    [InlineData("记事", "记事本")]
    public void Score_SubsequenceMatch_GreaterThanZero(string query, string name)
    {
        Assert.True(ProgramSearchProvider.Score(query, name) > 0, $"{query} 应匹配 {name}");
    }

    [Theory]
    [InlineData("xyz", "Notepad")]       // 字符不在名称中
    [InlineData("tno", "Notepad")]       // 顺序错乱（t 在 o 前，子序列不满足）
    [InlineData("", "Notepad")]          // 空查询
    [InlineData("notepadx", "Notepad")]  // 查询比名称长
    public void Score_NoMatch_ReturnsZero(string query, string name)
    {
        Assert.Equal(0, ProgramSearchProvider.Score(query, name));
    }

    [Fact]
    public void Score_NotVsNotepad_Matches()
    {
        // 步骤6 验收原文："输入 not 能匹配 Notepad"
        Assert.True(ProgramSearchProvider.Score("not", "Notepad") > 0);
    }
}
