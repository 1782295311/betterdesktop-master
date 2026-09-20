// BetterDesktopShellMenu — 菜单配置模型实现（含配置缓存、谓词、平台小工具）

#include "MenuModel.h"

#include <windows.h>
#include <shlwapi.h>
#include <shlobj.h>
#include <strsafe.h>

#include <algorithm>
#include <cstdarg>
#include <cstdio>
#include <string>

namespace bdshell
{
    // 存活 COM 对象计数（BdShell.h 声明）。定义在此（本文件是两个 native 目标共享的公共 TU），
    // 使 ShellMenuHandler.cpp 这类只依赖公共逻辑的单元无需链接 dllmain.cpp 也能编译。
    LONG g_liveObjectCount = 0;

    // =====================================================================
    // 平台小工具：诊断日志 + 配置路径
    // =====================================================================

    namespace
    {
        // 诊断日志：追加式、共享读写、失败静默。
        // 只在低频事件（DLL 加载 / 菜单查询 / 派发）写入——绝不进菜单项回调热路径。
        void AppendLog(const wchar_t* line) noexcept
        {
            if (line == nullptr)
            {
                return;
            }
            wchar_t tempPath[MAX_PATH] = {};
            const DWORD len = ::GetTempPathW(MAX_PATH, tempPath);
            if (len == 0 || len >= MAX_PATH)
            {
                return;
            }
            wchar_t fullPath[MAX_PATH * 2] = {};
            if (::StringCchPrintfW(fullPath, ARRAYSIZE(fullPath), L"%sbdt-shellmenu.log", tempPath) != S_OK)
            {
                return;
            }

            const HANDLE file = ::CreateFileW(
                fullPath,
                FILE_APPEND_DATA,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                nullptr,
                OPEN_ALWAYS,
                FILE_ATTRIBUTE_NORMAL,
                nullptr);
            if (file == INVALID_HANDLE_VALUE)
            {
                return;
            }

            SYSTEMTIME now = {};
            ::GetLocalTime(&now);
            wchar_t stamped[2048] = {};
            ::StringCchPrintfW(
                stamped, ARRAYSIZE(stamped),
                L"[%04u-%02u-%02u %02u:%02u:%02u][pid=%lu] %s\r\n",
                now.wYear, now.wMonth, now.wDay, now.wHour, now.wMinute, now.wSecond,
                ::GetCurrentProcessId(), line);

            const size_t bytes = wcslen(stamped) * sizeof(wchar_t);
            DWORD written = 0;
            ::WriteFile(file, stamped, static_cast<DWORD>(bytes), &written, nullptr);
            ::CloseHandle(file);
        }
    }

    void LogLine(const wchar_t* format, ...) noexcept
    {
        if (format == nullptr)
        {
            return;
        }
        wchar_t buffer[1024] = {};
        va_list args;
        va_start(args, format);
        const int written = _vsnwprintf_s(buffer, _TRUNCATE, format, args);
        va_end(args);
        if (written < 0)
        {
            // 超长：截断尾部仍写出首段，便于定位问题
            buffer[ARRAYSIZE(buffer) - 1] = L'\0';
        }
        AppendLog(buffer);
    }

    std::wstring ConfigFilePath()
    {
        PWSTR roaming = nullptr;
        if (FAILED(::SHGetKnownFolderPath(FOLDERID_RoamingAppData, 0, nullptr, &roaming)) ||
            roaming == nullptr)
        {
            return std::wstring();
        }
        std::wstring path(roaming);
        ::CoTaskMemFree(roaming);

        // 与宿主写入端约定：%APPDATA%\BetterDesktop\shellmenu.json
        path += L"\\BetterDesktop\\shellmenu.json";
        return path;
    }

    // =====================================================================
    // 配置解析
    // =====================================================================

    namespace
    {
        MenuScene ParseScene(const std::wstring& text) noexcept
        {
            if (text == L"files") { return MenuScene::Files; }
            if (text == L"directory") { return MenuScene::Directory; }
            // 未知/background 一律按 Background（最小权限：只影响谓词，不影响安全）
            return MenuScene::Background;
        }

        MenuItemKind ParseKind(const std::wstring& text) noexcept
        {
            if (text == L"submenu") { return MenuItemKind::Submenu; }
            if (text == L"toggle") { return MenuItemKind::Toggle; }
            if (text == L"radio") { return MenuItemKind::Radio; }
            return MenuItemKind::Command;
        }

        void ParseStringArray(const json::Value* array, std::vector<std::wstring>& out)
        {
            out.clear();
            if (array == nullptr || !array->IsArray())
            {
                return;
            }
            for (const auto& element : array->array)
            {
                if (element != nullptr && element->IsString())
                {
                    out.push_back(element->str);
                }
            }
        }

