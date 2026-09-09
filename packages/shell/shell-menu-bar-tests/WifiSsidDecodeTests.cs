// DecodeSsid 单测：SSID 是任意字节串——UTF-8 严格解码 + Latin-1 兜底 + 尾 NUL 清理
// （wlanapi-wifi-status.md 红线 2：禁止产生 U+FFFD 乱码替换符）。
using BetterDesktop.Shell.MenuBar.Services;
using Xunit;

namespace BetterDesktop.Shell.MenuBar.Tests;

public class WifiSsidDecodeTests
{
    [Fact]
    public void Utf8_Chinese_DecodesCorrectly()
    {
        Assert.Equal("中文测试", WlanInterop.DecodeSsid("中文测试"u8.ToArray()));
    }

    [Fact]
    public void Latin1_Fallback_NoReplacementChar()
    {
        // 0xC4 0xE3 0xBA 0xC3 = GBK "你好"——非法 UTF-8 序列，必须走 Latin-1 而非 U+FFFD
        var ssid = WlanInterop.DecodeSsid(new byte[] { 0xC4, 0xE3, 0xBA, 0xC3 });
        Assert.DoesNotContain('\uFFFD', ssid);
        Assert.Equal("\u00C4\u00E3\u00BA\u00C3", ssid);
    }

    [Fact]
    public void TrailingNul_Trimmed()
    {
        Assert.Equal("ab", WlanInterop.DecodeSsid(new byte[] { (byte)'a', (byte)'b', 0, 0 }));
    }

    [Fact]
    public void Empty_OrAllNul_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, WlanInterop.DecodeSsid(ReadOnlySpan<byte>.Empty));
        Assert.Equal(string.Empty, WlanInterop.DecodeSsid(new byte[] { 0, 0, 0 }));
    }

    [Fact]
    public void Mixed_ValidUtf8Prefix_Truncated_StillLatin1()
    {
        // UTF-8 多字节序列被截断 → 非法 → Latin-1 逐字节映射
        var ssid = WlanInterop.DecodeSsid(new byte[] { 0xE4, 0xB8 }); // "中" 的前两个字节
        Assert.DoesNotContain('\uFFFD', ssid);
        Assert.Equal(2, ssid.Length);
    }
}
