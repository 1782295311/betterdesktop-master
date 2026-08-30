// CpuCore.dll — 底层原生 API 封装工（纯 C 导出，零 UI、零业务逻辑）。
// 封装 NtQuerySystemInformation(SystemProcessorPerformanceInformation)
//   计算总体 CPU 使用率（0-100 整数百分比）。
// C++ 层内部缓存前一次采样的 idle/total 时间（按调用进程私有的静态缓存），
//   所以调用方必须按 1 秒左右的节奏连续两次调用才会拿到有意义的数据。
//   第 1 次调用会在 ok=1 但 utilization=0 或近似 0 的情况下返回基准值。
#pragma once

#ifdef __cplusplus
extern "C" {
#endif

// 读取总体 CPU 使用率。
//   utilization  输出：整数百分比 0-100
//   ok           输出：1=采样可用 0=内部异常（无 NT 导出 / 调失败）
__declspec(dllexport) int __stdcall Cpu_ReadUtilization(int* utilization, int* ok);

// 重置内部 idle/total 缓存（用户想立即重采时用）。
__declspec(dllexport) void __stdcall Cpu_ResetCounters(void);

// 读取处理器具体型号（SMBIOS Type4 Processor Version，如"Intel(R) Core(TM) i7-8750H CPU @ 2.20GHz"）。
//   name     输出：宽字符串，截断到 maxChars（含结尾 NUL）。不可用时为空串。
//   成功返回 0。
__declspec(dllexport) int __stdcall Cpu_ReadModel(wchar_t* name, int maxChars);

// 读取 CPU/主板热区温度（WMI MSAcpi_ThermalZoneTemperature）。
//   temperatureCelsius  输出：摄氏度，取所有热区读数均值；不可用为 0
//   ok                  输出：1=读到至少一个热区 0=不可用/失败
// 注意：多数台式机/笔记本只有 ACPI 热区（近似 CPU 温度），非逐核温度；读不到时 ok=0。
__declspec(dllexport) int __stdcall Cpu_ReadTemperature(int* temperatureCelsius, int* ok);

#ifdef __cplusplus
}
#endif
