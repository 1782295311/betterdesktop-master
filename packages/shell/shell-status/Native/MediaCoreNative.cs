// MediaCore.dll 的 C# Interop 薄封装（纯转发，无业务逻辑）。
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using BetterDesktop.Shell.Music.Contracts;

namespace BetterDesktop.Shell.Status.Native;

/// <summary>原生会话快照（MediaCore.dll 直出，字段与 C ABI 一一对应）。</summary>
public readonly record struct MediaSessionNative(
    string Title,
    string Artist,
    string SourceAppId,
    bool CanPlay, bool CanPause, bool CanNext, bool CanPrev, bool CanSeek,
    bool IsShuffle, bool IsRepeat, int RepeatMode, bool HasThumbnail,
    MediaPlaybackState State,
    TimeSpan? Position, TimeSpan? StartTime, TimeSpan? EndTime,
    long SessionId);

/// <summary>MediaCore.dll 的薄封装。仅转发，不做任何业务判断。</summary>
public static class MediaCoreNative
{
    private delegate int MediaGetSessionCount(out int count);

    private delegate int MediaGetSession(
        int index,
        [Out] ushort[] title, int titleCch,
        [Out] ushort[] artist, int artistCch,
        [Out] ushort[] src, int srcCch,
        out int canPlay, out int canPause, out int canNext, out int canPrev, out int canSeek,
        out int isShuffle, out int isRepeat, out int repeatMode, out int hasThumbnail,
        out int playbackState,
        out long positionTicks, out long startTicks, out long endTicks,
        out long sessionId);

    private delegate int MediaGetSessionThumbnail(long sessionId, out IntPtr data, out uint size);

    private delegate void MediaFreeBuffer(IntPtr data);

    private delegate int MediaSendControl(long sessionId, int cmd, long positionTicks, int shuffleActive, int repeatMode);

    private const int TitleCch = 256;
    private const int ArtistCch = 256;
    private const int SrcCch = 128;

    private static readonly MediaGetSessionCount? _count;
    private static readonly MediaGetSession? _get;
    private static readonly MediaGetSessionThumbnail? _thumbnail;
    private static readonly MediaFreeBuffer? _freeBuffer;
    private static readonly MediaSendControl? _send;

    static MediaCoreNative()
    {
        _count = NativeLoader.GetExport<MediaGetSessionCount>("MediaCore.dll", "Media_GetSessionCount");
        _get = NativeLoader.GetExport<MediaGetSession>("MediaCore.dll", "Media_GetSession");
        _thumbnail = NativeLoader.GetExport<MediaGetSessionThumbnail>("MediaCore.dll", "Media_GetSessionThumbnail");
        _freeBuffer = NativeLoader.GetExport<MediaFreeBuffer>("MediaCore.dll", "Media_FreeBuffer");
        _send = NativeLoader.GetExport<MediaSendControl>("MediaCore.dll", "Media_SendControl");
    }

    public static bool IsAvailable => _count is not null && _get is not null && _thumbnail is not null && _freeBuffer is not null && _send is not null;

    public static IReadOnlyList<MediaSessionNative> GetSessions()
    {
        if (!IsAvailable) return Array.Empty<MediaSessionNative>();
        try
        {
            // 原生 SMTC 调用含 WinRT 异步阻塞（RequestAsync / TryGetMediaProperties / OpenReadAsync 等）。
            // 若调用线程为 STA（测试宿主/UI 线程），阻塞等待会死锁——完成回调需回送 STA 消息泵，
            // 而 .get() 不泵消息。统一切到线程池 MTA 线程执行（线程池线程恒为 MTA）。
            // 生产路径（MediaPlayerCore 已 Task.Run）为双保险，成本可忽略。
            return Task.Run(GetSessionsCore).GetAwaiter().GetResult();
        }
        catch { return Array.Empty<MediaSessionNative>(); }
    }

    private static IReadOnlyList<MediaSessionNative> GetSessionsCore()
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
                out int cp, out int cpause, out int cn, out int cprev, out int cseek,
                out int shuffle, out int repeat, out int repeatMode, out int hasThumbnail,
                out int state,
                out long posTicks, out long startTicks, out long endTicks, out long sessionId);
            if (hr != 0) continue;
            result.Add(new MediaSessionNative(
                Trim(title), Trim(artist), Trim(src),
                cp != 0, cpause != 0, cn != 0, cprev != 0, cseek != 0,
                shuffle != 0, repeat != 0, repeatMode, hasThumbnail != 0,
                (MediaPlaybackState)state,
                TicksToTimespan(posTicks), TicksToTimespan(startTicks), TicksToTimespan(endTicks),
                sessionId));
        }
        return result;
    }

    /// <summary>读取会话缩略图字节（PNG/JPEG）；无缩略图或失败返回 null。</summary>
    public static byte[]? GetThumbnailBytes(long sessionId)
    {
        if (_thumbnail is null || _freeBuffer is null) return null;
        try
        {
            // 同 GetSessions：原生 OpenReadAsync().get() 在 STA 线程会死锁，统一切 MTA。
            return Task.Run(() => GetThumbnailBytesCore(sessionId)).GetAwaiter().GetResult();
        }
        catch { return null; }
    }

    private static byte[]? GetThumbnailBytesCore(long sessionId)
    {
        int hr = _thumbnail!(sessionId, out IntPtr data, out uint size);
        if (hr != 0 || data == IntPtr.Zero || size == 0) return null;
        try
        {
            var bytes = new byte[size];
            Marshal.Copy(data, bytes, 0, (int)size);
            return bytes;
        }
        finally
        {
            _freeBuffer!(data);
        }
    }

    /// <summary>向会话下发控制命令。cmd：0=Play, 1=Pause, 2=Next, 3=Previous, 4=Seek, 5=Toggle, 6=Shuffle, 7=Repeat。
    /// 返回 true=命令已下发（不保证 App 执行）；会话已消失/失败返回 false。</summary>
    public static bool SendControl(long sessionId, int cmd, long positionTicks = 0, bool shuffleActive = false, int repeatMode = 0)
    {
        if (_send is null) return false;
        try
        {
            // 同 GetSessions：TryXxxAsync().get() 在 STA 线程会死锁，统一切 MTA。
            return Task.Run(() => _send(sessionId, cmd, positionTicks, shuffleActive ? 1 : 0, repeatMode) == 0).GetAwaiter().GetResult();
        }
        catch { return false; }
    }

    private static TimeSpan? TicksToTimespan(long ticks) => ticks <= 0 ? null : TimeSpan.FromTicks(ticks);

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
