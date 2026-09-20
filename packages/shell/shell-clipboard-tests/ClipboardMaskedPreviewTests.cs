using BetterDesktop.Shell.Clipboard.Contracts;
using Xunit;

namespace BetterDesktop.Shell.Clipboard.Tests;

/// <summary>
/// 【P2-2 敏感信息遮罩 · 2026-09-13】<see cref="ClipboardEntry.BuildMaskedPreview"/> 的边界回归：
/// 遮罩**只影响展示**（不得改变 <see cref="ClipboardEntry.Preview"/> 本身），且不得切断代理对。
/// </summary>
public class ClipboardMaskedPreviewTests
{
    [Fact]
    public void NonSensitive_ReturnsPreviewAsIs()
    {
        var entry = new ClipboardEntry
        {
            ContentType = ClipboardItemKind.Text,
            Content = "13812345678",
            IsSensitive = false,
        };

        Assert.Equal(entry.Preview, entry.BuildMaskedPreview(3, 2));
    }

    [Fact]
    public void Sensitive_MasksMiddleKeepingHeadAndTail()
    {
        var entry = new ClipboardEntry
        {
            ContentType = ClipboardItemKind.Text,
            Content = "13812345678",
            IsSensitive = true,
        };

        var masked = entry.BuildMaskedPreview(3, 2);

        Assert.Equal("138••••78", masked);
    }

    [Fact]
    public void TooShort_NotMasked_ToStayIdentifiable()
    {
        var entry = new ClipboardEntry
        {
            ContentType = ClipboardItemKind.Text,
            Content = "1234567", // 长度 7 <= 3 + 2 + 2
            IsSensitive = true,
        };

        Assert.Equal(entry.Preview, entry.BuildMaskedPreview(3, 2));
    }

    [Fact]
    public void ZeroVisibility_StillMasked()
    {
        var entry = new ClipboardEntry
        {
            ContentType = ClipboardItemKind.Text,
            Content = "abcdefghijklmn",
            IsSensitive = true,
        };

        Assert.Equal("••••", entry.BuildMaskedPreview(0, 0));
    }

    [Fact]
    public void Mask_DoesNotSplitSurrogatePair()
    {
        var entry = new ClipboardEntry
        {
            ContentType = ClipboardItemKind.Text,
            // 前 4 个字符是 emoji（代理对），把 leading 边界正落在代理对中间
            Content = "😀😀😀😀abcdef",
            IsSensitive = true,
        };

        var masked = entry.BuildMaskedPreview(2, 2);

        for (var i = 0; i < masked.Length; i++)
        {
            if (char.IsHighSurrogate(masked[i]))
            {
                Assert.True(i + 1 < masked.Length && char.IsLowSurrogate(masked[i + 1]));
                i++;
            }
            else
            {
                Assert.False(char.IsLowSurrogate(masked[i]));
            }
        }
    }

    /// <summary>遮罩是"派生展示"，不得污染原始内容（用户复制出去的必须是全文）。</summary>
    [Fact]
    public void Masking_DoesNotMutateContent()
    {
        var entry = new ClipboardEntry
        {
            ContentType = ClipboardItemKind.Text,
            Content = "13812345678",
            IsSensitive = true,
        };

        _ = entry.BuildMaskedPreview(3, 2);

        Assert.Equal("13812345678", entry.Content);
    }
}
