// BetterDesktopMenuBroker — 进程外第三方 shell 扩展 broker 实现（见 include/ShellBroker.h）
//
// 【行为与 in-proc 实现（C# ShellMenuInterop）逐条对齐】
//   idCmdFirst=1 / idCmdLast=0x7FFF；CMF_NORMAL(±CMF_EXTENDEDVERBS=0x100)；深度护栏 3；
//   进子菜单前转发 WM_INITMENUPOPUP（IContextMenu3 优先，其次 IContextMenu2）；
//   文本回退链 GetMenuStringW → GCS_VERBW(4) → GCS_HELPTEXTW(5)；ID 落在段外（系统自带插入项）跳过。
//   为什么必须对齐：宿主侧两套路径（broker / in-proc 降级）输出必须同形，否则"有没有 broker"
//   会变成用户可见的菜单差异。
//
// 【两个 AV 根因在 C++ 侧自然消失（§4.1 收益 2）】
//   ① CMINVOKECOMMANDINFOEX 用 SDK 结构体（不再手工排布）→ 不可能再出现 lpDirectoryW 缺位/错位；
//   ② HandleMenuMsg2 的 plResult 是 SDK 签名里的 LRESULT* → 不可能再传空指针。
//
// 【线程模型】全程单 STA 线程 + OleInitialize（STA-only COM 的生死线）。OleInitialize 同时完成
//   CoInitializeEx(STA)，并补上 CLR 那条教训里缺的 Ole 层（剪贴板/拖放/激活）——进程内那两次真机 AV
//   的诱因正是宿主线程缺它。不跨套间，故不需要消息泵。

#include "ShellBroker.h"

#include "JsonLite.h"
#include "Launcher.h"

#include <windows.h>
#include <shellapi.h>   // CMIC_MASK_UNICODE 在 SDK 里是 SEE_MASK_UNICODE 的别名（定义于此头）
#include <shlobj.h>
#include <shobjidl.h>
#include <shlwapi.h>
#include <strsafe.h>

#include <cstdio>
#include <string>
#include <utility>
#include <vector>

namespace bdshell::broker
{
    namespace
    {
        constexpr UINT kCmdFirst = 1;
        constexpr UINT kCmdLast = 0x7FFF;
        constexpr UINT kCmfNormal = 0;
        constexpr UINT kCmfExtendedVerbs = 0x100;
        constexpr UINT kGcsVerbW = 4;
        constexpr UINT kGcsHelpTextW = 5;
        constexpr int kMaxDepth = 3;

        /// <summary>STA + Ole 初始化（一次；RPC_E_CHANGED_MODE 视为已初始化）。</summary>
        struct OleSession
        {
            bool initialized = false;

            OleSession() noexcept
            {
                const HRESULT hr = ::OleInitialize(nullptr);
                initialized = SUCCEEDED(hr) || hr == RPC_E_CHANGED_MODE;
            }

            ~OleSession() noexcept
            {
                if (initialized)
                {
                    ::OleUninitialize();
                }
            }

            OleSession(const OleSession&) = delete;
            OleSession& operator=(const OleSession&) = delete;
        };

        /// <summary>菜单节点（宿主按此 JSON 还原为 ShellVerbItem）。</summary>
        struct Node
        {
            std::wstring text;
            std::wstring verb;
            std::vector<Node> children;
            bool separator = false;
            bool submenu = false;
            bool enabled = true;
            bool checked = false;
        };

        /// <summary>
        /// 菜单文字归一：去访问键标记 &amp;、去首尾空白、剔除 C1 控制符（0x80–0x9F）。
        /// C1 过滤是硬要求——"UTF-8 字节被当成 Latin-1 塞进宽串"产生的乱码恰好落在这个区间，
        /// 当年中文乱码就是靠这个指纹在探针里被抓出来的（见 ShellMenuHostProbe.cpp 头注）。
        /// </summary>
        std::wstring NormalizeText(const std::wstring& raw)
        {
            std::wstring out;
            out.reserve(raw.size());
            for (size_t i = 0; i < raw.size(); ++i)
            {
                const wchar_t ch = raw[i];
                if (ch == L'&')
                {
                    continue; // 访问键标记不进显示文本
                }
                if (ch >= 0x80 && ch <= 0x9F)
                {
                    continue; // C1 控制符 = 编码误读指纹
                }
                if (ch < 0x20 && ch != L'\t')
                {
                    continue;
                }
                out.push_back(ch);
            }

            const size_t begin = out.find_first_not_of(L" \t\r\n");
            if (begin == std::wstring::npos)
            {
                return std::wstring();
            }
            const size_t end = out.find_last_not_of(L" \t\r\n");
            return out.substr(begin, end - begin + 1);
        }

