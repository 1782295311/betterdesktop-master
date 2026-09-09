// BuildProfileXml 单测（W5）：认证/加密映射、protected 双路径、hex SSID、XML 转义。
// 算法值与本机 SDK wlantypes.h 对齐（WPA3_SAE=9、OWE=10，勿信记忆里的 8）。
using System;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using BetterDesktop.Shell.MenuBar.Services;
using Xunit;

namespace BetterDesktop.Shell.MenuBar.Tests;

public class WifiProfileXmlTests
{
    private const uint AuthOpen = 1;
    private const uint AuthWpaPsk = 4;
    private const uint AuthRsnPsk = 7;
    private const uint AuthWpa3Sae = 9;
    private const uint AuthOwe = 10;
    private const uint CipherNone = 0;
    private const uint CipherCcmp = 4;

    [Theory]
    [InlineData(AuthWpa3Sae, CipherCcmp, "WPA3SAE", "AES", true)]
    [InlineData(AuthOwe, CipherCcmp, "OWE", "AES", false)]
    [InlineData(AuthWpaPsk, CipherNone, "WPAPSK", "TKIP", true)]
    [InlineData(AuthOpen, CipherNone, "open", "none", false)]
    [InlineData(AuthRsnPsk, CipherCcmp, "WPA2PSK", "AES", true)]
    [InlineData(99, CipherCcmp, "WPA2PSK", "AES", true)] // 未知算法 → WPA2PSK 兜底
    public void AuthCipher_Mapping(uint auth, uint cipher, string expectedAuth, string expectedEnc, bool expectKey)
    {
        var doc = XDocument.Parse(WlanInterop.BuildProfileXml("Net", "pwd", auth, cipher, protectedKey: false));
        var ns = doc.Root!.Name.Namespace;
        Assert.Equal(expectedAuth, doc.Descendants(ns + "authentication").Single().Value);
        Assert.Equal(expectedEnc, doc.Descendants(ns + "encryption").Single().Value);
        Assert.Equal(expectKey, doc.Descendants(ns + "sharedKey").Any());
    }

    [Fact]
    public void ProtectedTrue_HasHexKeyMaterial_AndProtectedFlag()
    {
        var doc = XDocument.Parse(WlanInterop.BuildProfileXml("Net", "pwd", AuthWpaPsk, CipherCcmp, protectedKey: true));
        var ns = doc.Root!.Name.Namespace;
        Assert.Equal("true", doc.Descendants(ns + "protected").Single().Value);
        Assert.Matches("^[0-9A-F]+$", doc.Descendants(ns + "keyMaterial").Single().Value);
    }

    [Fact]
    public void ProtectedFalse_EscapesPassword_AndCanRoundTrip()
    {
        var doc = XDocument.Parse(WlanInterop.BuildProfileXml("Net", "p<ss&word>", AuthWpaPsk, CipherCcmp, protectedKey: false));
        var ns = doc.Root!.Name.Namespace;
        Assert.Equal("false", doc.Descendants(ns + "protected").Single().Value);
        Assert.Equal("p<ss&word>", doc.Descendants(ns + "keyMaterial").Single().Value);
    }

    [Fact]
    public void HexSsid_IsUtf8Bytes()
    {
        var doc = XDocument.Parse(WlanInterop.BuildProfileXml("中文", "pwd", AuthOpen, CipherNone, protectedKey: false));
        var ns = doc.Root!.Name.Namespace;
        Assert.Equal(System.Convert.ToHexString(Encoding.UTF8.GetBytes("中文")), doc.Descendants(ns + "hex").Single().Value);
    }

    [Fact]
    public void Ssid_WithXmlSpecialChars_ProducesParseableXml()
    {
        var doc = XDocument.Parse(WlanInterop.BuildProfileXml("a<b&c>d", "pwd", AuthOpen, CipherNone, protectedKey: false));
        var ns = doc.Root!.Name.Namespace;
        Assert.Equal("a<b&c>d", doc.Descendants(ns + "name").First().Value);
    }
}
