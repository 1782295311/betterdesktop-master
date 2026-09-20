// BetterDesktopShellMenu — 菜单管线探针（本进程内，绝不进 explorer）
//
// 验证链条：shellmenu.json 文本 → MenuConfig → ShellMenuHandler::Initialize/QueryContextMenu → HMENU。
// 这是「配置写对了但菜单没出现」这类问题唯一能在安全环境下复现的位置——先在这里查清楚，
// 再决定是否重启 explorer 验证（explorer 只该用来验证"外壳是否调用我们"，不该当调试器）。
//
// 覆盖：
//   ① 背景场景（pDataObj == NULL，对应目录/桌面空白）
//   ② 文件场景（真实 IDataObject：临时文件 + 真实扩展名，走 SHCreateDataObject）
//   ③ 混合扩展名多选（应触发 filterSameExtension 过滤）
//   ④ 坏配置（应为 0 项且不崩）
//
// 用法：ShellMenuHostProbe.exe [configPath]

#include "MenuModel.h"
#include "ShellMenuHandler.h"

#include <windows.h>
#include <shlobj.h>
#include <shlwapi.h>
#include <strsafe.h>

#include <cstdio>
#include <string>
#include <vector>

namespace
{
    int g_failures = 0;
    int g_checks = 0;

    void Check(bool condition, const char* what)
    {
        ++g_checks;
        if (condition)
        {
            std::printf("  [ok]   %s\n", what);
        }
        else
        {
            ++g_failures;
            std::printf("  [FAIL] %s\n", what);
        }
    }

    /// <summary>
    /// 宽字符 → UTF-8 后打印。
    ///
    /// ⚠️ 绝不能用 printf("%ls") 直接打印菜单文字（2026-09-11 事故根因之一）：
    ///    MSVC 默认 "C" locale 下 printf("%ls") 把每个 wchar_t 按**字节截断**输出。
    ///    而 "UTF-8 字节被当成 Latin-1 塞进宽串" 产生的乱码宽串 U+00E6 U+00A1 U+008C…
    ///    恰好被逐字节吐回原始 UTF-8 字节 E6 A1 8C…，于是**乱码在终端里看起来完全正确**。
    ///    中文乱码就是这样骗过探针、一路漏到 explorer 的。
    /// </summary>
    std::string ToUtf8ForPrint(const wchar_t* text)
    {
        if (text == nullptr)
        {
            return std::string();
        }
        const int needed = ::WideCharToMultiByte(
            CP_UTF8, 0, text, -1, nullptr, 0, nullptr, nullptr);
        if (needed <= 1)
        {
            return std::string();
        }
        std::string utf8(static_cast<size_t>(needed), '\0');
        ::WideCharToMultiByte(CP_UTF8, 0, text, -1, utf8.data(), needed, nullptr, nullptr);
        utf8.resize(static_cast<size_t>(needed) - 1);
        return utf8;
    }

    /// <summary>递归打印 HMENU（含子菜单与选中/禁用态），并统计条目数。</summary>
    void DumpMenu(HMENU menu, int depth, int* itemCount)
    {
        const int count = ::GetMenuItemCount(menu);
        for (int index = 0; index < count; ++index)
        {
            wchar_t text[256] = {};
            const int len = ::GetMenuStringW(menu, index, text, ARRAYSIZE(text), MF_BYPOSITION);
            const UINT state = ::GetMenuState(menu, index, MF_BYPOSITION);

            std::printf("      %*s", depth * 2, "");
            if (len <= 0 || (state & MF_SEPARATOR) != 0)
            {
                std::printf("---- (separator)\n");
                continue;
            }

            std::printf("- %s\n", ToUtf8ForPrint(text).c_str());
            ++(*itemCount);

            HMENU sub = ::GetSubMenu(menu, index);
            if (sub != nullptr)
            {
                DumpMenu(sub, depth + 1, itemCount);
            }
        }
    }

    /// <summary>为给定文件生成真实 shell IDataObject（模拟"用户选中若干文件"）。</summary>
    bool MakeFileDataObject(const std::vector<std::wstring>& files, IDataObject** out) noexcept
    {
        *out = nullptr;
        std::vector<PIDLIST_ABSOLUTE> fulls;
        for (const auto& file : files)
        {
            PIDLIST_ABSOLUTE full = nullptr;
            if (SUCCEEDED(::SHParseDisplayName(file.c_str(), nullptr, &full, 0, nullptr)) && full != nullptr)
            {
                fulls.push_back(full);
            }
        }
        if (fulls.empty())
        {
            return false;
        }

        // 所有文件必须同目录（本探针只造同目录场景）。以第一个文件所在目录为 pidlFolder。
        PIDLIST_ABSOLUTE folder = ::ILClone(fulls[0]);
        ::ILRemoveLastID(folder);

        std::vector<PCUITEMID_CHILD> children;
        for (const auto& full : fulls)
        {
            children.push_back(::ILFindLastID(full));
        }

        // 签名：SHCreateDataObject(pidlFolder, cidl, apidl, pdtInner, riid, ppv)
        const HRESULT hr = ::SHCreateDataObject(
            folder, static_cast<UINT>(children.size()), children.data(), nullptr, IID_IDataObject,
            reinterpret_cast<void**>(out));

        ::ILFree(folder);
        for (const auto& full : fulls)
        {
            ::ILFree(full);
        }
        return SUCCEEDED(hr) && *out != nullptr;
    }

