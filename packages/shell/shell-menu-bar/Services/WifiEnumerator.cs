// BetterDesktop.Shell.MenuBar — Wi‑Fi 扫描（真实系统数据源，零硬编码）。
// 优先走 C++ 原生层 WlanCore.dll（wlanapi）读连接属性 + 附近扫描；
//   原生层不可用时降级到托管实现（NetworkInterface + 内联 wlanapi P/Invoke）。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using BetterDesktop.Shell.Status.Native;

namespace BetterDesktop.Shell.MenuBar.Services;

/// <summary>当前连接的 Wi‑Fi 信息快照。</summary>
public sealed record WifiConnectedInfo(
    string Ssid,                // 当前连接的 SSID（无连接时为空）
    string AdapterName,         // 无线适配器名称
    string IpAddress,           // IPv4 地址（无则空）
    long LinkSpeedBytesPerSec,  // 链路速度（字节/秒，转 UI 显示时用 Bps）
    string MacAddress,          // MAC 地址（冒号分隔十六进制）
    bool IsConnected,           // 是否真正已连接
    int SignalQuality);         // 信号质量 0-100（0=未知/不支持）

/// <summary>附近可扫描的 Wi‑Fi 网络。</summary>
public sealed record WifiNearbyItem(
    string Ssid,                // SSID（UTF-8 解码，含中文/特殊字符也可读）
    int SignalQuality,          // 0-100 信号质量（WLAN 接口直接给出的百分比）
    bool IsEncrypted,           // 是否有加密（有密码=锁图标）
    string BandDescription);    // 2.4G / 5G / 未知

/// <summary>
/// Wi‑Fi 枚举：当前连接 + 附近扫描（全部真实系统数据源）。
/// 不做主动连接/断开（避免需要凭据交互）。
/// </summary>
internal static class WifiEnumerator
{
    public static WifiConnectedInfo ReadCurrentConnection()
    {
        // 优先 C++ 原生层 WlanCore.dll。
        if (WlanCoreNative.IsAvailable)
        {
            try
            {
                var c = WlanCoreNative.ReadConnected();
                if (c.Connected)
                {
                    // 原生层可能不返回 IPv4 / 信号强度，用托管层补充
                    string ipv4 = c.Ipv4 ?? string.Empty;
                    int signal = 0;
                    string mac = c.Mac ?? string.Empty;
                    SupplementFromManaged(ref ipv4, ref signal, ref mac);
                    return new WifiConnectedInfo(
                        c.Ssid,
                        c.AdapterName,
                        ipv4,
                        c.LinkSpeedBps,
                        mac,
                        true,
                        signal);
                }
                return new WifiConnectedInfo(string.Empty, string.Empty, string.Empty, 0, string.Empty, false, 0);
            }
            catch
            {
                // 原生层异常时降级托管实现。
            }
        }
        return ReadCurrentConnectionManaged();
    }

