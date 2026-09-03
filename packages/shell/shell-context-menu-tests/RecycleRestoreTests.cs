// 回收站 $I 元数据解析（计划 §8 RecycleRestoreTests）：手写字节样本 → 断言原路径解析。
// 格式（实现头注释）：$I 文件 0x14 头 + 520 字节 UTF-16 原路径；隔离红线——解析失败即降级隐藏。

using System.Text;
using BetterDesktop.Shell.ContextMenus.Services;
using Xunit;

namespace BetterDesktop.Shell.ContextMenu.Tests;

public class RecycleRestoreTests : IDisposable
{
    private readonly string _dir;

    public RecycleRestoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "rrestore_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 尽力而为 */ }
    }

    private (string R, string I) MakePair(string originalPath)
    {
        var r = Path.Combine(_dir, "$R000001.txt");
        var i = Path.Combine(_dir, "$I000001.txt");

        var bytes = new byte[0x14 + 520];
        // 头部：版本/长度字段按公开 $I 规范占位（解析只读 0x14 起的路径区，头部内容不参与断言）
        bytes[0] = 2;
        BitConverter.GetBytes(0x14 + 520).CopyTo(bytes, 8);
        Encoding.Unicode.GetBytes(originalPath).CopyTo(bytes, 0x14);
        File.WriteAllBytes(i, bytes);
        File.WriteAllText(r, "body");
        return (r, i);
    }

    [Fact]
    public void ParseOriginalPath_ReadsUnicodePathFromMeta()
    {
        var original = Path.Combine(_dir, "目标报告.txt");
        var (r, i) = MakePair(original);

        Assert.Equal(original, RecycleRestore.ParseOriginalPath(r));
        Assert.True(File.Exists(i));
    }

    [Fact]
    public void MissingMeta_ReturnsNull()
    {
        var r = Path.Combine(_dir, "$R000002.txt");
        File.WriteAllText(r, "body");

        Assert.Null(RecycleRestore.ParseOriginalPath(r));
    }

    [Fact]
    public void NonDollarRPath_ReturnsNull()
    {
        Assert.Null(RecycleRestore.ParseOriginalPath(Path.Combine(_dir, "plain.txt")));
    }
}
