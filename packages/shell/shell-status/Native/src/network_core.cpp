// NetworkCore.dll 实现 — IPHLPAPI 封装（零外部依赖）。
#ifndef NTDDI_VERSION
#define NTDDI_VERSION 0x06010000 // 目标 Windows 7+，保证 Netioapi 定义可见
#endif
#ifndef _WIN32_WINNT
#define _WIN32_WINNT 0x0601
#endif
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif

#include <windows.h>
#include <winsock2.h>
#include <ws2tcpip.h>
#include <iphlpapi.h>
#include <ipmib.h>
#include <netioapi.h>
#include <stdint.h>
#include <cstring>
#include <cwchar>
#include <cstdio>

#include "network_core.h"

#pragma comment(lib, "iphlpapi.lib")
#pragma comment(lib, "ws2_32.lib")

namespace
{
bool IsPrimaryCandidate(const MIB_IF_ROW2& row)
{
    if (row.OperStatus != IfOperStatusUp) return false;
    if (row.Type == 24 /* softwareLoopback */) return false;
    if (row.Type == 131 /* tunnel */) return false;
    // InterfaceAndOperStatusFlags 是位域结构体，按字节视图访问第 2 位(HardwareInterface=bit 2?)？
    // 该位主要区分 SW/HW 接口；为避免位域类型不同，转 BYTE 后判断：跳过 bit 2 为 1 的（不是硬件接口）
    BYTE b = *(const BYTE*)&row.InterfaceAndOperStatusFlags;
    if ((b & 0x04) != 0) return false; // 对应 bit 2
    switch (row.Type)
    {
    case 6:  return true;
    case 71: return true; // IF_TYPE_IEEE80211
    case 237: return true;
    default: return false;
    }
}

// 用 GetBestInterface 找到访问默认路由(0.0.0.0)的真实出口接口，
// 而不是按接口类型/速度打分的"猜测"。这样插着网线时就能正确显示有线而非 WiFi。
bool PickPrimaryByRoute(MIB_IF_TABLE2* table, NET_LUID& outLuid, MIB_IF_ROW2** outRow)
{
    DWORD bestIfIndex = 0;
    if (::GetBestInterface((IPAddr)0, &bestIfIndex) != NO_ERROR)
    {
        return false;
    }
    for (ULONG i = 0; i < table->NumEntries; ++i)
    {
        auto& r = table->Table[i];
        if (r.InterfaceIndex == bestIfIndex)
        {
            outLuid = r.InterfaceLuid;
            if (outRow) *outRow = &r;
            return true;
        }
    }
    return false;
}

bool PickPrimary(MIB_IF_TABLE2* table, NET_LUID& outLuid, MIB_IF_ROW2** outRow)
{
    MIB_IF_ROW2* best = nullptr;
    int bestScore = -1;
    for (ULONG i = 0; i < table->NumEntries; ++i)
    {
        auto& r = table->Table[i];
        if (!IsPrimaryCandidate(r)) continue;
        int score = 0;
        switch (r.Type)
        {
        case 71: score = 300; break;
        case 6:  score = 200; break;
        case 237: score = 100; break;
        default: score = 50; break;
        }
        score += (int)(r.ReceiveLinkSpeed / 10000000LL);
        if (score > bestScore)
        {
            bestScore = score;
            best = &r;
        }
    }
    if (!best) return false;
    outLuid = best->InterfaceLuid;
    if (outRow) *outRow = best;
    return true;
}

void CopyAsciiToUtf16(const char* ascii, wchar_t* out, int cch)
{
    if (!out || cch <= 0) return;
    int i = 0;
    while (ascii && ascii[i] && (i + 1) < cch)
    {
        out[i] = (wchar_t)(unsigned char)ascii[i];
        ++i;
    }
    out[i] = L'\0';
}

void CopyWchar(const WCHAR* src, wchar_t* dst, int cch)
{
    if (!dst || cch <= 0) return;
    if (cch == 1) { dst[0] = L'\0'; return; }
    if (!src) { dst[0] = L'\0'; return; }
    wcsncpy_s(dst, (size_t)cch, src, _TRUNCATE);
}

const wchar_t* TypeName(IFTYPE type)
{
    switch (type)
    {
    case 6:  return L"Ethernet";
    case 71: return L"WLAN";
    case 237: return L"WWAN";
    case 24: return L"Loopback";
    case 131: return L"Tunnel";
    default: return L"Other";
    }
}

bool FetchFirstIPv4(const NET_LUID& luid, wchar_t* buf, int cch)
{
    if (!buf || cch <= 0) return false;
    buf[0] = L'\0';
    MIB_UNICASTIPADDRESS_TABLE* tbl = nullptr;
    ADDRESS_FAMILY family = AF_INET;
    ULONG hr = GetUnicastIpAddressTable(family, &tbl);
    if (hr != NO_ERROR) return false;
    bool found = false;
    for (ULONG i = 0; i < tbl->NumEntries; ++i)
    {
        auto& a = tbl->Table[i];
        if (a.InterfaceLuid.Value != luid.Value) continue;
        if (a.Address.si_family != AF_INET) continue;
        auto& sin = a.Address.Ipv4.sin_addr;
        unsigned char* b = (unsigned char*)&sin;
        // 跳过 APIPA 自动私有地址(169.254.0.0/16)，这类地址无实际上网意义。
        if (b[0] == 169 && b[1] == 254) continue;
        char ipAscii[64] = {};
        _snprintf_s(ipAscii, sizeof(ipAscii) / sizeof(ipAscii[0]), _TRUNCATE, "%d.%d.%d.%d",
            (int)b[0], (int)b[1], (int)b[2], (int)b[3]);
        CopyAsciiToUtf16(ipAscii, buf, cch);
        found = true;
        break;
    }
    FreeMibTable(tbl);
    return found;
}

// ============================================================================
// 阶段 4 事件驱动：NotifyAddrChange / NotifyRouteChange 网络变更监听
// ============================================================================
// 说明：
//   * 地址/路由任一变化（插拔网线、切换 WiFi 热点、IP 变更）时立即触发一次
//     NetChangeCallback（C# 层用其调 PollNow），替代"至多等一个轮询周期"。
//   * NotifyAddrChange/NotifyRouteChange 每进程同一时刻各自只能挂一个监听，
//     且都是"先注册、返回句柄、句柄被触发再重新挂"的异步模型，因此各用一条
//     专用后台线程串行 re-arm；两条互不干扰。
//   * 回调由后台线程触发，须与音频 FireChange 一样线程安全；业务侧需自行处理
//     （C# 端经 PollNow 回抛 UI 线程）。

using NetChangeCallback = void(__stdcall*)();

static CRITICAL_SECTION g_netLock;
static bool g_netLockInit = false;
static volatile NetChangeCallback g_netCb = nullptr; // 仅在 g_netLock 保护下读写
static HANDLE g_netShutdown = nullptr;               // 停止信号（创建线程前赋值）
static HANDLE g_addrThread = nullptr;
static HANDLE g_routeThread = nullptr;

static void EnsureNetLock()
{
    if (!g_netLockInit)
    {
        InitializeCriticalSection(&g_netLock);
        g_netLockInit = true;
    }
}

static void FireNetChange()
{
    EnsureNetLock();
    NetChangeCallback cb = nullptr;
    EnterCriticalSection(&g_netLock);
    cb = g_netCb;
    LeaveCriticalSection(&g_netLock);
    if (cb) cb();
}

// 网络地址/路由变更监听：反复调用 waitOnce() → 触发则回调，否则（停止信号）退出循环。
// 两条各用一函数，避免模板 + 调用约定在 MSVC 下的歧义。waitOnce 先注册返回句柄，
// 注册失败（可能重复挂接）退避重试，直到收到停止信号。
static DWORD WINAPI NetAddrWatchLoop(LPVOID)
{
    for (;;)
    {
        HANDLE h = nullptr;
        DWORD r = ::NotifyAddrChange(&h, nullptr);
        if (r != NO_ERROR)
        {
            if (g_netShutdown && WaitForSingleObject(g_netShutdown, 1000) == WAIT_OBJECT_0)
                break;
            continue;
        }
        if (!h)
        {
            if (g_netShutdown && WaitForSingleObject(g_netShutdown, 250) == WAIT_OBJECT_0)
                break;
            continue;
        }
        HANDLE waiters[2] = { h, g_netShutdown };
        DWORD wr = WaitForMultipleObjects(2, waiters, FALSE, INFINITE);
        CloseHandle(h);
        if (wr == WAIT_OBJECT_0)
        {
            FireNetChange();
        }
        else
        {
            break; // g_netShutdown 被置位 → 停止监听
        }
    }
    return 0;
}

static DWORD WINAPI NetRouteWatchLoop(LPVOID)
{
    for (;;)
    {
        HANDLE h = nullptr;
        DWORD r = ::NotifyRouteChange(&h, nullptr);
        if (r != NO_ERROR)
        {
            if (g_netShutdown && WaitForSingleObject(g_netShutdown, 1000) == WAIT_OBJECT_0)
                break;
            continue;
        }
        if (!h)
        {
            if (g_netShutdown && WaitForSingleObject(g_netShutdown, 250) == WAIT_OBJECT_0)
                break;
            continue;
        }
        HANDLE waiters[2] = { h, g_netShutdown };
        DWORD wr = WaitForMultipleObjects(2, waiters, FALSE, INFINITE);
        CloseHandle(h);
        if (wr == WAIT_OBJECT_0)
        {
            FireNetChange();
        }
        else
        {
            break; // g_netShutdown 被置位 → 停止监听
        }
    }
    return 0;
}
} // namespace

