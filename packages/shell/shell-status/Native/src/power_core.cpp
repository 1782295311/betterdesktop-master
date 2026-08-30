// PowerCore.dll 实现 — 纯 powrprof/kernel32 封装（零业务逻辑、零 UI）。
// 方案枚举结果用静态缓存保存（GUID + 友好名 + 是否活动），调用间复用。
#include <windows.h>
#include <powrprof.h>
#include <winerror.h>
#include <cstdint>
#include <cstring>
#include <vector>
#include <cwchar>
#include <algorithm>

#include "power_core.h"

#pragma comment(lib, "powrprof.lib")
#pragma comment(lib, "kernel32.lib")

namespace
{
// 方案缓存项。
struct PlanItem
{
    GUID guid;
    wchar_t name[POWERCORE_MAX_NAME_CHARS];
    bool active;
};

std::vector<PlanItem> g_plans;
CRITICAL_SECTION g_planLock;
bool g_planLockInit = false;

inline void EnsurePlanLock()
{
    if (!g_planLockInit)
    {
        InitializeCriticalSection(&g_planLock);
        g_planLockInit = true;
    }
}

// 读方案友好名称。成功返回 true。name 为 UTF-16 输出缓冲。
bool ReadPlanName(const GUID& scheme, wchar_t* out, int cch)
{
    if (!out) return false;
    out[0] = L'\0';
    DWORD size = 0;
    auto hr = ::PowerReadFriendlyName(nullptr, &scheme, nullptr, nullptr, nullptr, &size);
    if (hr != ERROR_SUCCESS || size == 0 || size % 2 != 0)
    {
        return false;
    }
    // size 为 UTF-16 字节数（含结尾 NUL）。
    auto chars = size / sizeof(wchar_t);
    if (chars > cch)
    {
        chars = static_cast<DWORD>(cch);
    }
    // 逐字节读入临时 BYTE 缓冲，再按 UTF-16 解释。
    std::vector<BYTE> raw(size);
    hr = ::PowerReadFriendlyName(nullptr, &scheme, nullptr, nullptr, raw.data(), &size);
    if (hr != ERROR_SUCCESS)
    {
        return false;
    }
    memcpy_s(out, static_cast<size_t>(cch) * sizeof(wchar_t), raw.data(),
             std::min<size_t>(raw.size(), static_cast<size_t>(cch - 1) * sizeof(wchar_t)));
    // 确保 NUL 结尾。
    if (chars > 0)
    {
        out[chars - 1] = L'\0';
    }
    return true;
}
} // namespace

extern "C" int __stdcall Power_ReadStatus(int* acLine, int* batteryFlag, int* percent, int* lifeSeconds)
{
    SYSTEM_POWER_STATUS st{};
    if (!::GetSystemPowerStatus(&st))
    {
        return static_cast<int>(HRESULT_FROM_WIN32(::GetLastError()));
    }
    if (acLine) *acLine = st.ACLineStatus;
    if (batteryFlag) *batteryFlag = st.BatteryFlag;
    if (percent)
    {
        *percent = st.BatteryLifePercent > 100 ? -1 : st.BatteryLifePercent;
    }
    if (lifeSeconds)
    {
        *lifeSeconds = (st.BatteryLifeTime == static_cast<DWORD>(-1)) ? -1 : static_cast<int>(st.BatteryLifeTime);
    }
    return static_cast<int>(S_OK);
}

extern "C" int __stdcall Power_EnumeratePlans(int* planCount)
{
    if (!planCount) return static_cast<int>(E_POINTER);
    EnsurePlanLock();
    EnterCriticalSection(&g_planLock);
    g_plans.clear();
    *planCount = 0;

    // 当前活动方案。
    GUID active{};
    bool haveActive = false;
    {
        GUID* p = nullptr;
        if (::PowerGetActiveScheme(nullptr, &p) == ERROR_SUCCESS && p)
        {
            active = *p;
            haveActive = true;
            ::LocalFree(p);
        }
    }

    // 枚举全部方案。
    for (DWORD i = 0;; ++i)
    {
        DWORD size = sizeof(GUID);
        GUID scheme{};
        auto hr = ::PowerEnumerate(nullptr, nullptr, nullptr, ACCESS_SCHEME, i,
                                   reinterpret_cast<BYTE*>(&scheme), &size);
        if (hr != ERROR_SUCCESS)
        {
            break;
        }
        PlanItem item{};
        item.guid = scheme;
        item.active = haveActive && (scheme == active);
        ReadPlanName(scheme, item.name, POWERCORE_MAX_NAME_CHARS);
        if (item.name[0] == L'\0')
        {
            // 兜底：至少给一个可识别名称（默认/未命名方案）。
            swprintf_s(item.name, L"Power Scheme %lu", i + 1);
        }
        g_plans.push_back(item);
    }

    *planCount = static_cast<int>(g_plans.size());
    LeaveCriticalSection(&g_planLock);
    return static_cast<int>(S_OK);
}

