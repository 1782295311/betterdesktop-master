// MemoryCore.dll 实现 — GlobalMemoryStatusEx + PSAPI 进程枚举 + 插槽信息。
#include <windows.h>
#include <psapi.h>
#include <stdint.h>
#include <stdlib.h>
#include <cwchar>
#include <vector>
#include <algorithm>

#include "memory_core.h"
#include "smbios_reader.h"

#pragma comment(lib, "psapi.lib")

namespace
{
void CopyNameStride(const wchar_t* src, wchar_t* dst, int strideChars)
{
    if (!dst || strideChars <= 0) return;
    if (strideChars == 1) { dst[0] = L'\0'; return; }
    if (!src) { dst[0] = L'\0'; return; }
    // 先清零整块，避免尾脏
    for (int i = 0; i < strideChars; ++i) dst[i] = L'\0';
    wcsncpy_s(dst, static_cast<size_t>(strideChars), src, _TRUNCATE);
}
} // namespace

extern "C" int __stdcall Mem_ReadGlobal(
    unsigned long long* totalPhysKb,
    unsigned long long* availPhysKb)
{
    if (!totalPhysKb || !availPhysKb) return static_cast<int>(E_POINTER);
    *totalPhysKb = 0;
    *availPhysKb = 0;
    MEMORYSTATUSEX ms{};
    ms.dwLength = sizeof(ms);
    if (!GlobalMemoryStatusEx(&ms))
    {
        return static_cast<int>(HRESULT_FROM_WIN32(GetLastError()));
    }
    *totalPhysKb = (unsigned long long)(ms.ullTotalPhys / 1024ULL);
    *availPhysKb = (unsigned long long)(ms.ullAvailPhys / 1024ULL);
    return 0;
}

extern "C" int __stdcall Mem_ReadTopProcesses(
    int topCount,
    wchar_t* names, int nameStrideChars,
    unsigned long* pids,
    unsigned long long* workingSetKb,
    int* filled)
{
    if (!names || !pids || !workingSetKb || !filled || topCount <= 0 || nameStrideChars <= 0)
    {
        return static_cast<int>(E_POINTER);
    }
    *filled = 0;

    std::vector<DWORD> pidBuf(2048, 0);
    DWORD cbNeeded = 0;
    if (!EnumProcesses(pidBuf.data(), (DWORD)(pidBuf.size() * sizeof(DWORD)), &cbNeeded))
    {
        return static_cast<int>(HRESULT_FROM_WIN32(GetLastError()));
    }
    DWORD nPids = cbNeeded / sizeof(DWORD);

    struct Item {
        DWORD pid;
        ULONGLONG wsKb;
        WCHAR name[MEMCORE_MAX_PROC_NAME_CHARS];
    };
    std::vector<Item> items;
    items.reserve(nPids);

    for (DWORD i = 0; i < nPids; ++i)
    {
        DWORD pid = pidBuf[i];
        if (pid == 0) continue;
        // 只读 WorkingSet 其实只需 QUERY_LIMITED_INFORMATION，删掉 PROCESS_VM_READ，
        // 否则系统进程(受保护)会拒绝访问，导致 Top 列表比任务管理器少一批。
        HANDLE h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
        if (!h) continue;

        PROCESS_MEMORY_COUNTERS_EX pmc{};
        ULONGLONG wsKb = 0;
        if (GetProcessMemoryInfo(h, (PROCESS_MEMORY_COUNTERS*)&pmc, sizeof(pmc)))
        {
            wsKb = pmc.WorkingSetSize / 1024ULL;
        }
        else
        {
            CloseHandle(h);
            continue;
        }
        Item it{};
        it.pid = pid;
        it.wsKb = wsKb;
        wchar_t nameBuf[MAX_PATH * 2] = {};
        DWORD cch = MAX_PATH * 2;
        BOOL gotName = QueryFullProcessImageNameW(h, 0, nameBuf, &cch);
        if (gotName && cch > 0)
        {
            // 只取文件名部分（base name）
            const wchar_t* slash = wcsrchr(nameBuf, L'\\');
            const wchar_t* base = slash ? (slash + 1) : nameBuf;
            wcsncpy_s(it.name, _countof(it.name), base, _TRUNCATE);
        }
        else
        {
            // 失败时回退到 GetModuleBaseName（需PROCESS_VM_READ + PROCESS_QUERY_INFORMATION）
            HMODULE hmod = nullptr;
            DWORD cb = 0;
            if (EnumProcessModulesEx(h, &hmod, sizeof(hmod), &cb, LIST_MODULES_ALL))
            {
                if (!GetModuleBaseNameW(h, hmod, it.name, _countof(it.name)))
                {
                    wcscpy_s(it.name, L"(Unknown)");
                }
            }
            else
            {
                // 用 PID 占位
                swprintf_s(it.name, L"pid:%u", pid);
            }
        }
        CloseHandle(h);
        if (wsKb > 0)
        {
            items.push_back(it);
        }
    }

    int want = min((int)topCount, 16);
    if ((int)items.size() > want)
    {
        std::partial_sort(items.begin(), items.begin() + want, items.end(),
            [](const Item& a, const Item& b) { return a.wsKb > b.wsKb; });
    }
    else
    {
        std::sort(items.begin(), items.end(),
            [](const Item& a, const Item& b) { return a.wsKb > b.wsKb; });
    }

    int n = min((int)items.size(), want);
    for (int i = 0; i < n; ++i)
    {
        wchar_t* row = names + ((ptrdiff_t)i) * nameStrideChars;
        CopyNameStride(items[i].name, row, nameStrideChars);
        pids[i] = items[i].pid;
        workingSetKb[i] = items[i].wsKb;
    }
    *filled = n;
    return 0;
}

