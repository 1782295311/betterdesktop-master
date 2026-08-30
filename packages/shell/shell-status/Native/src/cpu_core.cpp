// CpuCore.dll 实现 — 使用 NtQuerySystemInformation 计算 CPU 总使用率。
#include <windows.h>
#include <wbemidl.h>
#include <comdef.h>
#include <cstdint>
#include <cstring>
#include <cstdlib>

#include "cpu_core.h"
#include "smbios_reader.h"

typedef LONG NTSTATUS;
#define STATUS_INFO_LENGTH_MISMATCH ((NTSTATUS)0xC0000004L)

namespace
{
typedef LONG (NTAPI* FnNtQuerySystemInformation)(
    ULONG SystemInformationClass,
    PVOID SystemInformation,
    ULONG SystemInformationLength,
    PULONG ReturnLength);

FnNtQuerySystemInformation g_ntqsi = nullptr;

struct SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION
{
    LARGE_INTEGER IdleTime;
    LARGE_INTEGER KernelTime;
    LARGE_INTEGER UserTime;
    LARGE_INTEGER DpcTime;
    LARGE_INTEGER InterruptTime;
    ULONG         InterruptCount;
};
// 在 x64 MSVC 下 sizeof=8*5 + 4=44，然后 4 字节对齐填充到 48（常见结构体对齐）。
// 不同编译器/平台可能略有差异，以实际为准。
static_assert(sizeof(SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION) == 44 ||
              sizeof(SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION) == 48,
              "SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION size unexpected");

constexpr ULONG kSystemProcessorPerformanceInformation = 8;

bool EnsureNtqsi()
{
    if (g_ntqsi) return true;
    HMODULE mod = GetModuleHandleW(L"ntdll.dll");
    if (!mod) mod = LoadLibraryW(L"ntdll.dll");
    if (!mod) return false;
    auto fn = reinterpret_cast<FnNtQuerySystemInformation>(
        GetProcAddress(mod, "NtQuerySystemInformation"));
    if (!fn) return false;
    InterlockedCompareExchangePointer(
        reinterpret_cast<volatile PVOID*>(&g_ntqsi), fn, nullptr);
    return true;
}

// 静态缓存上次的 kernel/user/idle 各分量；首次调用给出基线。
// Windows 的 KernelTime 已包含 IdleTime/DpcTime/InterruptTime，
// 故只需分别累加 kernel/user/idle，用 (dKernel - dIdle) + dUser 求真正忙时，
// 避免把 Dpc/Interrupt 重复计数导致使用率系统性偏高。
struct Cache
{
    LARGE_INTEGER lastKernel;
    LARGE_INTEGER lastUser;
    LARGE_INTEGER lastIdle;
    bool hasBaseline;
    CRITICAL_SECTION lock;
};
Cache g_cache = {{0}, {0}, {0}, false, {}};
bool g_csInit = false;
} // namespace

extern "C" void __stdcall Cpu_ResetCounters(void)
{
    if (!g_csInit) return;
    EnterCriticalSection(&g_cache.lock);
    g_cache.lastKernel.QuadPart = 0;
    g_cache.lastUser.QuadPart = 0;
    g_cache.lastIdle.QuadPart = 0;
    g_cache.hasBaseline = false;
    LeaveCriticalSection(&g_cache.lock);
}

