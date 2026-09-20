using BetterDesktop.Shell.Clipboard.Contracts;
using Xunit;

namespace BetterDesktop.Shell.Clipboard.Tests;

/// <summary>
/// 列表预览（<see cref="ClipboardEntry.Preview"/>）的边界回归：
/// 截断点不得切在 UTF-16 代理对中间（否则 emoji / 扩展汉字显示为乱码方块）。
/// </summary>
public class ClipboardEntryPreviewTests
{
    [Fact]
    public void Preview_DoesNotSplitSurrogatePairAtTruncationBoundary()
    {
        // 500 字截断点正好落在 emoji（代理对）中间
        var entry = new ClipboardEntry
        {
            ContentType = ClipboardItemKind.Text,
            Content = new string('a', 499) + "😀" + "tail",
        };

        var preview = entry.Preview;

        for (var i = 0; i < preview.Length; i++)
        {
            if (char.IsHighSurrogate(preview[i]))
            {
                Assert.True(
                    i + 1 < preview.Length && char.IsLowSurrogate(preview[i + 1]),
                    $"位置 {i} 出现孤立高代理项（截断切开了代理对）"
                );
                i++;
            }
            else
            {
                Assert.False(char.IsLowSurrogate(preview[i]), $"位置 {i} 出现孤立低代理项");
            }
        }
    }

    [Fact]
    public void Preview_TruncatesWithEllipsisBeyondLimit()
    {
        var entry = new ClipboardEntry
        {
            ContentType = ClipboardItemKind.Text,
            Content = new string('x', 1200),
        };

        var preview = entry.Preview;

        Assert.EndsWith("…", preview);
        Assert.True(preview.Length <= 501, $"预览不应超过 500 字 + 省略号，实际 {preview.Length}");
    }
}
