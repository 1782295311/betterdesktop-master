// BetterDesktopMenuBroker — 进程入口（stdio 一进一出；见 include/ShellBroker.h 的协议说明）
//
// 【为什么 stdio 而不是命名管道】与 shell-convert 的 convert-engine 同款：宿主 CreateProcessW +
//   重定向 stdin/stdout 即可，不需要握手/单实例/管道安全描述符。本 broker 是**一次性**的
//   （一个 CLSID 一次调用）——换来的是崩溃可精确归因：进程非 0 退出 == 这个 CLSID 把 broker 干掉了。
//
// 【退出码契约】0 = 正常应答（含所有"handler 拒绝/不存在"）；非 0 = 进程异常终止（handler 里的 AV 等）。
//   所以这里除了 --self-test 失败之外，永远 return 0。

#include "ShellBroker.h"

#include <windows.h>

#include <string>

namespace
{
    /// <summary>读尽 stdin（宿主写完请求即关闭管道，读到 EOF 为止）。</summary>
    std::string ReadAllStdin()
    {
        std::string data;
        const HANDLE input = ::GetStdHandle(STD_INPUT_HANDLE);
        if (input == nullptr || input == INVALID_HANDLE_VALUE)
        {
            return data;
        }

        char buffer[4096] = {};
        DWORD read = 0;
        while (::ReadFile(input, buffer, sizeof(buffer), &read, nullptr) && read > 0)
        {
            data.append(buffer, static_cast<size_t>(read));
        }
        return data;
    }

    /// <summary>原样写出 UTF-8 字节（不走 printf 的编码转换）。</summary>
    bool WriteAllStdout(const std::string& bytes)
    {
        const HANDLE output = ::GetStdHandle(STD_OUTPUT_HANDLE);
        if (output == nullptr || output == INVALID_HANDLE_VALUE)
        {
            return false;
        }

        size_t offset = 0;
        while (offset < bytes.size())
        {
            DWORD written = 0;
            const DWORD chunk = static_cast<DWORD>(bytes.size() - offset);
            if (!::WriteFile(output, bytes.data() + offset, chunk, &written, nullptr) || written == 0)
            {
                return false;
            }
            offset += written;
        }
        return true;
    }
}

int wmain(int argc, wchar_t** argv)
{
    if (argc >= 2 && argv[1] != nullptr && ::_wcsicmp(argv[1], L"--self-test") == 0)
    {
        return bdshell::broker::SelfTest();
    }

    const std::string request = ReadAllStdin();
    const std::string response = bdshell::broker::ExecuteJson(request);
    WriteAllStdout(response);
    return 0;
}
