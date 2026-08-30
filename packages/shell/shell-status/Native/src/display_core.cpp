// DisplayCore.dll 实现 — 纯 dxva2 显示器亮度封装（零业务逻辑、零 UI）。
// dxva2.h 不在所有 SDK 都有，故全部物理显示器 API 用 GetProcAddress 运行时装载，
// 结构体手工对齐。这样可同时兼容桌面扩展屏（无亮度调节）与笔记本内建屏。
#include <windows.h>
#include <winerror.h>
#include <wbemidl.h>
#include <cstdint>
#include <vector>

#include "display_core.h"

#pragma comment(lib, "user32.lib")
#pragma comment(lib, "wbemuuid.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "oleaut32.lib")

namespace
{
// ---- 手工声明的 dxva2 结构/类型（对齐官方头文件） ----
using HMONITOR_HANDLE = HMONITOR;

struct PHYSICAL_MONITOR_DEF
{
    HANDLE hPhysicalMonitor;
    WCHAR szPhysicalMonitorDescription[128];
};

enum MC_DISPLAY_TECHNOLOGY_DEF_ : DWORD
{
    MTD_OTHER = 0,
};

// ---- 函数指针类型 ----
using FnGetNumberPhysicalMonitors = BOOL(WINAPI*)(HMONITOR h, LPDWORD pdwNumberOfPhysicalMonitors);
using FnGetPhysicalMonitors = BOOL(WINAPI*)(HMONITOR hMonitor, DWORD dwPhysicalMonitorArraySize,
                                            PHYSICAL_MONITOR_DEF* pPhysicalMonitorArray);
using FnGetMonitorCapabilities = BOOL(WINAPI*)(HANDLE hMonitor, LPDWORD pdwMonitorCapabilities,
                                               LPDWORD pdwSupportedColorTemperatures);
using FnGetMonitorBrightness = BOOL(WINAPI*)(HANDLE hMonitor, LPDWORD pdwMinimumBrightness,
                                             LPDWORD pdwCurrentBrightness, LPDWORD pdwMaximumBrightness);
using FnSetMonitorBrightness = BOOL(WINAPI*)(HANDLE hMonitor, DWORD dwNewBrightness);
using FnDestroyPhysicalMonitors = void(WINAPI*)(DWORD dwPhysicalMonitorArraySize,
                                                PHYSICAL_MONITOR_DEF* pPhysicalMonitorArray);

struct Dxva2Api
{
    FnGetNumberPhysicalMonitors GetNumber = nullptr;
    FnGetPhysicalMonitors GetPhysical = nullptr;
    FnGetMonitorCapabilities GetCaps = nullptr;
    FnGetMonitorBrightness GetBright = nullptr;
    FnSetMonitorBrightness SetBright = nullptr;
    FnDestroyPhysicalMonitors Destroy = nullptr;
    bool loaded = false;
};

const Dxva2Api& LoadDxva2()
{
    static Dxva2Api api = [] {
        Dxva2Api a;
        HMODULE m = ::LoadLibraryW(L"dxva2.dll");
        if (m)
        {
            a.GetNumber = reinterpret_cast<FnGetNumberPhysicalMonitors>(
                ::GetProcAddress(m, "GetNumberOfPhysicalMonitorsFromHMONITOR"));
            a.GetPhysical = reinterpret_cast<FnGetPhysicalMonitors>(
                ::GetProcAddress(m, "GetPhysicalMonitorsFromHMONITOR"));
            a.GetCaps = reinterpret_cast<FnGetMonitorCapabilities>(
                ::GetProcAddress(m, "GetMonitorCapabilities"));
            a.GetBright = reinterpret_cast<FnGetMonitorBrightness>(
                ::GetProcAddress(m, "GetMonitorBrightness"));
            a.SetBright = reinterpret_cast<FnSetMonitorBrightness>(
                ::GetProcAddress(m, "SetMonitorBrightness"));
            a.Destroy = reinterpret_cast<FnDestroyPhysicalMonitors>(
                ::GetProcAddress(m, "DestroyPhysicalMonitors"));
            a.loaded = a.GetNumber && a.GetPhysical && a.GetCaps &&
                       a.GetBright && a.SetBright && a.Destroy;
        }
        return a;
    }();
    return api;
}

// 不再永久缓存物理显示器句柄——拔插显示器后句柄会失效。
// 每次调用都重新枚举，且只取主显示器(MONITORINFOF_PRIMARY)上第一个支持亮度的物理显示器，
// 避免控错屏、避免拿到失效句柄。

struct BrightnessMonitor
{
    HANDLE handle = nullptr;
    DWORD mn = 0, cur = 0, mx = 0;
};

inline void API_DESTROY_PHYSICAL_MONITOR(const Dxva2Api& api, PHYSICAL_MONITOR_DEF* p)
{
    if (p && p->hPhysicalMonitor)
    {
        // 单句柄销毁（DestroyPhysicalMonitor 支持单个）。
        PHYSICAL_MONITOR_DEF one[1] = {*p};
        api.Destroy(1, one);
        p->hPhysicalMonitor = nullptr;
    }
}

inline void DestroyBrightnessMonitor(const Dxva2Api& api, BrightnessMonitor& bm)
{
    if (bm.handle)
    {
        PHYSICAL_MONITOR_DEF pm = {bm.handle, {0}};
        API_DESTROY_PHYSICAL_MONITOR(api, &pm);
        bm.handle = nullptr;
    }
}

// 找到主显示器 HMONITOR。返回 true 表示找到了主屏。
BOOL CALLBACK FindPrimaryProc(HMONITOR hmon, HDC, LPRECT, LPARAM lParam)
{
    MONITORINFO mi{};
    mi.cbSize = sizeof(mi);
    if (::GetMonitorInfoW(hmon, &mi) && (mi.dwFlags & MONITORINFOF_PRIMARY))
    {
        *reinterpret_cast<HMONITOR*>(lParam) = hmon;
        return FALSE; // 只取主屏
    }
    return TRUE;
}

bool TryGetPrimaryMonitor(HMONITOR* outHmon)
{
    if (!outHmon) return false;
    *outHmon = nullptr;
    ::EnumDisplayMonitors(nullptr, nullptr, FindPrimaryProc, reinterpret_cast<LPARAM>(outHmon));
    return *outHmon != nullptr;
}

// 在主显示器上找到首个支持亮度调节的物理显示器；找到则填充 bm.handle，其余句柄销毁。
// 返回的 bm.handle 需调用方用 API_DESTROY_PHYSICAL_MONITOR 释放。
bool ScanPrimaryBrightnessMonitor(BrightnessMonitor& bm)
{
    const auto& api = LoadDxva2();
    if (!api.loaded) return false;

    HMONITOR primary = nullptr;
    if (!TryGetPrimaryMonitor(&primary)) return false;

    DWORD count = 0;
    if (!api.GetNumber(primary, &count) || count == 0) return false;
    std::vector<PHYSICAL_MONITOR_DEF> phys(count);
    if (!api.GetPhysical(primary, count, phys.data())) return false;

    for (DWORD i = 0; i < count; ++i)
    {
        DWORD caps = 0, temp = 0;
        if (api.GetCaps(phys[i].hPhysicalMonitor, &caps, &temp) &&
            (caps & 2 /* MC_CAPS_BRIGHTNESS = 0x0002 */))
        {
            DWORD mn = 0, cur = 0, mx = 0;
            if (api.GetBright(phys[i].hPhysicalMonitor, &mn, &cur, &mx) &&
                cur >= mn && cur <= mx && mn < mx)
            {
                bm.handle = phys[i].hPhysicalMonitor;
                bm.mn = mn; bm.cur = cur; bm.mx = mx;
                // 释放同屏上其它物理显示器句柄。
                for (DWORD k = 0; k < count; ++k)
                {
                    if (k != i)
                    {
                        API_DESTROY_PHYSICAL_MONITOR(api, &phys[k]);
                    }
                }
                return true;
            }
        }
    }
    // 无支持亮度的物理屏，释放本屏所有句柄。
    for (DWORD k = 0; k < count; ++k)
    {
        API_DESTROY_PHYSICAL_MONITOR(api, &phys[k]);
    }
    return false;
}

// ===================== WMI 亮度降级方案 =====================
// dxva2 仅对部分内建屏有效；扩展屏/某些显卡无亮度能力，走 WMI 内置亮度类：
//   - 读：WmiMonitorBrightness（CurrentBrightness + Levels → min/max/current）
//   - 写：WmiMonitorBrightnessMethods.WmiSetBrightness（亮度须取 Levels 中最近档）
// 纯 C 风格 COM，不引第三方；任何一步失败即返回 false（调用方再用其它路径/提示）。
// 说明：只有正确初始化的 COM 才调用 CoUninitialize；RPC_E_CHANGED_MODE 表示已由其它
//      线程初始化，不能重复释放。

struct BrightnessRange
{
    int minV = 0;
    int curV = 0;
    int maxV = 0;
};

// WMI root\WMI 的亮度类在进程默认授权级别下会返回 WBEM_E_ACCESS_DENIED（0x80041003）。
// ConnectServer 后须对服务代理提升模拟级别（Impersonate），否则 Next 读不到任何实例。
void ProxyImpersonate(IWbemServices* services)
{
    if (!services) return;
    ::CoSetProxyBlanket(services, RPC_C_AUTHN_WINNT, RPC_C_AUTHZ_NONE, nullptr,
                        RPC_C_AUTHN_LEVEL_CALL, RPC_C_IMP_LEVEL_IMPERSONATE, nullptr, EOAC_NONE);
}

// 解析 Levels 字段得到亮度范围。
// 部分驱动把 Levels 暴露为 uint8 数组（各档亮度值）；另一些则暴露为 uint32 标量档位数（如 101 → 0..100）。
// 能解析则返回 true 并填充范围，否则返回 false。
bool ParseLevels(const VARIANT& v, int* minB, int* maxB)
{
    if (minB) *minB = 0;
    if (maxB) *maxB = -1;

    if (v.vt & VT_ARRAY)
    {
        if ((v.vt & ~VT_ARRAY) != VT_UI1 || !v.parray) return false;
        SAFEARRAY* sa = v.parray;
        LONG lb = 0, ub = -1;
        SafeArrayGetLBound(sa, 1, &lb);
        SafeArrayGetUBound(sa, 1, &ub);
        LONG n = ub - lb + 1;
        BYTE* data = nullptr;
        if (n > 0 && SafeArrayAccessData(sa, (void**)&data) == S_OK)
        {
            if (minB) *minB = data[0];
            if (maxB) *maxB = data[n - 1];
            SafeArrayUnaccessData(sa);
            return true;
        }
        return false;
    }

    // 标量：按档位数 N 估算范围 0..N-1（如 101 档 → 0..100）。
    // 注意：驱动常以 VT_UI4（uint32）暴露，需读 ulVal 而不是先强转 VT_I4 再判 vt。
    VARIANT vv = v;
    LONG levelMax = -1;
    if (vv.vt == VT_I4)
    {
        levelMax = vv.lVal;
    }
    else if (vv.vt == VT_UI4)
    {
        levelMax = static_cast<LONG>(vv.ulVal);
    }
    else
    {
        if (FAILED(VariantChangeType(&vv, &vv, 0, VT_I4))) return false;
        levelMax = vv.lVal;
    }
    if (levelMax > 1)
    {
        if (maxB) *maxB = static_cast<int>(levelMax) - 1;
        return true;
    }
    return false;
}

// 在 Levels 中取最接近 target 的档位并写入 outLevel；找不到返回 false。
bool NearestWmiLevel(int target, int* outLevel)
{
    int clamped = target;
    if (clamped < 0) clamped = 0;
    if (clamped > 255) clamped = 255;

    IWbemLocator* locator = nullptr;
    IWbemServices* services = nullptr;
    IEnumWbemClassObject* enumerator = nullptr;
    IWbemClassObject* obj = nullptr;

    HRESULT hr = ::CoCreateInstance(CLSID_WbemLocator, nullptr, CLSCTX_INPROC_SERVER,
                                    IID_IWbemLocator, (void**)&locator);
    if (SUCCEEDED(hr) && locator)
    {
        hr = locator->ConnectServer(BSTR(L"ROOT\\WMI"), nullptr, nullptr, nullptr, 0,
                                    nullptr, nullptr, &services);
    }
    ProxyImpersonate(services);
    if (SUCCEEDED(hr) && services)
    {
        hr = services->ExecQuery(BSTR(L"WQL"),
                                 BSTR(L"SELECT * FROM WmiMonitorBrightness"),
                                 WBEM_FLAG_FORWARD_ONLY | WBEM_FLAG_RETURN_IMMEDIATELY,
                                 nullptr, &enumerator);
    }

    bool found = false;
    int nearest = 0;
    LONG bestSpan = 0;

    if (SUCCEEDED(hr) && enumerator)
    {
        ULONG returned = 0;
        while (enumerator->Next(WBEM_INFINITE, 1, &obj, &returned) == WBEM_S_NO_ERROR && returned > 0)
        {
            VARIANT v{};
            HRESULT gh = obj->Get(L"Levels", 0, &v, nullptr, nullptr);
            if (SUCCEEDED(gh))
            {
                int minB = 0, maxB = -1;
                if (ParseLevels(v, &minB, &maxB))
                {
                    LONG span = static_cast<LONG>(maxB) - minB;
                    // 档位跨度越大通常越接近"主内建屏"；取跨度最大者作一致性目标。
                    if (!found || span > bestSpan)
                    {
                        bestSpan = span;
                        if (v.vt & VT_ARRAY)
                        {
                            // 数组：逐档取最接近 clamped 的离散档位
                            SAFEARRAY* sa = v.parray;
                            LONG lb = 0, ub = -1;
                            SafeArrayGetLBound(sa, 1, &lb);
                            SafeArrayGetUBound(sa, 1, &ub);
                            LONG n = ub - lb + 1;
                            BYTE* data = nullptr;
                            LONG bestDist = -1;
                            if (n > 0 && SafeArrayAccessData(sa, (void**)&data) == S_OK)
                            {
                                for (LONG i = 0; i < n; ++i)
                                {
                                    LONG dist = static_cast<LONG>(data[i]) - clamped;
                                    if (dist < 0) dist = -dist;
                                    if (bestDist < 0 || dist < bestDist)
                                    {
                                        bestDist = dist;
                                        nearest = data[i];
                                    }
                                }
                                SafeArrayUnaccessData(sa);
                            }
                        }
                        else
                        {
                            // 标量档数：亮度即为 0..maxB 的整数值，直接取夹紧后的目标
                            nearest = clamped;
                            if (nearest < minB) nearest = minB;
                            if (nearest > maxB) nearest = maxB;
                        }
                        found = true;
                    }
                }
            }
            VariantClear(&v);
            obj->Release();
            obj = nullptr;
            returned = 0;
        }
    }

    if (enumerator) enumerator->Release();
    if (services) services->Release();
    if (locator) locator->Release();

    if (found && outLevel) *outLevel = nearest;
    return found;
}

// 读亮度。返回 true 且范围有效时填充 out；否则返回 false。
bool ReadWmiBrightness(BrightnessRange& out)
{
    IWbemLocator* locator = nullptr;
    IWbemServices* services = nullptr;
    IEnumWbemClassObject* enumerator = nullptr;
    IWbemClassObject* obj = nullptr;

    HRESULT hr = ::CoCreateInstance(CLSID_WbemLocator, nullptr, CLSCTX_INPROC_SERVER,
                                    IID_IWbemLocator, (void**)&locator);
    if (SUCCEEDED(hr) && locator)
    {
        hr = locator->ConnectServer(BSTR(L"ROOT\\WMI"), nullptr, nullptr, nullptr, 0,
                                    nullptr, nullptr, &services);
    }
    ProxyImpersonate(services);
    if (SUCCEEDED(hr) && services)
    {
        hr = services->ExecQuery(BSTR(L"WQL"),
                                 BSTR(L"SELECT * FROM WmiMonitorBrightness"),
                                 WBEM_FLAG_FORWARD_ONLY | WBEM_FLAG_RETURN_IMMEDIATELY,
                                 nullptr, &enumerator);
    }

    bool found = false;
    LONG bestSpan = 0;
    BrightnessRange pick{};

    if (SUCCEEDED(hr) && enumerator)
    {
        ULONG returned = 0;
        while (enumerator->Next(WBEM_INFINITE, 1, &obj, &returned) == WBEM_S_NO_ERROR && returned > 0)
        {
            LONG cur = -1;
            int minB = 0, maxB = -1;
            {
                VARIANT v{};
                HRESULT gh = obj->Get(L"CurrentBrightness", 0, &v, nullptr, nullptr);
                if (SUCCEEDED(gh) && v.vt != VT_NULL)
                {
                    if (v.vt != VT_I4) VariantChangeType(&v, &v, 0, VT_I4);
                    if (v.vt == VT_I4) cur = static_cast<LONG>(v.lVal);
                }
                VariantClear(&v);
            }
            {
                VARIANT v{};
                HRESULT gh = obj->Get(L"Levels", 0, &v, nullptr, nullptr);
                if (SUCCEEDED(gh))
                {
                    ParseLevels(v, &minB, &maxB);
                }
                VariantClear(&v);
            }
            if (cur >= 0 && maxB >= minB)
            {
                LONG span = static_cast<LONG>(maxB) - minB;
                if (!found || span > bestSpan)
                {
                    bestSpan = span;
                    pick = { static_cast<int>(minB), static_cast<int>(cur), static_cast<int>(maxB) };
                    found = true;
                }
            }
            obj->Release();
            obj = nullptr;
            returned = 0;
        }
    }

    if (enumerator) enumerator->Release();
    if (services) services->Release();
    if (locator) locator->Release();

    // 注意：此处应校验 pick 而非 out —— out 是调用方传入的全零结构，对 out 判断恒为 false，
    // 会导致 WMI 亮度读取永远失败（原 bug：slider 因此恒显示"亮度调节不可用"）。
    if (found && pick.maxV > pick.minV && pick.curV >= pick.minV && pick.curV <= pick.maxV)
    {
        out = pick;
        return true;
    }
    return false;
}

// 设置亮度：将 val 映射到最近的 Levels 档位后调用 WmiSetBrightness。
// 成功返回 S_OK；无实例/失败返回对应 HRESULT。
HRESULT SetWmiBrightness(int val)
{
    int clamped = val;
    if (clamped < 0) clamped = 0;
    if (clamped > 255) clamped = 255;

    int level = clamped;

    IWbemLocator* locator = nullptr;
    IWbemServices* services = nullptr;
    IEnumWbemClassObject* enumerator = nullptr;
    IWbemClassObject* obj = nullptr;

    HRESULT hr = ::CoCreateInstance(CLSID_WbemLocator, nullptr, CLSCTX_INPROC_SERVER,
                                    IID_IWbemLocator, (void**)&locator);
    if (SUCCEEDED(hr) && locator)
    {
        hr = locator->ConnectServer(BSTR(L"ROOT\\WMI"), nullptr, nullptr, nullptr, 0,
                                    nullptr, nullptr, &services);
    }
    ProxyImpersonate(services);

    BSTR path = nullptr;
    if (SUCCEEDED(hr) && services)
    {
        hr = services->ExecQuery(BSTR(L"WQL"),
                                 BSTR(L"SELECT * FROM WmiMonitorBrightnessMethods"),
                                 WBEM_FLAG_FORWARD_ONLY | WBEM_FLAG_RETURN_IMMEDIATELY,
                                 nullptr, &enumerator);
    }
    if (SUCCEEDED(hr) && enumerator)
    {
        ULONG returned = 0;
        if (enumerator->Next(WBEM_INFINITE, 1, &obj, &returned) == WBEM_S_NO_ERROR && returned > 0)
        {
            VARIANT vp{};
            if (SUCCEEDED(obj->Get(L"__PATH", 0, &vp, nullptr, nullptr)) && vp.vt == VT_BSTR)
            {
                path = SysAllocString(vp.bstrVal);
            }
            VariantClear(&vp);
        }
    }
    if (enumerator) { enumerator->Release(); enumerator = nullptr; }

    if (path && services)
    {
        IWbemClassObject* inClass = nullptr;
        IWbemClassObject* inSig = nullptr;
        IWbemClassObject* inParams = nullptr;
        IWbemClassObject* outParams = nullptr;

        // 方法签名须从"类对象"取（用类名）。若对实例路径 GetObject 得到的实例对象再调
        // GetMethod，会返回 WBEM_E_INVALID_METHOD，导致写入无效果。
        HRESULT h1 = services->GetObject(BSTR(L"WmiMonitorBrightnessMethods"), 0, nullptr, &inClass, nullptr);
        if (SUCCEEDED(h1) && inClass)
        {
            HRESULT h2 = inClass->GetMethod(BSTR(L"WmiSetBrightness"), 0, &inSig, nullptr);
            if (SUCCEEDED(h2) && inSig)
            {
                HRESULT h3 = inSig->SpawnInstance(0, &inParams);
                if (SUCCEEDED(h3) && inParams)
                {
                    VARIANT vt{};
                    vt.vt = VT_I4;
                    vt.lVal = 0;                       // Timeout=0（立即生效）
                    inParams->Put(L"Timeout", 0, &vt, 0);
                    VariantClear(&vt);
                    vt.vt = VT_UI1;
                    vt.bVal = static_cast<BYTE>(level); // Brightness=最近档位
                    inParams->Put(L"Brightness", 0, &vt, 0);
                    VariantClear(&vt);

                    // 调用直接针对实例路径
                    services->ExecMethod(path, BSTR(L"WmiSetBrightness"), 0,
                                         nullptr, inParams, &outParams, nullptr);
                }
            }
        }
        if (outParams) outParams->Release();
        if (inParams) inParams->Release();
        if (inSig) inSig->Release();
        if (inClass) inClass->Release();
    }

    if (obj) { obj->Release(); obj = nullptr; }
    if (services) services->Release();
    if (locator) locator->Release();
    if (path) SysFreeString(path);

    return SUCCEEDED(hr) ? S_OK : hr;
}
} // namespace