        /// <summary>GCS_VERBW / GCS_HELPTEXTW 取串（宽字符拷进 LPSTR 是 Win32 约定形态）。</summary>
        std::wstring TryCommandString(IContextMenu* contextMenu, UINT offset, UINT type)
        {
            if (contextMenu == nullptr)
            {
                return std::wstring();
            }
            wchar_t buffer[256] = {};
            const UINT capacity = static_cast<UINT>(ARRAYSIZE(buffer));
            const HRESULT hr = contextMenu->GetCommandString(
                static_cast<UINT_PTR>(offset), type, nullptr,
                reinterpret_cast<LPSTR>(buffer), capacity);
            if (FAILED(hr))
            {
                return std::wstring();
            }
            return std::wstring(buffer);
        }

        /// <summary>项文本回退链：HMENU 文本 → verb → 帮助文本（owner-draw/懒文本项的兜底）。</summary>
        std::wstring ReadItemText(HMENU menu, int position, IContextMenu* contextMenu, bool allowFallback, UINT offset)
        {
            wchar_t buffer[256] = {};
            if (::GetMenuStringW(menu, position, buffer, ARRAYSIZE(buffer), MF_BYPOSITION) > 0)
            {
                const std::wstring text = NormalizeText(buffer);
                if (!text.empty())
                {
                    return text;
                }
            }
            if (!allowFallback)
            {
                return std::wstring();
            }

            const std::wstring verb = NormalizeText(TryCommandString(contextMenu, offset, kGcsVerbW));
            if (!verb.empty())
            {
                return verb;
            }
            return NormalizeText(TryCommandString(contextMenu, offset, kGcsHelpTextW));
        }

        /// <summary>WM_INITMENUPOPUP 合成转发：懒填充 handler 此刻才往子菜单里填项。</summary>
        void ForwardInitMenuPopup(IContextMenu3* cm3, IContextMenu2* cm2, HMENU subMenu, int position) noexcept
        {
            try
            {
                const LPARAM lParam = static_cast<LPARAM>(position);
                if (cm3 != nullptr)
                {
                    // LRESULT 必须是真实局部变量：SDK 签名即 LRESULT*，空指针被写入就是 AV（历史根因之一）
                    LRESULT result = 0;
                    cm3->HandleMenuMsg2(WM_INITMENUPOPUP, reinterpret_cast<WPARAM>(subMenu), lParam, &result);
                    return;
                }
                if (cm2 != nullptr)
                {
                    cm2->HandleMenuMsg(WM_INITMENUPOPUP, reinterpret_cast<WPARAM>(subMenu), lParam);
                }
            }
            catch (...)
            {
                // handler 不支持消息转发
            }
        }

        void WalkMenu(HMENU menu, IContextMenu* contextMenu, IContextMenu2* cm2, IContextMenu3* cm3,
            int depth, std::vector<Node>& out)
        {
            if (depth > kMaxDepth)
            {
                return; // 深度护栏（与 in-proc 实现一致）
            }

            const int count = ::GetMenuItemCount(menu);
            for (int position = 0; position < count; ++position)
            {
                const UINT state = ::GetMenuState(menu, position, MF_BYPOSITION);
                if (state == static_cast<UINT>(-1))
                {
                    continue;
                }
                if ((state & MF_SEPARATOR) != 0)
                {
                    Node separator;
                    separator.separator = true;
                    out.push_back(std::move(separator));
                    continue;
                }

                HMENU sub = ::GetSubMenu(menu, position);
                if (sub != nullptr)
                {
                    ForwardInitMenuPopup(cm3, cm2, sub, position);
                    Node node;
                    node.text = ReadItemText(menu, position, nullptr, false, 0);
                    node.submenu = true;
                    WalkMenu(sub, contextMenu, cm2, cm3, depth + 1, node.children);
                    out.push_back(std::move(node));
                    continue;
                }

                const UINT id = ::GetMenuItemID(menu, position);
                if (id < kCmdFirst || id > kCmdLast)
                {
                    continue; // 不归本查询段（系统自带插入项）
                }

                const UINT offset = id - kCmdFirst;
                const std::wstring text = ReadItemText(menu, position, contextMenu, true, offset);
                if (text.empty())
                {
                    continue;
                }

                Node node;
                node.text = text;
                node.verb = TryCommandString(contextMenu, offset, kGcsVerbW);
                node.enabled = (state & (MF_DISABLED | MF_GRAYED)) == 0;
                node.checked = (state & MF_CHECKED) != 0;
                out.push_back(std::move(node));
            }
        }