    /// <summary>从托管层补充 IPv4、信号强度、MAC（原生层可能缺失这些字段）。</summary>
    private static void SupplementFromManaged(ref string ipv4, ref int signal, ref string mac)
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) continue;
                if (ni.OperationalStatus != OperationalStatus.Up) continue;

                if (string.IsNullOrEmpty(ipv4))
                {
                    var ip = ni.GetIPProperties();
                    foreach (var ua in ip.UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        {
                            ipv4 = ua.Address.ToString();
                            break;
                        }
                    }
                }
                if (string.IsNullOrEmpty(mac))
                {
                    mac = FormatMac(ni.GetPhysicalAddress()?.GetAddressBytes());
                }
                if (signal == 0)
                {
                    var (_, _, sig) = WlanInterop.ReadConnectedSsidAndPhySpeed(ni.Id);
                    signal = sig;
                }
                break;
            }
        }
        catch { /* 补充失败静默，保持原值 */ }
    }

    private static WifiConnectedInfo ReadCurrentConnectionManaged()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.NetworkInterfaceType != NetworkInterfaceType.Wireless80211)
                {
                    continue;
                }
                if (ni.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                var ip = ni.GetIPProperties();
                string ipv4 = string.Empty;
                foreach (var ua in ip.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        ipv4 = ua.Address.ToString();
                        break;
                    }
                }
                string ssid = ni.Name; // 真实 SSID 需要 wlanapi，先给接口名兜底
                string mac = FormatMac(ni.GetPhysicalAddress()?.GetAddressBytes());
                // 通过 wlanapi 再读真正 SSID 和信号强度
                var (trueSsid, _, signalQuality) = WlanInterop.ReadConnectedSsidAndPhySpeed(ni.Id);
                if (!string.IsNullOrEmpty(trueSsid))
                {
                    ssid = trueSsid;
                }
                return new WifiConnectedInfo(
                    ssid,
                    ni.Description,
                    ipv4,
                    ni.Speed >= 0 ? ni.Speed : 0,
                    mac,
                    true,
                    signalQuality);
            }
        }
        catch
        {
            // 降级为未连接
        }
        return new WifiConnectedInfo(string.Empty, string.Empty, string.Empty, 0, string.Empty, false, 0);
    }

    public static async Task<IReadOnlyList<WifiNearbyItem>> ScanNearbyAsync()
    {
        // 8 秒超时：密码错误后系统反复重试连接会导致适配器繁忙、扫描阻塞，
        // 超时后返回空列表，由上层显示"未扫描到"而非永远"正在搜索"。
        var scanTask = ScanNearbyInternalAsync();
        var timeout = Task.Delay(8000);
        if (await Task.WhenAny(scanTask, timeout).ConfigureAwait(false) == timeout)
            return Array.Empty<WifiNearbyItem>();
        return await scanTask.ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<WifiNearbyItem>> ScanNearbyInternalAsync()
    {
        // 优先 C++ 原生层 WlanCore.dll（异步：先触发扫描、稍后汇总，不阻塞调用线程）。
        if (WlanCoreNative.IsAvailable)
        {
            try
            {
                var nets = await WlanCoreNative.ScanNearbyAsync().ConfigureAwait(false);
                if (nets.Length > 0)
                {
                    // wlanapi 一个 SSID 会按 BSSID/频段返回多条；以 SSID 去重，避免列表重复。
                    var seenSsids = new HashSet<string>(StringComparer.Ordinal);
                    return nets
                        .Where(n => seenSsids.Add(n.Ssid))
                        .Select(n => new WifiNearbyItem(
                            n.Ssid,
                            n.SignalQuality,
                            n.Encrypted,
                            n.Is5G ? "5G" : "2.4G"))
                        .OrderBy(SortHiddenLast)
                        .ThenByDescending(x => x.SignalQuality)
                        .ThenBy(x => x.Ssid, StringComparer.Ordinal)
                        .ToList();
                }
            }
            catch
            {
                // 原生层异常时降级托管实现。
            }
        }
        return await Task.Run(() => WlanInterop.ScanAvailableNetworks()).ConfigureAwait(false);
    }

    /// <summary>隐藏网络（SSID 为空）排在列表末尾，命名网络优先显示。</summary>
    private static int SortHiddenLast(WifiNearbyItem x)
        => string.IsNullOrEmpty(x.Ssid) ? 1 : 0;

    // ==================== 连接/断开（委托给 WlanInterop） ====================

    /// <summary>断开当前 WiFi 连接。</summary>
    public static bool Disconnect() => WlanInterop.Disconnect();

    /// <summary>连接到指定 SSID（需已保存配置文件）。</summary>
    public static bool Connect(string ssid) => WlanInterop.Connect(ssid);

    /// <summary>用密码连接到指定 SSID（自动写入 WLAN profile 后发起连接）。返回 true 表示连接请求已发出。</summary>
    public static bool ConnectWithPassword(string ssid, string password) => WlanInterop.ConnectWithPassword(ssid, password);

    /// <summary>检查指定 SSID 是否已保存配置文件。</summary>
    public static bool HasSavedProfile(string ssid) => WlanInterop.HasSavedProfile(ssid);

    /// <summary>删除指定 SSID 的已保存配置文件（密码错误后清理，避免系统反复重试连接导致适配器繁忙）。</summary>
    public static bool DeleteProfile(string ssid) => WlanInterop.DeleteProfile(ssid);

    /// <summary>获取无线接口当前状态：0=断开, 1=已连接, 2=关联中, 3=搜索中, 4=认证中, 5=漫游中, 6=AdHoc已连接, 7=断开中, -1=未知/无适配器。</summary>
    public static int GetInterfaceState() => WlanInterop.GetInterfaceState();

    private static string FormatMac(byte[]? bytes)
    {
        if (bytes is null || bytes.Length < 6) return string.Empty;
        var sb = new StringBuilder(17);
        for (int i = 0; i < 6; i++)
        {
            if (i > 0) sb.Append(':');
            sb.Append(bytes[i].ToString("X2"));
        }
        return sb.ToString();
    }
}