        /// <summary>归一化扩展名列表：小写、补前导点。</summary>
        void NormalizeExtensions(std::vector<std::wstring>& list)
        {
            for (auto& item : list)
            {
                std::transform(item.begin(), item.end(), item.begin(),
                    [](wchar_t c) { return static_cast<wchar_t>(::towlower(c)); });
                if (!item.empty() && item[0] != L'.')
                {
                    item.insert(item.begin(), L'.');
                }
            }
        }

        bool ParseItem(const json::Value& node, MenuItemSpec* out, int depth, std::wstring* error)
        {
            if (out == nullptr || !node.IsObject())
            {
                *error = L"items 元素必须是对象";
                return false;
            }
            if (depth > 8)
            {
                *error = L"菜单嵌套层数超限（>8）";
                return false;
            }

            out->id = node.GetString(L"id");
            out->title = node.GetString(L"title");
            out->icon = node.GetString(L"icon");
            out->kind = ParseKind(node.GetString(L"kind", L"command"));
            out->checked = node.GetBool(L"checked", false);
            out->enabled = node.GetBool(L"enabled", true);
            out->highlight = node.GetBool(L"highlight", false);
            out->isDefault = node.GetBool(L"default", false);
            out->action = node.GetString(L"action");
            out->filterSameExtension = node.GetBool(L"filterSameExtension", false);
            out->requiresCountMin = node.GetInt(L"requiresCountMin", 0);

            const json::Value* args = node.GetArray(L"args");
            ParseStringArray(args, out->args);

            const json::Value* scenes = node.GetArray(L"scenes");
            if (scenes != nullptr)
            {
                for (const auto& element : scenes->array)
                {
                    if (element != nullptr && element->IsString())
                    {
                        out->scenes.push_back(ParseScene(element->str));
                    }
                }
            }

            const json::Value* filter = node.Find(L"filter");
            if (filter != nullptr && filter->IsObject())
            {
                ParseStringArray(filter->GetArray(L"extensions"), out->filterExtensions);
                NormalizeExtensions(out->filterExtensions);
                const std::wstring selection = filter->GetString(L"selection");
                if (selection == L"sameExtension")
                {
                    out->filterSameExtension = true;
                }
            }

            ParseStringArray(node.GetArray(L"requiresAllExtIn"), out->requiresAllExtIn);
            NormalizeExtensions(out->requiresAllExtIn);

            // 标题红线：MUIVerb 超长会被系统静默隐藏（见技术力文档 shell-menu-injection 红线 5）。
            constexpr size_t kTitleMaxBytes = 80 * sizeof(wchar_t);
            if (out->title.size() * sizeof(wchar_t) > kTitleMaxBytes)
            {
                out->title.resize(80);
                LogLine(L"配置项标题超 80 字符已截断: id=%s", out->id.c_str());
            }

            const json::Value* children = node.GetArray(L"children");
            if (children != nullptr)
            {
                for (const auto& child : children->array)
                {
                    if (child == nullptr)
                    {
                        continue;
                    }
                    MenuItemSpec spec;
                    if (!ParseItem(*child, &spec, depth + 1, error))
                    {
                        return false;
                    }
                    out->children.push_back(std::move(spec));
                }
            }
            return true;
        }
    }

    bool ParseConfigText(const std::string& utf8, MenuConfig* out, std::wstring* error) noexcept
    {
        if (out == nullptr || error == nullptr)
        {
            return false;
        }
        try
        {
            const json::ValuePtr root = json::Parse(utf8, error);
            if (root == nullptr || !root->IsObject())
            {
                if (error->empty())
                {
                    *error = L"配置文件根节点不是对象";
                }
                return false;
            }

            MenuConfig config;
            config.version = root->GetInt(L"version", 1);
            config.extensionEnabled = root->GetBool(L"extensionEnabled", true);

            const json::Value* items = root->GetArray(L"items");
            if (items != nullptr)
            {
                for (const auto& node : items->array)
                {
                    if (node == nullptr)
                    {
                        continue;
                    }
                    MenuItemSpec spec;
                    if (!ParseItem(*node, &spec, 0, error))
                    {
                        return false;
                    }
                    // 无 title 的项是脏数据：解析阶段丢弃，避免菜单出现空条目。
                    if (spec.title.empty())
                    {
                        LogLine(L"配置项缺 title 已丢弃: id=%s", spec.id.c_str());
                        continue;
                    }
                    config.items.push_back(std::move(spec));
                }
            }

            *out = std::move(config);
            return true;
        }
        catch (const std::exception& ex)
        {
            *error = L"解析配置时发生异常";
            LogLine(L"ParseConfigText 异常: %S", ex.what());
            return false;
        }
        catch (...)
        {
            *error = L"解析配置时发生未知异常";
            return false;
        }
    }

    // =====================================================================
    // 配置快照缓存
    // =====================================================================