        /// <summary>构造目标 IDataObject / pidlFolder（与 in-proc 实现同源：pidlFolder 取首个路径所在目录）。</summary>
        bool MakeDataObject(const Request& request, PIDLIST_ABSOLUTE* folder, IDataObject** dataObject) noexcept
        {
            *folder = nullptr;
            *dataObject = nullptr;

            if (request.background)
            {
                // 背景场景：pidlFolder = 桌面、pDataObj = nullptr（背景 handler 的标准初始化形态）
                const HRESULT hr = ::SHGetFolderLocation(nullptr, CSIDL_DESKTOPDIRECTORY, nullptr, 0, folder);
                return SUCCEEDED(hr) && *folder != nullptr;
            }

            if (request.paths.empty())
            {
                return false;
            }

            std::vector<PIDLIST_ABSOLUTE> fulls;
            for (const auto& path : request.paths)
            {
                PIDLIST_ABSOLUTE full = nullptr;
                if (SUCCEEDED(::SHParseDisplayName(path.c_str(), nullptr, &full, 0, nullptr)) && full != nullptr)
                {
                    fulls.push_back(full);
                }
            }
            if (fulls.empty())
            {
                return false;
            }

            *folder = ::ILClone(fulls[0]);
            ::ILRemoveLastID(*folder);

            std::vector<PCUITEMID_CHILD> children;
            children.reserve(fulls.size());
            for (auto* full : fulls)
            {
                children.push_back(::ILFindLastID(full));
            }

            const HRESULT hr = ::SHCreateDataObject(
                *folder, static_cast<UINT>(children.size()), children.data(), nullptr,
                IID_IDataObject, reinterpret_cast<void**>(dataObject));

            for (auto* full : fulls)
            {
                ::ILFree(full);
            }
            return SUCCEEDED(hr) && *dataObject != nullptr;
        }

        /// <summary>实例化一个第三方 handler 并把它的菜单项追加到 out。返回 false 表示"这个 handler 没产出"。</summary>
        bool QueryOneHandler(const Request& request, const std::wstring& clsidText,
            PIDLIST_ABSOLUTE folder, IDataObject* dataObject, std::vector<Node>& out, std::wstring* error)
        {
            CLSID clsid = {};
            if (FAILED(::CLSIDFromString(clsidText.c_str(), &clsid)))
            {
                *error = L"CLSID 非法";
                return false;
            }

            IContextMenu* contextMenu = nullptr;
            const HRESULT created = ::CoCreateInstance(
                clsid, nullptr, CLSCTX_INPROC_SERVER | CLSCTX_LOCAL_SERVER, IID_PPV_ARGS(&contextMenu));
            if (FAILED(created) || contextMenu == nullptr)
            {
                *error = L"CoCreateInstance 失败（未注册或拒绝加载）";
                return false;
            }

            IShellExtInit* shellExtInit = nullptr;
            if (FAILED(contextMenu->QueryInterface(IID_PPV_ARGS(&shellExtInit))) || shellExtInit == nullptr)
            {
                contextMenu->Release();
                *error = L"未实现 IShellExtInit";
                return false;
            }

            const HRESULT initialized = shellExtInit->Initialize(folder, dataObject, nullptr);
            shellExtInit->Release();
            if (FAILED(initialized))
            {
                contextMenu->Release();
                *error = L"Initialize 拒绝";
                return false;
            }

            const HMENU menu = ::CreatePopupMenu();
            if (menu == nullptr)
            {
                contextMenu->Release();
                *error = L"CreatePopupMenu 失败";
                return false;
            }

            const UINT flags = (request.extendedVerbs && !request.background)
                ? (kCmfNormal | kCmfExtendedVerbs)
                : kCmfNormal;
            const HRESULT queried = contextMenu->QueryContextMenu(menu, 0, kCmdFirst, kCmdLast, flags);
            if (::GetMenuItemCount(menu) > 0)
            {
                IContextMenu3* cm3 = nullptr;
                IContextMenu2* cm2 = nullptr;
                if (FAILED(contextMenu->QueryInterface(IID_PPV_ARGS(&cm3))) || cm3 == nullptr)
                {
                    contextMenu->QueryInterface(IID_PPV_ARGS(&cm2));
                }
                WalkMenu(menu, contextMenu, cm2, cm3, 0, out);
                if (cm2 != nullptr)
                {
                    cm2->Release();
                }
                if (cm3 != nullptr)
                {
                    cm3->Release();
                }
            }

            ::DestroyMenu(menu);
            contextMenu->Release();

            if (FAILED(queried) && out.empty())
            {
                *error = L"QueryContextMenu 返回失败";
                return false;
            }
            return true;
        }