/// <summary>wlanapi 扫描附近网络 + 当前连接 SSID 的 P/Invoke。</summary>
internal static class WlanInterop
{
    /// <summary>
    /// SSID 是任意字节串：中文/Unicode 路由常为 UTF-8；历史设备常为 GBK/拉丁字节。
    /// 优先按 UTF-8 严格解码（无替换字符才算合法），否则按单字节(Latin-1)映射，避免产生"乱码"替换符。
    /// </summary>
    private static string DecodeSsid(ReadOnlySpan<byte> raw)
    {
        if (raw.Length == 0) return string.Empty;
        // 去掉尾部 NUL。
        var bytes = new byte[raw.Length];
        int len = raw.Length;
        for (int i = 0; i < raw.Length; i++) bytes[i] = raw[i];
        while (len > 0 && bytes[len - 1] == 0) len--;
        if (len == 0) return string.Empty;

        // 严格 UTF-8 校验：成功且无非法序列才采纳。
        var utf8 = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        try
        {
            var decoded = utf8.GetString(bytes, 0, len);
            if (decoded.IndexOf('\uFFFD') < 0) return decoded;
        }
        catch (System.Text.DecoderFallbackException)
        {
            // 不是合法 UTF-8，走 Latin-1。
        }
        var sb = new StringBuilder(len);
        for (int i = 0; i < len; i++) sb.Append((char)bytes[i]);
        return sb.ToString();
    }
    private const int WlanApiVersion = 2;

    public static (string Ssid, long LinkSpeed, int SignalQuality) ReadConnectedSsidAndPhySpeed(string adapterId)
    {
        IntPtr hClient = IntPtr.Zero;
        IntPtr pList = IntPtr.Zero;
        try
        {
            if (WlanOpenHandle(WlanApiVersion, IntPtr.Zero, out _, out hClient) != 0)
                return (string.Empty, 0, 0);
            if (WlanEnumInterfaces(hClient, IntPtr.Zero, out pList) != 0)
                return (string.Empty, 0, 0);

            var count = Marshal.ReadInt32(pList);
            var cursor = pList + 8;
            var infoSize = Marshal.SizeOf<WlanInterfaceInfoNative>();
            Guid targetGuid;
            if (!Guid.TryParse(adapterId, out targetGuid))
            {
                // 若传的不是 Guid，退化为首个已连接接口
                targetGuid = Guid.Empty;
            }
            for (int i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WlanInterfaceInfoNative>(cursor);
                cursor += infoSize;
                bool matched = info.InterfaceGuid == targetGuid || targetGuid == Guid.Empty;
                if (!matched) continue;
                if (info.isState != 1) // 1 = Connected
                {
                    if (targetGuid != Guid.Empty) return (string.Empty, 0, 0);
                    continue;
                }

                // 读连接属性拿真实 SSID
                IntPtr pConn = IntPtr.Zero;
                if (WlanQueryInterface(hClient, ref info.InterfaceGuid,
                        WLAN_INTF_OPCODE.wlan_intf_opcode_current_connection,
                        IntPtr.Zero, out _, out pConn, IntPtr.Zero) == 0 && pConn != IntPtr.Zero)
                {
                    try
                    {
                        var conn = Marshal.PtrToStructure<WLANConnectionAttributes>(pConn);
                        var ssidBytes = new byte[conn.wlanAssociationAttributes.dot11Ssid.uSSIDLength];
                        for (int b = 0; b < ssidBytes.Length; b++)
                        {
                            ssidBytes[b] = conn.wlanAssociationAttributes.dot11Ssid.ucSSID[b];
                        }
                        var ssid = DecodeSsid(ssidBytes);
                        var speed = (long)conn.wlanAssociationAttributes.ulLinkSpeed * 1000; // Kbps → bps
                        var signal = (int)conn.wlanAssociationAttributes.wlanSignalQuality;
                        return (ssid, speed, signal);
                    }
                    finally
                    {
                        WlanFreeMemory(pConn);
                    }
                }
            }
        }
        catch { /* ignore */ }
        finally
        {
            if (pList != IntPtr.Zero) WlanFreeMemory(pList);
            if (hClient != IntPtr.Zero) _ = WlanCloseHandle(hClient, IntPtr.Zero);
        }
        return (string.Empty, 0, 0);
    }

