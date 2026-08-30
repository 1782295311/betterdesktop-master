// MediaCore.dll 的 C# Interop 薄封装（纯转发，无业务逻辑）。
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace BetterDesktop.Shell.Status.Native;

/// <summary>SMTC 播放状态枚举，与 C++ 约定一致。</summary>
public enum MediaPlaybackState
{
    Closed = 0,
    Opened = 1,
    Playing = 2,
    Paused = 3,
    Changing = 4,
    Stopped = 5
}

public readonly record struct MediaSessionNative(
    string Title,
    string Artist,
    string SourceAppId,
    bool CanPlay, bool CanPause, bool CanNext, bool CanPrev,
    MediaPlaybackState State);

/// <summary>MediaCore.dll 的薄封装。仅转发，不做任何业务判断。</summary>
public static class MediaCoreNative
{
    private delegate int MediaGetSessionCount(out int count);

    private delegate int MediaGetSession(
        int index,
        [Out] ushort[] title, int titleCch,
        [Out] ushort[] artist, int artistCch,
        [Out] ushort[] src, int srcCch,
        out int canPlay, out int canPause, out int canNext, out int canPrev,
        out int playbackState);

    private delegate int MediaSendControl(int cmd);

    private const int TitleCch = 256;
    private const int ArtistCch = 256;
    private const int SrcCch = 128;

    private static readonly MediaGetSessionCount? _count;
    private static readonly MediaGetSession? _get;
    private static readonly MediaSendControl? _send;

    static MediaCoreNative()
    {
        _count = NativeLoader.GetExport<MediaGetSessionCount>("MediaCore.dll", "Media_GetSessionCount");
        _get = NativeLoader.GetExport<MediaGetSession>("MediaCore.dll", "Media_GetSession");
        _send = NativeLoader.GetExport<MediaSendControl>("MediaCore.dll", "Media_SendControl");
    }

    public static bool IsAvailable => _count is not null && _get is not null && _send is not null;

    public static IReadOnlyList<MediaSessionNative> GetSessions()
    {
        if (!IsAvailable) return Array.Empty<MediaSessionNative>();
        try
        {
            int hr = _count!(out int c);
            if (hr != 0 || c <= 0) return Array.Empty<MediaSessionNative>();
            var result = new List<MediaSessionNative>(c);
            for (int i = 0; i < c; i++)
            {
                var title = new ushort[TitleCch];
                var artist = new ushort[ArtistCch];
                var src = new ushort[SrcCch];
                hr = _get!(i, title, title.Length, artist, artist.Length, src, src.Length,
                    out int cp, out int cpause, out int cn, out int cprev, out int state);
                if (hr != 0) continue;
                result.Add(new MediaSessionNative(
                    Trim(title), Trim(artist), Trim(src),
                    cp != 0, cpause != 0, cn != 0, cprev != 0,
                    (MediaPlaybackState)state));
            }
            return result;
        }
        catch { return Array.Empty<MediaSessionNative>(); }
    }

    /// <summary>cmd: 0=TogglePlayPause, 1=Play, 2=Pause, 3=Next, 4=Previous。</summary>
    public static int SendControl(int cmd)
    {
        if (_send is null) return -1;
        try { return _send(cmd); }
        catch { return -1; }
    }

    private static string Trim(ushort[] buf)
    {
        var sb = new StringBuilder(buf.Length);
        foreach (var c in buf)
        {
            if (c == '\0') break;
            sb.Append((char)c);
        }
        return sb.ToString();
    }
}