    namespace
    {
        SRWLOCK g_configLock = SRWLOCK_INIT;
        bool g_cacheInitialized = false;
        FILETIME g_cacheWriteTime = {};
        ULONGLONG g_cacheSize = 0;
        std::shared_ptr<const MenuConfig> g_cacheConfig;
        std::wstring g_cacheError;

        bool ReadAllBytes(const std::wstring& path, std::string* out, std::wstring* error)
        {
            const HANDLE file = ::CreateFileW(
                path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr,
                OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
            if (file == INVALID_HANDLE_VALUE)
            {
                *error = L"无法打开配置文件";
                return false;
            }

            LARGE_INTEGER size = {};
            if (!::GetFileSizeEx(file, &size) || size.QuadPart < 0 || size.QuadPart > (16 * 1024 * 1024))
            {
                ::CloseHandle(file);
                *error = L"配置文件大小异常（>16MB）";
                return false;
            }

            out->resize(static_cast<size_t>(size.QuadPart));
            if (size.QuadPart > 0)
            {
                DWORD read = 0;
                if (!::ReadFile(file, out->data(), static_cast<DWORD>(out->size()), &read, nullptr))
                {
                    ::CloseHandle(file);
                    *error = L"读取配置文件失败";
                    return false;
                }
                out->resize(read);
            }
            ::CloseHandle(file);
            return true;
        }
    }

    std::shared_ptr<const MenuConfig> LoadConfigFromFile(const std::wstring& path, std::wstring* error) noexcept
    {
        if (error == nullptr)
        {
            return nullptr;
        }
        error->clear();

        try
        {
            if (path.empty())
            {
                *error = L"配置路径为空";
                return nullptr;
            }

            std::string bytes;
            if (!ReadAllBytes(path, &bytes, error))
            {
                return nullptr;
            }

            MenuConfig parsed;
            if (!ParseConfigText(bytes, &parsed, error))
            {
                return nullptr;
            }
            return std::make_shared<const MenuConfig>(std::move(parsed));
        }
        catch (const std::exception& ex)
        {
            *error = L"读取配置异常";
            LogLine(L"LoadConfigFromFile 异常: %S", ex.what());
            return nullptr;
        }
        catch (...)
        {
            *error = L"读取配置未知异常";
            return nullptr;
        }
    }

    std::shared_ptr<const MenuConfig> LoadConfig(std::wstring* error) noexcept
    {
        if (error == nullptr)
        {
            return nullptr;
        }
        error->clear();

        try
        {
            const std::wstring path = ConfigFilePath();
            if (path.empty())
            {
                *error = L"无法解析配置路径（%APPDATA% 不可用）";
                return nullptr;
            }

            WIN32_FILE_ATTRIBUTE_DATA attributes = {};
            if (!::GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &attributes))
            {
                *error = L"配置文件不存在";
                ::AcquireSRWLockExclusive(&g_configLock);
                g_cacheInitialized = true;
                g_cacheConfig.reset();
                g_cacheError = *error;
                ::ReleaseSRWLockExclusive(&g_configLock);
                return nullptr;
            }

            const ULONGLONG size =
                (static_cast<ULONGLONG>(attributes.nFileSizeHigh) << 32) | attributes.nFileSizeLow;

            // 快路径：mtime + size 未变 → 复用已解析结果（菜单构建路径上零解析开销）。
            ::AcquireSRWLockExclusive(&g_configLock);
            if (g_cacheInitialized &&
                g_cacheWriteTime.dwLowDateTime == attributes.ftLastWriteTime.dwLowDateTime &&
                g_cacheWriteTime.dwHighDateTime == attributes.ftLastWriteTime.dwHighDateTime &&
                g_cacheSize == size)
            {
                const auto cached = g_cacheConfig;
                *error = g_cacheError;
                ::ReleaseSRWLockExclusive(&g_configLock);
                return cached;
            }

            // 慢路径：重新读盘解析。此时持有排它锁，保证并发右键不会重复解析。
            std::string bytes;
            std::wstring readError;
            if (!ReadAllBytes(path, &bytes, &readError))
            {
                g_cacheInitialized = true;
                g_cacheWriteTime = attributes.ftLastWriteTime;
                g_cacheSize = size;
                g_cacheConfig.reset();
                g_cacheError = readError;
                ::ReleaseSRWLockExclusive(&g_configLock);
                *error = readError;
                LogLine(L"配置读取失败: %s", readError.c_str());
                return nullptr;
            }

            MenuConfig parsed;
            std::wstring parseError;
            if (!ParseConfigText(bytes, &parsed, &parseError))
            {
                g_cacheInitialized = true;
                g_cacheWriteTime = attributes.ftLastWriteTime;
                g_cacheSize = size;
                g_cacheConfig.reset();
                g_cacheError = parseError;
                ::ReleaseSRWLockExclusive(&g_configLock);
                *error = parseError;
                LogLine(L"配置解析失败: %s", parseError.c_str());
                return nullptr;
            }

            g_cacheInitialized = true;
            g_cacheWriteTime = attributes.ftLastWriteTime;
            g_cacheSize = size;
            g_cacheConfig = std::make_shared<const MenuConfig>(std::move(parsed));
            g_cacheError.clear();
            const auto result = g_cacheConfig;
            ::ReleaseSRWLockExclusive(&g_configLock);

            LogLine(L"配置已加载: 顶级项=%zu extensionEnabled=%s",
                result->items.size(), result->extensionEnabled ? L"true" : L"false");
            return result;
        }
        catch (const std::exception& ex)
        {
            *error = L"加载配置异常";
            LogLine(L"LoadConfig 异常: %S", ex.what());
            return nullptr;
        }
        catch (...)
        {
            *error = L"加载配置未知异常";
            return nullptr;
        }
    }

