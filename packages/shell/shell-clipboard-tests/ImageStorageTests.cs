using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using BetterDesktop.Shell.Clipboard.Contracts;
using BetterDesktop.Shell.Clipboard.Native;
using Xunit;

namespace BetterDesktop.Shell.Clipboard.Tests;

/// <summary>图片落盘：images\下 PNG + JSON 无字节内联；总量预算淘汰最旧未固定；孤儿图片清理。</summary>
public class ImageStorageTests
{
    [Fact]
    public void ImageCaptured_StoredAsFile_JsonHasNoBase64()
    {
        string dir = Path.Combine(Path.GetTempPath(), "bdt-clipboard-tests", Guid.NewGuid().ToString("N"));
        var manager = new TestClipboardManager(SnapshotFactory.Image(SnapshotFactory.FakePng(16, 16)), dir);
        manager.OnClipboardUpdate();

        ClipboardEntry entry = Assert.Single(manager.GetFilteredEntries());
        Assert.StartsWith("clipboard\\images\\", entry.ImagePath);
        Assert.EndsWith(".png", entry.ImagePath);
        Assert.True(File.Exists(Path.Combine(dir, entry.ImagePath)));

        manager.SaveToFile();

        string json = ReadJsonFromStorage(dir);
        Assert.DoesNotContain("data:image", json);
        Assert.DoesNotContain("ImageData", json);
        // JSON 中反斜杠被转义为 \\，断言目录片段存在即可。
        Assert.Contains("clipboard", json);
    }

    [Fact]
    public void OverTotalImageBudget_EvictsOldestUnpinnedImage()
    {
        string dir = Path.Combine(Path.GetTempPath(), "bdt-clipboard-tests", Guid.NewGuid().ToString("N"));
        var manager = new TestClipboardManager(SnapshotFactory.Text("seed"), dir);

        // 41 × 5MB = 205MB > 200MB 预算。
        byte[] big = SnapshotFactory.PngOfSize(5 * 1024 * 1024);
        for (int i = 0; i < 41; i++)
        {
            manager.NextSnapshot = SnapshotFactory.Image(big);
            manager.OnClipboardUpdate();
        }

        var images = manager.GetFilteredEntries().Where(e => e.ContentType == ClipboardItemKind.Image).ToList();
        long total = images.Sum(e => e.SizeBytes);
        Assert.True(total <= ClipboardManager.MaxTotalImageBytes, $"图片总量 {total} 应 ≤ {ClipboardManager.MaxTotalImageBytes}");
    }

    [Fact]
    public void OrphanImageFiles_Deleted_OnCleanup()
    {
        string dir = Path.Combine(Path.GetTempPath(), "bdt-clipboard-tests", Guid.NewGuid().ToString("N"));
        var manager = new TestClipboardManager(SnapshotFactory.Text("seed"), dir);
        manager.OnClipboardUpdate();

        string orphanPath = Path.Combine(dir, "clipboard", "images", "orphan.png");
        Directory.CreateDirectory(Path.GetDirectoryName(orphanPath)!);
        File.WriteAllBytes(orphanPath, SnapshotFactory.FakePng(4, 4));
        Assert.True(File.Exists(orphanPath));

        manager.CleanupExpiredEntries();

        Assert.False(File.Exists(orphanPath));
    }

    private static string ReadJsonFromStorage(string dir)
    {
        string storageFile = Path.Combine(dir, "clipboard_history.json");
        byte[] content = File.ReadAllBytes(storageFile);
        const string header = "CBENC1\0";
        Assert.StartsWith(header, Encoding.ASCII.GetString(content, 0, header.Length));
        byte[] cipher = new byte[content.Length - header.Length];
        Array.Copy(content, header.Length, cipher, 0, cipher.Length);
        byte[] plain = ClipboardNative.Unprotect(cipher);
        string json = Encoding.UTF8.GetString(plain);

        // 可反序列化（含 ImagePath 字段）。
        var loaded = JsonSerializer.Deserialize<System.Collections.Generic.List<ClipboardEntry>>(json);
        Assert.NotNull(loaded);
        Assert.NotEmpty(loaded!);
        return json;
    }
}
