// AudioCore.dll 的 C# Interop 薄封装（纯转发，无业务逻辑）。
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace BetterDesktop.Shell.Status.Native;

public enum AudioFlow
{
    Render = 0,
    Capture = 1
}

public readonly record struct AudioEndpointStatusNative(float VolumeFloat, bool Muted, bool Ok);

public readonly record struct AudioDeviceNative(string Id, string Name, bool IsDefault);

public readonly record struct AudioSessionNative(string Name, float VolumeFloat, bool IsMuted, int ProcessId);

/// <summary>AudioCore.dll 的薄封装。仅转发，不做任何业务判断。</summary>
public static class AudioCoreNative
{
    private delegate int AudioGetEndpointStatus(int flow, out float volume, out int muted, out int ok);
    private delegate int AudioSetMasterVolume(float volume);
    private delegate int AudioSetMute(int muted);
    private delegate int AudioSetCaptureVolume(float volume, int muted);

    private delegate int AudioEnumerateDevices(
        int flow,
        int maxDevices,
        [Out] ushort[] ids, int idStrideChars,
        [Out] ushort[] names, int nameStrideChars,
        out int defaultConsoleIndex,
        out int filled);

    private delegate int AudioSetDefaultDevice(int flow, [MarshalAs(UnmanagedType.LPWStr)] string deviceId);

    private delegate int AudioEnumerateSessions(
        int maxSessions,
        [Out] ushort[] names, int nameStrideChars,
        [Out] float[] volumes, [Out] int[] muted, [Out] int[] pids,
        out int filled);

    private delegate int AudioSetSessionVolume(int pid, int muted, float volume);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void AudioChangeCallback();
    private delegate int AudioInitialize();
    private delegate void AudioShutdownFn();
    private delegate void AudioSetChangeCallback(AudioChangeCallback cb);

    private const int Stride = 260;
    private const int SessionNameChars = 256;

    private static readonly AudioGetEndpointStatus? _getStatus;
    private static readonly AudioSetMasterVolume? _setMaster;
    private static readonly AudioSetMute? _setMute;
    private static readonly AudioSetCaptureVolume? _setCapture;
    private static readonly AudioEnumerateDevices? _enum;
    private static readonly AudioSetDefaultDevice? _setDefault;
    private static readonly AudioEnumerateSessions? _enumSessions;
    private static readonly AudioSetSessionVolume? _setSession;
    private static readonly AudioInitialize? _init;
    private static readonly AudioShutdownFn? _shutdown;
    private static readonly AudioSetChangeCallback? _setChangeCb;

    // 原生回调通过函数指针调用此委托；必须持有引用，防止被 GC 回收。
    private static AudioChangeCallback? _changeCallback;

    static AudioCoreNative()
    {
        _getStatus = NativeLoader.GetExport<AudioGetEndpointStatus>("AudioCore.dll", "Audio_GetEndpointStatus");
        _setMaster = NativeLoader.GetExport<AudioSetMasterVolume>("AudioCore.dll", "Audio_SetMasterVolume");
        _setMute = NativeLoader.GetExport<AudioSetMute>("AudioCore.dll", "Audio_SetMute");
        _setCapture = NativeLoader.GetExport<AudioSetCaptureVolume>("AudioCore.dll", "Audio_SetCaptureVolume");
        _enum = NativeLoader.GetExport<AudioEnumerateDevices>("AudioCore.dll", "Audio_EnumerateDevices");
        _setDefault = NativeLoader.GetExport<AudioSetDefaultDevice>("AudioCore.dll", "Audio_SetDefaultDevice");
        _enumSessions = NativeLoader.GetExport<AudioEnumerateSessions>("AudioCore.dll", "Audio_EnumerateSessions");
        _setSession = NativeLoader.GetExport<AudioSetSessionVolume>("AudioCore.dll", "Audio_SetSessionVolume");
        _init = NativeLoader.GetExport<AudioInitialize>("AudioCore.dll", "Audio_Initialize");
        _shutdown = NativeLoader.GetExport<AudioShutdownFn>("AudioCore.dll", "Audio_Shutdown");
        _setChangeCb = NativeLoader.GetExport<AudioSetChangeCallback>("AudioCore.dll", "Audio_SetChangeCallback");
    }

    /// <summary>音频原生模块是否可用。</summary>
    public static bool IsAvailable => _getStatus is not null;

    /// <summary>是否已启用事件监听（G2/P1-A：模块级 COM + 事件回调）。</summary>
    public static bool IsEventDriven => _init is not null && _init() == 0;

    /// <summary>启动音频事件监听。启动失败不抛异常，调用方应继续依赖兜底轮询。</summary>
    public static bool TryInitialize()
    {
        if (_init is null)
        {
            return false;
        }
        try { return _init() == 0; }
        catch { return false; }
    }

    /// <summary>关闭音频事件监听（注销回调并释放模块级 COM 对象）。</summary>
    public static void Shutdown()
    {
        if (_shutdown is null)
        {
            return;
        }
        try { _shutdown(); }
        catch { }
        _changeCallback = null;
    }

