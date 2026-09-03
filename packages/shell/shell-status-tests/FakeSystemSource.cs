// BetterDesktop.Shell.Status.Tests — 可编写 FakeSystemSource
// 复用公共源接口 ISystemSource，测试可控注入每路原始值，稳定验证语义层输出。

using BetterDesktop.Shell.Status.Contracts;
using BetterDesktop.Shell.Status.Native;

namespace BetterDesktop.Shell.Status.Tests;

/// <summary>
/// 可编辑的假系统源：模拟各 Interop 的读取结果，供语义层单测。
/// </summary>
internal sealed class FakeSystemSource : ISystemSource
{
    public MemoryStatusEx? Memory { get; set; }
    public SystemPowerStatus? Power { get; set; }
    public AudioEndpointStatus Volume { get; set; }
    public AudioEndpointStatus Microphone { get; set; }
    public IReadOnlyList<WirelessAdapterStatus> WirelessAdapters { get; set; } = Array.Empty<WirelessAdapterStatus>();
    public bool HasConnection { get; set; }
    public string LayoutId { get; set; } = string.Empty;
    public IReadOnlyList<KeyboardLayoutItem> KeyboardLayouts { get; set; } = Array.Empty<KeyboardLayoutItem>();
    public IReadOnlyList<KeyboardLayoutItem> RegisteredLayouts { get; set; } = Array.Empty<KeyboardLayoutItem>();

    public MemoryStatusEx? ReadMemory() => Memory;
    public SystemPowerStatus? ReadPower() => Power;
    public AudioEndpointStatus ReadVolumeEndpoint() => Volume;
    public AudioEndpointStatus ReadMicrophoneEndpoint() => Microphone;
    public IReadOnlyList<WirelessAdapterStatus> ReadWirelessAdapters() => WirelessAdapters;
    public bool HasActiveConnection() => HasConnection;
    public string ReadKeyboardLayoutId() => LayoutId;
    public IReadOnlyList<KeyboardLayoutItem>? ReadKeyboardLayouts() => KeyboardLayouts;
    public IReadOnlyList<KeyboardLayoutItem>? ReadRegisteredKeyboardLayouts() => RegisteredLayouts;

    /// <summary>便捷：构造一条原始内存记录。</summary>
    public static MemoryStatusEx MakeMemory(uint loadPercent, ulong total = 32ul * 1024 * 1024 * 1024)
        => new() { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MemoryStatusEx>(), dwMemoryLoad = loadPercent, ullTotalPhys = total, ullAvailPhys = total * (100 - loadPercent) / 100 };

    /// <summary>便捷：构造一条原始电源记录。</summary>
    public static SystemPowerStatus MakePower(byte acLine, byte flag, byte percent, uint lifeTimeSeconds = 3600)
        => new() { ACLineStatus = acLine, BatteryFlag = flag, BatteryLifePercent = percent, BatteryLifeTime = lifeTimeSeconds };
}