    /// <summary>在 %TEMP% 下建一个空文件（带真实扩展名），用于构造文件场景。</summary>
    bool CreateTempFileWithExtension(const wchar_t* extension, std::wstring* outPath) noexcept
    {
        wchar_t temp[MAX_PATH] = {};
        if (::GetTempPathW(MAX_PATH, temp) == 0)
        {
            return false;
        }
        wchar_t path[MAX_PATH * 2] = {};
        if (FAILED(::StringCchPrintfW(path, ARRAYSIZE(path), L"%sbdshellprobe-%lu%s",
                temp, ::GetCurrentProcessId(), extension)))
        {
            return false;
        }
        const HANDLE file = ::CreateFileW(path, GENERIC_WRITE, 0, nullptr,
            CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE)
        {
            return false;
        }
        ::CloseHandle(file);
        *outPath = path;
        return true;
    }

    /// <summary>构造一次处理器并跑 QueryContextMenu，返回菜单条目数（含子菜单项）。</summary>
    int RunScene(const wchar_t* title, bool background, const std::vector<std::wstring>& files, bool dump)
    {
        std::printf("  --- 场景: %ls ---\n", title);

        bdshell::ShellMenuHandler handler;

        IDataObject* dataObject = nullptr;
        if (!background && !MakeFileDataObject(files, &dataObject))
        {
            std::printf("      (无法构造 IDataObject，跳过)\n");
            ++g_failures;
            return -1;
        }

        handler.Initialize(nullptr, dataObject, nullptr);

        HMENU menu = ::CreatePopupMenu();
        int items = 0;
        const HRESULT hr = handler.QueryContextMenu(menu, 0, 1, 0x7FFE, 0);
        const int topLevel = ::GetMenuItemCount(menu);

        std::printf("      hr=0x%08X 顶级项=%d\n", static_cast<unsigned>(hr), topLevel);
        if (dump)
        {
            DumpMenu(menu, 3, &items);
        }

        ::DestroyMenu(menu);
        if (dataObject != nullptr)
        {
            dataObject->Release();
        }
        return items;
    }
    /// <summary>
    /// 把测试配置安装到「生产路径」并备份已有文件。
    ///
    /// 为什么必须装到生产路径：QueryContextMenu 内部调用的是带缓存的 LoadConfig()，
    /// 它只看 %APPDATA%\BetterDesktop\shellmenu.json。若探针只解析自己的副本，测到的
    /// 会是"配置不存在 → 0 项"，与真实链路脱节（这是本探针第一版踩过的坑）。
    /// 备份/还原保证任何时候都不破坏用户既有配置。
    /// </summary>
    struct ConfigSandbox
    {
        std::wstring productionPath;
        std::wstring backupPath;
        bool hadExisting = false;
        bool installed = false;

        ~ConfigSandbox() { Restore(); }

        bool Install(const std::wstring& sourcePath) noexcept
        {
            productionPath = bdshell::ConfigFilePath();
            if (productionPath.empty())
            {
                return false;
            }

            // 目标目录
            const size_t slash = productionPath.find_last_of(L'\\');
            if (slash != std::wstring::npos)
            {
                const std::wstring dir = productionPath.substr(0, slash);
                ::CreateDirectoryW(dir.c_str(), nullptr);
            }

            backupPath = productionPath + L".probe-backup";
            hadExisting = ::PathFileExistsW(productionPath.c_str()) != FALSE;
            if (hadExisting)
            {
                ::CopyFileW(productionPath.c_str(), backupPath.c_str(), FALSE);
            }

            if (!::CopyFileW(sourcePath.c_str(), productionPath.c_str(), FALSE))
            {
                return false;
            }
            installed = true;
            return true;
        }

        void Restore() noexcept
        {
            if (!installed)
            {
                return;
            }
            installed = false;
            if (hadExisting)
            {
                ::CopyFileW(backupPath.c_str(), productionPath.c_str(), FALSE);
                ::DeleteFileW(backupPath.c_str());
            }
            else
            {
                ::DeleteFileW(productionPath.c_str());
            }
        }
    };
}