    /// <summary>注册音频变更回调（设备插拔/默认设备切换/音量静音/会话变化时触发）。
    /// onChanged 传 null 注销。由原生 STA 通知线程触发，回调须短小、非阻塞。</summary>
    public static bool SetChangeCallback(Action? onChanged)
    {
        if (_setChangeCb is null)
        {
            return false;
        }
        _changeCallback = onChanged is null ? null : new AudioChangeCallback(() => onChanged());
        // 注销时传 null 是合法的（原生层将回调槽置空）；用 ! 关闭本处的可空性误报。
        try { _setChangeCb(_changeCallback!); }
        catch { return false; }
        return true;
    }

    public static AudioEndpointStatusNative GetStatus(AudioFlow flow)
    {
        if (_getStatus is null) return default;
        try
        {
            int hr = _getStatus((int)flow, out float vol, out int muted, out int ok);
            if (hr != 0) return default;
            return new AudioEndpointStatusNative(vol, muted != 0, ok != 0);
        }
        catch { return default; }
    }

    public static int SetMasterVolume(float volume) => _setMaster is null ? -1 : SafeInvoke(() => _setMaster!(volume));
    public static int SetMute(bool muted) => _setMute is null ? -1 : SafeInvoke(() => _setMute!(muted ? 1 : 0));
    public static int SetCaptureVolume(float volume, bool muted) => _setCapture is null ? -1 : SafeInvoke(() => _setCapture!(volume, muted ? 1 : 0));

    public static IReadOnlyList<AudioDeviceNative> EnumerateDevices(AudioFlow flow, int maxDevices = 16)
    {
        if (maxDevices <= 0 || _enum is null) return Array.Empty<AudioDeviceNative>();
        if (maxDevices > 32) maxDevices = 32;
        try
        {
            var ids = new ushort[maxDevices * Stride];
            var names = new ushort[maxDevices * Stride];
            int hr = _enum((int)flow, maxDevices, ids, Stride, names, Stride, out int defIdx, out int filled);
            if (hr != 0 || filled <= 0) return Array.Empty<AudioDeviceNative>();
            var list = new List<AudioDeviceNative>(filled);
            for (int i = 0; i < filled; i++)
            {
                list.Add(new AudioDeviceNative(
                    ExtractRow(ids, i, Stride),
                    ExtractRow(names, i, Stride),
                    i == defIdx));
            }
            return list;
        }
        catch { return Array.Empty<AudioDeviceNative>(); }
    }

    public static int SetDefaultDevice(AudioFlow flow, string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId) || _setDefault is null) return -1;
        try { return _setDefault((int)flow, deviceId); }
        catch { return -1; }
    }

    /// <summary>枚举默认输出端点上的按应用音频会话（系统"音量合成器"）。</summary>
    public static IReadOnlyList<AudioSessionNative> EnumerateSessions(int maxSessions = 16)
    {
        if (maxSessions <= 0 || _enumSessions is null) return Array.Empty<AudioSessionNative>();
        if (maxSessions > 32) maxSessions = 32;
        try
        {
            var names = new ushort[maxSessions * SessionNameChars];
            var volumes = new float[maxSessions];
            var muted = new int[maxSessions];
            var pids = new int[maxSessions];
            int hr = _enumSessions(maxSessions, names, SessionNameChars, volumes, muted, pids, out int filled);
            if (hr != 0 || filled <= 0) return Array.Empty<AudioSessionNative>();
            var list = new List<AudioSessionNative>(filled);
            for (int i = 0; i < filled; i++)
            {
                list.Add(new AudioSessionNative(
                    ExtractRow(names, i, SessionNameChars),
                    volumes[i], muted[i] != 0, pids[i]));
            }
            return list;
        }
        catch { return Array.Empty<AudioSessionNative>(); }
    }

    /// <summary>按进程 ID 设置某应用会话音量/静音。</summary>
    public static int SetSessionVolume(int pid, bool muted, float volume)
    {
        if (pid <= 0 || _setSession is null) return -1;
        try { return _setSession(pid, muted ? 1 : 0, Math.Clamp(volume, 0f, 1f)); }
        catch { return -1; }
    }

    private static int SafeInvoke(Func<int> f) { try { return f(); } catch { return -1; } }

    /// <summary>
    /// 兼容 NativeSystemSource 等历史调用点：读取指定方向端点状态，包装为 AudioEndpointStatus。
    /// C++ 层内部已做驱动/接口兜底，失败返回 Ok=false 供上层降级。
    /// </summary>
    public static AudioEndpointStatus ReadEndpoint(bool capture)
    {
        var s = GetStatus(capture ? AudioFlow.Capture : AudioFlow.Render);
        return new AudioEndpointStatus(s.VolumeFloat, s.Muted, s.Ok);
    }

    private static string ExtractRow(ushort[] buf, int row, int stride)
    {
        var sb = new StringBuilder(stride);
        int start = row * stride;
        for (int i = 0; i < stride; i++)
        {
            var u = buf[start + i];
            if (u == 0) break;
            sb.Append((char)u);
        }
        return sb.ToString();
    }
}
