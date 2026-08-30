// BetterDesktop.Shell.Status — WlanCore.dll 的 C# Interop 薄封装（纯转发，无业务逻辑）。
// 通过 NativeLoader 装载 natives/WlanCore.dll，转发 Wlan_* 导出函数。
// DLL 缺失/加载失败时 IsAvailable=false，由调用方降级到托管实现。
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace BetterDesktop.Shell.Status.Native;

/// <summary>当前 Wi‑Fi 连接信息（C++ 层给出的原生快照）。</summary>
public readonly record struct WlanConnectedNative(
    bool Connected,
    string Ssid,
    string AdapterName,
    string? Ipv4,
    string? Mac,
    long LinkSpeedBps);

/// <summary>附近扫描到的单个网络。</summary>
public readonly record struct WlanNearbyNative(
    string Ssid,
    int SignalQuality,
    bool Encrypted,
    bool Is5G);

/// <summary>WlanCore.dll 的薄封装。仅转发，不做任何业务判断。</summary>
public static class WlanCoreNative
{
    private delegate int WlanReadConnected(
        out int connected,
        [Out] ushort[] ssid, int ssidCch,
        [Out] ushort[] desc, int descCch,
        [Out] ushort[] ipv4, int ipv4Cch,
        [Out] ushort[] mac, int macCch,
        out long linkSpeedBps);
    private delegate int WlanScanStart(out int networkCount);
    private delegate int WlanScanCollect(out int networkCount);
    private delegate int WlanScanGetItem(int index, [Out] ushort[] ssid, int ssidCch,
        out int signalQuality, out int encrypted, out int is5G);

    private static readonly WlanReadConnected? _read;
    private static readonly WlanScanStart? _scanStart;
    private static readonly WlanScanCollect? _scanCollect;
    private static readonly WlanScanGetItem? _scanGet;

    static WlanCoreNative()
    {
        _read = NativeLoader.GetExport<WlanReadConnected>("WlanCore.dll", "Wlan_ReadConnected");
        _scanStart = NativeLoader.GetExport<WlanScanStart>("WlanCore.dll", "Wlan_ScanStart");
        _scanCollect = NativeLoader.GetExport<WlanScanCollect>("WlanCore.dll", "Wlan_ScanCollect");
        _scanGet = NativeLoader.GetExport<WlanScanGetItem>("WlanCore.dll", "Wlan_ScanGetItem");
    }

    /// <summary>原生 DLL 是否成功装载。</summary>
    public static bool IsAvailable => _read is not null && _scanStart is not null && _scanCollect is not null && _scanGet is not null;

    /// <summary>读当前无线连接。失败时返回 Connected=false。</summary>
    public static WlanConnectedNative ReadConnected(int maxSsid = 64)
    {
        if (_read is null)
        {
            return default;
        }
        try
        {
            var ssid = new ushort[64];
            var desc = new ushort[128];
            var ipv4 = new ushort[48];
            var mac = new ushort[32];
            int hr = _read(out int connected, ssid, ssid.Length, desc, desc.Length,
                ipv4, ipv4.Length, mac, mac.Length, out long linkSpeedBps);
            if (hr != 0)
            {
                return default;
            }
            return new WlanConnectedNative(
                connected != 0,
                Trim(ssid),
                Trim(desc),
                Trim(ipv4),
                Trim(mac),
                linkSpeedBps);
        }
        catch
        {
            return default;
        }
    }

    /// <summary>附近扫描（异步）：先触发异步扫描，稍等让结果落地，再汇总读取。
    /// 非阻塞：仅在 await 等待期间让出调用线程。</summary>
    public static async Task<WlanNearbyNative[]> ScanNearbyAsync()
    {
        if (_scanStart is null || _scanCollect is null || _scanGet is null)
        {
            return Array.Empty<WlanNearbyNative>();
        }
        try
        {
            // 1) 触发扫描（原生层 Wlan_ScanStart 已改为非阻塞、仅发请求）。
            int hr = _scanStart(out _);
            if (hr != 0)
            {
                return Array.Empty<WlanNearbyNative>();
            }
            // 2) 留出扫描落地的窗口，期间不占用调用线程。
            await Task.Delay(1100).ConfigureAwait(false);
            // 3) 汇总结果到原生缓存。
            hr = _scanCollect(out int count);
            if (hr != 0 || count <= 0 || count > 128)
            {
                return Array.Empty<WlanNearbyNative>();
            }
            var result = new WlanNearbyNative[count];
            for (int i = 0; i < count; i++)
            {
                var ssid = new ushort[64];
                if (_scanGet(i, ssid, ssid.Length, out int sig, out int enc, out int five) != 0)
                {
                    continue;
                }
                result[i] = new WlanNearbyNative(Trim(ssid), sig, enc != 0, five != 0);
            }
            return result;
        }
        catch
        {
            return Array.Empty<WlanNearbyNative>();
        }
    }

    private static string Trim(ushort[] buf)
    {
        var sb = new StringBuilder();
        foreach (var c in buf)
        {
            if (c == '\0')
            {
                break;
            }
            sb.Append((char)c);
        }
        return sb.ToString();
    }
}