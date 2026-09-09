// BetterDesktop.Shell.Status — 内存状态 P/Invoke 收口：GlobalMemoryStatusEx。
// 结论验证点：结构体 ABI 布局（dwLength 必须预填）、dwMemoryLoad 为整百分比。

using System.Runtime.InteropServices;
using BetterDesktop.Shell.Core.Native;

namespace BetterDesktop.Shell.Status.Native;

/// <summary>
/// 物理/虚拟内存状态（与 Win32 MEMORYSTATUSEX 内存布局一致）。
/// </summary>
/// <summary>
/// 内存采集的 Interop 封装（GlobalMemoryStatusEx）。
/// 失败/异常时返回 null，由调用方降级（对齐 M10 策略）。
/// </summary>
internal static class MemoryInterop
{

    /// <summary>读取当前内存状态；失败返回 null。</summary>
    public static NativeMethods.MemoryStatusEx? Read()
    {
        try
        {
            var info = new NativeMethods.MemoryStatusEx();
            info.dwLength = (uint)Marshal.SizeOf<NativeMethods.MemoryStatusEx>();
            if (NativeMethods.GlobalMemoryStatusEx(ref info))
            {
                return info;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }
}
