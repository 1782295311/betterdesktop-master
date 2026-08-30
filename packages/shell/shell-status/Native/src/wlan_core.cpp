// WlanCore.dll 实现 — 纯 wlanapi 封装（零业务逻辑、零 UI）。
// 线程模型：每次调用独立临时持有 wlan handle（WlanOpenHandle/WlanCloseHandle），
// 不做全局状态，避免线程/生命周期竞态。扫描结果在调用间用静态快照缓存。
// winsock2 必须先于 windows.h 引入，以获得 IP/MAC（GetAdaptersAddresses）。
#include <winsock2.h>
#include <ws2tcpip.h>
#include <iphlpapi.h>
#include <windows.h>
#include <wlanapi.h>
#include <winerror.h>
#include <objbase.h>
#include <cstdint>
#include <cwchar>
#include <cstring>
#include <vector>

#include "wlan_core.h"

#pragma comment(lib, "wlanapi.lib")
#pragma comment(lib, "iphlpapi.lib")
#pragma comment(lib, "ws2_32.lib")

// IF_TYPE_IEEE80211（71）在不同 SDK 中宏名不稳定，使用数值常量避免找不到。
#ifndef IF_TYPE_IEEE80211
#define IF_TYPE_IEEE80211 71
#endif

namespace
{
constexpr DWORD kWlanVersion = 2;

// 获取 wlan handle。失败返回 HRESULT。
HRESULT OpenWlan(HANDLE* out)
{
    DWORD negotiated = 0;
    auto hr = ::WlanOpenHandle(kWlanVersion, nullptr, &negotiated, out);
    return (hr == ERROR_SUCCESS) ? S_OK : HRESULT_FROM_WIN32(hr);
}

void CloseWlan(HANDLE h)
{
    if (h)
    {
        ::WlanCloseHandle(h, nullptr);
    }
}

// WLAN_INTERFACE_INFO_LIST 首元素指针。
const WLAN_INTERFACE_INFO* FirstInterfaceInfo(const WLAN_INTERFACE_INFO_LIST* list)
{
    return reinterpret_cast<const WLAN_INTERFACE_INFO*>(&list->InterfaceInfo[0]);
}

// 查找第一个已连接（wlan_interface_state_connected）的无线接口，返回其 GUID。
// 找不到已连接接口时，返回第一个接口 GUID（用于扫描）。
bool FindTargetGuid(const WLAN_INTERFACE_INFO_LIST* list, bool requireConnected, GUID* out)
{
    GUID first{};
    bool any = false;
    auto* it = FirstInterfaceInfo(list);
    for (DWORD i = 0; i < list->dwNumberOfItems; ++i)
    {
        if (!any)
        {
            first = it->InterfaceGuid;
            any = true;
        }
        if (it->isState == wlan_interface_state_connected)
        {
            *out = it->InterfaceGuid;
            return true;
        }
        ++it;
    }
    if (!requireConnected && any)
    {
        *out = first;
        return true;
    }
    return false;
}
} // namespace

