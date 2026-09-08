// BetterDesktop.Shell.Status — 系统原始值读取源（测试替换缝）
// monitor 不直接触碰 Interop，统一经 ISystemSource 取原始值；
// 生产用 NativeSystemSource，单测注入 FakeSystemSource 即可稳定验证语义层。

using BetterDesktop.Shell.Core.Native;
using BetterDesktop.Shell.Status.Native;

namespace BetterDesktop.Shell.Status.Contracts;

/// <summary>
/// 系统原始值读取源的抽象。P/Invoke 层抽接口，可替换为 fake 实现（对齐任务验收：内存/电量/音量注入可测）。
/// 各实现都应保证异常安全（降级返回），不向调用方抛异常。
/// </summary>
public interface ISystemSource
{
    /// <summary>读取物理内存状态；失败返回 null。</summary>
    NativeMethods.MemoryStatusEx? ReadMemory();

    /// <summary>读取系统电源状态；失败返回 null。</summary>
    SystemPowerStatus? ReadPower();

    /// <summary>读取输出端点（音量）状态。</summary>
    AudioEndpointStatus ReadVolumeEndpoint();

    /// <summary>读取输入端点（麦克风）状态。</summary>
    AudioEndpointStatus ReadMicrophoneEndpoint();

    /// <summary>读取无线适配器连接状态列表；失败返回空列表。</summary>
    IReadOnlyList<WirelessAdapterStatus> ReadWirelessAdapters();

    /// <summary>是否有至少一条高速（非回环/隧道）链路处于连接态（含有线/无线）。</summary>
    bool HasActiveConnection();

    /// <summary>读取当前键盘布局 KLID；失败返回空串。</summary>
    string ReadKeyboardLayoutId();

    /// <summary>枚举当前已加载键盘布局/输入法（含激活标记）；失败返回 null（调用方降级 KLID 映射）。</summary>
    IReadOnlyList<KeyboardLayoutItem>? ReadKeyboardLayouts();

    /// <summary>枚举系统已注册键盘布局（注册表 Layouts 表）；失败返回 null。</summary>
    IReadOnlyList<KeyboardLayoutItem>? ReadRegisteredKeyboardLayouts();
}
