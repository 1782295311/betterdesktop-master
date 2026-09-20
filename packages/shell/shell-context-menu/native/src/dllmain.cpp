// BetterDesktopShellMenu — DLL 入口、类工厂导出与诊断用自注册
//
// 【注册分工】
//   生产路径：由宿主 C# 侧 ComShellExtensionRegistrar（B 路）/ 稀疏包清单（A 路）注册，
//   它们是唯一权威、可被设置开关随时启停。
//   本文件的 DllRegisterServer / DllUnregisterServer 只是**诊断用**便捷入口
//   （regsvr32 / 冒烟脚本），同样只写 HKCU（免管理员）。两处写出的 GUID 与键位必须一致。

#include "BdShell.h"
#include "ClassFactory.h"

#include <windows.h>
#include <strsafe.h>
#include <unknwn.h>

namespace bdshell
{
    // g_liveObjectCount 定义在 MenuModel.cpp（公共 TU，两个 native 目标共享）。
    LONG g_moduleLockCount = 0;
}

namespace
{
    HINSTANCE g_instance = nullptr;
    bool g_loadLogged = false;

    constexpr wchar_t kHandlerKeyName[] = L"BetterDesktop";

    bool FormatClsidString(REFGUID clsid, wchar_t* buffer, size_t capacity) noexcept
    {
        return ::StringFromGUID2(clsid, buffer, static_cast<int>(capacity)) > 0;
    }

    /// <summary>DLL 自身的绝对路径。</summary>
    bool ModulePath(wchar_t* buffer, DWORD capacity) noexcept
    {
        const DWORD len = ::GetModuleFileNameW(g_instance, buffer, capacity);
        return len != 0 && len < capacity;
    }

    /// <summary>写一个 REG_SZ 值（HKCU\Software\Classes 下，免夺权）。</summary>
    bool WriteClassValue(const wchar_t* subKey, const wchar_t* valueName, const wchar_t* value) noexcept
    {
        HKEY key = nullptr;
        if (::RegCreateKeyExW(HKEY_CURRENT_USER, subKey, 0, nullptr, 0,
                KEY_WRITE, nullptr, &key, nullptr) != ERROR_SUCCESS)
        {
            return false;
        }
        const DWORD bytes = static_cast<DWORD>((wcslen(value) + 1) * sizeof(wchar_t));
        const LSTATUS status = ::RegSetValueExW(key, valueName, 0, REG_SZ,
            reinterpret_cast<const BYTE*>(value), bytes);
        ::RegCloseKey(key);
        return status == ERROR_SUCCESS;
    }
}

// =====================================================================
// COM 导出
// =====================================================================

STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, void** ppv)
{
    if (ppv == nullptr)
    {
        return E_POINTER;
    }
    *ppv = nullptr;

    if (!g_loadLogged)
    {
        g_loadLogged = true;
        wchar_t path[MAX_PATH * 2] = {};
        if (ModulePath(path, ARRAYSIZE(path)))
        {
            bdshell::LogLine(L"DLL 已加载: %s", path);
        }
        else
        {
            bdshell::LogLine(L"DLL 已加载（路径未知）");
        }
    }

    return bdshell::CreateClassFactory(rclsid, riid, ppv);
}

STDAPI DllCanUnloadNow()
{
    // 存活对象由 LiveObjectGuard 维护（BdShell.h）；锁计数由 IClassFactory::LockServer 维护。
    const LONG objects = ::InterlockedCompareExchange(&bdshell::g_liveObjectCount, 0, 0);
    const LONG locks = ::InterlockedCompareExchange(&bdshell::g_moduleLockCount, 0, 0);
    return (objects == 0 && locks == 0) ? S_OK : S_FALSE;
}

// =====================================================================
// 诊断用自注册（生产注册见文件头注释）
// =====================================================================

