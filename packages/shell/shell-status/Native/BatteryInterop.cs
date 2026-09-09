// BetterDesktop.Shell.Status — 系统电源状态 P/Invoke 收口：GetSystemPowerStatus（含 AC/电池、剩余百分比、剩余时间估算）。

using System.Runtime.InteropServices;
using BetterDesktop.Shell.Core.Native;

namespace BetterDesktop.Shell.Status.Native;

/// <summary>
/// 系统电源状态（与 Win32 SYSTEM_POWER_STATUS 内存布局一致）。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct SystemPowerStatus
{
    public byte ACLineStatus;        // 0=离线(电池), 1=在线(接电), 255=未知
    public byte BatteryFlag;         // 1=高,2=低,4=极低,8=充电,128=无电池,255=未知
    public byte BatteryLifePercent;  // 0-100, 255=未知
    public byte SystemStatusFlag;    // 保留
    public uint BatteryLifeTime;     // 剩余秒数, 0xFFFFFFFF=未知
    public uint BatteryFullLifeTime; // 满电可用秒数
}

/// <summary>
/// 电源状态采集的 Interop 封装（GetSystemPowerStatus）。
/// 失败/异常时返回 null，由调用方降级。
/// </summary>
public static class BatteryInterop
{
    /// <summary>读取当前电源状态；失败返回 null。</summary>
    public static SystemPowerStatus? Read()
    {
        try
        {
            var status = default(NativeMethods.SYSTEM_POWER_STATUS);
            if (NativeMethods.GetSystemPowerStatus(ref status))
            {
                return new SystemPowerStatus
                {
                    ACLineStatus = status.ACLineStatus,
                    BatteryFlag = status.BatteryFlag,
                    BatteryLifePercent = status.BatteryLifePercent,
                    SystemStatusFlag = status.SystemStatusFlag,
                    BatteryLifeTime = status.BatteryLifeTime,
                    BatteryFullLifeTime = status.BatteryFullLifeTime,
                };
            }
            return null;
        }
        catch
        {
            return null;
        }
    }
}