int wmain(int argc, wchar_t** argv)
{
    ::CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);

    std::printf("== ShellMenuHostProbe ==\n");

    std::wstring configPath;
    if (argc >= 2 && argv[1] != nullptr && argv[1][0] != L'\0')
    {
        configPath = argv[1];
    }
    else
    {
        configPath = bdshell::ConfigFilePath();
    }
    std::printf("source: %ls\n", configPath.c_str());

    // QueryContextMenu 只认生产路径 → 必须先把测试配置安装过去（sandbox 在 wmain 结束时还原）。
    const std::wstring productionPath = bdshell::ConfigFilePath();
    ConfigSandbox sandbox;
    if (::_wcsicmp(configPath.c_str(), productionPath.c_str()) != 0)
    {
        if (!sandbox.Install(configPath))
        {
            std::printf("FAIL: 无法把测试配置安装到生产路径 %ls\n", productionPath.c_str());
            ::CoUninitialize();
            return 1;
        }
        std::printf("installed -> %ls (退出时自动还原)\n", productionPath.c_str());
    }
    else
    {
        std::printf("already at production path\n");
    }

    std::wstring error;
    const auto config = bdshell::LoadConfigFromFile(configPath, &error);
    if (config == nullptr)
    {
        std::printf("配置不可用: %ls -> 期望「任何场景都不产生菜单项」\n", error.c_str());
        // 坏配置不得导致崩溃或出现半截菜单
        const int items = RunScene(L"坏配置下重跑", true, {}, false);
        Check(items == 0, "坏配置：背景场景 0 项且未崩溃");
        ::CoUninitialize();
        return g_failures == 0 ? 0 : 1;
    }

    std::printf("配置已解析: 顶级项=%zu extensionEnabled=%ls\n",
        config->items.size(), config->extensionEnabled ? L"true" : L"false");

    // ---- [0] 解析器自检 ----
    // 探针的价值是「不进 explorer 就能复现菜单问题」，但它唯一的盲区恰恰是**文字编码**：
    // 旧版用 printf("%ls") 打印，MSVC "C" locale 按字节截断输出，把乱码宽串又还原成
    // 原始 UTF-8 字节，于是中文乱码在探针里 100% 看起来正常，一路漏到 explorer。
    // 所以这里显式断言宽字符串本身，而不是"打印出来看看"。
    std::printf("\n[0] 解析器自检（UTF-8 解码）\n");
    {
        bdshell::MenuConfig sanity;
        std::wstring sanityError;
        const bool sanityOk = bdshell::ParseConfigText(
            "{\"items\":[{\"id\":\"a\",\"title\":\"桌面控制\",\"action\":\"toggle-key\"}]}",
            &sanity, &sanityError);
        Check(sanityOk && !sanity.items.empty() && sanity.items[0].title == L"桌面控制",
            "内嵌 UTF-8 中文 → 宽串逐码点还原");

        // 真实配置里的中文标题同样必须还原（不能只验内嵌样例）
        bool garbled = false;
        std::string firstTitle;
        for (const auto& item : config->items)
        {
            for (const wchar_t ch : item.title)
            {
                garbled = garbled || (ch >= 0x80 && ch <= 0x9F);
            }
            if (firstTitle.empty())
            {
                firstTitle = ToUtf8ForPrint(item.title.c_str());
            }
        }
        Check(!garbled, "真实配置标题不含 C1 控制符（UTF-8 误读指纹）");
        std::printf("      首个顶级标题(UTF-8) = %s\n", firstTitle.c_str());
    }

    std::printf("\n[1] 背景场景（目录/桌面空白）\n");
    const int backgroundItems = RunScene(L"Background", true, {}, true);
    Check(backgroundItems > 0, "背景场景产生了菜单项");

    std::printf("\n[2] 文件场景（真实 .docx）\n");
    std::wstring docx;
    if (!CreateTempFileWithExtension(L".docx", &docx))
    {
        std::printf("  无法创建临时文件\n");
        ::CoUninitialize();
        return 1;
    }
    const int fileItems = RunScene(L"Files (.docx)", false, { docx }, true);
    Check(fileItems > 0, "文件场景产生了菜单项");

    std::printf("\n[3] 文件场景（混合扩展名 .docx + .md）\n");
    std::wstring md;
    CreateTempFileWithExtension(L".md", &md);
    const int mixedItems = RunScene(L"Files (mixed)", false, { docx, md }, true);
    Check(mixedItems == 0, "混合扩展名：filterSameExtension 生效 -> 0 项");

    std::printf("\n[4] 清理\n");
    ::DeleteFileW(docx.c_str());
    ::DeleteFileW(md.c_str());
    Check(!docx.empty() && !::PathFileExistsW(docx.c_str()), "临时文件已清理");

    std::printf("\n合计: %d 项断言, 失败 %d\n", g_checks, g_failures);
    ::CoUninitialize();
    return g_failures == 0 ? 0 : 1;
}
