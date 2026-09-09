// FormatSpeed 边界单测（G1：旧实现 ≥1Gbps 分支与 ≥1Mbps 分支逐字相同=死分支，合并后回归保护）。
using BetterDesktop.Shell.MenuBar.Windows;
using Xunit;

namespace BetterDesktop.Shell.MenuBar.Tests;

public class WifiFormatSpeedTests
{
    [Theory]
    [InlineData(0, "— Mbps")]
    [InlineData(-5, "— Mbps")]
    [InlineData(999, "999 bps")]
    [InlineData(1000, "1 Kbps")]
    [InlineData(999_999, "1000 Kbps")]
    [InlineData(1_000_000, "1 Mbps")]
    [InlineData(5_800_000, "6 Mbps")]
    [InlineData(1_000_000_000, "1000 Mbps")] // Gbps 档并入 Mbps 显示（deferred 单独拆档）
    public void FormatSpeed_Boundaries(long input, string expected)
        => Assert.Equal(expected, WifiPopupWindow.FormatSpeed(input));
}
