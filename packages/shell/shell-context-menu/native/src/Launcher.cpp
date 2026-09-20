// BetterDesktopShellMenu — 动作派发实现（见 Launcher.h 说明）

#include "Launcher.h"
#include "JsonLite.h"

#include <windows.h>
#include <shlwapi.h>
#include <strsafe.h>

#include <string>
#include <vector>

namespace bdshell
{
    namespace
    {
        /// <summary>本 DLL 所在目录（尾带反斜杠）。</summary>
        std::wstring ModuleDirectory()
        {
            HMODULE self = nullptr;
            if (!::GetModuleHandleExW(
                    GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                    reinterpret_cast<LPCWSTR>(&ModuleDirectory),
                    &self) ||
                self == nullptr)
            {
                return std::wstring();
            }

            wchar_t path[MAX_PATH * 2] = {};
            const DWORD len = ::GetModuleFileNameW(self, path, ARRAYSIZE(path));
            if (len == 0 || len >= ARRAYSIZE(path))
            {
                return std::wstring();
            }

            std::wstring full(path, len);
            const size_t slash = full.find_last_of(L'\\');
            if (slash == std::wstring::npos)
            {
                return std::wstring();
            }
            return full.substr(0, slash + 1);
        }

        /// <summary>去掉尾部反斜杠后的目录（用于向上走一层）。</summary>
        std::wstring ParentDirectory(const std::wstring& dir)
        {
            if (dir.size() < 2)
            {
                return std::wstring();
            }
            const std::wstring trimmed = dir.substr(0, dir.size() - 1);
            const size_t slash = trimmed.find_last_of(L'\\');
            if (slash == std::wstring::npos || slash + 1 >= trimmed.size())
            {
                return std::wstring();
            }
            return trimmed.substr(0, slash + 1);
        }

        /// <summary>
        /// 同级可执行文件路径；不存在返回空串。
        ///
        /// ⚠️ 必须**向上多找一层**（2026-09-12 实测踩坑，勿回退）：
        ///   DLL 的部署位置是「安装目录\native\BetterDesktopShellMenu.dll」，而
        ///   BetterDesktop.Cli.exe / BetterDesktop.Host.exe 在**安装目录根**。只查
        ///   DLL 自己所在目录（即 native\）永远找不到 → Invoke 进来了却静默放弃，菜单
        ///   显示正常、点下去毫无反应（用户反馈的"花架子"就是这个）。
        /// </summary>
        std::wstring ResolveSiblingExecutable(const wchar_t* fileName)
        {
            if (fileName == nullptr)
            {
                return std::wstring();
            }

            std::wstring dir = ModuleDirectory();
            // 最多向上三层：native\ -> 安装根 -> （再上留给将来 dist\<ver>\ 之类布局）
            for (int level = 0; level < 3 && !dir.empty(); ++level)
            {
                std::wstring candidate = dir + fileName;
                if (::PathFileExistsW(candidate.c_str()))
                {
                    return candidate;
                }
                dir = ParentDirectory(dir);
            }
            return std::wstring();
        }

        /// <summary>写 UTF-8 文件（无 BOM；CLI 侧按 UTF-8 读）。</summary>
        bool WriteAllBytes(const std::wstring& path, const std::string& bytes)
        {
            const HANDLE file = ::CreateFileW(
                path.c_str(), GENERIC_WRITE, 0, nullptr,
                CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
            if (file == INVALID_HANDLE_VALUE)
            {
                return false;
            }
            bool ok = true;
            if (!bytes.empty())
            {
                DWORD written = 0;
                ok = ::WriteFile(file, bytes.data(), static_cast<DWORD>(bytes.size()), &written, nullptr) != FALSE &&
                     written == bytes.size();
            }
            ::CloseHandle(file);
            if (!ok)
            {
                ::DeleteFileW(path.c_str());
            }
            return ok;
        }
    }

    void AppendJsonString(std::string& out, const std::wstring& text)
    {
        out.push_back('"');
        for (const wchar_t ch : text)
        {
            switch (ch)
            {
            case L'"':  out += "\\\""; break;
            case L'\\': out += "\\\\"; break;
            case L'\b': out += "\\b"; break;
            case L'\f': out += "\\f"; break;
            case L'\n': out += "\\n"; break;
            case L'\r': out += "\\r"; break;
            case L'\t': out += "\\t"; break;
            default:
                if (ch < 0x20)
                {
                    char buffer[8] = {};
                    ::StringCchPrintfA(buffer, ARRAYSIZE(buffer), "\\u%04X", static_cast<unsigned>(ch));
                    out += buffer;
                }
                else
                {
                    // 非 ASCII 统一走 UTF-8 编码（批文件是 UTF-8）
                    const std::string utf8 = json::WideToUtf8(std::wstring(1, ch));
                    out += utf8;
                }
                break;
            }
        }
        out.push_back('"');
    }

