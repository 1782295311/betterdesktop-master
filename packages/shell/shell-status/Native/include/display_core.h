// DisplayCore.dll — 底层原生 API 封装工（纯 C 导出，零 UI、零业务逻辑）。
// 封装 dxva2：枚举物理显示器并读写亮度。若环境无支持亮度调节的显示器（如
// 扩展屏/部分显卡）返回 ok=0，由 C# 层降级提示。
#pragma once

#ifdef __cplusplus
extern "C" {
#endif

// 读第一台支持亮度调节的物理显示器亮度。
//   minVal/curVal/maxVal 输出：亮度范围与当前值（三者都有效时 ok=1）
//   ok 输出：1=成功读取到真实亮度 0=无支持亮度调节的显示器
__declspec(dllexport) int __stdcall Display_GetBrightness(int* minVal, int* curVal, int* maxVal, int* ok);

// 设置第一台支持亮度调节的物理显示器亮度（val 应在 min~max 范围内）。
//   成功返回 S_OK；无支持可见 -> ok=0 且返回 S_OK（由调用方提示）。
__declspec(dllexport) int __stdcall Display_SetBrightness(int val);

#ifdef __cplusplus
}
#endif