extern "C" int __stdcall Wlan_ReadConnected(
    int* connected,
    wchar_t* ssid, int ssidCch,
    wchar_t* desc, int descCch,
    wchar_t* ipv4, int ipv4Cch,
    wchar_t* mac, int macCch,
    unsigned long long* linkSpeedBps)
{
    if (!connected)
    {
        return static_cast<int>(E_POINTER);
    }
    *connected = 0;
    if (ssid && ssidCch > 0) ssid[0] = L'\0';
    if (desc && descCch > 0) desc[0] = L'\0';
    if (ipv4 && ipv4Cch > 0) ipv4[0] = L'\0';
    if (mac && macCch > 0) mac[0] = L'\0';
    if (linkSpeedBps) *linkSpeedBps = 0;

    HANDLE h = nullptr;
    HRESULT hr = OpenWlan(&h);
    if (FAILED(hr)) return hr;

    WLAN_INTERFACE_INFO_LIST* iflist = nullptr;
    hr = ::WlanEnumInterfaces(h, nullptr, &iflist);
    if (SUCCEEDED(hr) && iflist)
    {
        GUID target;
        if (FindTargetGuid(iflist, /*requireConnected=*/true, &target))
        {
            // 读连接属性拿真实 SSID + 链路速度。
            WLAN_CONNECTION_ATTRIBUTES* conn = nullptr;
            DWORD connSize = 0;
            auto qhr = ::WlanQueryInterface(
                h, &target, wlan_intf_opcode_current_connection,
                nullptr, &connSize, reinterpret_cast<PVOID*>(&conn), nullptr);
            if (qhr == ERROR_SUCCESS && conn)
            {
                const auto& asso = conn->wlanAssociationAttributes;
                const auto& dot11 = asso.dot11Ssid;
                if (dot11.uSSIDLength > 0 && ssid && ssidCch > 0)
                {
                    // SSID 是字节串（可能无 NUL），逐字节转 UTF-16。
                    auto len = dot11.uSSIDLength > WLANCORE_MAX_SSID_CHARS - 1
                                   ? WLANCORE_MAX_SSID_CHARS - 1
                                   : dot11.uSSIDLength;
                    for (DWORD i = 0; i < len; ++i)
                    {
                        ssid[i] = static_cast<wchar_t>(dot11.ucSSID[i]);
                    }
                    ssid[len] = L'\0';
                }
                if (linkSpeedBps)
                {
                    // ulRxRate 单位：Kbps → bps。
                    *linkSpeedBps = static_cast<unsigned long long>(asso.ulRxRate) * 1000ULL;
                }
                *connected = 1;
                ::WlanFreeMemory(conn);
            }

            // 适配器描述。
            auto* it = FirstInterfaceInfo(iflist);
            for (DWORD i = 0; i < iflist->dwNumberOfItems; ++i)
            {
                if (it->InterfaceGuid == target && desc && descCch > 0)
                {
                    wcsncpy_s(desc, descCch, it->strInterfaceDescription, _TRUNCATE);
                    break;
                }
                ++it;
            }
        }
        ::WlanFreeMemory(iflist);
    }

    // 适配器 IPv4/MAC：通过 GetAdaptersAddresses 匹配已连接接口。
    if (*connected)
    {
        IP_ADAPTER_ADDRESSES* buf = nullptr;
        ULONG size = 16 * 1024;
        for (int attempt = 0; attempt < 3; ++attempt)
        {
            buf = static_cast<IP_ADAPTER_ADDRESSES*>(::HeapAlloc(::GetProcessHeap(), 0, size));
            if (!buf) break;
            auto r = ::GetAdaptersAddresses(AF_UNSPEC, 0, nullptr, buf, &size);
            if (r == ERROR_BUFFER_OVERFLOW)
            {
                ::HeapFree(::GetProcessHeap(), 0, buf);
                buf = nullptr;
                continue;
            }
            if (r == ERROR_SUCCESS)
            {
                for (auto* a = buf; a; a = a->Next)
                {
                    if (a->OperStatus != IfOperStatusUp) continue;
                    if (a->IfType == IF_TYPE_IEEE80211)
                    {
                        if (ipv4 && ipv4Cch > 0)
                        {
                            for (auto* u = a->FirstUnicastAddress; u; u = u->Next)
                            {
                                if (u->Address.lpSockaddr->sa_family == AF_INET)
                                {
                                    DWORD v4len = static_cast<DWORD>(ipv4Cch);
                                    WSAAddressToStringW(u->Address.lpSockaddr,
                                        static_cast<DWORD>(u->Address.iSockaddrLength),
                                        nullptr, ipv4, &v4len);
                                    break;
                                }
                            }
                        }
                        if (mac && macCch > 0)
                        {
                            wchar_t macBuf[WLANCORE_MAX_MAC_CHARS];
                            swprintf_s(macBuf, L"%02X:%02X:%02X:%02X:%02X:%02X",
                                a->PhysicalAddress[0], a->PhysicalAddress[1],
                                a->PhysicalAddress[2], a->PhysicalAddress[3],
                                a->PhysicalAddress[4], a->PhysicalAddress[5]);
                            wcsncpy_s(mac, macCch, macBuf, _TRUNCATE);
                        }
                        break;
                    }
                }
            }
            ::HeapFree(::GetProcessHeap(), 0, buf);
            break;
        }
    }

    CloseWlan(h);
    return static_cast<int>(S_OK);
}

// 已扫描的附近网络快照（静态缓存）。用临界区保护，供 Collect 写、GetItem 读。
static std::vector<WLAN_AVAILABLE_NETWORK> g_scanResults;
static bool g_scanValid = false;
static CRITICAL_SECTION g_scanLock;
static bool g_scanLockInit = false;

inline void EnsureScanLock()
{
    if (!g_scanLockInit)
    {
        InitializeCriticalSection(&g_scanLock);
        g_scanLockInit = true;
    }
}

// 触发扫描（异步，不阻塞）：仅向无线接口发出 WlanScan 请求后立即返回。
// 结果由 Wlan_ScanCollect 在稍后（C# 侧延迟读取）汇总到 g_scanResults。
extern "C" int __stdcall Wlan_ScanStart(int* networkCount)
{
    if (!networkCount) return static_cast<int>(E_POINTER);
    *networkCount = 0;

    HANDLE h = nullptr;
    HRESULT hr = OpenWlan(&h);
    if (FAILED(hr)) return hr;

    WLAN_INTERFACE_INFO_LIST* iflist = nullptr;
    hr = ::WlanEnumInterfaces(h, nullptr, &iflist);
    if (SUCCEEDED(hr) && iflist)
    {
        GUID target;
        if (FindTargetGuid(iflist, /*requireConnected=*/false, &target))
        {
            // 只发扫描请求，不等扫描结束，避免把调用线程（如 UI）拖住数百毫秒。
            ::WlanScan(h, &target, nullptr, nullptr, nullptr);
        }
        ::WlanFreeMemory(iflist);
    }

    CloseWlan(h);
    return SUCCEEDED(hr) ? static_cast<int>(S_OK) : hr;
}