    std::string BuildBatchJson(const LaunchRequest& request)
    {
        std::string out;
        out.reserve(256 + request.paths.size() * 96);
        out += "{\"version\":1,\"action\":";
        AppendJsonString(out, request.action);

        out += ",\"args\":[";
        for (size_t i = 0; i < request.args.size(); ++i)
        {
            if (i > 0) { out.push_back(','); }
            AppendJsonString(out, request.args[i]);
        }
        out += "],\"paths\":[";
        for (size_t i = 0; i < request.paths.size(); ++i)
        {
            if (i > 0) { out.push_back(','); }
            AppendJsonString(out, request.paths[i]);
        }
        out += "]}";
        return out;
    }

    std::wstring ResolveCliPath()
    {
        return ResolveSiblingExecutable(L"BetterDesktop.Cli.exe");
    }

    std::wstring ResolveHostPath()
    {
        return ResolveSiblingExecutable(L"BetterDesktop.Host.exe");
    }

    bool LaunchAction(const LaunchRequest& request) noexcept
    {
        try
        {
            if (request.action.empty())
            {
                LogLine(L"派发放弃：action 为空");
                return false;
            }

            // CLI 缺失时退到宿主（宿主内部同样有 --menu-batch 分派），保证注册不被"同目录缺 CLI"打断。
            std::wstring target = ResolveCliPath();
            if (target.empty())
            {
                target = ResolveHostPath();
                if (target.empty())
                {
                    LogLine(L"派发放弃：同目录既无 BetterDesktop.Cli.exe 也无 BetterDesktop.Host.exe");
                    return false;
                }
                LogLine(L"CLI 缺失，回退宿主承载: %s", target.c_str());
            }

            // 临时批文件（UTF-8 无 BOM）
            wchar_t tempPath[MAX_PATH] = {};
            const DWORD tempLen = ::GetTempPathW(MAX_PATH, tempPath);
            if (tempLen == 0 || tempLen >= MAX_PATH)
            {
                LogLine(L"派发放弃：GetTempPathW 失败");
                return false;
            }

            wchar_t batchPath[MAX_PATH * 2] = {};
            if (::StringCchPrintfW(
                    batchPath, ARRAYSIZE(batchPath), L"%sbdt-menu-%lu-%llu.json",
                    tempPath, ::GetCurrentProcessId(),
                    static_cast<unsigned long long>(::GetTickCount64())) != S_OK)
            {
                LogLine(L"派发放弃：批文件路径生成失败");
                return false;
            }

            const std::string payload = BuildBatchJson(request);
            if (!WriteAllBytes(batchPath, payload))
            {
                LogLine(L"派发放弃：批文件写入失败 %s", batchPath);
                return false;
            }

            std::wstring commandLine;
            commandLine.reserve(target.size() + 64);
            commandLine += L"\"";
            commandLine += target;
            commandLine += L"\" --menu-batch \"";
            commandLine += batchPath;
            commandLine += L"\"";

            STARTUPINFOW startup = {};
            startup.cb = sizeof(startup);
            PROCESS_INFORMATION process = {};

            // 可写副本：CreateProcessW 可能改写命令行缓冲。
            std::vector<wchar_t> mutableCommand(commandLine.begin(), commandLine.end());
            mutableCommand.push_back(L'\0');

            const BOOL started = ::CreateProcessW(
                nullptr,
                mutableCommand.data(),
                nullptr,
                nullptr,
                FALSE,
                CREATE_NO_WINDOW,
                nullptr,
                nullptr, // 继承当前工作目录：CLI 内部按自身目录解析同目录程序集
                &startup,
                &process);

            if (!started)
            {
                ::DeleteFileW(batchPath);
                LogLine(L"派发失败：CreateProcessW 错误码=%lu target=%s",
                    ::GetLastError(), target.c_str());
                return false;
            }

            // 立即返回，不等 CLI 结束（耗时工作由子进程承担）。
            ::CloseHandle(process.hThread);
            ::CloseHandle(process.hProcess);

            LogLine(L"已派发: action=%s paths=%zu batch=%s",
                request.action.c_str(), request.paths.size(), batchPath);
            return true;
        }
        catch (const std::exception& ex)
        {
            LogLine(L"LaunchAction 异常: %S", ex.what());
            return false;
        }
        catch (...)
        {
            LogLine(L"LaunchAction 未知异常");
            return false;
        }
    }
}
