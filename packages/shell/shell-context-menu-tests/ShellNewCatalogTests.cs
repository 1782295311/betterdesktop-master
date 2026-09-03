// ShellNew 枚举/创建（计划 §8 ShellNewCatalogTests）：真实 HKCR 只读枚举 + NullFile 创建落地。
// 四分支解析抽 IShellNewSource 的重构未做（本批未含）——这里以真实注册表做行为级验证。

using BetterDesktop.Shell.ContextMenus.Services;
using Xunit;

namespace BetterDesktop.Shell.ContextMenu.Tests;

public class ShellNewCatalogTests : IDisposable
{
    private readonly string _dir;

    public ShellNewCatalogTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "shellnew_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 尽力而为 */ }
    }

    [Fact]
    public void Enumerate_ContainsFolderEntry_AndSorted()
    {
        var entries = ShellNewCatalog.Enumerate();

        Assert.NotEmpty(entries);
        Assert.Contains(entries, e => e.DisplayName == "文件夹");
        // 排序：显示名非降序（实现按显示名排序）
        for (var i = 1; i < entries.Count; i++)
        {
            Assert.True(string.Compare(entries[i - 1].DisplayName, entries[i].DisplayName,
                StringComparison.CurrentCulture) <= 0);
        }
    }

    [Fact]
    public void Create_TxtEntry_CreatesFile_InDirectory()
    {
        // 条件性：本机 HKCR 无 .txt\ShellNew 时跳过（不失败）
        var txt = ShellNewCatalog.Enumerate().FirstOrDefault(e => e.Extension == ".txt");
        if (txt is null)
        {
            return;
        }

        var created = ShellNewCatalog.Create(txt, _dir);

        Assert.NotNull(created);
        Assert.True(File.Exists(created));
        Assert.StartsWith(_dir, created);
    }
}