extern "C" int __stdcall Cpu_ReadUtilization(int* utilization, int* ok)
{
    if (!utilization || !ok) return static_cast<int>(E_POINTER);
    *utilization = 0;
    *ok = 0;

    if (!g_csInit)
    {
        InitializeCriticalSection(&g_cache.lock);
        g_csInit = true;
    }
    if (!EnsureNtqsi()) return static_cast<int>(HRESULT_FROM_WIN32(ERROR_PROC_NOT_FOUND));

    // 第一次小尺寸试探获得所需大小
    ULONG retLen = 0;
    NTSTATUS ns = g_ntqsi(kSystemProcessorPerformanceInformation,
                         nullptr, 0, &retLen);
    if (ns != STATUS_INFO_LENGTH_MISMATCH && ns != 0)
    {
        return static_cast<int>(ns);
    }
    if (retLen == 0) return 0;

    auto buf = static_cast<SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION*>(
        calloc(1, retLen));
    if (!buf) return static_cast<int>(E_OUTOFMEMORY);

    ns = g_ntqsi(kSystemProcessorPerformanceInformation, buf, retLen, &retLen);
    if (ns != 0)
    {
        free(buf);
        return static_cast<int>(ns);
    }

    ULONG count = retLen / sizeof(SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION);
    if (count == 0)
    {
        free(buf);
        return 0;
    }

    // 分别累加 kernel / user / idle。注意：KernelTime 已含 IdleTime/DpcTime/InterruptTime，
    // 这里不能再把 DpcTime/InterruptTime 单独加进去，否则它们会被重复计算。
    LARGE_INTEGER kernel{};
    LARGE_INTEGER user{};
    LARGE_INTEGER idle{};
    for (ULONG i = 0; i < count; ++i)
    {
        auto& p = buf[i];
        kernel.QuadPart += p.KernelTime.QuadPart;
        user.QuadPart   += p.UserTime.QuadPart;
        idle.QuadPart   += p.IdleTime.QuadPart;
    }
    free(buf);

    EnterCriticalSection(&g_cache.lock);
    if (!g_cache.hasBaseline)
    {
        g_cache.lastKernel = kernel;
        g_cache.lastUser   = user;
        g_cache.lastIdle   = idle;
        g_cache.hasBaseline = true;
        LeaveCriticalSection(&g_cache.lock);
        *ok = 1;
        *utilization = 0;
        return 0;
    }
    // 两次采样差值。若出现回退（新值 < 旧值），差值为负，按无变化处理并重置基线。
    if (kernel.QuadPart < g_cache.lastKernel.QuadPart ||
        user.QuadPart   < g_cache.lastUser.QuadPart ||
        idle.QuadPart   < g_cache.lastIdle.QuadPart)
    {
        g_cache.lastKernel = kernel;
        g_cache.lastUser   = user;
        g_cache.lastIdle   = idle;
        LeaveCriticalSection(&g_cache.lock);
        *ok = 1;
        *utilization = 0;
        return 0;
    }
    ULONGLONG dKernel = (ULONGLONG)(kernel.QuadPart - g_cache.lastKernel.QuadPart);
    ULONGLONG dUser   = (ULONGLONG)(user.QuadPart   - g_cache.lastUser.QuadPart);
    ULONGLONG dIdle   = (ULONGLONG)(idle.QuadPart   - g_cache.lastIdle.QuadPart);
    g_cache.lastKernel = kernel;
    g_cache.lastUser   = user;
    g_cache.lastIdle   = idle;
    LeaveCriticalSection(&g_cache.lock);

    ULONGLONG total = dKernel + dUser;              // 总 tick（kernel 已含 idle）
    if (total == 0)
    {
        *ok = 1;
        *utilization = 0;
        return 0;
    }
    // dIdle 理论上不超过 dKernel（IdleTime 属于 KernelTime），防御性夹紧。
    if (dIdle > dKernel) dIdle = dKernel;
    ULONGLONG busy = (dKernel - dIdle) + dUser;     // 真正忙的时间（非空闲）
    int pct = (int)((busy * 100ULL + total / 2ULL) / total);
    if (pct < 0) pct = 0;
    if (pct > 100) pct = 100;
    *ok = 1;
    *utilization = pct;
    return 0;
}

