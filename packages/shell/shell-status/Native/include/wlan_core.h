// WlanCore.dll — 底层原生 API 封装工（纯 C 导出，零 UI、零业务逻辑）。
// 封装 wlanapi：当前连接属性 + 附近网络扫描。所有失败返回 HRESULT（0=成功），
// 字符串通过调用方提供的缓冲区传出。本 DLL 不做凭据交互、不主动连接/断开。
#pragma once

#ifdef __cplusplus
extern "C" {
#endif

// 最大缓冲区长度（SSID/适配器名/IP/MAC）。
#define WLANCORE_MAX_SSID_CHARS  64
#define WLANCORE_MAX_DESC_CHARS  128
#define WLANCORE_MAX_IP_CHARS    48
#define WLANCORE_MAX_MAC_CHARS   32

// 读当前无线连接信息。wlanapi 无无线网卡或未连接时返回 S_OK 但 *connected=0。
//   connected     输出：1=已连接，0=未连接/无无线网卡
//   ssid/ssidCch  输出：当前连接 SSID（UTF-16，可为空缓冲区+0）
//   desc/descCch  输出：无线适配器描述（用于 UI 展示网卡名）
//   ipv4/ipv4Cch  输出：无线适配器 IPv4 地址
//   mac/macCch    输出：无线适配器 MAC（"AA:BB:.."）
//   linkSpeedBps  输出：当前链路速度（bps，未知时 0）
//   所有字符串缓冲区由调用方分配；Cch 为缓冲区容量（含结尾 NUL）。
__declspec(dllexport) int __stdcall Wlan_ReadConnected(
    int* connected,
    wchar_t* ssid, int ssidCch,
    wchar_t* desc, int descCch,
    wchar_t* ipv4, int ipv4Cch,
    wchar_t* mac, int macCch,
    unsigned long long* linkSpeedBps);

// 异步触发一次附近网络扫描（不阻塞调用线程）。发出 WlanScan 请求后立即返回；
// 结果需在稍后调用 Wlan_ScanCollect 汇总。失败返回负 HRESULT。
__declspec(dllexport) int __stdcall Wlan_ScanStart(int* networkCount);

// 汇总之前触发的扫描结果到缓存，并给出网络数量。应在 ScanStart 后延迟片刻（约 1s）再调用。
__declspec(dllexport) int __stdcall Wlan_ScanCollect(int* networkCount);

// 读取第 index 个扫描结果（0 起）。
//   ssid          输出：SSID（UTF-16）
//   signalQuality 输出：0-100 信号质量
//   encrypted     输出：1=有加密（需密码）
//   is5G          输出：1=5GHz，0=2.4GHz
__declspec(dllexport) int __stdcall Wlan_ScanGetItem(
    int index,
    wchar_t* ssid, int ssidCch,
    int* signalQuality,
    int* encrypted,
    int* is5G);

#ifdef __cplusplus
}
#endif