extern "C" int __stdcall Display_GetBrightness(int* minVal, int* curVal, int* maxVal, int* ok)
{
    if (!ok) return static_cast<int>(E_POINTER);
    *ok = 0;

    // 1) 优先 dxva2：部分内建屏走物理显示器亮度接口。
    const auto& api = LoadDxva2();
    if (api.loaded)
    {
        BrightnessMonitor bm{};
        if (ScanPrimaryBrightnessMonitor(bm))
        {
            if (minVal) *minVal = static_cast<int>(bm.mn);
            if (curVal) *curVal = static_cast<int>(bm.cur);
            if (maxVal) *maxVal = static_cast<int>(bm.mx);
            *ok = 1;
            DestroyBrightnessMonitor(api, bm);
            return static_cast<int>(S_OK);
        }
    }

    // 2) 降级 WMI 内置亮度类（扩展屏/某些显卡）。
    BrightnessRange wb{};
    if (ReadWmiBrightness(wb))
    {
        if (minVal) *minVal = wb.minV;
        if (curVal) *curVal = wb.curV;
        if (maxVal) *maxVal = wb.maxV;
        *ok = 1;
    }
    return static_cast<int>(S_OK);
}

extern "C" int __stdcall Display_SetBrightness(int val)
{
    // 1) dxva2 可写时优先走物理显示器接口。
    const auto& api = LoadDxva2();
    if (api.loaded)
    {
        BrightnessMonitor bm{};
        if (ScanPrimaryBrightnessMonitor(bm))
        {
            if (val < static_cast<int>(bm.mn) || val > static_cast<int>(bm.mx))
            {
                DestroyBrightnessMonitor(api, bm);
                return static_cast<int>(E_INVALIDARG);
            }
            BOOL applied = api.SetBright(bm.handle, static_cast<DWORD>(val));
            DestroyBrightnessMonitor(api, bm);
            if (applied)
            {
                return static_cast<int>(S_OK);
            }
            // dxva2 写失败（如驱动不支持真正写入）→ 落到 WMI。
        }
    }

    // 2) 降级 WMI：映射到最近档位后调用 WmiSetBrightness。
    return static_cast<int>(SetWmiBrightness(val));
}