extern "C" int __stdcall Power_GetPlan(int index, GUID* guid, wchar_t* name, int nameCch, int* isActive)
{
    EnsurePlanLock();
    EnterCriticalSection(&g_planLock);
    if (index < 0 || static_cast<size_t>(index) >= g_plans.size())
    {
        LeaveCriticalSection(&g_planLock);
        return static_cast<int>(E_BOUNDS);
    }
    const auto& item = g_plans[static_cast<size_t>(index)];
    if (guid) *guid = item.guid;
    if (name && nameCch > 0)
    {
        wcsncpy_s(name, nameCch, item.name, _TRUNCATE);
    }
    if (isActive) *isActive = item.active ? 1 : 0;
    LeaveCriticalSection(&g_planLock);
    return static_cast<int>(S_OK);
}

// 将指定方案设为当前活动方案。切换成功后同时刷新本地缓存的活动标志，方便 UI 立即反映。
extern "C" int __stdcall Power_SetActivePlan(const GUID* guid)
{
    if (!guid) return static_cast<int>(E_POINTER);
    auto hr = ::PowerSetActiveScheme(nullptr, guid);
    if (hr != ERROR_SUCCESS)
    {
        return static_cast<int>(HRESULT_FROM_WIN32(hr));
    }

    // 仅刷新缓存：活动标志 = 匹配成功者，其余置否。
    EnsurePlanLock();
    EnterCriticalSection(&g_planLock);
    for (auto& item : g_plans)
    {
        item.active = (item.guid == *guid);
    }
    LeaveCriticalSection(&g_planLock);
    return static_cast<int>(S_OK);
}

// ==================== 电池详细读取（IOCTL 直读） ====================
// 通过 SetupAPI 枚举电池设备，再以 IOCTL_BATTERY_QUERY_* 直读容量/健康度/功率/循环等。
// 相比 GetSystemPowerStatus 只能拿到百分比/AC 状态，这里能补全健康度、功率、剩余时间。
#include <setupapi.h>
#include <initguid.h>
#include <devguid.h>
#include <batclass.h>

#pragma comment(lib, "setupapi.lib")

namespace
{
// 电池设备类型码与 IOCTL 控制码（batclass.h 依赖 devioctl.h 的 CTL_CODE，此处显式展开）。
#include <devioctl.h>
#ifndef FILE_DEVICE_BATTERY
#define FILE_DEVICE_BATTERY 0x00000029u
#endif
#define BATT_IOCTL_QUERY_TAG \
    CTL_CODE(FILE_DEVICE_BATTERY, 0x10, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define BATT_IOCTL_QUERY_INFORMATION \
    CTL_CODE(FILE_DEVICE_BATTERY, 0x11, METHOD_BUFFERED, FILE_READ_ACCESS)
#define BATT_IOCTL_QUERY_STATUS \
    CTL_CODE(FILE_DEVICE_BATTERY, 0x13, METHOD_BUFFERED, FILE_ANY_ACCESS)

// 电源状态标志（batclass.h 的 BATTERY_POWER_STATE 位）。
#define BATT_POWER_CHARGING     0x00000008

// 打开第 index 个电池设备句柄；失败返回 INVALID_HANDLE_VALUE。
HANDLE OpenBatteryDevice(int index)
{
    HDEVINFO hdev = ::SetupDiGetClassDevs(&GUID_DEVCLASS_BATTERY, nullptr, nullptr,
                                          DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
    if (hdev == INVALID_HANDLE_VALUE) return INVALID_HANDLE_VALUE;

    SP_DEVICE_INTERFACE_DATA iface{};
    iface.cbSize = sizeof(iface);
    if (!::SetupDiEnumDeviceInterfaces(hdev, nullptr, &GUID_DEVCLASS_BATTERY, index, &iface))
    {
        ::SetupDiDestroyDeviceInfoList(hdev);
        return INVALID_HANDLE_VALUE;
    }

    DWORD required = 0;
    ::SetupDiGetDeviceInterfaceDetail(hdev, &iface, nullptr, 0, &required, nullptr);
    if (required == 0 || required < sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA))
    {
        ::SetupDiDestroyDeviceInfoList(hdev);
        return INVALID_HANDLE_VALUE;
    }

    auto* detail = reinterpret_cast<SP_DEVICE_INTERFACE_DETAIL_DATA*>(malloc(required));
    if (!detail)
    {
        ::SetupDiDestroyDeviceInfoList(hdev);
        return INVALID_HANDLE_VALUE;
    }
    detail->cbSize = sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA);

    HANDLE hDevice = INVALID_HANDLE_VALUE;
    if (::SetupDiGetDeviceInterfaceDetail(hdev, &iface, detail, required, nullptr, nullptr))
    {
        hDevice = ::CreateFileW(detail->DevicePath,
                                GENERIC_READ | GENERIC_WRITE,
                                FILE_SHARE_READ | FILE_SHARE_WRITE,
                                nullptr, OPEN_EXISTING, 0, nullptr);
    }
    free(detail);
    ::SetupDiDestroyDeviceInfoList(hdev);
    return hDevice;
}

// 读电池 tag（设备更换后 tag 会变）。
bool ReadBatteryTag(HANDLE hDevice, ULONG* tag)
{
    DWORD bytes = 0;
    return ::DeviceIoControl(hDevice, BATT_IOCTL_QUERY_TAG,
                             nullptr, 0, tag, sizeof(ULONG), &bytes, nullptr)
        && bytes >= sizeof(ULONG);
}

// 读电池信息（设计/满充容量、循环次数）。
bool ReadBatteryInformation(HANDLE hDevice, ULONG tag, BATTERY_INFORMATION* info)
{
    BATTERY_QUERY_INFORMATION qry{};
    qry.BatteryTag = tag;
    qry.InformationLevel = BatteryInformation;
    DWORD bytes = 0;
    return ::DeviceIoControl(hDevice, BATT_IOCTL_QUERY_INFORMATION,
                             &qry, sizeof(qry), info,
                             sizeof(BATTERY_INFORMATION), &bytes, nullptr)
        && bytes >= sizeof(BATTERY_INFORMATION);
}

// 读电池状态（当前容量/速率/电源状态）。
bool ReadBatteryStatus(HANDLE hDevice, ULONG tag, BATTERY_STATUS* status)
{
    BATTERY_WAIT_STATUS wait{};
    wait.BatteryTag = tag;
    DWORD bytes = 0;
    return ::DeviceIoControl(hDevice, BATT_IOCTL_QUERY_STATUS,
                             &wait, sizeof(wait), status,
                             sizeof(BATTERY_STATUS), &bytes, nullptr)
        && bytes >= sizeof(BATTERY_STATUS);
}
} // namespace