        /// <summary>invoke：重建实例后按 verb 调用（verb 是稳定的公开契约，比 ID 偏移稳）。</summary>
        bool InvokeOneHandler(const Request& request, const std::wstring& clsidText,
            PIDLIST_ABSOLUTE folder, IDataObject* dataObject, HRESULT* hr, std::wstring* error)
        {
            *hr = E_FAIL;
            if (request.verb.empty())
            {
                *error = L"verb 为空（动态项不支持按 verb 调用）";
                return false;
            }

            CLSID clsid = {};
            if (FAILED(::CLSIDFromString(clsidText.c_str(), &clsid)))
            {
                *error = L"CLSID 非法";
                return false;
            }

            IContextMenu* contextMenu = nullptr;
            const HRESULT created = ::CoCreateInstance(
                clsid, nullptr, CLSCTX_INPROC_SERVER | CLSCTX_LOCAL_SERVER, IID_PPV_ARGS(&contextMenu));
            if (FAILED(created) || contextMenu == nullptr)
            {
                *error = L"CoCreateInstance 失败（未注册或拒绝加载）";
                return false;
            }

            IShellExtInit* shellExtInit = nullptr;
            if (FAILED(contextMenu->QueryInterface(IID_PPV_ARGS(&shellExtInit))) || shellExtInit == nullptr)
            {
                contextMenu->Release();
                *error = L"未实现 IShellExtInit";
                return false;
            }
            shellExtInit->Initialize(folder, dataObject, nullptr);
            shellExtInit->Release();

            // SDK 结构体：cbSize/fMask 正确、lpDirectoryW 存在（historically 手工排布踩过的坑）
            CMINVOKECOMMANDINFOEX info = {};
            info.cbSize = sizeof(CMINVOKECOMMANDINFOEX);
            info.fMask = CMIC_MASK_UNICODE;
            info.lpVerb = reinterpret_cast<LPCSTR>(request.verb.c_str()); // ANSI 槽：部分 handler 读它
            info.lpVerbW = request.verb.c_str();
            info.nShow = SW_SHOWNORMAL;

            *hr = contextMenu->InvokeCommand(reinterpret_cast<CMINVOKECOMMANDINFO*>(&info));
            contextMenu->Release();
            return true;
        }

        void AppendNodeJson(std::string& out, const Node& node)
        {
            out += "{\"text\":";
            AppendJsonString(out, node.text);
            out += ",\"verb\":";
            AppendJsonString(out, node.verb);
            out += ",\"sep\":";
            out += node.separator ? "true" : "false";
            out += ",\"sub\":";
            out += node.submenu ? "true" : "false";
            out += ",\"enabled\":";
            out += node.enabled ? "true" : "false";
            out += ",\"checked\":";
            out += node.checked ? "true" : "false";
            out += ",\"children\":[";
            for (size_t i = 0; i < node.children.size(); ++i)
            {
                if (i > 0)
                {
                    out += ",";
                }
                AppendNodeJson(out, node.children[i]);
            }
            out += "]}";
        }

