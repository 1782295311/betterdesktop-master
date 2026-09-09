// 回收站 $I 元数据解析（计划 §8 RecycleRestoreTests）：手写字节样本 → 断言原路径解析。
// 格式（2026-09-04 审查 S1 修正后）：v2 = 24 字节头（version+size+time）+ 0x18 起 520 字节
// UTF-16 原路径，总长 544；v1（280B）拒收。隔离红线——解析失败即降级隐藏。

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

        var bytes = new byte[0x18 + 520];
        // v2 头：version=2（低字节）、原大小字段占位（解析只读 0x18 起的路径区，头部其余不参与断言）
        bytes[0] = 2;
        BitConverter.GetBytes(0x18 + 520).CopyTo(bytes, 8);
        Encoding.Unicode.GetBytes(originalPath).CopyTo(bytes, 0x18);
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
    public void V1Meta_Rejected()
    {
        // v1（280B）：暂不支持 → null（还原项隐藏，不弹坏路径）
        var r = Path.Combine(_dir, "$R000003.txt");
        var i = Path.Combine(_dir, "$I000003.txt");
        File.WriteAllBytes(i, new byte[280]);
        File.WriteAllText(r, "body");

        Assert.Null(RecycleRestore.ParseOriginalPath(r));
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