    public static IReadOnlyList<WifiNearbyItem> ScanAvailableNetworks()
    {
        IntPtr hClient = IntPtr.Zero;
        IntPtr pList = IntPtr.Zero;
        IntPtr pAvail = IntPtr.Zero;
        try
        {
            if (WlanOpenHandle(WlanApiVersion, IntPtr.Zero, out _, out hClient) != 0)
                return Array.Empty<WifiNearbyItem>();
            if (WlanEnumInterfaces(hClient, IntPtr.Zero, out pList) != 0)
                return Array.Empty<WifiNearbyItem>();

            var count = Marshal.ReadInt32(pList);
            var cursor = pList + 8;
            var infoSize = Marshal.SizeOf<WlanInterfaceInfoNative>();
            var result = new List<WifiNearbyItem>(capacity: 32);
            var seenSsids = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WlanInterfaceInfoNative>(cursor);
                cursor += infoSize;

                if (WlanGetAvailableNetworkList(hClient, ref info.InterfaceGuid,
                        WLAN_AVAILABLE_NETWORK_INCLUDE_ALL_ADHOC_PROFILES |
                        WLAN_AVAILABLE_NETWORK_INCLUDE_ALL_MANUAL_HIDDEN_PROFILES,
                        IntPtr.Zero, out pAvail, out _) != 0 || pAvail == IntPtr.Zero)
                {
                    continue;
                }

                var total = Marshal.ReadInt32(pAvail);
                var pNetworks = pAvail + 8;
                var nativeSize = Marshal.SizeOf<WlanAvailableNetworkNative>();
                for (int j = 0; j < total; j++)
                {
                    var net = Marshal.PtrToStructure<WlanAvailableNetworkNative>(pNetworks + j * nativeSize);
                    int ssidLen = (int)net.dot11Ssid.uSSIDLength;
                    if (ssidLen < 0 || ssidLen > 32) continue;
                    byte[] ssidBytes = new byte[ssidLen];
                    for (int b = 0; b < ssidLen; b++) ssidBytes[b] = net.dot11Ssid.ucSSID[b];
                    // 隐藏网络广播为空 SSID：保留为“空名”条目，由上层排序置底并显示为“隐藏网络”。
                    var ssid = DecodeSsid(ssidBytes);

                    if (!seenSsids.Add(ssid)) continue;
                    bool encrypted = net.dot11DefaultAuthAlgorithm != DOT11_AUTH_ALGO_DOT11_AUTH_ALGO_80211_OPEN ||
                                     net.dot11DefaultCipherAlgorithm != DOT11_CIPHER_ALGO_DOT11_CIPHER_NO_ENCRYPTION;
                    string band = (net.flags & 1) != 0 ? "5G" : "2.4G"; // heuristic: bPhyType not directly given
                    result.Add(new WifiNearbyItem(ssid, (int)net.wlanSignalQuality, encrypted, band));
                }
                WlanFreeMemory(pAvail);
                pAvail = IntPtr.Zero;
            }

            return result
                .OrderBy(x => string.IsNullOrEmpty(x.Ssid) ? 1 : 0)
                .ThenByDescending(x => x.SignalQuality)
                .ThenBy(x => x.Ssid, StringComparer.Ordinal)
                .ToList();
        }
        catch { return Array.Empty<WifiNearbyItem>(); }
        finally
        {
            if (pAvail != IntPtr.Zero) WlanFreeMemory(pAvail);
            if (pList != IntPtr.Zero) WlanFreeMemory(pList);
            if (hClient != IntPtr.Zero) _ = WlanCloseHandle(hClient, IntPtr.Zero);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanInterfaceInfoNative
    {
        public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 512)] public byte[] strInterfaceDescription;
        public int isState;
    }

    private enum WLAN_INTF_OPCODE
    {
        wlan_intf_opcode_current_connection = 7
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DOT11_MAC_ADDRESS
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public byte[] ucDot11MacAddress;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DOT11_SSID
    {
        public uint uSSIDLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] ucSSID;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WLANAssociationAttributes
    {
        public DOT11_SSID dot11Ssid;
        public uint uReserved;
        public DOT11_MAC_ADDRESS dot11Bssid;
        public uint dot11BssType;
        public uint dot11PhyType;
        public uint uDot11PhyIndex;
        public uint wlanSignalQuality;
        public uint ulRxRate;
        public uint ulLinkSpeed;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLANConnectionAttributes
    {
        public uint isState;
        public WLANAssociationAttributes wlanAssociationAttributes;
        public uint wlanSecurityAttributes; // simplified
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanAvailableNetworkNative
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strProfileName;
        public DOT11_SSID dot11Ssid;
        public uint dot11BssType;
        public uint uNumberOfBssids;
        [MarshalAs(UnmanagedType.Bool)] public bool bNetworkConnectable;
        public uint wlanNotConnectableReason;
        public uint uNumberOfPhyTypes;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public uint[] dot11PhyTypes;
        public byte wlanSignalQuality;
        [MarshalAs(UnmanagedType.Bool)] public bool bSecurityEnabled;
        public uint flags;
        public uint wlanDefaultAuthAlgorithm;
        public uint dot11DefaultAuthAlgorithm;
        public uint dot11DefaultCipherAlgorithm;
        public uint dwFlags;
        public uint dwReserved;
    }

    private const uint WLAN_AVAILABLE_NETWORK_INCLUDE_ALL_ADHOC_PROFILES = 0x00000001;
    private const uint WLAN_AVAILABLE_NETWORK_INCLUDE_ALL_MANUAL_HIDDEN_PROFILES = 0x00000002;
    private const uint DOT11_AUTH_ALGO_DOT11_AUTH_ALGO_80211_OPEN = 1;
    private const uint DOT11_CIPHER_ALGO_DOT11_CIPHER_NO_ENCRYPTION = 0x00;

    // ==================== WiFi 连接/断开 ====================

    private enum WLAN_CONNECTION_MODE
    {
        wlan_connection_mode_profile = 0,       // 使用已保存配置文件连接
        wlan_connection_mode_temporary_profile,  // 临时配置文件
        wlan_connection_mode_discovery_secure,   // 安全发现
        wlan_connection_mode_discovery_unsecure, // 非安全发现
        wlan_connection_mode_auto,               // 自动
        wlan_connection_mode_invalid
    }

    private enum DOT11_BSS_TYPE
    {
        dot11_BSS_type_infrastructure = 1,
        dot11_BSS_type_independent = 2,
        dot11_BSS_type_any = 3
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_CONNECTION_PARAMETERS
    {
        public WLAN_CONNECTION_MODE wlanConnectionMode;
        public string strProfile;
        public IntPtr pDot11Ssid;
        public IntPtr pDesiredBssidList;
        public DOT11_BSS_TYPE dot11BssType;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_PROFILE_INFO
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strProfileName;
        public uint dwFlags;
    }

    /// <summary>断开当前 WiFi 连接。返回 true 表示调用成功。</summary>
    public static bool Disconnect()
    {
        IntPtr hClient = IntPtr.Zero;
        IntPtr pList = IntPtr.Zero;
        try
        {
            if (WlanOpenHandle(WlanApiVersion, IntPtr.Zero, out _, out hClient) != 0) return false;
            if (WlanEnumInterfaces(hClient, IntPtr.Zero, out pList) != 0) return false;
            var count = Marshal.ReadInt32(pList);
            var cursor = pList + 8;
            var infoSize = Marshal.SizeOf<WlanInterfaceInfoNative>();
            for (int i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WlanInterfaceInfoNative>(cursor);
                cursor += infoSize;
                if (info.isState == 1) // Connected
                {
                    return WlanDisconnect(hClient, ref info.InterfaceGuid, IntPtr.Zero) == 0;
                }
            }
            return false;
        }
        catch { return false; }
        finally
        {
            if (pList != IntPtr.Zero) WlanFreeMemory(pList);
            if (hClient != IntPtr.Zero) _ = WlanCloseHandle(hClient, IntPtr.Zero);
        }
    }

    /// <summary>连接到指定 SSID 的 WiFi 网络（需已保存配置文件）。返回 true 表示连接请求已发出。</summary>
    public static bool Connect(string ssid)
    {
        if (string.IsNullOrEmpty(ssid)) return false;
        IntPtr hClient = IntPtr.Zero;
        IntPtr pList = IntPtr.Zero;
        try
        {
            if (WlanOpenHandle(WlanApiVersion, IntPtr.Zero, out _, out hClient) != 0) return false;
            if (WlanEnumInterfaces(hClient, IntPtr.Zero, out pList) != 0) return false;
            var count = Marshal.ReadInt32(pList);
            var cursor = pList + 8;
            var infoSize = Marshal.SizeOf<WlanInterfaceInfoNative>();
            for (int i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WlanInterfaceInfoNative>(cursor);
                cursor += infoSize;

                // 检查是否有该 SSID 的已保存配置文件
                if (!HasProfileInternal(hClient, ref info.InterfaceGuid, ssid))
                    continue;

                var cp = new WLAN_CONNECTION_PARAMETERS
                {
                    wlanConnectionMode = WLAN_CONNECTION_MODE.wlan_connection_mode_profile,
                    strProfile = ssid, // 配置文件名通常等于 SSID
                    pDot11Ssid = IntPtr.Zero,
                    pDesiredBssidList = IntPtr.Zero,
                    dot11BssType = DOT11_BSS_TYPE.dot11_BSS_type_any,
                    dwFlags = 0
                };
                return WlanConnect(hClient, ref info.InterfaceGuid, ref cp, IntPtr.Zero) == 0;
            }
            return false;
        }
        catch { return false; }
        finally
        {
            if (pList != IntPtr.Zero) WlanFreeMemory(pList);
            if (hClient != IntPtr.Zero) _ = WlanCloseHandle(hClient, IntPtr.Zero);
        }
    }

    /// <summary>用密码连接到指定 SSID：自动写入 WLAN profile（WPA2PSK/AES）后发起连接。
    /// 返回 true 表示连接请求已发出；最终连接结果由系统异步决定（密码错误会随后失败）。</summary>
    public static bool ConnectWithPassword(string ssid, string password)
    {
        if (string.IsNullOrEmpty(ssid) || string.IsNullOrEmpty(password)) return false;
        IntPtr hClient = IntPtr.Zero;
        IntPtr pList = IntPtr.Zero;
        try
        {
            if (WlanOpenHandle(WlanApiVersion, IntPtr.Zero, out _, out hClient) != 0) return false;
            if (WlanEnumInterfaces(hClient, IntPtr.Zero, out pList) != 0) return false;
            var count = Marshal.ReadInt32(pList);
            var cursor = pList + 8;
            var infoSize = Marshal.SizeOf<WlanInterfaceInfoNative>();
            for (int i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WlanInterfaceInfoNative>(cursor);
                cursor += infoSize;

                // 写入 profile（覆盖已存在的同名 profile），密码以明文 passPhrase 写入（protected=false）
                string profileXml = BuildWpa2ProfileXml(ssid, password);
                uint reasonCode;
                int setResult = WlanSetProfile(hClient, ref info.InterfaceGuid, 0, profileXml, null, true, IntPtr.Zero, out reasonCode);
                if (setResult != 0)
                {
                    continue;
                }

                var cp = new WLAN_CONNECTION_PARAMETERS
                {
                    wlanConnectionMode = WLAN_CONNECTION_MODE.wlan_connection_mode_profile,
                    strProfile = ssid,
                    pDot11Ssid = IntPtr.Zero,
                    pDesiredBssidList = IntPtr.Zero,
                    dot11BssType = DOT11_BSS_TYPE.dot11_BSS_type_any,
                    dwFlags = 0
                };
                return WlanConnect(hClient, ref info.InterfaceGuid, ref cp, IntPtr.Zero) == 0;
            }
            return false;
        }
        catch { return false; }
        finally
        {
            if (pList != IntPtr.Zero) WlanFreeMemory(pList);
            if (hClient != IntPtr.Zero) _ = WlanCloseHandle(hClient, IntPtr.Zero);
        }
    }

    /// <summary>生成 WPA2-PSK/AES 的 WLAN profile XML（最常见家用/办公加密方式）。</summary>
    private static string BuildWpa2ProfileXml(string ssid, string password)
    {
        string hexSsid = BitConverter.ToString(Encoding.UTF8.GetBytes(ssid)).Replace("-", "");
        string safeSsid = SecurityElement.Escape(ssid) ?? ssid;
        string safePwd = SecurityElement.Escape(password) ?? password;
        return $@"<?xml version=""1.0""?>
<WLANProfile xmlns=""http://www.microsoft.com/networking/WLAN/profile/v1"">
  <name>{safeSsid}</name>
  <SSIDConfig>
    <SSID>
      <hex>{hexSsid}</hex>
      <name>{safeSsid}</name>
    </SSID>
  </SSIDConfig>
  <connectionType>ESS</connectionType>
  <connectionMode>auto</connectionMode>
  <MSM>
    <security>
      <authEncryption>
        <authentication>WPA2PSK</authentication>
        <encryption>AES</encryption>
        <useOneX>false</useOneX>
      </authEncryption>
      <sharedKey>
        <keyType>passPhrase</keyType>
        <protected>false</protected>
        <keyMaterial>{safePwd}</keyMaterial>
      </sharedKey>
    </security>
  </MSM>
</WLANProfile>";
    }

    /// <summary>检查指定 SSID 是否已保存配置文件。</summary>
    public static bool HasSavedProfile(string ssid)
    {
        if (string.IsNullOrEmpty(ssid)) return false;
        IntPtr hClient = IntPtr.Zero;
        IntPtr pList = IntPtr.Zero;
        try
        {
            if (WlanOpenHandle(WlanApiVersion, IntPtr.Zero, out _, out hClient) != 0) return false;
            if (WlanEnumInterfaces(hClient, IntPtr.Zero, out pList) != 0) return false;
            var count = Marshal.ReadInt32(pList);
            var cursor = pList + 8;
            var infoSize = Marshal.SizeOf<WlanInterfaceInfoNative>();
            for (int i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WlanInterfaceInfoNative>(cursor);
                cursor += infoSize;
                if (HasProfileInternal(hClient, ref info.InterfaceGuid, ssid)) return true;
            }
            return false;
        }
        catch { return false; }
        finally
        {
            if (pList != IntPtr.Zero) WlanFreeMemory(pList);
            if (hClient != IntPtr.Zero) _ = WlanCloseHandle(hClient, IntPtr.Zero);
        }
    }

    /// <summary>删除指定 SSID 的已保存配置文件（密码错误后清理，避免系统反复重试导致适配器繁忙）。</summary>
    public static bool DeleteProfile(string ssid)
    {
        if (string.IsNullOrEmpty(ssid)) return false;
        IntPtr hClient = IntPtr.Zero;
        IntPtr pList = IntPtr.Zero;
        try
        {
            if (WlanOpenHandle(WlanApiVersion, IntPtr.Zero, out _, out hClient) != 0) return false;
            if (WlanEnumInterfaces(hClient, IntPtr.Zero, out pList) != 0) return false;
            var count = Marshal.ReadInt32(pList);
            var cursor = pList + 8;
            var infoSize = Marshal.SizeOf<WlanInterfaceInfoNative>();
            bool any = false;
            for (int i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WlanInterfaceInfoNative>(cursor);
                cursor += infoSize;
                if (WlanDeleteProfile(hClient, ref info.InterfaceGuid, ssid, IntPtr.Zero) == 0)
                    any = true;
            }
            return any;
        }
        catch { return false; }
        finally
        {
            if (pList != IntPtr.Zero) WlanFreeMemory(pList);
            if (hClient != IntPtr.Zero) _ = WlanCloseHandle(hClient, IntPtr.Zero);
        }
    }

    /// <summary>获取第一个无线接口的当前连接状态：0=断开,1=已连接,2=关联中,3=搜索中,4=认证中,5=漫游中,6=AdHoc已连接,7=断开中,-1=未知/无适配器。</summary>
    public static int GetInterfaceState()
    {
        IntPtr hClient = IntPtr.Zero;
        IntPtr pList = IntPtr.Zero;
        try
        {
            if (WlanOpenHandle(WlanApiVersion, IntPtr.Zero, out _, out hClient) != 0) return -1;
            if (WlanEnumInterfaces(hClient, IntPtr.Zero, out pList) != 0) return -1;
            var count = Marshal.ReadInt32(pList);
            if (count == 0) return -1;
            var info = Marshal.PtrToStructure<WlanInterfaceInfoNative>(pList + 8);
            return info.isState;
        }
        catch { return -1; }
        finally
        {
            if (pList != IntPtr.Zero) WlanFreeMemory(pList);
            if (hClient != IntPtr.Zero) _ = WlanCloseHandle(hClient, IntPtr.Zero);
        }
    }

    private static bool HasProfileInternal(IntPtr hClient, ref Guid interfaceGuid, string ssid)
    {
        IntPtr pProfiles = IntPtr.Zero;
        try
        {
            if (WlanGetProfileList(hClient, ref interfaceGuid, IntPtr.Zero, out pProfiles) != 0 || pProfiles == IntPtr.Zero)
                return false;
            var total = Marshal.ReadInt32(pProfiles);
            var pItems = pProfiles + 8; // 跳过 dwNumberOfItems + dwIndex
            var itemSize = Marshal.SizeOf<WLAN_PROFILE_INFO>();
            for (int j = 0; j < total; j++)
            {
                var prof = Marshal.PtrToStructure<WLAN_PROFILE_INFO>(pItems + j * itemSize);
                if (string.Equals(prof.strProfileName, ssid, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }
        catch { return false; }
        finally
        {
            if (pProfiles != IntPtr.Zero) WlanFreeMemory(pProfiles);
        }
    }

    [DllImport("wlanapi.dll")]
    private static extern int WlanDisconnect(IntPtr hClientHandle, ref Guid pInterfaceGuid, IntPtr pReserved);
    [DllImport("wlanapi.dll")]
    private static extern int WlanConnect(IntPtr hClientHandle, ref Guid pInterfaceGuid, ref WLAN_CONNECTION_PARAMETERS pConnectionParameters, IntPtr pReserved);
    [DllImport("wlanapi.dll")]
    private static extern int WlanGetProfileList(IntPtr hClientHandle, ref Guid pInterfaceGuid, IntPtr pReserved, out IntPtr ppProfileList);
    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)]
    private static extern int WlanSetProfile(IntPtr hClientHandle, ref Guid pInterfaceGuid, uint dwFlags, string strProfileXml, string? strAllUserProfileSecurity, bool bOverwrite, IntPtr pReserved, out uint pdwReasonCode);
    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)]
    private static extern int WlanDeleteProfile(IntPtr hClientHandle, ref Guid pInterfaceGuid, string strProfileName, IntPtr pReserved);

    [DllImport("wlanapi.dll")]
    private static extern int WlanOpenHandle(uint dwClientVersion, IntPtr pReserved, out uint pdwNegotiatedVersion, out IntPtr phClientHandle);
    [DllImport("wlanapi.dll")]
    private static extern int WlanEnumInterfaces(IntPtr hClientHandle, IntPtr pReserved, out IntPtr ppInterfaceList);
    [DllImport("wlanapi.dll")]
    private static extern int WlanQueryInterface(IntPtr hClientHandle, ref Guid pInterfaceGuid, WLAN_INTF_OPCODE OpCode, IntPtr pReserved, out uint pdwDataSize, out IntPtr ppData, IntPtr pWlanOpCodeValueType);
    [DllImport("wlanapi.dll")]
    private static extern int WlanGetAvailableNetworkList(IntPtr hClientHandle, ref Guid pInterfaceGuid, uint dwFlags, IntPtr pReserved, out IntPtr ppAvailableNetworkList, out uint pdwDataSize);
    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr pMemory);
    [DllImport("wlanapi.dll")]
    private static extern int WlanCloseHandle(IntPtr hClientHandle, IntPtr pReserved);
}
