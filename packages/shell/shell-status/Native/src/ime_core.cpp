// ImeCore.dll 实现 — 输入法循环切换按键模拟。
// 原实现位于 C# KeyboardLayoutInterop.SimulateWinSpace / SimulateCtrlShift
// （keybd_event 定序 + Sleep 间隔，异常时释放修饰键防卡键）。此处按原语义原样迁移：
//   - Win+Space：Win↓ 80ms Space↓ 60ms Space↑ 60ms Win↑ 150ms
//   - Ctrl+Shift：Ctrl↓ 50ms Shift↓ 50ms Shift↑ 50ms Ctrl↑
// 供 C# 端经 ImeCoreNative 薄封装调用；DLL 缺失时 C# 侧回退托管实现（防回归）。
#include <windows.h>

#include "ime_core.h"

namespace
{
// VK 码（与托管原实现一致）
constexpr BYTE kVkLWin = 0x5B;    // VK_LWIN
constexpr BYTE kVkSpace = 0x20;   // VK_SPACE
constexpr BYTE kVkLCtrl = 0xA2;   // VK_LCONTROL
constexpr BYTE kVkLShift = 0xA0;  // VK_LSHIFT
} // namespace

extern "C" int __stdcall Ime_CycleOnce(void)
{
    // Win+Space 每调用一次全局切一步，不校验 HKL（校验会引入"连跳"风险，语义同 C# 原实现）。
    keybd_event(kVkLWin, 0, 0, 0);
    Sleep(80);
    keybd_event(kVkSpace, 0, 0, 0);
    Sleep(60);
    keybd_event(kVkSpace, 0, KEYEVENTF_KEYUP, 0);
    Sleep(60);
    keybd_event(kVkLWin, 0, KEYEVENTF_KEYUP, 0);
    Sleep(150);
    return 0;
}

extern "C" int __stdcall Ime_SimulateCtrlShift(void)
{
    keybd_event(kVkLCtrl, 0, 0, 0);
    Sleep(50);
    keybd_event(kVkLShift, 0, 0, 0);
    Sleep(50);
    keybd_event(kVkLShift, 0, KEYEVENTF_KEYUP, 0);
    Sleep(50);
    keybd_event(kVkLCtrl, 0, KEYEVENTF_KEYUP, 0);
    return 0;
}
