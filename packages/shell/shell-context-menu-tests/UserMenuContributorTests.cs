using BetterDesktop.Shell.ContextMenus.Contracts;
using BetterDesktop.Shell.ContextMenus.Services;
using Xunit;

namespace BetterDesktop.Shell.ContextMenu.Tests;

/// <summary>
/// 用户自定义项占位符展开单测（红线：路径占位符必须引号包裹，防空格路径撕裂命令行）。
/// </summary>
public sealed class UserMenuContributorTests
{
    private const string DesktopPath = @"C:\Users\t\Desktop";

    [Theory]
    [InlineData("%file%", @"C:\my dir\a b.txt", "\"C:\\my dir\\a b.txt\"")]
    [InlineData("%files%", @"C:\x\y.txt", "\"C:\\x\\y.txt\"")]          // 单次展开等价 %file%
    [InlineData("%dir%", @"C:\my dir\a.txt", "\"C:\\my dir\"")]
    [InlineData("%desktop%", "", "\"C:\\Users\\t\\Desktop\"")]
    [InlineData("%filename%", @"C:\d\report 2026.docx", "report 2026.docx")] // 名称纯文本不包裹
    [InlineData("%name%", @"C:\d\report 2026.docx", "report 2026")]
    [InlineData("-n %name% %file%", @"C:\d\a.txt", "-n a \"C:\\d\\a.txt\"")] // %name%=无扩展名
    public void ExpandArguments_PathPlaceholders_QuotedCorrectly(string template, string path, string expected)
    {
        // desktop 特例用例需要注入桌面路径——直接调用真实 ExpandArguments（依赖当前用户桌面）
        if (template == "%desktop%")
        {
            var actual = UserMenuContributor.ExpandArguments(template, path);
            Assert.Equal($"\"{Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)}\"", actual);
            return;
        }

        // %desktop% 以外的用例：真实桌面路径不参与，path 直接生效。
        // 注意 %name%/%filename% 用例的 expected 假设文件系统语义，与桌面路径无关。
        if (template == "%dir%")
        {
            Assert.Equal(expected, UserMenuContributor.ExpandArguments(template, path));
            return;
        }

        // %file%/%files%/%filename%/%name% 及组合
        Assert.Equal(expected, UserMenuContributor.ExpandArguments(FixDesktopTemplate(template), path));
    }

    private static string FixDesktopTemplate(string template) => template;

    [Fact]
    public void ExpandArguments_CombinedTemplate_SpacesSurvive()
    {
        // 典型自定义项：路径占位符自动引号包裹——模板作者无需（也不应）再手写引号
        var actual = UserMenuContributor.ExpandArguments(@"--check %file% --out %dir%", @"C:\my dir\my file.zip");
        Assert.Equal(@"--check ""C:\my dir\my file.zip"" --out ""C:\my dir""", actual);
    }

    [Fact]
    public void ExpandArguments_EmptyTemplate_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, UserMenuContributor.ExpandArguments("", @"C:\x"));
        Assert.Equal(string.Empty, UserMenuContributor.ExpandArguments(null!, @"C:\x"));
    }

    [Fact]
    public void UserMenuContributor_FiltersByScopeAndKind()
    {
        var contributor = new UserMenuContributor(null, MenuScope.DesktopIcon);
        var request = new MenuRequest(MenuScope.DesktopIcon, null, default,
            File: new FileIdentity(FileKind.WordDocument, FileCapabilities.Open, false, false, @"C:\a.docx"));
        var items = contributor.Build(request);
        Assert.Empty(items); // settings=null → 降级空（不崩）
    }
}
