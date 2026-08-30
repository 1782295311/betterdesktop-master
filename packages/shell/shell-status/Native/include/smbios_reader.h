// smbios_reader.h — SMBIOS 原始表读取工具（仅头文件实现，供各 Core DLL 复用）。
// 通过 kernel32!GetSystemFirmwareTable('RSMB', 0) 读取整块原始 SMBIOS 数据，
// 并提供按结构类型遍历 + 结构内 1-based 字符串索引取串的能力。
// 只做底层数据读取，不做任何业务判断；失败返回 ok=false，调用方降级。
#pragma once
#include <windows.h>
#include <cstdint>
#include <vector>
#include <cstring>
#include <cwchar>

namespace Smbios
{
// 结构类型常量：Type 17 内存设备、Type 4 处理器
enum : uint8_t
{
    TypeProcessor = 4,
    TypeMemoryDevice = 17,
};

// 一整块原始 SMBIOS 数据。data 持有生命周期，raw/rawLen 指向 TableData 区。
struct Table
{
    std::vector<uint8_t> data;    // 头部(8字节)+TableData 的整块原始拷贝
    const uint8_t* raw = nullptr; // TableData 起始（已跳过 8 字节公用表头）
    size_t rawLen = 0;            // TableData 长度
    bool ok = false;
};

// 读取 SMBIOS 原始表。失败 ok=false。零外部依赖，GetSystemFirmwareTable 不存在则返回空。
inline Table Read()
{
    Table t;
    typedef UINT (WINAPI* FnGetSystemFirmwareTable)(DWORD, DWORD, PVOID, DWORD);
    HMODULE k = GetModuleHandleW(L"kernel32.dll");
    if (!k) k = LoadLibraryW(L"kernel32.dll");
    if (!k) return t;
    auto fn = reinterpret_cast<FnGetSystemFirmwareTable>(GetProcAddress(k, "GetSystemFirmwareTable"));
    if (!fn) return t;
    DWORD size = fn('RSMB', 0, nullptr, 0);
    if (size < 8) return t;
    t.data.assign(size, 0);
    DWORD used = fn('RSMB', 0, t.data.data(), size);
    if (used < 8)
    {
        t.data.clear();
        return t;
    }
    uint32_t len = 0;
    memcpy(&len, t.data.data() + 4, 4); // 偏移4为 TableData 长度（小端）
    if (len == 0 || len > used - 8)
    {
        t.data.clear();
        return t;
    }
    t.raw = t.data.data() + 8;
    t.rawLen = len;
    t.ok = true;
    return t;
}

// 从结构 p（起始于 raw）的字符串区中取 1-based 索引 index 对应的字符串。
// 成功返回 true 并写入 dst（UTF-16）。字符串区内索引越界/双 NULL 结束返回 false。
inline bool GetStructString(const uint8_t* raw, size_t rawLen, const uint8_t* p,
                            uint8_t index, wchar_t* dst, size_t dstChars)
{
    if (dst && dstChars > 0) dst[0] = L'\0';
    if (index == 0 || !raw || !p) return false;
    if ((size_t)(p - raw) + 4 > rawLen) return false;
    size_t len = p[1];
    if (len < 4) return false;

    const uint8_t* cur = p + len;       // 字符串区起始
    size_t bufChars = dstChars;         // 保护的转换缓冲上限
    if (bufChars > 512) bufChars = 512;
    wchar_t conv[512];

    uint8_t idx = 1;
    while ((size_t)(cur - raw) < rawLen)
    {
        // 找本串结束（'\0'）
        size_t start = (size_t)(cur - raw);
        size_t end = start;
        while (end < rawLen && cur[end - start] != 0) ++end;
        if (end >= rawLen) return false; // 数据异常，无结束符
        size_t contentLen = end - start;
        if (contentLen == 0) return false; // 双 NULL -> 结构字符串区结束

        if (idx == index)
        {
            if (dst && dstChars > 0 && contentLen > 0)
            {
                size_t inLen = contentLen < (bufChars - 1) ? contentLen : (bufChars - 1);
                // 优先按 UTF-8 解码，失败（含 GBK/ANSI 中文）回退为当前代码页
                int cnv = MultiByteToWideChar(CP_UTF8, 0,
                    reinterpret_cast<const char*>(cur), (int)inLen, conv, (int)bufChars);
                if (cnv <= 0)
                {
                    cnv = MultiByteToWideChar(CP_ACP, 0,
                        reinterpret_cast<const char*>(cur), (int)inLen, conv, (int)bufChars);
                }
                if (cnv > 0)
                {
                    size_t cpy = (size_t)cnv;
                    if (cpy >= dstChars) cpy = dstChars - 1;
                    memcpy(dst, conv, cpy * sizeof(wchar_t));
                    dst[cpy] = L'\0';
                }
            }
            return true;
        }
        ++idx;
        cur = raw + end + 1; // 跳到下一个字符串
    }
    return false;
}
} // namespace Smbios