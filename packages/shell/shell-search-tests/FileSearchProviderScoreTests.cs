using BetterDesktop.Shell.Search.Services;
using Xunit;

namespace BetterDesktop.Shell.Search.Tests;

/// <summary>
/// ScoreFileName 分档测试（2026-09-17）：文件名前缀 > 文件名包含 > 纯路径包含，
/// 保证「按中文目录名搜」的路径命中不压过任何文件名命中。
/// </summary>
public class FileSearchProviderScoreTests
{
    [Theory]
    [InlineData("report", "report-final.txt", "D:/Projects/report-final.txt", 55)] // 文件名前缀
    [InlineData("report", "my-report.pdf", "D:/Projects/my-report.pdf", 30)]       // 文件名包含
    [InlineData("迅雷下载", "完蛋.txt", "D:/迅雷下载/完蛋.txt", 20)]                 // 纯路径包含（中文目录）
    [InlineData("nothing", "完蛋.txt", "D:/迅雷下载/完蛋.txt", 30)]                 // 未命中（WS 全文兜底量纲）
    public void ScoreFileName_Tiers(string query, string name, string path, int expected)
    {
        Assert.Equal(expected, FileSearchProvider.ScoreFileName(name, path, query));
    }

    [Fact]
    public void ScoreFileName_CaseInsensitive()
    {
        // 大小写不敏感：名称与路径均 OrdinalIgnoreCase
        Assert.Equal(55, FileSearchProvider.ScoreFileName("Report.PDF", "C:/x/report.pdf", "Report.PDF"));
        Assert.Equal(20, FileSearchProvider.ScoreFileName("notes.txt", "D:/REPORT/notes.txt", "report"));
    }
}
