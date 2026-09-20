// BetterDesktopShellMenu — B 路处理器实现（IShellExtInit + IContextMenu）

#include "ShellMenuHandler.h"
#include "ComPtrLite.h"
#include "Launcher.h"
#include "JsonLite.h"

#include <windows.h>
#include <shellapi.h> // ExtractIconExW（WIN32_LEAN_AND_MEAN 下 windows.h 不会带上它）
#include <shlwapi.h>
#include <strsafe.h>

#include <algorithm>
#include <cwchar>

namespace bdshell
{
    /// <summary>MenuItemSpec → 可派发描述符（B 路与 A 路共用，保证两条路径动作一致）。</summary>
    CommandDescriptor ToDescriptor(const MenuItemSpec& spec)
    {
        CommandDescriptor descriptor;
        descriptor.id = spec.id;
        descriptor.title = spec.title;
        descriptor.icon = spec.icon;
        descriptor.action = spec.action;
        descriptor.args = spec.args;
        descriptor.isToggle = spec.kind == MenuItemKind::Toggle || spec.kind == MenuItemKind::Radio;
        descriptor.checked = spec.checked;
        descriptor.enabled = spec.enabled;
        return descriptor;
    }

    namespace
    {
        /// <summary>空字符串安全拷贝到 ANSI 缓冲（GetCommandString 用）。</summary>
        void CopyAnsi(char* destination, UINT capacity, const std::wstring& text)
        {
            if (destination == nullptr || capacity == 0)
            {
                return;
            }
            const int written = ::WideCharToMultiByte(
                CP_ACP, 0, text.c_str(), static_cast<int>(text.size()),
                destination, static_cast<int>(capacity) - 1, nullptr, nullptr);
            destination[written < 0 ? 0 : written] = '\0';
        }

        /// <summary>拆分 "path,index"（index 可省略；path 允许被双引号包裹，沿用注册表 Icon 值惯例）。</summary>
        void SplitIconLocation(const std::wstring& location, std::wstring* path, int* index)
        {
            *path = location;
            *index = 0;

            const size_t comma = path->find_last_of(L',');
            if (comma != std::wstring::npos)
            {
                const std::wstring tail = path->substr(comma + 1);
                if (!tail.empty())
                {
                    *index = static_cast<int>(::wcstol(tail.c_str(), nullptr, 10));
                }
                path->resize(comma);
            }

            if (path->size() >= 2 && path->front() == L'"' && path->back() == L'"')
            {
                *path = path->substr(1, path->size() - 2);
            }
        }

        /// <summary>
        /// 图标位置 → 菜单可用位图（失败返回 nullptr，调用方当作"无图标"继续）。
        ///
        /// 【为什么缓存且不主动释放】SetMenuItemBitmaps **不复制**位图：菜单存活期间位图必须
        /// 一直有效，而 HMENU 由 explorer 在弹层结束后销毁，我们拿不到那个时刻。若每次新建
        /// 再释放，菜单绘制时会读到已释放的位图。图标只有一种，进程内缓存一次即封顶。
        /// 尺寸取 SM_CXSMICON（系统小图标），系统会自行缩放，不匹配 DPI 时也不会变形。
        /// </summary>
        HBITMAP LoadIconBitmap(const std::wstring& iconLocation)
        {
            if (iconLocation.empty())
            {
                return nullptr;
            }

            std::wstring path;
            int index = 0;
            SplitIconLocation(iconLocation, &path, &index);
            if (path.empty() || !::PathFileExistsW(path.c_str()))
            {
                return nullptr;
            }

            static std::wstring cachedKey;
            static HBITMAP cachedBitmap = nullptr;
            const std::wstring key = path + L"," + std::to_wstring(index);
            if (cachedBitmap != nullptr && cachedKey == key)
            {
                return cachedBitmap;
            }

            HICON source = nullptr;
            if (::ExtractIconExW(path.c_str(), index, &source, nullptr, 1) == 0 || source == nullptr)
            {
                return nullptr;
            }

            const int width = ::GetSystemMetrics(SM_CXSMICON);
            const int height = ::GetSystemMetrics(SM_CYSMICON);

            BITMAPINFO info = {};
            info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
            info.bmiHeader.biWidth = width;
            info.bmiHeader.biHeight = -height; // 负高度 = 自上而下，避免图标上下翻转
            info.bmiHeader.biPlanes = 1;
            info.bmiHeader.biBitCount = 32;    // 32bpp：保住图标自身的 alpha
            info.bmiHeader.biCompression = BI_RGB;

            void* bits = nullptr;
            HBITMAP bitmap = ::CreateDIBSection(nullptr, &info, DIB_RGB_COLORS, &bits, nullptr, 0);
            if (bitmap == nullptr)
            {
                ::DestroyIcon(source);
                return nullptr;
            }

            const HDC dc = ::CreateCompatibleDC(nullptr);
            if (dc != nullptr)
            {
                const HGDIOBJ previous = ::SelectObject(dc, bitmap);
                ::DrawIconEx(dc, 0, 0, source, width, height, 0, nullptr, DI_NORMAL);
                ::SelectObject(dc, previous);
                ::DeleteDC(dc);
            }
            ::DestroyIcon(source);

            cachedKey = key;
            cachedBitmap = bitmap;
            return cachedBitmap;
        }