extern "C" int __stdcall Mem_ReadPhysicalSlots(
    int slotCount,
    wchar_t* slotPartNumbers, int nameStrideChars,
    unsigned long long* slotSizeKb,
    unsigned int* slotSpeedMhz,
    int* filled)
{
    if (!slotPartNumbers || !slotSizeKb || !slotSpeedMhz || !filled || slotCount <= 0 || nameStrideChars <= 0)
    {
        return static_cast<int>(E_POINTER);
    }
    *filled = 0;

    // 先清零输出
    for (int i = 0; i < slotCount; ++i)
    {
        wchar_t* row = slotPartNumbers + ((ptrdiff_t)i) * nameStrideChars;
        for (int j = 0; j < nameStrideChars; ++j) row[j] = L'\0';
        slotSizeKb[i] = 0;
        slotSpeedMhz[i] = 0;
    }

    // 用 SMBIOS Type17 逐根内存条读取真实 SPD（插槽/厂商/型号/容量/频率）。
    auto table = Smbios::Read();
    if (!table.ok || !table.raw || table.rawLen == 0)
        return 0; // 读取失败时保持空，C# 端会降级显示总容量一行

    const uint8_t* end = table.raw + table.rawLen;
    const uint8_t* p = table.raw;
    int n = 0;
    while (p + 4 <= end && n < slotCount)
    {
        uint8_t type = p[0];
        size_t len = p[1];
        if (len < 4) { p += 1; continue; }

        if (type == Smbios::TypeMemoryDevice && len >= 0x1B)
        {
            // 偏移布局：0x0C Size(2)  0x10 DeviceLocator(1串)  0x15 Speed(2)
            //          0x17 Manufacturer(1串)  0x1A PartNumber(1串)
            uint16_t sizeField = 0;
            memcpy(&sizeField, p + 0x0C, 2);
            // 0=空槽 / 0xFFFF=未安装，跳过；只取实际安装的内存条
            if (sizeField != 0 && sizeField != 0xFFFF)
            {
                uint64_t sizeMb = 0;
                if (sizeField == 0x7FFF && len >= 0x20)
                    memcpy(&sizeMb, p + 0x1C, 4);          // ExtendedSize（MB）
                else if (sizeField & 0x8000)
                    sizeMb = (uint64_t)(sizeField & 0x7FFF) / 1024ULL; // 值=KB
                else
                    sizeMb = sizeField;                    // 值=MB

                uint16_t speed = 0;                        // MT/s（MHz）
                if (len >= 0x17) memcpy(&speed, p + 0x15, 2);

                wchar_t locator[128] = {}, mfr[128] = {}, part[128] = {};
                Smbios::GetStructString(table.raw, table.rawLen, p, p[0x10], locator, 128);
                Smbios::GetStructString(table.raw, table.rawLen, p, p[0x17], mfr, 128);
                Smbios::GetStructString(table.raw, table.rawLen, p, p[0x1A], part, 128);

                // 拼出“更具体”的名称：插槽 · 厂商 型号（与任务管理器口径一致）
                wchar_t model[128] = {};
                if (mfr[0] && part[0]) swprintf_s(model, L"%s %s", mfr, part);
                else if (part[0])      wcscpy_s(model, part);
                else if (mfr[0])       wcscpy_s(model, mfr);

                wchar_t label[MEMCORE_MAX_SERIAL_PART_CHARS] = {};
                if (locator[0] && model[0]) swprintf_s(label, L"%s · %s", locator, model);
                else if (model[0])          swprintf_s(label, L"%s", model);
                else if (locator[0])        swprintf_s(label, L"%s", locator);
                else                        swprintf_s(label, L"PhysicalMemory");

                wchar_t* row = slotPartNumbers + ((ptrdiff_t)n) * nameStrideChars;
                CopyNameStride(label, row, nameStrideChars);
                slotSizeKb[n] = sizeMb > 0 ? (sizeMb * 1024ULL) : 0;
                slotSpeedMhz[n] = speed;
                ++n;
            }
        }

        // 跳到下一条结构：格式化区(len) 之后是字符串区，终结于双 NULL。
        const uint8_t* ns = p + len;
        const uint8_t* next = ns >= end ? end : ns;
        while (next + 1 < end && !(next[0] == 0 && next[1] == 0)) ++next;
        next = next + 2 <= end ? next + 2 : end;
        if (next <= p) break; // 防御：防止死循环
        p = next;
    }
    *filled = n;
    return 0;
}
