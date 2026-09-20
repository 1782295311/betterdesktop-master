namespace BetterDesktop.Shell.Music.Contracts;

/// <summary>SMTC 播放状态枚举，与 MediaCore.dll 的 C++ 约定一致（值不可变，迁移自 shell-status/Native）。</summary>
public enum MediaPlaybackState
{
    Closed = 0,
    Opened = 1,
    Playing = 2,
    Paused = 3,
    Changing = 4,
    Stopped = 5
}
