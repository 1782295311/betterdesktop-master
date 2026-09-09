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
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.Core.Native;
using BetterDesktop.Shell.Status.Native;

namespace BetterDesktop.Shell.MenuBar.Services;

// ── 本文件方法级白话索引（WiFi 原生枚举/连接，白话 → 方法）──
//   "读当前连接（SSID/IP/信号/MAC/速率）" → ReadCurrentConnection（缓存）/ ReadCurrentConnectionCore / ReadCurrentConnectionManaged / SupplementFromManaged / ReadConnectedSsidAndPhySpeed；缓存失效 InvalidateConnectionCache
//   "扫附近网络（托管 + 原生 WLAN API）" → ScanNearbyAsync / ScanAvailableNetworks；隐藏网络排后 SortHiddenLast；SSID 解码 DecodeSsid
//   "连接/带密码连接/断开/删配置/等待连上" → Connect / ConnectWithPassword / Disconnect / DeleteProfile / WaitForConnectionAsync / CleanupFailedConnection
//   "探测网络认证/加密方式"           → ProbeNetworkAuthCipher；MAC 格式化 FormatMac
//   面板 UI 在 Windows/WifiPopupWindow.cs。
// ────────────────────────────────────

/// <summary>当前连接的 Wi‑Fi 信息快照。</summary>
public sealed record WifiConnectedInfo(
    string Ssid,                // 当前连接的 SSID（无连接时为空）
    string AdapterName,         // 无线适配器名称
    string IpAddress,           // IPv4 地址（无则空）
    long LinkSpeedBytesPerSec,  // 链路速度（bits/秒；历史命名 BytesPerSec 不准确，外部契约字段名冻结不改）
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
    // 2026-09-04 回归修复：切换网络时 UI 线程（弹窗刷新）与后台轮询同时走本方法，
    // 原生 Connected=false（断开/关联中）触发 W4 托管降级 → GetAllNetworkInterfaces 可达秒级，
    // 多处并发重复执行造成剧烈卡顿。加 1s 结果缓存收敛；连接/断开/删 profile 动作后主动失效。
    private static readonly object _cacheGate = new();
    private static WifiConnectedInfo _lastResult = new(string.Empty, string.Empty, string.Empty, 0, string.Empty, false, 0);
    private static DateTime _lastAtUtc = DateTime.MinValue;

    public static WifiConnectedInfo ReadCurrentConnection()
    {
        lock (_cacheGate)
        {
            if (DateTime.UtcNow - _lastAtUtc < TimeSpan.FromSeconds(1))
            {
                return _lastResult;
            }
        }

        var result = ReadCurrentConnectionCore();

        lock (_cacheGate)
        {
            _lastResult = result;
            _lastAtUtc = DateTime.UtcNow;
        }
        return result;
    }

    /// <summary>连接/断开/删除 profile 后调用：立即失效状态缓存，让下次读取反映最新状态。</summary>
    private static void InvalidateConnectionCache()
    {
        lock (_cacheGate)
        {
            _lastAtUtc = DateTime.MinValue;
        }
    }

    private static WifiConnectedInfo ReadCurrentConnectionCore()
    {
        // 优先 C++ 原生层 WlanCore.dll。
        if (WlanCoreNative.IsAvailable)
        {
            try
            {
                var c = WlanCoreNative.ReadConnected();
                if (c.Connected)
                {
                    // 信号优先取原生层直读值（W2b）；IPv4/MAC 原生层可能缺失，仍由托管层补充。
                    string ipv4 = c.Ipv4 ?? string.Empty;
                    int signal = c.SignalQuality;
                    string mac = c.Mac ?? string.Empty;
                    if (signal <= 0)
                    {
                        SupplementFromManaged(ref ipv4, ref signal, ref mac);
                    }
                    else
                    {
                        SupplementFromManaged(ref ipv4, ref signal, ref mac, skipSignal: true);
                    }
                    return new WifiConnectedInfo(
                        c.Ssid,
                        c.AdapterName,
                        ipv4,
                        c.LinkSpeedBps,
                        mac,
                        true,
                        signal);
                }
                // W4：原生层 Connected=false 不等于"未连接"（原生层行为异常时也会走到这），
                // 必须经托管层核验后才可定未连接，避免图标误判断网。
            }
            catch (Exception ex)
            {
                // 原生层异常时降级托管实现（G6：关键降级决策点记日志）。
                DiagnosticLog.Trace("menu-bar.wifi", "原生 ReadConnected 异常，降级托管: " + ex.Message);
            }
        }
        return ReadCurrentConnectionManaged();
    }

    /// <summary>从托管层补充 IPv4、信号强度、MAC（原生层可能缺失这些字段）。</summary>
    private static void SupplementFromManaged(ref string ipv4, ref int signal, ref string mac, bool skipSignal = false)
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
                if (!skipSignal && signal == 0)
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
        {
            // G6：超时是"适配器繁忙"的关键信号（密码错误重试等），必须可从日志定位。
            DiagnosticLog.Trace("menu-bar.wifi", "扫描 8s 超时，返回空列表（适配器可能正忙）");
            return Array.Empty<WifiNearbyItem>();
        }
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
    public static bool Disconnect()
    {
        var ok = WlanInterop.Disconnect();
        if (ok) InvalidateConnectionCache();
        else DiagnosticLog.Trace("menu-bar.wifi", "Disconnect 失败（无连接/无适配器/被系统拒绝）");
        return ok;
    }

    /// <summary>连接到指定 SSID（需已保存配置文件）。</summary>
    public static bool Connect(string ssid)
    {
        var ok = WlanInterop.Connect(ssid);
        if (ok) InvalidateConnectionCache();
        else DiagnosticLog.Trace("menu-bar.wifi", $"Connect 失败（{ssid}）");
        return ok;
    }

    /// <summary>用密码连接到指定 SSID（自动写入 WLAN profile 后发起连接）。返回 true 表示连接请求已发出。</summary>
    public static bool ConnectWithPassword(string ssid, string password)
    {
        var ok = WlanInterop.ConnectWithPassword(ssid, password);
        if (ok) InvalidateConnectionCache();
        else DiagnosticLog.Trace("menu-bar.wifi", $"ConnectWithPassword 失败（{ssid}）：profile 写入或连接请求被拒绝");
        return ok;
    }

    /// <summary>检查指定 SSID 是否已保存配置文件。</summary>
    public static bool HasSavedProfile(string ssid) => WlanInterop.HasSavedProfile(ssid);

    /// <summary>删除指定 SSID 的已保存配置文件（密码错误后清理，避免系统反复重试连接导致适配器繁忙）。</summary>
    public static bool DeleteProfile(string ssid)
    {
        var ok = WlanInterop.DeleteProfile(ssid);
        if (ok) InvalidateConnectionCache();
        else DiagnosticLog.Trace("menu-bar.wifi", $"DeleteProfile 失败（{ssid}）：profile 可能不存在");
        return ok;
    }

    /// <summary>获取无线接口当前状态：0=断开, 1=已连接, 2=关联中, 3=搜索中, 4=认证中, 5=漫游中, 6=AdHoc已连接, 7=断开中, -1=未知/无适配器。</summary>
    public static int GetInterfaceState() => WlanInterop.GetInterfaceState();

    /// <summary>
    /// G4：统一等待指定 SSID 连接完成（此前弹窗/密码窗各持有一份内联轮询且只看全局接口态，
    /// 多适配器或用户手动切换网络时会把别的连接误判为本次目标成功）。每 1s 查接口状态，
    /// state==1 时再经 ReadCurrentConnection 核对 SSID（Connect 已失效缓存，轮询读为真实刷新）。
    /// 返回 true=目标已连接；false=失败（断开/AdHoc 反馈）或超时。调用方负责失败后 CleanupFailedConnection。
    /// </summary>
    public static async Task<bool> WaitForConnectionAsync(string ssid, int timeoutSeconds = 10)
    {
        for (int i = 0; i < timeoutSeconds; i++)
        {
            await Task.Delay(1000).ConfigureAwait(false);
            int state = GetInterfaceState();
            if (state == 1) // 已连接：核对确实是目标 SSID
            {
                var current = ReadCurrentConnection();
                if (current.IsConnected && string.Equals(current.Ssid, ssid, StringComparison.Ordinal))
                {
                    return true;
                }
                continue; // 连上的是别的网络：继续观察，目标可能随后接管
            }
            if (state == 0 || state == 6) // 断开 / AdHoc = 系统已反馈连接失败
            {
                return false;
            }
            // 关联中/认证中/漫游中等：继续等待系统反馈
        }
        DiagnosticLog.Trace("menu-bar.wifi", $"等待连接 {ssid} 超时（{timeoutSeconds}s）");
        return false;
    }

    /// <summary>
    /// G4：连接失败/超时/取消后的统一清理。顺序固定：先断开释放适配器、再删错误 profile——
    /// profile 为 connectionMode=auto，不删会导致系统反复自动重连、适配器持续繁忙。
    /// </summary>
    public static void CleanupFailedConnection(string ssid)
    {
        Disconnect();
        DeleteProfile(ssid);
    }

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
    internal static string DecodeSsid(ReadOnlySpan<byte> raw)
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

    static WlanInterop()
    {
        // D2 布局断言：与官方 wlanapi.h 一致（WLAN_CONNECTION_ATTRIBUTES=604、WLAN_AVAILABLE_NETWORK=628）。
        // 结构体字段漂移会在 Debug 下立即暴露，避免"信号/SSID 错位却无任何报错"的静默回归。
        System.Diagnostics.Debug.Assert(
            Marshal.SizeOf<WLANConnectionAttributes>() == 604,
            $"WLANConnectionAttributes 布局漂移: {Marshal.SizeOf<WLANConnectionAttributes>()} != 604");
        System.Diagnostics.Debug.Assert(
            Marshal.SizeOf<WlanAvailableNetworkNative>() == 628,
            $"WlanAvailableNetworkNative 布局漂移: {Marshal.SizeOf<WlanAvailableNetworkNative>()} != 628");
    }

    public static (string Ssid, long LinkSpeed, int SignalQuality) ReadConnectedSsidAndPhySpeed(string adapterId)
    {
        IntPtr hClient = IntPtr.Zero;
        IntPtr pList = IntPtr.Zero;
        try
        {
            if (NativeMethods.WlanOpenHandle(WlanApiVersion, IntPtr.Zero, out _, out hClient) != 0)
                return (string.Empty, 0, 0);
            if (NativeMethods.WlanEnumInterfaces(hClient, IntPtr.Zero, out pList) != 0)
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
                        var speed = (long)conn.wlanAssociationAttributes.ulTxRate * 1000; // Kbps → bps（官方末位字段，旧 SDK 名 ulLinkSpeed 同位）
                        var signal = (int)conn.wlanAssociationAttributes.wlanSignalQuality;
                        return (ssid, speed, signal);
                    }
                    finally
                    {
                        NativeMethods.WlanFreeMemory(pConn);
                    }
                }
            }
        }
        catch { /* ignore */ }
        finally
        {
            if (pList != IntPtr.Zero) NativeMethods.WlanFreeMemory(pList);
            if (hClient != IntPtr.Zero) _ = NativeMethods.WlanCloseHandle(hClient, IntPtr.Zero);
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
            if (NativeMethods.WlanOpenHandle(WlanApiVersion, IntPtr.Zero, out _, out hClient) != 0)
                return Array.Empty<WifiNearbyItem>();
            if (NativeMethods.WlanEnumInterfaces(hClient, IntPtr.Zero, out pList) != 0)
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
                    // 频段启发式：按 PHY 类型判 5G（4=OFDM/a、8=VHT/ac、9+=ax/be 等；5/6/7 为 2.4G 系），
                    // 与原生层同判据。旧实现误用 flags&1——官方该位是 WLAN_AVAILABLE_NETWORK_CONNECTED，不是频段。
                    bool is5G = false;
                    for (uint p = 0; p < net.uNumberOfPhyTypes && p < 8; p++)
                    {
                        if (net.dot11PhyTypes[p] != 5 && net.dot11PhyTypes[p] != 6 && net.dot11PhyTypes[p] != 7)
                        {
                            is5G = true;
                        }
                    }
                    string band = is5G ? "5G" : "2.4G";
                    result.Add(new WifiNearbyItem(ssid, (int)net.wlanSignalQuality, encrypted, band));
                }
                NativeMethods.WlanFreeMemory(pAvail);
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
            if (pAvail != IntPtr.Zero) NativeMethods.WlanFreeMemory(pAvail);
            if (pList != IntPtr.Zero) NativeMethods.WlanFreeMemory(pList);
            if (hClient != IntPtr.Zero) _ = NativeMethods.WlanCloseHandle(hClient, IntPtr.Zero);
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

    // 官方布局（本机 SDK wlanapi.h 实证）：dot11Ssid(36) + dot11BssType(4) + dot11Bssid(6+2对齐) +
    // dot11PhyType(4) + uDot11PhyIndex(4) + wlanSignalQuality(4) + ulRxRate(4) + ulTxRate(4) = 68 字节。
    // 注意：官方末位字段为 ulTxRate（新版 SDK 由 ulLinkSpeed 更名），偏移一致。
    [StructLayout(LayoutKind.Sequential)]
    private struct WLANAssociationAttributes
    {
        public DOT11_SSID dot11Ssid;
        public uint dot11BssType;
        public DOT11_MAC_ADDRESS dot11Bssid;
        public uint dot11PhyType;
        public uint uDot11PhyIndex;
        public uint wlanSignalQuality;
        public uint ulRxRate;
        public uint ulTxRate;
    }

    // 官方布局：bSecurityEnabled(4) + bOneXEnabled(4) + dot11AuthAlgorithm(4) + dot11CipherAlgorithm(4) = 16 字节。
    [StructLayout(LayoutKind.Sequential)]
    private struct WLANSecurityAttributes
    {
        [MarshalAs(UnmanagedType.Bool)] public bool bSecurityEnabled;
        [MarshalAs(UnmanagedType.Bool)] public bool bOneXEnabled;
        public uint dot11AuthAlgorithm;
        public uint dot11CipherAlgorithm;
    }

    // W2 生死线：官方布局 = isState(4) + wlanConnectionMode(4) + strProfileName(256 WCHAR=512) +
    // wlanAssociationAttributes(68) + wlanSecurityAttributes(16) = 604 字节。
    // 旧实现跳过中间 516B（wlanConnectionMode + strProfileName）导致信号/速度/SSID 全部错位。
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLANConnectionAttributes
    {
        public uint isState;
        public uint wlanConnectionMode;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strProfileName;
        public WLANAssociationAttributes wlanAssociationAttributes;
        public WLANSecurityAttributes wlanSecurityAttributes;
    }

    // W3 生死线：官方布局（wlanapi.h 实证）含 bMorePhyTypes(BOOL 4B)，wlanSignalQuality 为 ULONG 4B，
    // 且不存在 wlanDefaultAuthAlgorithm 字段——旧实现从信号质量起全错位。
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
        [MarshalAs(UnmanagedType.Bool)] public bool bMorePhyTypes;
        public uint wlanSignalQuality;
        [MarshalAs(UnmanagedType.Bool)] public bool bSecurityEnabled;
        public uint dot11DefaultAuthAlgorithm;
        public uint dot11DefaultCipherAlgorithm;
        public uint dwFlags;
        public uint dwReserved;
    }

    private const uint WLAN_AVAILABLE_NETWORK_INCLUDE_ALL_ADHOC_PROFILES = 0x00000001;
    private const uint WLAN_AVAILABLE_NETWORK_INCLUDE_ALL_MANUAL_HIDDEN_PROFILES = 0x00000002;
    private const uint DOT11_AUTH_ALGO_DOT11_AUTH_ALGO_80211_OPEN = 1;
    private const uint DOT11_CIPHER_ALGO_DOT11_CIPHER_NO_ENCRYPTION = 0x00;

    // DOT11_AUTH/CIPHER 算法值（本机 SDK wlantypes.h 实证：WPA3_SAE=9、OWE=10）。
    private const uint DOT11_AUTH_ALGO_WPA_PSK = 4;
    private const uint DOT11_AUTH_ALGO_RSNA_PSK = 7;
    private const uint DOT11_AUTH_ALGO_WPA3_SAE = 9;
    private const uint DOT11_AUTH_ALGO_OWE = 10;
    private const uint DOT11_CIPHER_ALGO_CCMP = 4; // AES

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
            if (NativeMethods.WlanOpenHandle(WlanApiVersion, IntPtr.Zero, out _, out hClient) != 0) return false;
            if (NativeMethods.WlanEnumInterfaces(hClient, IntPtr.Zero, out pList) != 0) return false;
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
            if (pList != IntPtr.Zero) NativeMethods.WlanFreeMemory(pList);
            if (hClient != IntPtr.Zero) _ = NativeMethods.WlanCloseHandle(hClient, IntPtr.Zero);
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
            if (NativeMethods.WlanOpenHandle(WlanApiVersion, IntPtr.Zero, out _, out hClient) != 0) return false;
            if (NativeMethods.WlanEnumInterfaces(hClient, IntPtr.Zero, out pList) != 0) return false;
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
            if (pList != IntPtr.Zero) NativeMethods.WlanFreeMemory(pList);
            if (hClient != IntPtr.Zero) _ = NativeMethods.WlanCloseHandle(hClient, IntPtr.Zero);
        }
    }

    /// <summary>用密码连接到指定 SSID：先探测该网络的认证/加密算法（W5），按需生成
    /// WPA3/WPA2/开放 profile，口令经 DPAPI 加密后以 protected=true 写入（禁止明文落盘），
    /// 再发起连接。返回 true 表示连接请求已发出；最终连接结果由系统异步决定。</summary>
    public static bool ConnectWithPassword(string ssid, string password)
    {
        if (string.IsNullOrEmpty(ssid)) return false;
        IntPtr hClient = IntPtr.Zero;
        IntPtr pList = IntPtr.Zero;
        try
        {
            if (NativeMethods.WlanOpenHandle(WlanApiVersion, IntPtr.Zero, out _, out hClient) != 0) return false;
            if (NativeMethods.WlanEnumInterfaces(hClient, IntPtr.Zero, out pList) != 0) return false;
            var count = Marshal.ReadInt32(pList);
            var cursor = pList + 8;
            var infoSize = Marshal.SizeOf<WlanInterfaceInfoNative>();
            for (int i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WlanInterfaceInfoNative>(cursor);
                cursor += infoSize;

                // W5：探测可见网络的默认认证/加密（依赖 W3 修复后的正确布局）；探测不到按最常见 WPA2PSK/AES 兜底。
                var probe = ProbeNetworkAuthCipher(hClient, ref info.InterfaceGuid, ssid);
                var (auth, cipher) = probe ?? (DOT11_AUTH_ALGO_RSNA_PSK, DOT11_CIPHER_ALGO_CCMP);
                bool isOpen = (auth == DOT11_AUTH_ALGO_DOT11_AUTH_ALGO_80211_OPEN && cipher == DOT11_CIPHER_ALGO_DOT11_CIPHER_NO_ENCRYPTION)
                              || auth == DOT11_AUTH_ALGO_OWE;
                if (!isOpen && string.IsNullOrEmpty(password))
                {
                    return false; // 加密网络缺口令：参数错误，明确失败
                }

                // 主路径：口令 DPAPI（机器域）加密 + protected=true。
                string profileXml = BuildProfileXml(ssid, password, auth, cipher, protectedKey: true);
                uint reasonCode;
                int setResult = WlanSetProfile(hClient, ref info.InterfaceGuid, 0, profileXml, null, true, IntPtr.Zero, out reasonCode);
                if (setResult != 0)
                {
                    // 个别驱动/策略会拒绝系统外加密的 keyMaterial：回退 API 层 protected=false——
                    // 口令只经内存传给 wlansvc、由系统加密落盘，本侧零明文写盘。
                    profileXml = BuildProfileXml(ssid, password, auth, cipher, protectedKey: false);
                    setResult = WlanSetProfile(hClient, ref info.InterfaceGuid, 0, profileXml, null, true, IntPtr.Zero, out reasonCode);
                }
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
            if (pList != IntPtr.Zero) NativeMethods.WlanFreeMemory(pList);
            if (hClient != IntPtr.Zero) _ = NativeMethods.WlanCloseHandle(hClient, IntPtr.Zero);
        }
    }

    /// <summary>探测指定 SSID 在当前可见网络中的默认认证/加密算法（WlanGetAvailableNetworkList）。</summary>
    private static (uint Auth, uint Cipher)? ProbeNetworkAuthCipher(IntPtr hClient, ref Guid interfaceGuid, string ssid)
    {
        IntPtr pAvail = IntPtr.Zero;
        try
        {
            if (WlanGetAvailableNetworkList(hClient, ref interfaceGuid,
                    WLAN_AVAILABLE_NETWORK_INCLUDE_ALL_ADHOC_PROFILES |
                    WLAN_AVAILABLE_NETWORK_INCLUDE_ALL_MANUAL_HIDDEN_PROFILES,
                    IntPtr.Zero, out pAvail, out _) != 0 || pAvail == IntPtr.Zero)
            {
                return null;
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
                if (!string.Equals(DecodeSsid(ssidBytes), ssid, StringComparison.Ordinal)) continue;
                return (net.dot11DefaultAuthAlgorithm, net.dot11DefaultCipherAlgorithm);
            }
            return null;
        }
        catch
        {
            return null; // 探测失败由调用方走兜底算法
        }
        finally
        {
            if (pAvail != IntPtr.Zero) NativeMethods.WlanFreeMemory(pAvail);
        }
    }

    /// <summary>按探测到的认证/加密生成 WLAN profile XML（W5：WPA3/WPA2/WPA/开放，connectionMode=auto，口令 protected）。</summary>
    internal static string BuildProfileXml(string ssid, string password, uint auth, uint cipher, bool protectedKey)
    {
        string hexSsid = BitConverter.ToString(Encoding.UTF8.GetBytes(ssid)).Replace("-", "");
        string safeSsid = SecurityElement.Escape(ssid) ?? ssid;

        string authentication;
        string encryption;
        bool needKey;
        switch (auth)
        {
            case DOT11_AUTH_ALGO_WPA3_SAE:
                authentication = "WPA3SAE"; encryption = "AES"; needKey = true;
                break;
            case DOT11_AUTH_ALGO_OWE:
                authentication = "OWE"; encryption = "AES"; needKey = false;
                break;
            case DOT11_AUTH_ALGO_WPA_PSK:
                authentication = "WPAPSK"; encryption = cipher == DOT11_CIPHER_ALGO_CCMP ? "AES" : "TKIP"; needKey = true;
                break;
            case DOT11_AUTH_ALGO_DOT11_AUTH_ALGO_80211_OPEN when cipher == DOT11_CIPHER_ALGO_DOT11_CIPHER_NO_ENCRYPTION:
                authentication = "open"; encryption = "none"; needKey = false;
                break;
            default:
                // RSNA_PSK 与未知算法：按最常见 WPA2PSK 兜底（与历史行为一致）。
                authentication = "WPA2PSK"; encryption = cipher == DOT11_CIPHER_ALGO_CCMP ? "AES" : "TKIP"; needKey = true;
                break;
        }

        string sharedKey = string.Empty;
        if (needKey)
        {
            string keyMaterial;
            string protectFlag;
            string? encrypted = protectedKey ? ProtectKeyMaterial(password ?? string.Empty) : null;
            if (encrypted is not null)
            {
                keyMaterial = encrypted;       // DPAPI 机器域加密后的十六进制串
                protectFlag = "true";          // 生死线：禁止明文凭据落盘
            }
            else
            {
                // DPAPI 加密失败（罕见）：退回 API 层明文传输，由 wlansvc 加密落盘。
                keyMaterial = SecurityElement.Escape(password ?? string.Empty) ?? string.Empty;
                protectFlag = "false";
            }
            sharedKey = $@"
      <sharedKey>
        <keyType>passPhrase</keyType>
        <protected>{protectFlag}</protected>
        <keyMaterial>{keyMaterial}</keyMaterial>
      </sharedKey>";
        }

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
        <authentication>{authentication}</authentication>
        <encryption>{encryption}</encryption>
        <useOneX>false</useOneX>
      </authEncryption>{sharedKey}
    </security>
  </MSM>
</WLANProfile>";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    private const uint CRYPTPROTECT_LOCAL_MACHINE = 0x4;

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn, string szDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DATA_BLOB pDataOut);

    /// <summary>
    /// 把口令用 DPAPI（机器域：wlansvc 以 LocalSystem 运行，机器域 blob 才能解）加密为
    /// keyMaterial 十六进制串（&lt;protected&gt;true&lt;/protected&gt; 用）。失败返回 null。
    /// </summary>
    private static string? ProtectKeyMaterial(string password)
    {
        IntPtr plainBuf = IntPtr.Zero;
        try
        {
            byte[] plain = Encoding.UTF8.GetBytes(password);
            plainBuf = Marshal.AllocHGlobal(plain.Length);
            Marshal.Copy(plain, 0, plainBuf, plain.Length);
            var input = new DATA_BLOB { cbData = plain.Length, pbData = plainBuf };
            if (!CryptProtectData(ref input, "WLAN Key Material", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_LOCAL_MACHINE, out var output))
            {
                return null;
            }
            try
            {
                var encrypted = new byte[output.cbData];
                Marshal.Copy(output.pbData, encrypted, 0, output.cbData);
                return System.Convert.ToHexString(encrypted);
            }
            finally
            {
                Marshal.FreeHGlobal(output.pbData); // CryptProtectData 输出为 LocalAlloc，FreeHGlobal 即 LocalFree
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (plainBuf != IntPtr.Zero) Marshal.FreeHGlobal(plainBuf);
        }
    }

    /// <summary>检查指定 SSID 是否已保存配置文件。</summary>
    public static bool HasSavedProfile(string ssid)
    {
        if (string.IsNullOrEmpty(ssid)) return false;
        IntPtr hClient = IntPtr.Zero;
        IntPtr pList = IntPtr.Zero;
        try
        {
            if (NativeMethods.WlanOpenHandle(WlanApiVersion, IntPtr.Zero, out _, out hClient) != 0) return false;
            if (NativeMethods.WlanEnumInterfaces(hClient, IntPtr.Zero, out pList) != 0) return false;
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
            if (pList != IntPtr.Zero) NativeMethods.WlanFreeMemory(pList);
            if (hClient != IntPtr.Zero) _ = NativeMethods.WlanCloseHandle(hClient, IntPtr.Zero);
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
            if (NativeMethods.WlanOpenHandle(WlanApiVersion, IntPtr.Zero, out _, out hClient) != 0) return false;
            if (NativeMethods.WlanEnumInterfaces(hClient, IntPtr.Zero, out pList) != 0) return false;
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
            if (pList != IntPtr.Zero) NativeMethods.WlanFreeMemory(pList);
            if (hClient != IntPtr.Zero) _ = NativeMethods.WlanCloseHandle(hClient, IntPtr.Zero);
        }
    }

    /// <summary>获取第一个无线接口的当前连接状态：0=断开,1=已连接,2=关联中,3=搜索中,4=认证中,5=漫游中,6=AdHoc已连接,7=断开中,-1=未知/无适配器。</summary>
    public static int GetInterfaceState()
    {
        IntPtr hClient = IntPtr.Zero;
        IntPtr pList = IntPtr.Zero;
        try
        {
            if (NativeMethods.WlanOpenHandle(WlanApiVersion, IntPtr.Zero, out _, out hClient) != 0) return -1;
            if (NativeMethods.WlanEnumInterfaces(hClient, IntPtr.Zero, out pList) != 0) return -1;
            var count = Marshal.ReadInt32(pList);
            if (count == 0) return -1;
            var info = Marshal.PtrToStructure<WlanInterfaceInfoNative>(pList + 8);
            return info.isState;
        }
        catch { return -1; }
        finally
        {
            if (pList != IntPtr.Zero) NativeMethods.WlanFreeMemory(pList);
            if (hClient != IntPtr.Zero) _ = NativeMethods.WlanCloseHandle(hClient, IntPtr.Zero);
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
            if (pProfiles != IntPtr.Zero) NativeMethods.WlanFreeMemory(pProfiles);
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
    private static extern int WlanQueryInterface(IntPtr hClientHandle, ref Guid pInterfaceGuid, WLAN_INTF_OPCODE OpCode, IntPtr pReserved, out uint pdwDataSize, out IntPtr ppData, IntPtr pWlanOpCodeValueType);
    [DllImport("wlanapi.dll")]
    private static extern int WlanGetAvailableNetworkList(IntPtr hClientHandle, ref Guid pInterfaceGuid, uint dwFlags, IntPtr pReserved, out IntPtr ppAvailableNetworkList, out uint pdwDataSize);
}