extern "C" int __stdcall Power_ReadBatteryDetail(
    int* hasBattery, int* acOnline, int* charging, int* percent,
    int* currentCapacity, int* fullCapacity, int* designCapacity,
    int* healthPercent, int* rateMw, int* remainingSeconds,
    int* cycleCount, int* temperatureC)
{
    // 全部输出初始化为"未知"。
    if (hasBattery) *hasBattery = 0;
    if (acOnline) *acOnline = 0;
    if (charging) *charging = 0;
    if (percent) *percent = -1;
    if (currentCapacity) *currentCapacity = -1;
    if (fullCapacity) *fullCapacity = -1;
    if (designCapacity) *designCapacity = -1;
    if (healthPercent) *healthPercent = -1;
    if (rateMw) *rateMw = 0;
    if (remainingSeconds) *remainingSeconds = -1;
    if (cycleCount) *cycleCount = -1;
    if (temperatureC) *temperatureC = -1;

    // AC 状态用最可靠的 GetSystemPowerStatus。
    SYSTEM_POWER_STATUS st{};
    if (::GetSystemPowerStatus(&st))
    {
        if (acOnline) *acOnline = (st.ACLineStatus == 1) ? 1 : 0;
    }

    // 枚举所有电池，聚合数据。
    long long totalCurrent = 0, totalFull = 0, totalDesign = 0, totalRate = 0;
    int batteryReadCount = 0;
    bool anyCharging = false;
    int firstCycle = -1;

    for (int i = 0;; ++i)
    {
        HANDLE hDevice = OpenBatteryDevice(i);
        if (hDevice == INVALID_HANDLE_VALUE) break;

        ULONG tag = 0;
        if (ReadBatteryTag(hDevice, &tag))
        {
            BATTERY_INFORMATION info{};
            if (ReadBatteryInformation(hDevice, tag, &info))
            {
                totalFull += (long long)info.FullChargedCapacity;
                totalDesign += (long long)info.DesignedCapacity;
                if (firstCycle < 0 && info.CycleCount > 0) firstCycle = (int)info.CycleCount;
            }

            BATTERY_STATUS status{};
            if (ReadBatteryStatus(hDevice, tag, &status))
            {
                totalCurrent += (long long)status.Capacity;
                totalRate += (long long)status.Rate; // Rate: 正=放电(mW) 负=充电
                if (status.PowerState & BATT_POWER_CHARGING) anyCharging = true;
                batteryReadCount++;
            }
        }
        ::CloseHandle(hDevice);
    }

    if (batteryReadCount == 0)
    {
        return static_cast<int>(S_OK); // 无电池；输出保持初始"未知"。
    }

    if (hasBattery) *hasBattery = 1;
    if (charging) *charging = anyCharging ? 1 : 0;
    if (currentCapacity) *currentCapacity = (int)totalCurrent;
    if (fullCapacity) *fullCapacity = (int)totalFull;
    if (designCapacity) *designCapacity = (int)totalDesign;
    if (rateMw) *rateMw = (int)totalRate;
    if (cycleCount) *cycleCount = firstCycle;

    // 百分比 = 当前容量 / 满充容量（避免除零）。
    if (percent && totalFull > 0)
        *percent = (int)(totalCurrent * 100 / totalFull);

    // 健康度 = 满充容量 / 设计容量。
    if (healthPercent && totalDesign > 0)
        *healthPercent = (int)(totalFull * 100 / totalDesign);

    // 自算剩余时间（仅放电中：速率>0 且当前容量>0）。
    if (remainingSeconds && totalRate > 0 && totalCurrent > 0)
        *remainingSeconds = (int)(totalCurrent * 3600 / totalRate);

    return static_cast<int>(S_OK);
}