        /// <summary>给刚插入的菜单项挂图标（position = 插入位置，与 InsertMenuW 的位置一致）。</summary>
        void ApplyItemIcon(HMENU parent, UINT position, const std::wstring& iconLocation)
        {
            const HBITMAP bitmap = LoadIconBitmap(iconLocation);
            if (bitmap == nullptr)
            {
                return;
            }
            ::SetMenuItemBitmaps(parent, position, MF_BYPOSITION, bitmap, bitmap);
        }
    }

    std::wstring ResolveEffectiveIcon(const MenuItemSpec& spec)
    {
        if (!spec.icon.empty())
        {
            return spec.icon;
        }

        // 回退：自家程序图标。Host.exe 的 ApplicationIcon 就是 Assets\BetterDesktop.ico（索引 0）。
        const std::wstring host = ResolveHostPath();
        if (host.empty())
        {
            return std::wstring();
        }
        return host + L",0";
    }

    // =====================================================================
    // 生命周期
    // =====================================================================

    ShellMenuHandler::ShellMenuHandler() noexcept = default;

    ShellMenuHandler::~ShellMenuHandler() noexcept = default;

    IFACEMETHODIMP ShellMenuHandler::QueryInterface(REFIID riid, void** ppv) noexcept
    {
        if (ppv == nullptr)
        {
            return E_POINTER;
        }
        *ppv = nullptr;

        // ⚠️ 用 __uuidof（编译期常量，由 MIDL_INTERFACE 直接生成），比 extern IID 符号更难写错。
        //
        // 【IID 记忆坑，2026-09-11 SDK 10.0.26100 实证】这三个 GUID 极易记混：
        //     IContextMenu   = {000214E4-0000-0000-C000-000000000046}   ← E4
        //     IShellFolder   = {000214E6-0000-0000-C000-000000000046}   ← E6（不是 IContextMenu！）
        //     IShellExtInit  = {000214E8-0000-0000-C000-000000000046}   ← E8
        // 曾把 E6 当成 IContextMenu 写进探测脚本，导致 QI 被正确拒绝（E_NOINTERFACE）却被误判为
        // DLL 缺陷；README/技术力文档引用这三个值时必须照抄本注释。
        if (::IsEqualIID(riid, __uuidof(IUnknown)) || ::IsEqualIID(riid, __uuidof(IShellExtInit)))
        {
            *ppv = static_cast<IShellExtInit*>(this);
        }
        else if (::IsEqualIID(riid, __uuidof(IContextMenu)))
        {
            *ppv = static_cast<IContextMenu*>(this);
        }
        else
        {
            wchar_t text[64] = {};
            if (::StringFromGUID2(riid, text, ARRAYSIZE(text)) <= 0)
            {
                text[0] = L'\0';
            }
            LogLine(L"QueryInterface 不支持的 IID=%s", text);
            return E_NOINTERFACE;
        }

        AddRef();
        return S_OK;
    }