        std::string ItemsToJson(const std::vector<Node>& items)
        {
            std::string out;
            out += "\"items\":[";
            for (size_t i = 0; i < items.size(); ++i)
            {
                if (i > 0)
                {
                    out += ",";
                }
                AppendNodeJson(out, items[i]);
            }
            out += "]";
            return out;
        }

        bool DecodeRequest(const json::Value& root, Request* request, std::wstring* error)
        {
            if (!root.IsObject())
            {
                *error = L"请求不是 JSON 对象";
                return false;
            }

            request->op = root.GetString(L"op");
            request->background = root.GetBool(L"background", false);
            request->extendedVerbs = root.GetBool(L"extended", false);
            request->verb = root.GetString(L"verb");

            if (const json::Value* clsids = root.GetArray(L"clsids"))
            {
                for (const auto& item : clsids->array)
                {
                    if (item && item->IsString())
                    {
                        request->clsids.push_back(item->str);
                    }
                }
            }
            if (const json::Value* paths = root.GetArray(L"paths"))
            {
                for (const auto& item : paths->array)
                {
                    if (item && item->IsString())
                    {
                        request->paths.push_back(item->str);
                    }
                }
            }

            if (request->op != L"menu" && request->op != L"invoke")
            {
                *error = L"不支持的 op（只支持 menu / invoke）";
                return false;
            }
            return true;
        }

        std::string ErrorJson(const std::wstring& error)
        {
            std::string out = "{\"ok\":false,\"error\":";
            AppendJsonString(out, error);
            out += ",\"items\":[]}";
            return out;
        }

        std::string ExecuteMenu(const Request& request)
        {
            std::vector<Node> items;
            std::vector<std::wstring> skipped;

            PIDLIST_ABSOLUTE folder = nullptr;
            IDataObject* dataObject = nullptr;
            if (!MakeDataObject(request, &folder, &dataObject))
            {
                if (folder != nullptr)
                {
                    ::ILFree(folder);
                }
                return ErrorJson(L"构造目标 IDataObject 失败（路径不存在或不受 shell 支持）");
            }

            for (const auto& clsid : request.clsids)
            {
                std::wstring error;
                std::vector<Node> produced;
                if (QueryOneHandler(request, clsid, folder, dataObject, produced, &error))
                {
                    items.insert(items.end(),
                        std::make_move_iterator(produced.begin()), std::make_move_iterator(produced.end()));
                }
                else
                {
                    // 单个 handler 失败（未注册 / 拒绝初始化 / 不产出）不失败整次请求——与 in-proc 同语义
                    skipped.push_back(clsid + L":" + error);
                }
            }

            if (dataObject != nullptr)
            {
                dataObject->Release();
            }
            if (folder != nullptr)
            {
                ::ILFree(folder);
            }

            std::string out = "{\"ok\":true,\"count\":";
            out += std::to_string(items.size());
            out += ",";
            out += ItemsToJson(items);
            out += ",\"skipped\":[";
            for (size_t i = 0; i < skipped.size(); ++i)
            {
                if (i > 0)
                {
                    out += ",";
                }
                AppendJsonString(out, skipped[i]);
            }
            out += "]}";
            return out;
        }

        std::string ExecuteInvoke(const Request& request)
        {
            PIDLIST_ABSOLUTE folder = nullptr;
            IDataObject* dataObject = nullptr;
            if (!MakeDataObject(request, &folder, &dataObject))
            {
                if (folder != nullptr)
                {
                    ::ILFree(folder);
                }
                return ErrorJson(L"构造目标 IDataObject 失败（路径不存在或不受 shell 支持）");
            }

            std::string results = "[";
            size_t index = 0;
            bool invokedAny = false;
            for (const auto& clsid : request.clsids)
            {
                HRESULT hr = E_FAIL;
                std::wstring error;
                const bool ok = InvokeOneHandler(request, clsid, folder, dataObject, &hr, &error);
                invokedAny = invokedAny || ok;

                if (index > 0)
                {
                    results += ",";
                }
                ++index;
                results += "{\"clsid\":";
                AppendJsonString(results, clsid);
                results += ",\"invoked\":";
                results += ok ? "true" : "false";
                results += ",\"hr\":";
                results += std::to_string(static_cast<long>(hr));
                results += ",\"error\":";
                AppendJsonString(results, error);
                results += "}";
            }
            results += "]";

            if (dataObject != nullptr)
            {
                dataObject->Release();
            }
            if (folder != nullptr)
            {
                ::ILFree(folder);
            }

            std::string out = "{\"ok\":";
            out += invokedAny ? "true" : "false";
            out += ",\"results\":";
            out += results;
            out += ",\"items\":[]}";
            return out;
        }
    }

