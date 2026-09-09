using System.Linq;
using BetterDesktop.Shell.Clipboard.Contracts;
using Xunit;

namespace BetterDesktop.Shell.Clipboard.Tests;

/// <summary>过滤：kind/category/keyword/sourceApp；keyword 命中 Content/Preview/Tags/FilePaths。</summary>
public class ClipboardFilterTests
{
    private static ClipboardManager BuildWithData()
    {
        var manager = new TestClipboardManager(SnapshotFactory.Text("seed"));
        manager.ImportEntries(new[]
        {
            new ClipboardImportItem(ClipboardItemKind.Text, "function add(a, b) {\n    return a + b;\n}", null, null, null, null, "code-editor"),
            new ClipboardImportItem(ClipboardItemKind.Text, "今天天气很好", null, null, null, null, "wechat"),
            new ClipboardImportItem(ClipboardItemKind.Text, "晚上一起吃饭", null, null, null, null, "wechat"),
            new ClipboardImportItem(ClipboardItemKind.Html, "富文本正文", "<p>富文本</p>", "{\\rtf1}", null, null, "chrome"),
            new ClipboardImportItem(ClipboardItemKind.Files, null, null, null, null, new[] { @"D:\docs\报告.pdf" }, "explorer"),
        });
        return manager;
    }

    [Fact]
    public void FilterByKind()
    {
        var manager = BuildWithData();
        var files = manager.GetFilteredEntries(kind: ClipboardItemKind.Files);
        Assert.Single(files);
        Assert.Equal(ClipboardItemKind.Files, files[0].ContentType);

        var texts = manager.GetFilteredEntries(kind: ClipboardItemKind.Text);
        Assert.Equal(3, texts.Count);
    }

    [Fact]
    public void FilterByCategory()
    {
        var manager = BuildWithData();
        var code = manager.GetFilteredEntries(category: ContentCategory.Code);
        Assert.Single(code);
    }

    [Fact]
    public void FilterByKeyword_HitsContent_Preview_Tags_FilePaths()
    {
        var manager = BuildWithData();
        ClipboardEntry fileEntry = manager.GetFilteredEntries(kind: ClipboardItemKind.Files)[0];
        manager.SetEntryTags(fileEntry, "重要 标星");

        Assert.Single(manager.GetFilteredEntries(keyword: "报告"));
        Assert.Single(manager.GetFilteredEntries(keyword: "富文本"));
        Assert.Single(manager.GetFilteredEntries(keyword: "重要"));
        Assert.Empty(manager.GetFilteredEntries(keyword: "不存在的内容xyz"));
    }

    [Fact]
    public void FilterBySourceApp()
    {
        var manager = BuildWithData();
        var wechat = manager.GetFilteredEntries(sourceApp: "wechat");
        Assert.Equal(2, wechat.Count);
    }

    [Fact]
    public void GetSourceApps_OrderedByFrequency()
    {
        var manager = BuildWithData();
        var apps = manager.GetSourceApps();
        Assert.Contains("wechat", apps);
        Assert.Contains("explorer", apps);
        // 最高频（wechat 2 条）在首位。
        Assert.Equal("wechat", apps[0]);
    }

    [Fact]
    public void GetLastCopiedContent_ReturnsLatest()
    {
        var manager = BuildWithData();
        LastCopiedContent? latest = manager.GetLastCopiedContent();
        Assert.NotNull(latest);
        Assert.Equal(ClipboardItemKind.Files, latest!.Type);
    }
}