extern "C" int __stdcall Cpu_ReadModel(wchar_t* name, int maxChars)
{
    if (!name || maxChars <= 0) return static_cast<int>(E_POINTER);
    name[0] = L'\0';

    // SMBIOS Type4 处理器结构的 Processor Version（偏移 0x10）即系统显示的型号名。
    auto table = Smbios::Read();
    if (!table.ok || !table.raw || table.rawLen == 0) return 0;

    const uint8_t* end = table.raw + table.rawLen;
    const uint8_t* p = table.raw;
    while (p + 4 <= end)
    {
        uint8_t type = p[0];
        size_t len = p[1];
        if (len < 4) { p += 1; continue; }

        if (type == Smbios::TypeProcessor && len >= 0x11)
        {
            // p[0x10] 是 Processor Version 的 1-based 字符串索引
            if (p[0x10] != 0)
            {
                wchar_t buf[512] = {};
                if (Smbios::GetStructString(table.raw, table.rawLen, p, p[0x10], buf, 512))
                {
                    wcsncpy_s(name, (size_t)maxChars, buf, _TRUNCATE);
                }
            }
            return 0;
        }

        // 跳到下一条结构：格式化区(len) 之后是字符串区，终结于双 NULL。
        const uint8_t* ns = p + len;
        const uint8_t* next = ns >= end ? end : ns;
        while (next + 1 < end && !(next[0] == 0 && next[1] == 0)) ++next;
        next = next + 2 <= end ? next + 2 : end;
        if (next <= p) break; // 防御：防止死循环
        p = next;
    }
    return 0;
}

// ---- WMI 热区温度（MSAcpi_ThermalZoneTemperature.CurrentTemperature，单位 0.1K） ----
// 纯 C 风格 COM 访问，不引入任何第三方库；失败一律返回 0/ok=0，不抛异常。
extern "C" int __stdcall Cpu_ReadTemperature(int* temperatureCelsius, int* ok)
{
    if (!temperatureCelsius || !ok) return static_cast<int>(E_POINTER);
    *temperatureCelsius = 0;
    *ok = 0;

    HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    bool coInit = (hr == S_OK || hr == S_FALSE || hr == RPC_E_CHANGED_MODE);
    if (FAILED(hr) && !coInit)
    {
        return static_cast<int>(hr);
    }

    IWbemLocator* locator = nullptr;
    IWbemServices* services = nullptr;
    IEnumWbemClassObject* enumerator = nullptr;

    hr = CoCreateInstance(CLSID_WbemLocator, nullptr, CLSCTX_INPROC_SERVER,
                          IID_IWbemLocator, (void**)&locator);
    if (SUCCEEDED(hr) && locator)
    {
        hr = locator->ConnectServer(
            BSTR(L"ROOT\\WMI"), nullptr, nullptr, nullptr, 0, nullptr, nullptr, &services);
    }

    long long totalTenths = 0;
    int count = 0;

    if (SUCCEEDED(hr) && services)
    {
        hr = services->ExecQuery(
            BSTR(L"WQL"),
            BSTR(L"SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"),
            WBEM_FLAG_FORWARD_ONLY | WBEM_FLAG_RETURN_IMMEDIATELY,
            nullptr, &enumerator);
    }

    if (SUCCEEDED(hr) && enumerator)
    {
        IWbemClassObject* obj = nullptr;
        ULONG returned = 0;
        while (enumerator->Next(WBEM_INFINITE, 1, &obj, &returned) == WBEM_S_NO_ERROR && returned > 0)
        {
            VARIANT v{};
            HRESULT gh = obj->Get(L"CurrentTemperature", 0, &v, nullptr, nullptr);
            if (SUCCEEDED(gh) && v.vt != VT_NULL)
            {
                if (v.vt != VT_I4) VariantChangeType(&v, &v, 0, VT_I4);
                if (v.vt == VT_I4 && v.lVal > 0)
                {
                    totalTenths += v.lVal;   // 单位 0.1K
                    ++count;
                }
            }
            VariantClear(&v);
            obj->Release();
            obj = nullptr;
            returned = 0;
        }
    }

    // MSAcpi 读数 = 开尔文*10，摄氏 = (tenths/10) - 273.15；明显异常的过滤掉。
    if (count > 0)
    {
        double celsius = (totalTenths / 10.0) / count - 273.15;
        if (celsius >= 0 && celsius < 150)
        {
            *temperatureCelsius = (int)(celsius + 0.5);
            *ok = 1;
        }
    }

    if (enumerator) enumerator->Release();
    if (services) services->Release();
    if (locator) locator->Release();
    if (coInit) CoUninitialize();

    return 0;
}