    std::string ExecuteJson(const std::string& requestUtf8) noexcept
    {
        try
        {
            std::wstring parseError;
            const json::ValuePtr root = json::Parse(requestUtf8, &parseError);
            if (root == nullptr)
            {
                return ErrorJson(L"请求 JSON 解析失败: " + parseError);
            }

            Request request;
            std::wstring decodeError;
            if (!DecodeRequest(*root, &request, &decodeError))
            {
                return ErrorJson(decodeError);
            }

            // 空 CLSID 列表：不必初始化 COM（也让自检能在任何机器上跑通）
            if (request.clsids.empty())
            {
                return std::string("{\"ok\":true,\"count\":0,\"items\":[],\"skipped\":[]}");
            }

            const OleSession session;
            if (!session.initialized)
            {
                return ErrorJson(L"OleInitialize 失败（STA 未建立，拒绝在可疑套间里加载第三方 handler）");
            }

            return request.op == L"invoke" ? ExecuteInvoke(request) : ExecuteMenu(request);
        }
        catch (...)
        {
            // 绝不让异常越过边界：宿主靠"输出是不是合法 JSON"判断 broker 是否正常应答
            return ErrorJson(L"broker 内部未捕获异常（已转成错误应答）");
        }
    }

    int SelfTest() noexcept
    {
        int failures = 0;
        const auto check = [&failures](bool condition, const char* what)
        {
            std::printf("  [%s] %s\n", condition ? "ok" : "FAIL", what);
            if (!condition)
            {
                ++failures;
            }
        };

        std::printf("== BetterDesktopMenuBroker self-test ==\n");

        {
            const std::string out = ExecuteJson("{ this is not json");
            check(out.find("\"ok\":false") != std::string::npos, "坏 JSON → ok=false（应答仍是合法 JSON）");
        }
        {
            const std::string out = ExecuteJson("{\"op\":\"menu\",\"background\":true,\"clsids\":[]}");
            check(out.find("\"ok\":true") != std::string::npos, "背景场景 + 空 CLSID 列表 → ok=true");
            check(out.find("\"items\":[]") != std::string::npos, "空列表 → items=[]");
        }
        {
            const std::string out = ExecuteJson("{\"op\":\"unknown-op\"}");
            check(out.find("\"ok\":false") != std::string::npos, "未知 op → ok=false");
        }
        {
            // 不存在的 CLSID：必须优雅跳过而不是崩/抛（宿主据此区分"handler 拒绝"与"broker 死了"）
            const std::string out = ExecuteJson(
                "{\"op\":\"menu\",\"background\":true,\"clsids\":[\"{00000000-0000-0000-0000-000000000000}\"]}");
            check(out.find("\"ok\":true") != std::string::npos, "不存在的 CLSID → ok=true（优雅跳过）");
            check(out.find("\"count\":0") != std::string::npos, "不存在的 CLSID → 0 项");
        }
        {
            // 真实 built-in handler（shell32「打开方式」）：证明 COM 链路（实例化 + Initialize +
            // QueryContextMenu）在本机能真正跑通。它不产出项也算通过——本机是否注册不由自检假设。
            const std::string out = ExecuteJson(
                "{\"op\":\"menu\",\"background\":true,\"clsids\":[\"{09799AFB-AD67-11D1-ABCD-00C04FC30936}\"]}");
            check(out.find("\"ok\":true") != std::string::npos, "shell32 OpenWith handler 冒烟 → ok=true");
        }
        {
            // 空列表契约：无目标可做 → ok=true + 0 项（不是错误；错误留给"解析失败/op 不支持"）
            const std::string out = ExecuteJson("{\"op\":\"invoke\",\"background\":true,\"clsids\":[]}");
            check(out.find("\"ok\":true") != std::string::npos, "invoke + 空列表 → ok=true（无目标，不算失败）");
            check(out.find("\"count\":0") != std::string::npos, "invoke + 空列表 → count=0");
        }

        std::printf("合计失败 %d\n", failures);
        return failures;
    }
}
