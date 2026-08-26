// BetterDesktop.Kernel — SystemMemoryProbe 物理内存探测
// 取机器物理内存总量，供 ResourceGovernorOptions 按比例计算自适应阈值。
// 头号优先级：本地大模型 / 3D 渲染场景内存差异巨大，阈值必须按机器缩放。

using System.Runtime.InteropServices;

namespace BetterDesktop.Kernel.Core;

/// <summary>物理内存探测（封装 GlobalMemoryStatusEx）。</summary>
public static class SystemMemoryProbe
{
    /// <summary>机器物理内存总字节数（含 Windows 保留，非可用内存）。</summary>
    public static long TotalPhysicalBytes
    {
        get
        {
            var info = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            return GlobalMemoryStatusEx(ref info) ? (long)info.ullTotalPhys : 0;
        }
    }

    /// <summary>按物理内存百分比计算字节阈值（percent 取值 0–1）。</summary>
    public static long BytesFromPercent(double percent) => (long)(TotalPhysicalBytes * percent);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }
}