extern "C" int __stdcall Net_ReadPrimaryCounters(
    unsigned int* ifIndex,
    wchar_t* name, int nameCch,
    wchar_t* typeName, int typeCch,
    wchar_t* ipAddr, int ipCch,
    unsigned long long* rxBytes,
    unsigned long long* txBytes,
    int* ok)
{
    if (!ifIndex || !name || !typeName || !ipAddr || !rxBytes || !txBytes || !ok)
    {
        return (int)E_POINTER;
    }
    *ifIndex = 0; *rxBytes = 0; *txBytes = 0; *ok = 0;
    if (nameCch > 0) name[0] = L'\0';
    if (typeCch > 0) typeName[0] = L'\0';
    if (ipCch > 0) ipAddr[0] = L'\0';

    MIB_IF_TABLE2* ifTable = nullptr;
    ULONG nhr = GetIfTable2(&ifTable);
    if (nhr != NO_ERROR || !ifTable)
    {
        return (int)HRESULT_FROM_WIN32(nhr);
    }

    NET_LUID luid{};
    MIB_IF_ROW2* row = nullptr;
    bool havePrimary = PickPrimaryByRoute(ifTable, luid, &row);
    if (!havePrimary)
    {
        // 路由出口找不到（无外网/异常）时，回退到旧的类型打分选择。
        havePrimary = PickPrimary(ifTable, luid, &row);
    }
    if (!havePrimary)
    {
        FreeMibTable(ifTable);
        return 0;
    }

    MIB_IF_ROW2 copy = *row;
    ULONG hr2 = GetIfEntry2(&copy);
    if (hr2 != NO_ERROR)
    {
        FreeMibTable(ifTable);
        return (int)HRESULT_FROM_WIN32(hr2);
    }

    *ifIndex = copy.InterfaceIndex;
    *rxBytes = (unsigned long long)copy.InOctets;
    *txBytes = (unsigned long long)copy.OutOctets;
    CopyWchar(copy.Description, name, nameCch);
    CopyWchar(TypeName(copy.Type), typeName, typeCch);
    FetchFirstIPv4(luid, ipAddr, ipCch);
    *ok = 1;

    FreeMibTable(ifTable);
    return 0;
}

