// BetterDesktop.Shell.Status — 网络状态 P/Invoke 收口：
//   - ConnectionState：托管层 NetworkInterface（零依赖），区分接口类型/连接状态/速度。
//   - WirelessAdapters：wlanapi WlanOpenHandle + WlanEnumInterfaces 枚举无线适配器及其连接态。
//
// 结论验证点：
//   - wlanapi 的 clientHandle 必须通过 WlanCloseHandle 关闭，且每次重开。
//   - WLAN_INTERFACE_INFO_LIST 是变长结构，需手动按内存布局扫描，不能整体 Marshal。

using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace BetterDesktop.Shell.Status.Native;

/// <summary>某个网络接口的连接状态快照。</summary>
internal readonly record struct InterfaceLinkStatus(
    string Name,          // 接口名（如 "WLAN"）
    string LinkType,      // 接口类型名（Wireless / Ethernet / Loopback …）
    bool ConnectedFast,   // 是否处于高速可用链路（Up 且非回环/隧道）
    long SpeedBytesPerSec); // 链路速度（B/s；不支持时 0）

/// <summary>某个无线适配器的连接状态快照。</summary>
public readonly record struct WirelessAdapterStatus(
    string Description,   // 描述（如 "Intel(R) Wi-Fi 6E AX210"）
    string State,         // 连接态文本（Connected / Disconnected / …）
    bool IsConnected);    // 是否处于已连接态

/// <summary>
/// 网络状态采集的 Interop 封装。任何一步异常均降级为可靠的空态，不抛异常。
/// </summary>
internal static class NetworkInterop
{
    /// <summary>枚举当前所有物理链接（托管层，零依赖）；异常时返回空列表。</summary>
    public static IReadOnlyList<InterfaceLinkStatus> ReadPhysicalInterfaces()
    {
        try
        {
            var result = new List<InterfaceLinkStatus>();
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                // 只关注真实的物理/无线接口：排除回环、隧道、隐藏
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                var connected = ni.OperationalStatus == OperationalStatus.Up;
                result.Add(new InterfaceLinkStatus(
                    InteropGuard.SafeInvoke(() => ni.Name, string.Empty),
                    InteropGuard.SafeInvoke(() => ni.NetworkInterfaceType.ToString(), string.Empty),
                    connected,
                    InteropGuard.SafeInvoke(() => (long)ni.Speed, 0L)));
            }
            return result;
        }
        catch (Exception ex)
        {
            _ = ex;
            return Array.Empty<InterfaceLinkStatus>();
        }
    }

    /// <summary>是否有至少一条高速（非回环/隧道）链路处于 Up。</summary>
    public static bool HasActiveConnection()
        => ReadPhysicalInterfaces().Any(x => x.ConnectedFast);

    /// <summary>枚举当前无线适配器（wlanapi）；异常时返回空列表。</summary>
    public static IReadOnlyList<WirelessAdapterStatus> ReadWirelessAdapters()
    {
        IntPtr hClient = IntPtr.Zero;
        IntPtr pInterfaceList = IntPtr.Zero;
        try
        {
            if (WlanOpenHandle(2, IntPtr.Zero, out _, out hClient) != 0)
            {
                return Array.Empty<WirelessAdapterStatus>();
            }

            if (WlanEnumInterfaces(hClient, IntPtr.Zero, out pInterfaceList) != 0)
            {
                return Array.Empty<WirelessAdapterStatus>();
            }

            var result = new List<WirelessAdapterStatus>();
            var count = Marshal.ReadInt32(pInterfaceList);
            var sizeOfInfo = Marshal.SizeOf<WlanInterfaceInfoNative>();

            // 跳过列表头（dwNumberOfItems + dwIndex），进入首个 WLAN_INTERFACE_INFO 元素
            var cursor = pInterfaceList + Marshal.SizeOf<int>() + Marshal.SizeOf<int>();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WlanInterfaceInfoNative>(cursor);
                var desc = TrimAscii(info.strInterfaceDescription);
                var isConnected = ((int)info.isState) == (int)WlanInterfaceState.Connected;
                result.Add(new WirelessAdapterStatus(desc, info.isState.ToString(), isConnected));
                cursor += sizeOfInfo;
            }
            return result;
        }
        catch (Exception ex)
        {
            _ = ex;
            return Array.Empty<WirelessAdapterStatus>();
        }
        finally
        {
            if (pInterfaceList != IntPtr.Zero)
            {
                WlanFreeMemory(pInterfaceList);
            }
            if (hClient != IntPtr.Zero)
            {
                _ = WlanCloseHandle(hClient, IntPtr.Zero);
            }
        }
    }

    /// <summary>把固定宽度 UTF-16 宽字符数组（字节视图）转字符串，去脏尾 null。</summary>
    private static string TrimAscii(byte[] bytes)
    {
        var chars = new List<char>(bytes.Length / 2);
        for (var i = 0; i + 1 < bytes.Length; i += 2)
        {
            var c = (char)(bytes[i] | (bytes[i + 1] << 8));
            if (c == '\0')
            {
                break;
            }
            chars.Add(c);
        }
        return new string(chars.ToArray());
    }

    // ---- wlanapi P/Invoke ----

    private const int WlanInterfaceDescriptionMaxLength = 256;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanInterfaceInfoNative
    {
        public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = WlanInterfaceDescriptionMaxLength * 2)]
        public byte[] strInterfaceDescription; // 256 个 WCHAR = 512 字节
        public int isState;
    }

    private enum WlanInterfaceState
    {
        NotReady = 0,
        Connected = 1,
        AdHocNetworkFormed = 2,
        Disconnecting = 3,
        Disconnected = 4,
        Associating = 5,
        Discovering = 6,
        Authenticating = 7
    }

    [DllImport("wlanapi.dll")]
    private static extern int WlanOpenHandle(uint dwClientVersion, IntPtr pReserved, out uint pdwNegotiatedVersion, out IntPtr phClientHandle);

    [DllImport("wlanapi.dll")]
    private static extern int WlanEnumInterfaces(IntPtr hClientHandle, IntPtr pReserved, out IntPtr ppInterfaceList);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr pMemory);

    [DllImport("wlanapi.dll")]
    private static extern int WlanCloseHandle(IntPtr hClientHandle, IntPtr pReserved);
}