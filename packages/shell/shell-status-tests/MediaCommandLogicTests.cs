// 媒体命令状态机纯函数测试（内核逻辑）。
// ToggleRepeat 的三态推进（None→List→Track→None）为 C# 侧纯函数，可单测；
// 原生侧（MediaCore.dll）只做"按入参设置模式"的原始命令，无业务状态机。
using BetterDesktop.Shell.Status.Native;
using Xunit;

namespace BetterDesktop.Shell.Status.Tests;

public class MediaCommandLogicTests
{
    [Theory]
    [InlineData(0, 2)] // None → List
    [InlineData(2, 1)] // List → Track
    [InlineData(1, 0)] // Track → None
    public void NextRepeatMode_CyclesThreeStates(int current, int expected)
    {
        Assert.Equal(expected, MediaPlayerCore.NextRepeatMode(current));
    }

    [Fact]
    public void NextRepeatMode_UnknownMode_FallsBackToList()
    {
        // 未知值（如 -1 / 99）按 None 兜底 → 下一状态 List（2）。
        Assert.Equal(2, MediaPlayerCore.NextRepeatMode(99));
    }
}