// 启用/更新网络变更回调。传入非空回调时启动后台监听线程（幂等：已有则只换回调）；
// 传 null 仅解除回调、不中断监听。返回 0。
extern "C" int __stdcall Net_SetChangeCallback(NetChangeCallback cb)
{
    EnsureNetLock();
    EnterCriticalSection(&g_netLock);
    bool threadsUp = (g_netShutdown != nullptr);
    g_netCb = cb;
    if (threadsUp || !cb)
    {
        LeaveCriticalSection(&g_netLock);
        return 0; // 已启动或仅换回调
    }

    // 首次启动：先建停止信号并赋值，再启动监听线程——确保线程读到非空 g_netShutdown，
    // 避免 WaitForMultipleObjects 里出现 null 句柄返回 WAIT_FAILED。
    HANDLE stop = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!stop)
    {
        LeaveCriticalSection(&g_netLock);
        return static_cast<int>(HRESULT_FROM_WIN32(GetLastError()));
    }
    g_netShutdown = stop; // 先赋值，再建线程
    HANDLE addr = CreateThread(nullptr, 0, NetAddrWatchLoop, nullptr, 0, nullptr);
    HANDLE route = CreateThread(nullptr, 0, NetRouteWatchLoop, nullptr, 0, nullptr);
    if (!addr || !route)
    {
        // 任一创建失败：回滚已建线程与信号（SetEvent 让已启动线程尽快退出）。
        if (addr) SetEvent(stop);
        if (route) SetEvent(stop);
        if (addr) { WaitForSingleObject(addr, 1000); CloseHandle(addr); }
        if (route) { WaitForSingleObject(route, 1000); CloseHandle(route); }
        CloseHandle(stop);
        g_netShutdown = nullptr;
        LeaveCriticalSection(&g_netLock);
        return static_cast<int>(HRESULT_FROM_WIN32(GetLastError()));
    }
    g_addrThread = addr;
    g_routeThread = route;
    LeaveCriticalSection(&g_netLock);
    return 0;
}

// 停止网络变更监听并释放后台线程与回调。返回 0。
extern "C" int __stdcall Net_ShutdownChangeNotify(void)
{
    EnsureNetLock();
    EnterCriticalSection(&g_netLock);
    g_netCb = nullptr;
    HANDLE stop = g_netShutdown;
    HANDLE addr = g_addrThread;
    HANDLE route = g_routeThread;
    g_netShutdown = nullptr;
    g_addrThread = nullptr;
    g_routeThread = nullptr;
    if (stop) SetEvent(stop);
    LeaveCriticalSection(&g_netLock);

    if (addr) { if (stop) WaitForSingleObject(addr, 1500); CloseHandle(addr); }
    if (route) { if (stop) WaitForSingleObject(route, 1500); CloseHandle(route); }
    if (stop) CloseHandle(stop);
    return 0;
}