STDAPI DllRegisterServer()
{
    wchar_t dllPath[MAX_PATH * 2] = {};
    if (!ModulePath(dllPath, ARRAYSIZE(dllPath)))
    {
        return E_FAIL;
    }

    wchar_t clsidText[64] = {};
    if (!FormatClsidString(bdshell::kBClassic, clsidText, ARRAYSIZE(clsidText)))
    {
        return E_FAIL;
    }

    // ① CLSID 注册（InprocServer32 + Apartment）
    wchar_t subKey[512] = {};
    if (FAILED(::StringCchPrintfW(subKey, ARRAYSIZE(subKey),
            L"Software\\Classes\\CLSID\\%s", clsidText)) ||
        !WriteClassValue(subKey, nullptr, L"BetterDesktop 右键快捷功能"))
    {
        return E_FAIL;
    }

    if (FAILED(::StringCchPrintfW(subKey, ARRAYSIZE(subKey),
            L"Software\\Classes\\CLSID\\%s\\InprocServer32", clsidText)) ||
        !WriteClassValue(subKey, nullptr, dllPath) ||
        !WriteClassValue(subKey, L"ThreadingModel", L"Apartment"))
    {
        return E_FAIL;
    }

    // ② 四场景处理程序键（HKCU，免管理员；HKCU\Software\Classes 参与 HKCR 合并视图）
    static const wchar_t* kScenes[] = {
        L"*",
        L"Directory",
        L"Directory\\Background",
        L"DesktopBackground",
    };
    for (const wchar_t* scene : kScenes)
    {
        if (FAILED(::StringCchPrintfW(subKey, ARRAYSIZE(subKey),
                L"Software\\Classes\\%s\\shellex\\ContextMenuHandlers\\%s", scene, kHandlerKeyName)) ||
            !WriteClassValue(subKey, nullptr, clsidText))
        {
            bdshell::LogLine(L"注册场景失败: %s", scene);
            return E_FAIL;
        }
    }

    bdshell::LogLine(L"DllRegisterServer 完成: %s", dllPath);
    return S_OK;
}

STDAPI DllUnregisterServer()
{
    wchar_t subKey[512] = {};
    static const wchar_t* kScenes[] = {
        L"*",
        L"Directory",
        L"Directory\\Background",
        L"DesktopBackground",
    };
    for (const wchar_t* scene : kScenes)
    {
        if (SUCCEEDED(::StringCchPrintfW(subKey, ARRAYSIZE(subKey),
                L"Software\\Classes\\%s\\shellex\\ContextMenuHandlers\\%s", scene, kHandlerKeyName)))
        {
            ::RegDeleteKeyW(HKEY_CURRENT_USER, subKey);
        }
    }

    wchar_t clsidText[64] = {};
    if (FormatClsidString(bdshell::kBClassic, clsidText, ARRAYSIZE(clsidText)) &&
        SUCCEEDED(::StringCchPrintfW(subKey, ARRAYSIZE(subKey),
            L"Software\\Classes\\CLSID\\%s", clsidText)))
    {
        // 先删子键再删父键（RegDeleteKey 不递归）
        wchar_t child[512] = {};
        if (SUCCEEDED(::StringCchPrintfW(child, ARRAYSIZE(child), L"%s\\InprocServer32", subKey)))
        {
            ::RegDeleteKeyW(HKEY_CURRENT_USER, child);
        }
        ::RegDeleteKeyW(HKEY_CURRENT_USER, subKey);
    }

    bdshell::LogLine(L"DllUnregisterServer 完成");
    return S_OK;
}

// =====================================================================
// DllMain
// =====================================================================

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{
    UNREFERENCED_PARAMETER(reserved);
    switch (reason)
    {
    case DLL_PROCESS_ATTACH:
        g_instance = instance;
        ::DisableThreadLibraryCalls(instance);
        break;
    case DLL_PROCESS_DETACH:
        g_instance = nullptr;
        break;
    default:
        break;
    }
    return TRUE;
}
