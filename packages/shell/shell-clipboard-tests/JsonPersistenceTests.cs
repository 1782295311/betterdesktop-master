using System;
using System.IO;
using System.Linq;
using System.Text;
using BetterDesktop.Shell.Clipboard.Contracts;
using BetterDesktop.Shell.Clipboard.Native;
using Xunit;

namespace BetterDesktop.Shell.Clipboard.Tests;

/// <summary>持久化：往返序列化（含 ImagePath）、CBENC1 头、损坏数据空历史不抛、图片字节不入 JSON。</summary>
public class JsonPersistenceTests
{
    private static string NewDir() => Path.Combine(Path.GetTempPath(), "bdt-clipboard-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void SaveThenLoad_RoundTrips_EntriesWithImagePath()
    {
        string dir = NewDir();
        var writer = new TestClipboardManager(SnapshotFactory.Image(SnapshotFactory.FakePng(20, 30)), dir);
        writer.OnClipboardUpdate();
        writer.NextSnapshot = SnapshotFactory.Text("hello persistence");
        writer.OnClipboardUpdate();
        writer.SaveToFile();

        var reader = new TestClipboardManager(SnapshotFactory.Empty(), dir);
        reader.LoadFromFile();

        var entries = reader.GetFilteredEntries();
        Assert.Equal(2, entries.Count);

        ClipboardEntry image = entries.First(e => e.ContentType == ClipboardItemKind.Image);
        Assert.False(string.IsNullOrEmpty(image.ImagePath));
        Assert.Equal(20, image.ImageWidth);
        Assert.Equal(30, image.ImageHeight);

        ClipboardEntry text = entries.First(e => e.ContentType == ClipboardItemKind.Text);
        Assert.Equal("hello persistence", text.Content);
    }

    [Fact]
    public void StorageFile_HasCbenc1Header_AndNoImageBytes()
    {
        string dir = NewDir();
        var writer = new TestClipboardManager(SnapshotFactory.Image(SnapshotFactory.PngOfSize(4096)), dir);
        writer.OnClipboardUpdate();
        writer.SaveToFile();

        string storageFile = Path.Combine(dir, "clipboard_history.json");
        byte[] content = File.ReadAllBytes(storageFile);
        const string header = "CBENC1\0";
        Assert.True(content.Length >= header.Length);
        Assert.Equal(header, Encoding.ASCII.GetString(content, 0, header.Length));

        byte[] cipher = new byte[content.Length - header.Length];
        Array.Copy(content, header.Length, cipher, 0, cipher.Length);
        byte[] plain = ClipboardNative.Unprotect(cipher);
        string json = Encoding.UTF8.GetString(plain);

        // 密文+头远小于原图（4KB 图 + 元数据），图片字节未内联。
        Assert.True(content.Length < 4096, $"存储体积 {content.Length} 应小于原图字节 {4096}");
        Assert.DoesNotContain("data:image", json);
    }

    [Fact]
    public void CorruptedFile_LoadsEmptyHistory_NoThrow()
    {
        string dir = NewDir();
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "clipboard_history.json"), new byte[] { 0x00, 0x01, 0x02, 0x03 });

        var manager = new TestClipboardManager(SnapshotFactory.Empty(), dir);
        manager.LoadFromFile();

        Assert.Empty(manager.GetFilteredEntries());
    }

    [Fact]
    public void PlainJsonLegacy_LoadsWithoutHeader()
    {
        string dir = NewDir();
        Directory.CreateDirectory(dir);
        // 旧明文格式（无 CBENC1 头）回退读取。
        File.WriteAllText(Path.Combine(dir, "clipboard_history.json"),
            "[{\"Id\":\"legacy1\",\"ContentType\":0,\"Content\":\"legacy text\",\"Timestamp\":\"2026-01-01T00:00:00\"}]");

        var manager = new TestClipboardManager(SnapshotFactory.Empty(), dir);
        manager.LoadFromFile();

        var entries = manager.GetFilteredEntries();
        Assert.Single(entries);
        Assert.Equal("legacy text", entries[0].Content);
    }
}
