// ZIP 压解（计划 §8 ZipOpsTests）：多文件/目录往返一致 + 目标重名自增不覆盖。

using BetterDesktop.Shell.ContextMenus.Services;
using Xunit;

namespace BetterDesktop.Shell.ContextMenu.Tests;

public class ZipOpsTests : IDisposable
{
    private readonly string _dir;

    public ZipOpsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "zipops_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 尽力而为 */ }
    }

    [Fact]
    public void MultiFile_CompressExtract_Roundtrip()
    {
        var f1 = Path.Combine(_dir, "a.txt");
        var f2 = Path.Combine(_dir, "b.txt");
        File.WriteAllText(f1, "hello");
        File.WriteAllText(f2, "world");
        var zip = Path.Combine(_dir, "multi.zip");

        Assert.True(ZipOps.Compress([f1, f2], zip));
        Assert.True(File.Exists(zip));

        var dest = Path.Combine(_dir, "out");
        Assert.True(ZipOps.Extract(zip, dest));

        Assert.Equal("hello", File.ReadAllText(Path.Combine(dest, "a.txt")));
        Assert.Equal("world", File.ReadAllText(Path.Combine(dest, "b.txt")));
    }

    [Fact]
    public void Directory_CompressExtract_Roundtrip()
    {
        var sub = Path.Combine(_dir, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "nested.txt"), "deep");
        var zip = Path.Combine(_dir, "dir.zip");

        Assert.True(ZipOps.Compress([sub], zip));
        var dest = Path.Combine(_dir, "out2");
        Assert.True(ZipOps.Extract(zip, dest));
        Assert.True(Directory.Exists(Path.Combine(dest, "sub")));
        Assert.Equal("deep", File.ReadAllText(Path.Combine(dest, "sub", "nested.txt")));
    }

    [Fact]
    public void UniquePath_Increments_NoOverwrite()
    {
        var existing = Path.Combine(_dir, "same.zip");
        File.WriteAllText(existing, "occupied");

        var next = ZipOps.UniquePath(existing);

        Assert.NotEqual(existing, next);
        Assert.StartsWith(Path.Combine(_dir, "same"), next);
    }
}
