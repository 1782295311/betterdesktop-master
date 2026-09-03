// FileClassifier 增补（计划 §8 A4）：回收站 Restore 位 + 多选交集语义。

using BetterDesktop.Shell.ContextMenus.Contracts;
using BetterDesktop.Shell.ContextMenus.Services;
using Xunit;

namespace BetterDesktop.Shell.ContextMenu.Tests;

public class FileClassifierCapsTests : IDisposable
{
    private readonly string _dir;

    public FileClassifierCapsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fcccaps_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 尽力而为 */ }
    }

    private readonly FileClassifier _classifier = new();

    [Fact]
    public void RecycleBinPath_HasRestoreCap()
    {
        var identity = _classifier.Classify("C:\\$Recycle.Bin\\S-1-5-21-000\\$R000001.txt");

        Assert.Equal(FileKind.InRecycleBin, identity.Kind);
        Assert.True(identity.Caps.HasFlag(FileCapabilities.Restore), "回收站分支必须补给 Restore 位（A4）");
        Assert.True(identity.Caps.HasFlag(FileCapabilities.Browse));
    }

    [Fact]
    public void ClassifyMany_Intersection_OfSingles()
    {
        var png = Path.Combine(_dir, "a.png");
        var zip = Path.Combine(_dir, "b.zip");
        File.WriteAllBytes(png, [0x89, 0x50, 0x4E, 0x47]); // PNG 魔数
        File.WriteAllBytes(zip, [0x50, 0x4B, 0x03, 0x04]); // PK 魔数

        var singlePng = _classifier.Classify(png);
        var singleZip = _classifier.Classify(zip);
        var many = _classifier.ClassifyMany([png, zip]);

        // 交集契约：多选能力 = 单选能力按位与（单文件专属项自动隐藏）
        Assert.Equal(singlePng.Caps & singleZip.Caps, many.Caps);
        // 专属位确实存在于单选（保证本测试有意义）
        Assert.True(singlePng.Caps.HasFlag(FileCapabilities.SetAsWallpaper), "png 单选应有 SetAsWallpaper");
        Assert.True(singleZip.Caps.HasFlag(FileCapabilities.Extract), "zip 单选应有 Extract");
        // 交集后互斥位消失
        Assert.False(many.Caps.HasFlag(FileCapabilities.SetAsWallpaper));
        Assert.False(many.Caps.HasFlag(FileCapabilities.Extract));
    }
}
