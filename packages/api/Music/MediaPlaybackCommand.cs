namespace BetterDesktop.Shell.Music.Contracts;

/// <summary>媒体控制命令（与面板按钮一一对应）。Seek 需配合 <see cref="IMediaPlaybackService.SendCommandAsync"/> 的 position 参数。</summary>
public enum MediaPlaybackCommand
{
    Toggle = 0,
    Play = 1,
    Pause = 2,
    Next = 3,
    Previous = 4,
    Seek = 5,
    ToggleShuffle = 6,
    ToggleRepeat = 7
}
