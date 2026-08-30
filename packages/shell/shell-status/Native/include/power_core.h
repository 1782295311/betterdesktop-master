// PowerCore.dll — 底层原生 API 封装工（纯 C 导出，零 UI、零业务逻辑）。
// 封装 powrprof + kernel32：电源状态、性能方案枚举、活动方案、方案友好名。
#pragma once

#ifdef __cplusplus
extern "C" {
#endif

#define POWERCORE_MAX_NAME_CHARS 256

// 读电源状态（GetSystemPowerStatus）。
//   acLine      输出：1=接通电源 0=使用电池
//   batteryFlag 输出：状态位（0x80=无电池）
//   percent     输出：0-100（无电池或不可读时 -1）
//   lifeSeconds 输出：剩余电池秒数（未知时为 -1）
__declspec(dllexport) int __stdcall Power_ReadStatus(int* acLine, int* batteryFlag, int* percent, int* lifeSeconds);

// 枚举全部性能方案并缓存；返回方案数量。内部记录当前活动方案。
__declspec(dllexport) int __stdcall Power_EnumeratePlans(int* planCount);

// 读取第 index 个方案（0 起）。
//   guid         输出：方案 GUID（16 字节）；可传 nullptr 忽略
//   name/nameCch 输出：方案友好名称（系统本地化 UTF-16）
//   isActive     输出：1=当前活动方案
__declspec(dllexport) int __stdcall Power_GetPlan(int index, GUID* guid, wchar_t* name, int nameCch, int* isActive);

// 将指定性能方案设为当前活动方案（PowerSetActiveScheme）。
//   guid 输入：目标方案 GUID 指针（必须有效）
// 返回 0=成功；否则 HRESULT。
__declspec(dllexport) int __stdcall Power_SetActivePlan(const GUID* guid);

// 读取电池详细信息（通过 Battery IOCTL 直读，聚合系统中所有电池）。所有输出参数可为 NULL。
// 返回 0=成功；否则 HRESULT。
//   hasBattery       输出：1=有电池 0=无
//   acOnline         输出：1=接电 0=电池
//   charging         输出：1=充电中 0=未充电
//   percent          输出：0-100（当前容量/满充容量），-1=未知
//   currentCapacity  输出：当前容量(mWh)，-1=未知
//   fullCapacity     输出：满充容量(mWh)，-1=未知
//   designCapacity   输出：设计容量(mWh)，-1=未知
//   healthPercent    输出：健康度(满充/设计,%)，-1=未知
//   rateMw           输出：功率(mW)，正=放电 负=充电 0=空闲/未知
//   remainingSeconds 输出：自算剩余秒数（放电中时），-1=未知
//   cycleCount       输出：循环次数，-1=未知
//   temperatureC     输出：温度(摄氏度*10，如 325=32.5°C)，-1=未知
__declspec(dllexport) int __stdcall Power_ReadBatteryDetail(
    int* hasBattery, int* acOnline, int* charging, int* percent,
    int* currentCapacity, int* fullCapacity, int* designCapacity,
    int* healthPercent, int* rateMw, int* remainingSeconds,
    int* cycleCount, int* temperatureC);

#ifdef __cplusplus
}
#endif