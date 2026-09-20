// BetterDesktopShellMenu — 类工厂入口

#pragma once

#include <windows.h>
#include <unknwn.h>

namespace bdshell
{
    /// <summary>按 CLSID 创建类工厂（未登记的 CLSID 返回 CLASS_E_CLASSNOTAVAILABLE）。</summary>
    HRESULT CreateClassFactory(REFCLSID clsid, REFIID riid, void** ppv) noexcept;
}