    IFACEMETHODIMP_(ULONG) ShellMenuHandler::AddRef() noexcept
    {
        return static_cast<ULONG>(::InterlockedIncrement(&_refCount));
    }

    IFACEMETHODIMP_(ULONG) ShellMenuHandler::Release() noexcept
    {
        const LONG remaining = ::InterlockedDecrement(&_refCount);
        if (remaining == 0)
        {
            delete this;
        }
        return static_cast<ULONG>(remaining);
    }

    // =====================================================================
    // 选择集
    // =====================================================================

    void ShellMenuHandler::InferSceneFromSelection() noexcept
    {
        // 背景场景：pDataObj 为空（Directory\Background / 桌面空白）。
        if (_paths.empty())
        {
            _scene = MenuScene::Background;
            return;
        }

        // 目录场景：全部选中项都是目录（用文件属性判断，不看扩展名——"a.b" 目录不能误判为文件）。
        bool allDirectories = true;
        for (const auto& path : _paths)
        {
            const DWORD attributes = ::GetFileAttributesW(path.c_str());
            if (attributes == INVALID_FILE_ATTRIBUTES ||
                (attributes & FILE_ATTRIBUTE_DIRECTORY) == 0)
            {
                allDirectories = false;
                break;
            }
        }
        _scene = allDirectories ? MenuScene::Directory : MenuScene::Files;
    }

    IFACEMETHODIMP ShellMenuHandler::Initialize(
        PCIDLIST_ABSOLUTE pidlFolder, IDataObject* pdtobj, HKEY hkeyProgID) noexcept
    {
        UNREFERENCED_PARAMETER(pidlFolder);
        UNREFERENCED_PARAMETER(hkeyProgID);
        try
        {
            _paths.clear();
            _descriptors.clear();

            if (pdtobj == nullptr)
            {
                // 文件夹背景 / 桌面空白：没有选中对象。
                _scene = MenuScene::Background;
                return S_OK;
            }

            ComPtr<IShellItemArray> items;
            const HRESULT hr = ::SHCreateShellItemArrayFromDataObject(pdtobj, IID_PPV_ARGS(items.Put()));
            if (FAILED(hr) || !items)
            {
                LogLine(L"Initialize: SHCreateShellItemArrayFromDataObject 失败 hr=0x%08X", static_cast<unsigned>(hr));
                _scene = MenuScene::Background;
                return S_OK; // 不返回失败：避免 explorer 反复重试
            }

            DWORD count = 0;
            if (FAILED(items->GetCount(&count)))
            {
                count = 0;
            }

            for (DWORD index = 0; index < count; ++index)
            {
                ComPtr<IShellItem> item;
                if (FAILED(items->GetItemAt(index, item.Put())) || !item)
                {
                    continue;
                }
                PWSTR filePath = nullptr;
                if (SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH, &filePath)) && filePath != nullptr)
                {
                    _paths.emplace_back(filePath);
                    ::CoTaskMemFree(filePath);
                }
            }

            InferSceneFromSelection();

