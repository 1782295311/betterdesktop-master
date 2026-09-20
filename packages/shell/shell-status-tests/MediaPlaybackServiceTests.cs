// BetterDesktop.Shell.Status.Tests —— 媒体播放公共契约（IMediaPlaybackService）纯逻辑测试
// 覆盖：PickActive（活动会话选择）、MediaPlaybackChangeDetector（变更事件签名，Position 不触发）。
// WinRT 枚举/句柄层不可 fake，本组只测可单测的纯函数与 DTO 契约。
using BetterDesktop.Shell.Music.Contracts;
using BetterDesktop.Shell.Status.Native;
using BetterDesktop.Shell.Status.Services;
using Xunit;

namespace BetterDesktop.Shell.Status.Tests;

public class MediaPlaybackServiceTests
{
    private static MediaSessionSnapshot Snapshot(string title, MediaPlaybackState state, bool canPlay = true)
        => new()
        {
            Title = title,
            Artist = "artist",
            AppName = "app",
            State = state,
            CanPlay = canPlay,
            CanPause = false,
            CanNext = false,
            CanPrev = false
        };

    private static MediaPlaybackSnapshot Public(string title, MediaPlaybackState state, TimeSpan? position = null)
        => new()
        {
            Title = title,
            Artist = "artist",
            AppName = "app",
            State = state,
            CanPlay = true,
            CanPause = false,
            CanNext = false,
            CanPrev = false,
            Position = position
        };

    // ── PickActive：活动会话选择 ──

    [Fact]
    public void PickActive_PrefersPlayingSession()
    {
        var list = new[]
        {
            Snapshot("paused", MediaPlaybackState.Paused),
            Snapshot("playing", MediaPlaybackState.Playing),
            Snapshot("closed", MediaPlaybackState.Closed)
        };
        Assert.Equal("playing", MediaPlayerCore.PickActive(list)?.Title);
    }

    [Fact]
    public void PickActive_FallsBackToFirstNonPlaying()
    {
        var list = new[]
        {
            Snapshot("paused", MediaPlaybackState.Paused),
            Snapshot("stopped", MediaPlaybackState.Stopped)
        };
        Assert.Equal("paused", MediaPlayerCore.PickActive(list)?.Title);
    }

    [Fact]
    public void PickActive_Empty_ReturnsNull()
    {
        Assert.Null(MediaPlayerCore.PickActive(Array.Empty<MediaSessionSnapshot>()));
    }

    // ── 变更签名：Position 不触发，歌/状态变化触发 ──

    [Fact]
    public void ChangeSignature_IgnoresPosition()
    {
        var a = Public("song", MediaPlaybackState.Playing, position: TimeSpan.FromSeconds(10));
        var b = Public("song", MediaPlaybackState.Playing, position: TimeSpan.FromSeconds(120));
        Assert.Equal(MediaPlaybackChangeDetector.GetSignature(a), MediaPlaybackChangeDetector.GetSignature(b));
    }

    [Fact]
    public void ChangeSignature_DiffersOnTitleSwitch()
    {
        var a = Public("song-a", MediaPlaybackState.Playing);
        var b = Public("song-b", MediaPlaybackState.Playing);
        Assert.NotEqual(MediaPlaybackChangeDetector.GetSignature(a), MediaPlaybackChangeDetector.GetSignature(b));
    }

    [Fact]
    public void ChangeSignature_DiffersOnStateChange()
    {
        var a = Public("song", MediaPlaybackState.Playing);
        var b = Public("song", MediaPlaybackState.Paused);
        Assert.NotEqual(MediaPlaybackChangeDetector.GetSignature(a), MediaPlaybackChangeDetector.GetSignature(b));
    }

    [Fact]
    public void ChangeSignature_EmptyList_IsEmptyString()
    {
        Assert.Equal(string.Empty, MediaPlaybackChangeDetector.GetSignature(Array.Empty<MediaPlaybackSnapshot>()));
    }

    [Fact]
    public void ChangeSignature_MultiSession_OrderSensitive()
    {
        var a = new[] { Public("x", MediaPlaybackState.Playing), Public("y", MediaPlaybackState.Paused) };
        var b = new[] { Public("y", MediaPlaybackState.Paused), Public("x", MediaPlaybackState.Playing) };
        Assert.NotEqual(MediaPlaybackChangeDetector.GetSignature(a), MediaPlaybackChangeDetector.GetSignature(b));
    }
}
