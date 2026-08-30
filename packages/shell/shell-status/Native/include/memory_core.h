// MemoryCore.dll — 底层原生 API 封装工（纯 C 导出，零 UI、零业务逻辑）。
// 封装：
//   - GlobalMemoryStatusEx：物理内存总量/可用
//   - EnumProcesses + GetProcessMemoryInfo：Top N 进程工作集
//   - GetPhysicallyInstalledSystemMemory / MSFT_PhysicalMemory (Wbem 可选)：物理内存条规格
//     无法走 WMI COM 初始化的场景下退化为单条"总容量"占位。
#pragma once

#ifdef __cplusplus
extern "C" {
#endif

#define MEMCORE_MAX_PROC_NAME_CHARS   128
#define MEMCORE_MAX_SERIAL_PART_CHARS 64

// 读全局内存（字节）。
//   totalPhysKb / availPhysKb  输出：千字节（KB）
__declspec(dllexport) int __stdcall Mem_ReadGlobal(
    unsigned long long* totalPhysKb,
    unsigned long long* availPhysKb);

// 枚举前 N 个工作集最大的进程。
//   topCount        输入：要返回的条数（最多 16，实际填充的条数由 filled 输出）
//   names           输出：交错的宽字符串缓冲区，每个条固定 MEMCORE_MAX_PROC_NAME_CHARS 字符
//   pids            输出：每条对应的真实进程 ID（PID），长数组，长度 >= topCount
//   workingSetKb    输出：每条对应的 WorkingSet（KB），长数组，长度 >= topCount
//   filled          输出：实际填入的条数（<= topCount）
__declspec(dllexport) int __stdcall Mem_ReadTopProcesses(
    int topCount,
    wchar_t* names, int nameStrideChars,
    unsigned long* pids,
    unsigned long long* workingSetKb,
    int* filled);

// 物理内存条清单（优先通过 Windows 提供的内存装置信息 API，不可用时
//   退化为单条"总容量"占位）。插槽数由调用方按 slotCount 请求，实际由 filled 返回。
//   slotPartNumbers[]  输出：每条的部件号/厂商型号字符串
//   slotSizeKb[]       输出：每条容量（KB）
//   slotSpeedMhz[]     输出：每条速率（MHz，不支持为 0）
__declspec(dllexport) int __stdcall Mem_ReadPhysicalSlots(
    int slotCount,
    wchar_t* slotPartNumbers, int nameStrideChars,
    unsigned long long* slotSizeKb,
    unsigned int* slotSpeedMhz,
    int* filled);

#ifdef __cplusplus
}
#endif
