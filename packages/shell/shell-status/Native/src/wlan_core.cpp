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
// SSID 是任意字节串（可能无 NUL）：优先按 UTF-8 严格解码（MB_ERR_INVALID_CHARS），
// 非法字节序列时按 Latin-1 逐字节映射，与托管层 DecodeSsid 语义一致（W1：禁止逐字节 static_cast<wchar_t>）。
void DecodeSsidToWide(const unsigned char* bytes, DWORD len, wchar_t* out, int outCch)
{
    if (!out || outCch <= 0) return;
    out[0] = L'\0';
    if (!bytes || len == 0) return;
    if (len > static_cast<DWORD>(outCch) - 1)
    {
        len = static_cast<DWORD>(outCch) - 1;
    }
    while (len > 0 && bytes[len - 1] == 0) --len;
    if (len == 0) return;
    int wlen = ::MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS,
        reinterpret_cast<const char*>(bytes), static_cast<int>(len), out, outCch - 1);
    if (wlen <= 0)
    {
        for (DWORD i = 0; i < len; ++i)
        {
            out[i] = static_cast<wchar_t>(bytes[i]);
        }
        wlen = static_cast<int>(len);
    }
    out[wlen] = L'\0';
}
} // namespace

extern "C" int __stdcall Wlan_ReadConnected(
    int* connected,
    wchar_t* ssid, int ssidCch,
    wchar_t* desc, int descCch,
    wchar_t* ipv4, int ipv4Cch,
    wchar_t* mac, int macCch,
    unsigned long long* linkSpeedBps,
    int* signalQuality)
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
    if (signalQuality) *signalQuality = 0;

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
                    DecodeSsidToWide(dot11.ucSSID, dot11.uSSIDLength, ssid, ssidCch);
                }
                if (linkSpeedBps)
                {
                    // 修复（G2n）：与托管层 ReadConnectedSsidAndPhySpeed 统一取 ulTxRate（发送速率，
                    // 与 Windows 设置显示口径一致）；旧实现取 ulRxRate，两条降级路径口径不一致。
                    // 单位：Kbps → bps。
                    *linkSpeedBps = static_cast<unsigned long long>(asso.ulTxRate) * 1000ULL;
                }
                if (signalQuality)
                {
                    // W2b：连接属性直读信号质量（0-100），主路径不再依赖托管层补充。
                    *signalQuality = static_cast<int>(asso.wlanSignalQuality);
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
// 修复（S1）：旧实现用裸全局 flag 做 check-then-init，多线程并发首次进入会对同一
// CRITICAL_SECTION 双重 InitializeCriticalSection（UB）。改为函数内 static 结构体，
// 由 MSVC magic statics 保证跨线程恰好初始化一次（含析构 DeleteCriticalSection）。
struct ScanSnapshot
{
    CRITICAL_SECTION lock;
    std::vector<WLAN_AVAILABLE_NETWORK> results;
    bool valid = false;

    ScanSnapshot() { ::InitializeCriticalSection(&lock); }
    ~ScanSnapshot() { ::DeleteCriticalSection(&lock); }
};

ScanSnapshot& ScanState()
{
    static ScanSnapshot s;
    return s;
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

    ScanSnapshot& snap = ScanState();

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
                EnterCriticalSection(&snap.lock);
                snap.results.clear();
                const auto* base = avail->Network;
                snap.results.reserve(avail->dwNumberOfItems);
                for (DWORD i = 0; i < avail->dwNumberOfItems; ++i)
                {
                    snap.results.push_back(base[i]);
                }
                snap.valid = true;
                *networkCount = static_cast<int>(snap.results.size());
                LeaveCriticalSection(&snap.lock);
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
    ScanSnapshot& snap = ScanState();

    // 修复（S2）：旧实现"第一段锁内校验 → 退锁 → 第二段锁内取用"，两段之间 Collect
    // 可能 clear/缩短 vector，第二次下标取用越界（UB）。改为单次持锁完成校验+拷贝快照。
    WLAN_AVAILABLE_NETWORK net{};
    {
        EnterCriticalSection(&snap.lock);
        if (!snap.valid || index < 0 || static_cast<size_t>(index) >= snap.results.size())
        {
            LeaveCriticalSection(&snap.lock);
            return static_cast<int>(E_BOUNDS);
        }
        net = snap.results[static_cast<size_t>(index)];
        LeaveCriticalSection(&snap.lock);
    }

    if (signalQuality) *signalQuality = 0;
    if (encrypted) *encrypted = 0;
    if (is5G) *is5G = 0;

    const auto& dot11 = net.dot11Ssid;
    if (ssid && ssidCch > 0)
    {
        DecodeSsidToWide(dot11.ucSSID, dot11.uSSIDLength, ssid, ssidCch);
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