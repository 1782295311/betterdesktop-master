// NetworkCore.dll — 底层原生 API 封装工（纯 C 导出，零 UI、零业务逻辑）。
// 封装：
//   - GetIfTable2 / GetIfEntry2：各物理网卡累计收发字节数（真实系统数据）
//   - 主接口判定：找到首个 Up 且非回环、非隧道、链路类型为 Ethernet/Wireless80211 的接口
// 字符串通过调用方缓冲区传出；失败返回 HRESULT（0=成功）。
#pragma once

#ifdef __cplusplus
extern "C" {
#endif

#define NETCORE_MAX_NAME_CHARS   128
#define NETCORE_MAX_ADDR_CHARS   64

// 找主接口的当前累计流量 + 接口基本信息。
//   ifIndex          输出：主接口系统 ifIndex（跨调用稳定，用作采样锚点）
//   name/nameCch     输出：接口友好名称（IFR_DESCRIPTION -> UTF-16）
//   typeName/typeCch 输出：类型描述（"WLAN"/"Ethernet"/"Loopback"/…）
//   ipAddr/ipCch     输出：主 IPv4 地址文本（"10.2.83.51"；无则空串）
//   rxBytes          输出：累计接收字节数（自启动以来）
//   txBytes          输出：累计发送字节数（自启动以来）
//   ok               输出：1=成功 0=未找到主接口或读取失败
__declspec(dllexport) int __stdcall Net_ReadPrimaryCounters(
    unsigned int* ifIndex,
    wchar_t* name, int nameCch,
    wchar_t* typeName, int typeCch,
    wchar_t* ipAddr, int ipCch,
    unsigned long long* rxBytes,
    unsigned long long* txBytes,
    int* ok);

// 网络变更回调（地址/路由变化时由后台线程触发；线程安全，业务侧自行回抛 UI）。
typedef void(__stdcall* NetChangeCallback)(void);

// 启用/更新网络变更回调并启动后台监听线程（幂等）；传 null 仅解除回调。
__declspec(dllexport) int __stdcall Net_SetChangeCallback(NetChangeCallback cb);

// 停止网络变更监听并释放后台线程。返回 0。
__declspec(dllexport) int __stdcall Net_ShutdownChangeNotify(void);

#ifdef __cplusplus
}
#endif