// 汇总附近网络结果。应在发完 Wlan_ScanStart 并短暂等待后调用。
// 加锁写 g_scanResults，返回网络数量。
extern "C" int __stdcall Wlan_ScanCollect(int* networkCount)
{
    if (!networkCount) return static_cast<int>(E_POINTER);
    *networkCount = 0;
    EnsureScanLock();

    HANDLE h = nullptr;
    HRESULT hr = OpenWlan(&h);
    if (FAILED(hr)) return hr;

    WLAN_INTERFACE_INFO_LIST* iflist = nullptr;
    hr = ::WlanEnumInterfaces(h, nullptr, &iflist);
    if (SUCCEEDED(hr) && iflist)
    {
        GUID target;
        if (FindTargetGuid(iflist, /*requireConnected=*/false, &target))
        {
            WLAN_AVAILABLE_NETWORK_LIST* avail = nullptr;
            auto shr = ::WlanGetAvailableNetworkList(
                h, &target,
                WLAN_AVAILABLE_NETWORK_INCLUDE_ALL_ADHOC_PROFILES |
                WLAN_AVAILABLE_NETWORK_INCLUDE_ALL_MANUAL_HIDDEN_PROFILES,
                nullptr, &avail);
            if (shr == ERROR_SUCCESS && avail)
            {
                EnterCriticalSection(&g_scanLock);
                g_scanResults.clear();
                const auto* base = avail->Network;
                g_scanResults.reserve(avail->dwNumberOfItems);
                for (DWORD i = 0; i < avail->dwNumberOfItems; ++i)
                {
                    g_scanResults.push_back(base[i]);
                }
                g_scanValid = true;
                *networkCount = static_cast<int>(g_scanResults.size());
                LeaveCriticalSection(&g_scanLock);
                ::WlanFreeMemory(avail);
            }
        }
        ::WlanFreeMemory(iflist);
    }

    CloseWlan(h);
    return SUCCEEDED(hr) ? static_cast<int>(S_OK) : hr;
}

extern "C" int __stdcall Wlan_ScanGetItem(
    int index,
    wchar_t* ssid, int ssidCch,
    int* signalQuality,
    int* encrypted,
    int* is5G)
{
    EnsureScanLock();
    {
        EnterCriticalSection(&g_scanLock);
        if (!g_scanValid || index < 0 || static_cast<size_t>(index) >= g_scanResults.size())
        {
            LeaveCriticalSection(&g_scanLock);
            return static_cast<int>(E_BOUNDS);
        }
    }
    if (signalQuality) *signalQuality = 0;
    if (encrypted) *encrypted = 0;
    if (is5G) *is5G = 0;

    WLAN_AVAILABLE_NETWORK net{};
    EnterCriticalSection(&g_scanLock);
    net = g_scanResults[static_cast<size_t>(index)];
    LeaveCriticalSection(&g_scanLock);

    const auto& dot11 = net.dot11Ssid;
    if (ssid && ssidCch > 0)
    {
        ssid[0] = L'\0';
        auto len = dot11.uSSIDLength > static_cast<DWORD>(ssidCch) - 1
                       ? static_cast<DWORD>(ssidCch) - 1
                       : dot11.uSSIDLength;
        for (DWORD i = 0; i < len; ++i)
        {
            ssid[i] = static_cast<wchar_t>(dot11.ucSSID[i]);
        }
        ssid[len] = L'\0';
    }
    if (signalQuality) *signalQuality = net.wlanSignalQuality;
    if (encrypted) *encrypted = (net.dot11DefaultAuthAlgorithm != DOT11_AUTH_ALGO_80211_OPEN ||
                                 net.dot11DefaultCipherAlgorithm != DOT11_CIPHER_ALGO_NONE) ? 1 : 0;
    if (is5G)
    {
        // 通过 PHY 类型近似判断频段：80211a/80211ac/80211ax 为 5GHz。
        bool five = false;
        for (DWORD p = 0; p < net.uNumberOfPhyTypes && p < 8; ++p)
        {
            // DOT11_PHY_TYPE：5=HR/DSSS,6=ERP,7=HT 为 2.4G；4=OFDM(a),8=VHT(ac),9/10/11/12 为 5G。
            auto phy = net.dot11PhyTypes[p];
            if (phy == 5 || phy == 6 || phy == 7) { /* 2.4G */ }
            else { five = true; }
        }
        *is5G = five ? 1 : 0;
    }
    return static_cast<int>(S_OK);
}