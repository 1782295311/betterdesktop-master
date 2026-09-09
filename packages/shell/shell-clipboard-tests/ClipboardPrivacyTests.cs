using Xunit;

namespace BetterDesktop.Shell.Clipboard.Tests;

/// <summary>隐私黑名单：进程名/窗口标题关键词跳过；正常来源记录。</summary>
public class ClipboardPrivacyTests
{
    [Theory]
    [InlineData("1password")]
    [InlineData("Bitwarden")]
    [InlineData("keepassxc.exe")]
    [InlineData("authy")]
    [InlineData("vcred")]
    [InlineData("microsoft.aad.brokerplugin")]
    public void BlacklistedProcessName_IsSensitive(string processName)
    {
        Assert.True(ClipboardManager.IsPrivacySensitive((processName, "任意标题")));
    }

    [Theory]
    [InlineData("密码")]
    [InlineData("password")]
    [InlineData("网银登录")]
    [InlineData("Bank of China")]
    [InlineData("验证码")]
    [InlineData("verification code")]
    [InlineData("1Password")]
    public void BlacklistedTitleKeyword_IsSensitive(string title)
    {
        Assert.True(ClipboardManager.IsPrivacySensitive(("notepad", title)));
    }

    [Fact]
    public void NormalSource_NotSensitive()
    {
        Assert.False(ClipboardManager.IsPrivacySensitive(("notepad", "文档 - 记事本")));
        Assert.False(ClipboardManager.IsPrivacySensitive((string.Empty, string.Empty)));
    }
}
