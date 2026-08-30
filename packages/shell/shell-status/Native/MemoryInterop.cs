// BetterDesktop.Shell.Status — 内存状态 P/Invoke 收口：GlobalMemoryStatusEx。
// 结论验证点：结构体 ABI 布局（dwLength 必须预填）、dwMemoryLoad 为整百分比。

using System.Runtime.InteropServices;

namespace BetterDesktop.Shell.Status.Native;

/// <summary>
/// 物理/虚拟内存状态（与 Win32 MEMORYSTATUSEX 内存布局一致）。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct MemoryStatusEx
{
    public uint dwLength;
    public uint dwMemoryLoad;                 // 当前内存使用率（0-100），整百分比
    public ulong ullTotalPhys;                // 物理内存总量（字节）
    public ulong ullAvailPhys;                // 可用物理内存（字节）
    public ulong ullTotalPageFile;
    public ulong ullAvailPageFile;
    public ulong ullTotalVirtual;
    public ulong ullAvailVirtual;
    public ulong ullAvailExtendedVirtual;
}

/// <summary>
/// 内存采集的 Interop 封装（GlobalMemoryStatusEx）。
/// 失败/异常时返回 null，由调用方降级（对齐 M10 策略）。
/// </summary>
internal static class MemoryInterop
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    /// <summary>读取当前内存状态；失败返回 null。</summary>
    public static MemoryStatusEx? Read()
    {
        try
        {
            var info = new MemoryStatusEx();
            info.dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>();
            if (GlobalMemoryStatusEx(ref info))
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