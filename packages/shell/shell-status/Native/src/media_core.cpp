// MediaCore.dll 实现 — Windows.Media.Control（SMTC 会话）封装。
// 说明：SMTC 的"全局会话管理器"只有 Windows 10 1809+ 才能直接使用 WinRT 类型。
//   这里采用弱链接：动态加载 combase.dll!RoGetActivationFactory，失败则 ok=0 占位，
//   不会崩。C# 调用方负责降级。
#include <windows.h>
#include <roapi.h>
#include <inspectable.h>
#include <hstring.h>
#include <cstdint>
#include <cwchar>

#include "media_core.h"

#pragma comment(lib, "runtimeobject.lib")

namespace
{
// ABI 最小子集。如果编译环境缺少 winmd/IDL，此处手工声明 IID 会略繁琐；
// 为了避免 SDK 差异导致编译失败，本实现走"尽量降级"策略：
//   - RoInitialize 失败或找不到相关类型时直接返回 count=0（OK 但无会话）。
typedef HRESULT (WINAPI* FnRoInitialize)(RO_INIT_TYPE initType);
typedef HRESULT (WINAPI* FnRoUninitialize)(void);
typedef HRESULT (WINAPI* FnRoGetActivationFactory)(HSTRING activatableClassId, REFIID iid, void** factory);
typedef HRESULT (WINAPI* FnWindowsCreateString)(LPCWSTR sourceString, UINT32 length, HSTRING* string);
typedef HRESULT (WINAPI* FnWindowsDeleteString)(HSTRING string);
typedef PCWSTR (WINAPI* FnWindowsGetStringRawBuffer)(HSTRING string, UINT32* length);

struct Api
{
    bool loaded = false;
    FnRoInitialize init = nullptr;
    FnRoUninitialize uninit = nullptr;
    FnRoGetActivationFactory getFact = nullptr;
    FnWindowsCreateString createStr = nullptr;
    FnWindowsDeleteString delStr = nullptr;
    FnWindowsGetStringRawBuffer getBuf = nullptr;
};
Api& GetApi()
{
    static Api s;
    if (s.loaded) return s;
    HMODULE combase = LoadLibraryW(L"combase.dll");
    if (!combase) return s;
    s.init = reinterpret_cast<FnRoInitialize>(GetProcAddress(combase, "RoInitialize"));
    s.uninit = reinterpret_cast<FnRoUninitialize>(GetProcAddress(combase, "RoUninitialize"));
    s.getFact = reinterpret_cast<FnRoGetActivationFactory>(GetProcAddress(combase, "RoGetActivationFactory"));
    HMODULE winrtString = GetModuleHandleW(L"api-ms-win-core-winrt-string-l1-1-0.dll");
    if (!winrtString) winrtString = LoadLibraryW(L"api-ms-win-core-winrt-string-l1-1-0.dll");
    if (!winrtString) winrtString = combase;
    s.createStr = reinterpret_cast<FnWindowsCreateString>(
        GetProcAddress(winrtString, "WindowsCreateString"));
    s.delStr = reinterpret_cast<FnWindowsDeleteString>(
        GetProcAddress(winrtString, "WindowsDeleteString"));
    s.getBuf = reinterpret_cast<FnWindowsGetStringRawBuffer>(
        GetProcAddress(winrtString, "WindowsGetStringRawBuffer"));
    if (s.init && s.getFact && s.createStr && s.delStr && s.getBuf) s.loaded = true;
    return s;
}

void CopyHStringToBuf(HSTRING hs, wchar_t* out, int cch)
{
    if (!out || cch <= 0) return;
    out[0] = L'\0';
    auto& api = GetApi();
    if (!hs || !api.loaded) return;
    UINT32 len = 0;
    PCWSTR raw = api.getBuf(hs, &len);
    if (!raw) return;
    if (len >= (UINT32)cch) len = (UINT32)(cch - 1);
    wcsncpy_s(out, (size_t)cch, raw, len);
    out[len] = L'\0';
}
} // namespace

extern "C" int __stdcall Media_GetSessionCount(int* count)
{
    if (!count) return static_cast<int>(E_POINTER);
    *count = 0;
    auto& api = GetApi();
    if (!api.loaded) return 0;
    // 先尝试 RoInitialize（如果 WPF 已初始化，则会返回 RPC_E_CHANGED_MODE；忽略）
    HRESULT hr = api.init(RO_INIT_SINGLETHREADED);
    if (FAILED(hr) && hr != RPC_E_CHANGED_MODE)
    {
        return static_cast<int>(hr);
    }
    // 动态获取 SMTC Manager：Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager
    HSTRING hsName = nullptr;
    const wchar_t* name = L"Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager";
    if (FAILED(api.createStr(name, (UINT32)wcslen(name), &hsName)))
    {
        return 0;
    }
    // 没有静态 IID/接口声明——此处不硬编码 GUID，直接视为"未支持平台"返回 0 会话。
    // 真实完整实现需要包含 winrt/windows.media.control.h（WinRT C++/WinRT），
    // 但为了跨 SDK 可编译，本层先返回 0，交给 C# 层用 WinRT interop 读取。
    api.delStr(hsName);
    *count = 0;
    return 0;
}

extern "C" int __stdcall Media_GetSession(
    int index,
    wchar_t* title, int titleCch,
    wchar_t* artist, int artistCch,
    wchar_t* src, int srcCch,
    int* canPlay, int* canPause, int* canNext, int* canPrev,
    int* playbackState)
{
    if (!title || !artist || !src || !canPlay || !canPause || !canNext || !canPrev || !playbackState)
        return static_cast<int>(E_POINTER);
    if (titleCch > 0) title[0] = L'\0';
    if (artistCch > 0) artist[0] = L'\0';
    if (srcCch > 0) src[0] = L'\0';
    *canPlay = 0; *canPause = 0; *canNext = 0; *canPrev = 0; *playbackState = 0;
    // C++ 层暂不返回真实会话（C# 层用 WinRT interop 读取，详见 MediaCoreNative 的降级实现）。
    (void)index;
    return 0;
}

extern "C" int __stdcall Media_SendControl(int cmd)
{
    (void)cmd;
    return static_cast<int>(E_NOTIMPL);
}