            LogLine(L"Initialize: 场景=%s 选中=%zu", SceneName(_scene), _paths.size());
            return S_OK;
        }
        catch (...)
        {
            // 进程内红线：绝不把异常抛给 explorer
            LogLine(L"Initialize 未捕获异常（已吞）");
            _paths.clear();
            return S_OK;
        }
    }

    // =====================================================================
    // 菜单构建
    // =====================================================================

    IFACEMETHODIMP ShellMenuHandler::QueryContextMenu(
        HMENU hmenu, UINT indexMenu, UINT idCmdFirst, UINT idCmdLast, UINT uFlags) noexcept
    {
        try
        {
            // 只做「默认动作」时（回车键）不参与
            if ((uFlags & CMF_DEFAULTONLY) != 0)
            {
                return MAKE_HRESULT(SEVERITY_SUCCESS, 0, 0);
            }

            _descriptors.clear();

            std::wstring error;
            const auto config = LoadConfig(&error);
            if (!config || !config->extensionEnabled)
            {
                // 无可用配置 → 不显示任何项（宁可不显示也不阻塞/误导）
                return MAKE_HRESULT(SEVERITY_SUCCESS, 0, 0);
            }

            const SelectionContext selection{ _scene, _paths };
            const auto visible = SelectVisibleItems(*config, selection);
            if (visible.empty())
            {
                return MAKE_HRESULT(SEVERITY_SUCCESS, 0, 0);
            }

            // 与系统自带项之间插一条分隔线（explorer 惯例：第三方组独立成块）
            ::InsertMenuW(hmenu, indexMenu, MF_BYPOSITION | MF_SEPARATOR, 0, nullptr);
            UINT position = indexMenu + 1;

            UINT nextId = idCmdFirst;
            UINT inserted = 0;
            for (const auto* spec : visible)
            {
                bool didInsert = false;
                if (!InsertItemRecursive(hmenu, position, *spec, selection, idCmdFirst, idCmdLast, &nextId, &didInsert))
                {
                    break; // ID 用尽：停止（绝不越界，越界会覆盖系统菜单项 ID）
                }
                if (didInsert)
                {
                    ++position;
                    ++inserted;
                }
            }

            if (inserted == 0)
            {
                // 一项都没插进去（例如子菜单空）：把刚插的分隔线撤掉
                ::DeleteMenu(hmenu, indexMenu, MF_BYPOSITION);
                return MAKE_HRESULT(SEVERITY_SUCCESS, 0, 0);
            }

            LogLine(L"QueryContextMenu: 场景=%s 选中=%zu 插入=%u", SceneName(_scene), _paths.size(), inserted);
            return MAKE_HRESULT(SEVERITY_SUCCESS, 0, nextId - idCmdFirst);
        }
        catch (...)
        {
            LogLine(L"QueryContextMenu 未捕获异常（已吞）");
            _descriptors.clear();
            return MAKE_HRESULT(SEVERITY_SUCCESS, 0, 0);
        }
    }

    bool ShellMenuHandler::InsertItemRecursive(
        HMENU parent, UINT position, const MenuItemSpec& spec, const SelectionContext& selection,
        UINT idCmdFirst, UINT idCmdLast, UINT* nextId, bool* inserted) noexcept
    {
        *inserted = false;
        try
        {
            if (spec.kind == MenuItemKind::Submenu)
            {
                const auto children = SelectVisibleChildren(spec, selection);
                if (children.empty())
                {
                    return true; // 空子菜单不显示（不占位）
                }

                HMENU popup = ::CreatePopupMenu();
                if (popup == nullptr)
                {
                    return false;
                }

                bool anyChild = false;
                UINT childPosition = 0;
                for (const auto* child : children)
                {
                    bool childInserted = false;
                    if (!InsertItemRecursive(popup, childPosition, *child, selection, idCmdFirst, idCmdLast, nextId, &childInserted))
                    {
                        ::DestroyMenu(popup);
                        return false;
                    }
                    if (childInserted)
                    {
                        ++childPosition;
                        anyChild = true;
                    }
                }

                if (!anyChild)
                {
                    ::DestroyMenu(popup);
                    return true;
                }

                ::InsertMenuW(parent, position, MF_BYPOSITION | MF_POPUP | MF_STRING,
                    reinterpret_cast<UINT_PTR>(popup), spec.title.c_str());
                ApplyItemIcon(parent, position, ResolveEffectiveIcon(spec));
                *inserted = true;
                return true;
            }

            if (*nextId >= idCmdLast)
            {
                return false; // ID 区间用尽
            }

            const UINT id = *nextId;
            const UINT offset = id - idCmdFirst;
            ++(*nextId);

            // 描述符表按 offset 稠密存放（子菜项的 ID 也在同一区间内）
            if (_descriptors.size() <= offset)
            {
                _descriptors.resize(offset + 1);
            }
            CommandDescriptor& descriptor = _descriptors[offset];
            descriptor.id = spec.id;
            descriptor.title = spec.title;
            descriptor.icon = spec.icon;
            descriptor.action = spec.action;
            descriptor.args = spec.args;
            descriptor.isToggle = spec.kind == MenuItemKind::Toggle || spec.kind == MenuItemKind::Radio;
            descriptor.checked = spec.checked;
            descriptor.enabled = spec.enabled;

            UINT flags = MF_BYPOSITION | MF_STRING;
            if (!spec.enabled)
            {
                flags |= MF_GRAYED;
            }
            if (spec.checked && (spec.kind == MenuItemKind::Toggle || spec.kind == MenuItemKind::Radio))
            {
                flags |= MF_CHECKED;
            }
            if (spec.isDefault)
            {
                flags |= MF_DEFAULT;
            }

            ::InsertMenuW(parent, position, flags, id, spec.title.c_str());
            ApplyItemIcon(parent, position, ResolveEffectiveIcon(spec));
            *inserted = true;
            return true;
        }
        catch (...)
        {
            LogLine(L"InsertItemRecursive 未捕获异常（已吞）id=%s", spec.id.c_str());
            return false;
        }
    }

    // =====================================================================
    // 派发
    // =====================================================================

    IFACEMETHODIMP ShellMenuHandler::InvokeCommand(CMINVOKECOMMANDINFO* pici) noexcept
    {
        try
        {
            if (pici == nullptr)
            {
                return E_INVALIDARG;
            }

            // 字符串 verb（如 "open"/"properties"）：本处理器不提供，交回 shell 处理。
            if (HIWORD(pici->lpVerb) != 0)
            {
                return E_FAIL;
            }

            const UINT offset = LOWORD(pici->lpVerb);
            if (offset >= _descriptors.size())
            {
                LogLine(L"InvokeCommand: 越界 id=%u 描述符=%zu", offset, _descriptors.size());
                return E_INVALIDARG;
            }

            return InvokeByIndex(offset) ? S_OK : E_FAIL;
        }
        catch (...)
        {
            LogLine(L"InvokeCommand 未捕获异常（已吞）");
            return E_FAIL;
        }
    }

    bool ShellMenuHandler::InvokeByIndex(size_t index) noexcept
    {
        try
        {
            if (index >= _descriptors.size())
            {
                return false;
            }
            const CommandDescriptor& descriptor = _descriptors[index];

            LaunchRequest request;
            request.action = descriptor.action;
            request.args = descriptor.args;
            request.paths = _paths;
            return LaunchAction(request);
        }
        catch (...)
        {
            LogLine(L"InvokeByIndex 未捕获异常（已吞）");
            return false;
        }
    }

    IFACEMETHODIMP ShellMenuHandler::GetCommandString(
        UINT_PTR idCmd, UINT uFlags, UINT* pwReserved, LPSTR pszName, UINT cchMax) noexcept
    {
        UNREFERENCED_PARAMETER(pwReserved);
        try
        {
            if (HIWORD(idCmd) != 0)
            {
                return E_INVALIDARG;
            }
            const UINT offset = LOWORD(idCmd);

            const bool valid = offset < _descriptors.size();
            if (uFlags == GCS_VALIDATEA || uFlags == GCS_VALIDATEW)
            {
                return valid ? S_OK : S_FALSE;
            }
            if (!valid)
            {
                return E_INVALIDARG;
            }

            const CommandDescriptor& descriptor = _descriptors[offset];
            switch (uFlags)
            {
            case GCS_VERBA:
                CopyAnsi(pszName, cchMax, descriptor.id);
                return S_OK;
            case GCS_HELPTEXTA:
                CopyAnsi(pszName, cchMax, descriptor.title);
                return S_OK;
            case GCS_VERBW:
                return ::StringCchCopyW(reinterpret_cast<wchar_t*>(pszName), cchMax, descriptor.id.c_str());
            case GCS_HELPTEXTW:
                return ::StringCchCopyW(reinterpret_cast<wchar_t*>(pszName), cchMax, descriptor.title.c_str());
            default:
                return E_NOTIMPL;
            }
        }
        catch (...)
        {
            return E_NOTIMPL;
        }
    }
}
