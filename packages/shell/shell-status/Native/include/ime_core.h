// ImeCore.dll — 输入法循环切换按键模拟（纯 C 导出，零 UI、零业务逻辑）。
// 封装 keybd_event 定序模拟：Win+Space（Windows 全局输入法循环切换）与
// Ctrl+Shift（遗留热键路径）。语义与 C# KeyboardLayoutInterop 原实现一致。
#pragma once

#ifdef __cplusplus
extern "C" {
#endif

// 模拟一次完整的 Win+Space 按键序列（Windows 全局输入法循环切换一步）。
// 定序：Win↓ 80ms Space↓ 60ms Space↑ 60ms Win↑ 150ms（与托管原实现一致）。
// 返回 0=成功，非 0=失败。
__declspec(dllexport) int __stdcall Ime_CycleOnce(void);

// 模拟一次完整的 Ctrl+Shift 按键序列（Windows"在输入语言之间切换"热键，遗留路径保留）。
// 返回 0=成功，非 0=失败。
__declspec(dllexport) int __stdcall Ime_SimulateCtrlShift(void);

#ifdef __cplusplus
}
#endif
