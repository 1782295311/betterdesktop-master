// BetterDesktop.Kernel.Hmr.Tests — SemanticVersion 契约测试

using BetterDesktop.Kernel.Hmr;
using Xunit;

namespace BetterDesktop.Kernel.Hmr.Tests;

public sealed class SemanticVersionTests
{
    [Fact(DisplayName = "解析完整版本")]
    public void Parse_Full()
    {
        var version = SemanticVersion.Parse("1.2.3");
        Assert.Equal(1, version.Major);
        Assert.Equal(2, version.Minor);
        Assert.Equal(3, version.Patch);
        Assert.Null(version.PreRelease);
    }

    [Fact(DisplayName = "解析缺省段与 v 前缀")]
    public void Parse_DefaultsAndPrefix()
    {
        Assert.Equal(new SemanticVersion(2, 0, 0), SemanticVersion.Parse("2.0"));
        Assert.Equal(new SemanticVersion(1, 0, 0), SemanticVersion.Parse("v1"));
    }

    [Fact(DisplayName = "解析预发布段")]
    public void Parse_PreRelease()
    {
        var version = SemanticVersion.Parse("1.2.3-beta.1");
        Assert.Equal("beta.1", version.PreRelease);
        Assert.Equal("1.2.3-beta.1", version.ToString());
    }

    [Fact(DisplayName = "非法输入解析失败")]
    public void TryParse_Invalid_False()
    {
        Assert.False(SemanticVersion.TryParse("", out _));
        Assert.False(SemanticVersion.TryParse("a.b.c", out _));
        Assert.False(SemanticVersion.TryParse("1.2.3.4", out _));
    }

    [Fact(DisplayName = "版本比较正确")]
    public void Compare_Ordering()
    {
        Assert.True(new SemanticVersion(1, 2, 3) < new SemanticVersion(1, 2, 4));
        Assert.True(new SemanticVersion(2, 0, 0) > new SemanticVersion(1, 9, 9));
        Assert.True(new SemanticVersion(1, 0, 0, "beta") < new SemanticVersion(1, 0, 0));
    }

    [Fact(DisplayName = "ABI 兼容判定：主版本一致且不低于最低版本")]
    public void IsAbiCompatibleWith_Rules()
    {
        var current = new SemanticVersion(1, 3, 0);
        Assert.True(current.IsAbiCompatibleWith(new SemanticVersion(1, 0, 0)));
        Assert.False(current.IsAbiCompatibleWith(new SemanticVersion(1, 5, 0)));
        Assert.False(new SemanticVersion(2, 0, 0).IsAbiCompatibleWith(new SemanticVersion(1, 9, 0)));
    }
}