    void InvalidateConfigCache() noexcept
    {
        ::AcquireSRWLockExclusive(&g_configLock);
        g_cacheInitialized = false;
        g_cacheConfig.reset();
        g_cacheError.clear();
        ::ReleaseSRWLockExclusive(&g_configLock);
    }

    // =====================================================================
    // 谓词与选枝
    // =====================================================================

    std::wstring ExtensionOf(const std::wstring& path)
    {
        const wchar_t* ext = ::PathFindExtensionW(path.c_str());
        if (ext == nullptr || *ext == L'\0')
        {
            return std::wstring();
        }
        std::wstring result(ext);
        std::transform(result.begin(), result.end(), result.begin(),
            [](wchar_t c) { return static_cast<wchar_t>(::towlower(c)); });
        return result;
    }

    namespace
    {
        bool Contains(const std::vector<std::wstring>& list, const std::wstring& value)
        {
            return std::find(list.begin(), list.end(), value) != list.end();
        }
    }

    bool MatchesSelection(const MenuItemSpec& spec, const SelectionContext& selection) noexcept
    {
        try
        {
            // 1) 场景白名单
            if (!spec.scenes.empty() &&
                std::find(spec.scenes.begin(), spec.scenes.end(), selection.scene) == spec.scenes.end())
            {
                return false;
            }

            // 2) 选择数下限
            if (spec.requiresCountMin > 0 &&
                static_cast<int>(selection.paths.size()) < spec.requiresCountMin)
            {
                return false;
            }

            // 3) 扩展名谓词只在文件场景成立（目录/背景没有"文件扩展名"语义）
            const bool hasExtensionPredicate =
                !spec.filterExtensions.empty() || !spec.requiresAllExtIn.empty();
            if (hasExtensionPredicate && selection.scene != MenuScene::Files)
            {
                return false;
            }

            if (!spec.filterExtensions.empty())
            {
                for (const auto& path : selection.paths)
                {
                    if (!Contains(spec.filterExtensions, ExtensionOf(path)))
                    {
                        return false;
                    }
                }
            }

            if (!spec.requiresAllExtIn.empty())
            {
                if (selection.paths.empty())
                {
                    return false;
                }
                for (const auto& path : selection.paths)
                {
                    if (!Contains(spec.requiresAllExtIn, ExtensionOf(path)))
                    {
                        return false;
                    }
                }
            }

            // 4) 同扩展名约束（混合类型不提供批量转换——对齐 ConvertMenuService 既有语义）
            if (spec.filterSameExtension && selection.paths.size() > 1)
            {
                const std::wstring first = ExtensionOf(selection.paths.front());
                for (const auto& path : selection.paths)
                {
                    if (ExtensionOf(path) != first)
                    {
                        return false;
                    }
                }
            }

            return true;
        }
        catch (...)
        {
            // 谓词层绝不因异常放行（宁可不显示）
            return false;
        }
    }

    std::vector<const MenuItemSpec*> SelectVisibleItems(
        const MenuConfig& config, const SelectionContext& selection) noexcept
    {
        std::vector<const MenuItemSpec*> result;
        try
        {
            for (const auto& item : config.items)
            {
                if (MatchesSelection(item, selection))
                {
                    result.push_back(&item);
                }
            }
        }
        catch (...)
        {
            result.clear();
        }
        return result;
    }

    std::vector<const MenuItemSpec*> SelectVisibleChildren(
        const MenuItemSpec& parent, const SelectionContext& selection) noexcept
    {
        std::vector<const MenuItemSpec*> result;
        try
        {
            for (const auto& child : parent.children)
            {
                if (MatchesSelection(child, selection))
                {
                    result.push_back(&child);
                }
            }
        }
        catch (...)
        {
            result.clear();
        }
        return result;
    }
}
