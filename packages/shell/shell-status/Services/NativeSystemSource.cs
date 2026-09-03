// BetterDesktop.Shell.Status — 生产系统源实现
// 收口所有 P/Invoke：把 Native 各 Interop 适配成 ISystemSource，供 monitor 消费。

namespace BetterDesktop.Shell.Status.Services;

using Contracts;
using Native;

/// <summary>生产环境使用的系统原始值读取源（直接走 P/Invoke Interop）。</summary>
public sealed class NativeSystemSource : ISystemSource
{
    public MemoryStatusEx? ReadMemory() => MemoryInterop.Read();

    public SystemPowerStatus? ReadPower() => BatteryInterop.Read();

    public AudioEndpointStatus ReadVolumeEndpoint()
    {
        // 优先走 C++ 原生封装（AudioCore.dll）；装载失败时降级到纯 C# 托管实现。
        return AudioCoreNative.IsAvailable
            ? AudioCoreNative.ReadEndpoint(capture: false)
            : AudioInterop.ReadDefaultEndpoint(EDataFlow.Render);
    }

    public AudioEndpointStatus ReadMicrophoneEndpoint()
    {
        return AudioCoreNative.IsAvailable
            ? AudioCoreNative.ReadEndpoint(capture: true)
            : AudioInterop.ReadDefaultEndpoint(EDataFlow.Capture);
    }

    public IReadOnlyList<WirelessAdapterStatus> ReadWirelessAdapters() => NetworkInterop.ReadWirelessAdapters();

    public bool HasActiveConnection() => NetworkInterop.HasActiveConnection();

    public string ReadKeyboardLayoutId() => ImeInterop.GetKeyboardLayoutId();

    public IReadOnlyList<KeyboardLayoutItem>? ReadKeyboardLayouts() => KeyboardLayoutInterop.Enumerate();

    public IReadOnlyList<KeyboardLayoutItem>? ReadRegisteredKeyboardLayouts() => KeyboardLayoutInterop.GetRegisteredLayouts();
}