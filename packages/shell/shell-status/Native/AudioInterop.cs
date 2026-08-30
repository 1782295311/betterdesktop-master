// BetterDesktop.Shell.Status — Core Audio COM P/Invoke 收口（输出音量 + 输入麦克风端点）。
// 声明 IMMDeviceEnumerator → IMMDevice → IAudioEndpointVolume。
//
// 结论验证点：
//   - COM 接口 vtable 顺序必须与真源一致（C# [ComImport] 严格按照声明顺序排列）。
//   - CoCreateInstance(CLSID_MMDeviceEnumerator) 获取枚举器。

using System.Runtime.InteropServices;

namespace BetterDesktop.Shell.Status.Native;

/// <summary>端点数据流方向。</summary>
public enum EDataFlow
{
    Render = 0, // 输出（扬声器/耳机）
    Capture = 1 // 输入（麦克风）
}

/// <summary>端点角色。</summary>
internal enum ERole
{
    Console = 0
}

/// <summary>
/// 音频端点的状态快照。
/// </summary>
public readonly record struct AudioEndpointStatus(
    float Volume,   // 主音量 0-1
    bool IsMuted,
    bool Ok);       // 端点/接口是否成功激活

/// <summary>
/// Core Audio 采集的 Interop 封装。
/// 失败/异常时返回 Ok=false，由调用方降级（M10 策略）。
/// </summary>
public static class AudioInterop
{
    private static readonly Guid ClassIdMmDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IidIAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(EDataFlow dataFlow, int dwStateMask, out IMMDeviceCollection ppDevices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppEndpoint);
    }

    [ComImport]
    [Guid("0BD2AD2A-04FC-4425-B24E-BFB3B22A296F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig]
        int GetCount(out uint pcDevices);

        [PreserveSig]
        int Item(uint nDevice, out IMMDevice ppDevice);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, out IntPtr ppInterface);

        [PreserveSig]
        int OpenPropertyStore(int stgmAccess, out IntPtr ppProperties);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);

        [PreserveSig]
        int GetState(out int pdwState);
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig]
        int RegisterControlChangeNotify(IntPtr pNotify);

        [PreserveSig]
        int UnregisterControlChangeNotify(IntPtr pNotify);

        [PreserveSig]
        int GetChannelCount(out uint pnChannelCount);

        [PreserveSig]
        int SetMasterVolumeLevel(float fLevelDB, ref Guid guidEventContext);

        [PreserveSig]
        int SetMasterVolumeLevelScalar(float fLevel, ref Guid guidEventContext);

        [PreserveSig]
        int GetMasterVolumeLevel(out float pfLevelDB);

        [PreserveSig]
        int GetMasterVolumeLevelScalar(out float pfLevel);

        [PreserveSig]
        int SetChannelVolumeLevel(uint nChannel, float fLevelDB, ref Guid guidEventContext);

        [PreserveSig]
        int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, ref Guid guidEventContext);

        [PreserveSig]
        int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);

        [PreserveSig]
        int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);

        [PreserveSig]
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid guidEventContext);

        [PreserveSig]
        int GetMute(out bool pbMute);

        [PreserveSig]
        int GetVolumeStepInfo(out uint pnStep, out uint pnStepCount);
    }

    /// <summary>
    /// 取默认端点状态；输出端点可读主音量 + 静音，捕获端点验证能否激活。
    /// 返回 Ok=false 表示端点激活失败（如无声卡/驱动异常），由调用方降级。
    /// </summary>
    public static AudioEndpointStatus ReadDefaultEndpoint(EDataFlow flow)
    {
        try
        {
            var enumerator = CreateEnumerator();
            if (enumerator is null)
            {
                return new AudioEndpointStatus(0, false, false);
            }

            using var enumScope = new ComRef(enumerator);
            var hr = enumerator.GetDefaultAudioEndpoint(flow, ERole.Console, out var endpoint);
            if (hr != 0 || endpoint is null)
            {
                return new AudioEndpointStatus(0, false, false);
            }

            using var endpointScope = new ComRef(endpoint);
            return ActivateAndRead(endpoint);
        }
        catch (Exception ex)
        {
            _ = ex;
            return new AudioEndpointStatus(0, false, false);
        }
    }

    /// <summary>激活输出端点接口并读取状态。</summary>
    private static AudioEndpointStatus ActivateAndRead(IMMDevice endpoint)
    {
        var iid = IidIAudioEndpointVolume; // static readonly 不能直接作 ref，取局部副本
        var hr = endpoint.Activate(ref iid, 1 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, out var volumePtr);
        if (hr != 0 || volumePtr == IntPtr.Zero)
        {
            return new AudioEndpointStatus(0, false, false);
        }

        try
        {
            var volume = (IAudioEndpointVolume)Marshal.GetObjectForIUnknown(volumePtr);
            if (volume is null)
            {
                return new AudioEndpointStatus(0, false, false);
            }

            _ = volume.GetMasterVolumeLevelScalar(out var level);
            _ = volume.GetMute(out var muted);
            return new AudioEndpointStatus(level, muted, true);
        }
        finally
        {
            Marshal.Release(volumePtr);
        }
    }

    /// <summary>创建 IMMDeviceEnumerator ComObject；失败返回 null。</summary>
    private static IMMDeviceEnumerator? CreateEnumerator()
    {
        try
        {
            var t = Type.GetTypeFromCLSID(ClassIdMmDeviceEnumerator, true);
            if (t is null)
            {
                return null;
            }
            var instance = Activator.CreateInstance(t);
            return instance as IMMDeviceEnumerator;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>COM 引用托管作用域，确保 Marshal.ReleaseComObject 释放。</summary>
    private sealed class ComRef : IDisposable
    {
        private readonly object _obj;
        public ComRef(object obj) => _obj = obj;
        public void Dispose() => Marshal.ReleaseComObject(_obj);
    }
}