// BetterDesktopShellMenu — A 路 IExplorerCommand / IEnumExplorerCommand 实现
//
// 三类对象：
//   RootCommand      — 由清单里的 CLSID 激活，负责「本场景 + 本槽位」的顶级项（通常是子菜单）
//   SpecCommand      — 由 EnumSubCommands 产出的子项（叶子或再一层子菜单），spec 与选择集在
//                      枚举时固化，避免子项再回头解析选择集
//   CommandEnumerator— IEnumExplorerCommand 实现
//
// 红线（MS Learn 明文）：GetTitle/GetIcon/GetState/EnumSubCommands 在 explorer 菜单构建
// 路径上被调用，必须高效——本文件全部只读内存配置 + 短 TTL 记忆，不读盘、不启动进程。

#include "ExplorerCommand.h"
#include "Launcher.h"
#include "ShellMenuHandler.h"

#include <windows.h>
#include <shlwapi.h>
#include <shobjidl_core.h>

#include <algorithm>

namespace bdshell
{
    namespace
    {
        // 同一次菜单构建期内 explorer 会连续调用 GetState/GetTitle/GetIcon/EnumSubCommands。
        // 用短 TTL 记忆把「枚举选择集取路径」的开销摊薄到一次（多选 100 个文件时差别显著）。
        constexpr ULONGLONG kMemoTtlMs = 250;

        void AddRefRange(IExplorerCommand* const* commands, size_t count)
        {
            for (size_t i = 0; i < count; ++i)
            {
                if (commands[i] != nullptr)
                {
                    commands[i]->AddRef();
                }
            }
        }
    }

    void ExtractSelectionPaths(IShellItemArray* items, std::vector<std::wstring>& out) noexcept
    {
        out.clear();
        if (items == nullptr)
        {
            return;
        }
        try
        {
            DWORD count = 0;
            if (FAILED(items->GetCount(&count)))
            {
                return;
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
                    out.emplace_back(filePath);
                    ::CoTaskMemFree(filePath);
                }
            }
        }
        catch (...)
        {
            out.clear();
        }
    }

    // =====================================================================
    // 子项命令：spec 与选择集在枚举时固化
    // =====================================================================

    namespace
    {
        class SpecCommand final : public IExplorerCommand
        {
        public:
            SpecCommand(std::shared_ptr<const MenuConfig> config, const MenuItemSpec* spec,
                MenuScene scene, std::vector<std::wstring> paths, const SelectionContext& selection)
                : _config(std::move(config)), _spec(spec), _scene(scene),
                  _paths(std::move(paths)), _selection(selection)
            {
                _selection.paths = _paths;
            }

            // ---- IUnknown ----
            IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) noexcept override
            {
                if (ppv == nullptr) { return E_POINTER; }
                *ppv = nullptr;
                if (::IsEqualIID(riid, __uuidof(IUnknown)) || ::IsEqualIID(riid, __uuidof(IExplorerCommand)))
                {
                    *ppv = static_cast<IExplorerCommand*>(this);
                    AddRef();
                    return S_OK;
                }
                return E_NOINTERFACE;
            }
            IFACEMETHODIMP_(ULONG) AddRef() noexcept override
            {
                return static_cast<ULONG>(::InterlockedIncrement(&_refCount));
            }
            IFACEMETHODIMP_(ULONG) Release() noexcept override
            {
                const LONG remaining = ::InterlockedDecrement(&_refCount);
                if (remaining == 0) { delete this; }
                return static_cast<ULONG>(remaining);
            }

            // ---- IExplorerCommand ----
            IFACEMETHODIMP GetTitle(IShellItemArray*, LPWSTR* name) noexcept override
            {
                if (name == nullptr) { return E_POINTER; }
                *name = nullptr;
                if (_spec == nullptr || _spec->title.empty()) { return E_NOTIMPL; }
                return ::SHStrDupW(_spec->title.c_str(), name);
            }

            IFACEMETHODIMP GetIcon(IShellItemArray*, LPWSTR* icon) noexcept override
            {
                if (icon == nullptr) { return E_POINTER; }
                *icon = nullptr;
                if (_spec == nullptr) { return E_NOTIMPL; }
                const std::wstring location = ResolveEffectiveIcon(*_spec);
                if (location.empty()) { return E_NOTIMPL; }
                return ::SHStrDupW(location.c_str(), icon);
            }

            IFACEMETHODIMP GetToolTip(IShellItemArray*, LPWSTR* tooltip) noexcept override
            {
                if (tooltip == nullptr) { return E_POINTER; }
                *tooltip = nullptr;
                return E_NOTIMPL;
            }

            IFACEMETHODIMP GetCanonicalName(GUID* canonicalName) noexcept override
            {
                if (canonicalName == nullptr) { return E_POINTER; }
                *canonicalName = GUID_NULL;
                return S_OK;
            }

            IFACEMETHODIMP GetState(IShellItemArray*, BOOL, EXPCMDSTATE* state) noexcept override
            {
                if (state == nullptr) { return E_POINTER; }
                *state = ECS_ENABLED;
                if (_spec == nullptr) { *state = ECS_HIDDEN; return S_OK; }
                if (!_spec->enabled) { *state = ECS_DISABLED; return S_OK; }
                if (_spec->kind == MenuItemKind::Toggle || _spec->kind == MenuItemKind::Radio)
                {
                    *state = ECS_CHECKBOX | (_spec->checked ? ECS_CHECKED : 0);
                }
                return S_OK;
            }

            IFACEMETHODIMP Invoke(IShellItemArray* items, IBindCtx*) noexcept override
            {
                try
                {
                    if (_spec == nullptr || _spec->kind == MenuItemKind::Submenu)
                    {
                        return S_OK; // 子菜单父项不承载动作
                    }
                    LaunchRequest request;
                    request.action = _spec->action;
                    request.args = _spec->args;
                    request.paths = _paths;
                    if (request.paths.empty())
                    {
                        ExtractSelectionPaths(items, request.paths);
                    }
                    return LaunchAction(request) ? S_OK : E_FAIL;
                }
                catch (...)
                {
                    LogLine(L"SpecCommand::Invoke 未捕获异常（已吞）");
                    return E_FAIL;
                }
            }

            IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) noexcept override
            {
                if (flags == nullptr) { return E_POINTER; }
                *flags = ECF_DEFAULT;
                if (_spec != nullptr && _spec->kind == MenuItemKind::Submenu &&
                    !SelectVisibleChildren(*_spec, _selection).empty())
                {
                    *flags = static_cast<EXPCMDFLAGS>(ECF_HASSUBCOMMANDS | ECF_SEPARATORBEFORE);
                }
                return S_OK;
            }

            IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** enumerator) noexcept override;

        private:
            LiveObjectGuard _guard;
            LONG _refCount = 1;
            std::shared_ptr<const MenuConfig> _config;
            const MenuItemSpec* _spec = nullptr;
            MenuScene _scene;
            std::vector<std::wstring> _paths;
            SelectionContext _selection;
        };

        class CommandEnumerator final : public IEnumExplorerCommand
        {
        public:
            explicit CommandEnumerator(std::vector<ComPtr<IExplorerCommand>> commands)
                : _commands(std::move(commands))
            {
            }

            IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) noexcept override
            {
                if (ppv == nullptr) { return E_POINTER; }
                *ppv = nullptr;
                if (::IsEqualIID(riid, __uuidof(IUnknown)) || ::IsEqualIID(riid, __uuidof(IEnumExplorerCommand)))
                {
                    *ppv = static_cast<IEnumExplorerCommand*>(this);
                    AddRef();
                    return S_OK;
                }
                return E_NOINTERFACE;
            }
            IFACEMETHODIMP_(ULONG) AddRef() noexcept override
            {
                return static_cast<ULONG>(::InterlockedIncrement(&_refCount));
            }
            IFACEMETHODIMP_(ULONG) Release() noexcept override
            {
                const LONG remaining = ::InterlockedDecrement(&_refCount);
                if (remaining == 0) { delete this; }
                return static_cast<ULONG>(remaining);
            }

            IFACEMETHODIMP Next(ULONG celt, IExplorerCommand** command, ULONG* fetched) noexcept override
            {
                if (command == nullptr || (celt != 1 && fetched == nullptr))
                {
                    return E_POINTER;
                }
                ULONG produced = 0;
                while (produced < celt && _index < _commands.size())
                {
                    IExplorerCommand* current = _commands[_index].Get();
                    current->AddRef();
                    command[produced] = current;
                    ++_index;
                    ++produced;
                }
                if (fetched != nullptr)
                {
                    *fetched = produced;
                }
                return produced == celt ? S_OK : S_FALSE;
            }

            IFACEMETHODIMP Skip(ULONG celt) noexcept override
            {
                _index = (std::min)(_commands.size(), _index + celt);
                return S_OK;
            }

            IFACEMETHODIMP Reset() noexcept override
            {
                _index = 0;
                return S_OK;
            }

            IFACEMETHODIMP Clone(IEnumExplorerCommand** enumerator) noexcept override
            {
                if (enumerator == nullptr) { return E_POINTER; }
                *enumerator = nullptr;

                std::vector<ComPtr<IExplorerCommand>> copy;
                for (const auto& item : _commands)
                {
                    IExplorerCommand* raw = item.Get();
                    raw->AddRef();
                    ComPtr<IExplorerCommand> clone;
                    clone.Attach(raw);
                    copy.push_back(std::move(clone));
                }

                auto* result = new (std::nothrow) CommandEnumerator(std::move(copy));
                if (result == nullptr) { return E_OUTOFMEMORY; }
                result->_index = _index;
                result->AddRef();
                *enumerator = result;
                return S_OK;
            }

        private:
            LiveObjectGuard _guard;
            LONG _refCount = 1;
            std::vector<ComPtr<IExplorerCommand>> _commands;
            size_t _index = 0;
        };

        IFACEMETHODIMP SpecCommand::EnumSubCommands(IEnumExplorerCommand** enumerator) noexcept
        {
            if (enumerator == nullptr) { return E_POINTER; }
            *enumerator = nullptr;
            try
            {
                if (_spec == nullptr || _spec->kind != MenuItemKind::Submenu)
                {
                    return E_NOTIMPL;
                }
                const auto children = SelectVisibleChildren(*_spec, _selection);
                if (children.empty())
                {
                    return E_NOTIMPL;
                }

                std::vector<ComPtr<IExplorerCommand>> commands;
                commands.reserve(children.size());
                for (const auto* child : children)
                {
                    auto* command = new (std::nothrow)
                        SpecCommand(_config, child, _scene, _paths, _selection);
                    if (command == nullptr)
                    {
                        return E_OUTOFMEMORY;
                    }
                    ComPtr<IExplorerCommand> holder;
                    holder.Attach(static_cast<IExplorerCommand*>(command));
                    commands.push_back(std::move(holder));
                }

                auto* result = new (std::nothrow) CommandEnumerator(std::move(commands));
                if (result == nullptr) { return E_OUTOFMEMORY; }
                result->AddRef();
                *enumerator = result;
                return S_OK;
            }
            catch (...)
            {
                LogLine(L"SpecCommand::EnumSubCommands 未捕获异常（已吞）");
                return E_FAIL;
            }
        }

        // =================================================================
        // 顶级命令：按 (场景, 槽位) 动态解析当前选择集
        // =================================================================

        class RootCommand final : public IExplorerCommand
        {
        public:
            RootCommand(MenuScene scene, UINT slot) noexcept : _scene(scene), _slot(slot) {}

            IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) noexcept override
            {
                if (ppv == nullptr) { return E_POINTER; }
                *ppv = nullptr;
                if (::IsEqualIID(riid, __uuidof(IUnknown)) || ::IsEqualIID(riid, __uuidof(IExplorerCommand)))
                {
                    *ppv = static_cast<IExplorerCommand*>(this);
                    AddRef();
                    return S_OK;
                }
                return E_NOINTERFACE;
            }
            IFACEMETHODIMP_(ULONG) AddRef() noexcept override
            {
                return static_cast<ULONG>(::InterlockedIncrement(&_refCount));
            }
            IFACEMETHODIMP_(ULONG) Release() noexcept override
            {
                const LONG remaining = ::InterlockedDecrement(&_refCount);
                if (remaining == 0) { delete this; }
                return static_cast<ULONG>(remaining);
            }

            IFACEMETHODIMP GetTitle(IShellItemArray* items, LPWSTR* name) noexcept override
            {
                if (name == nullptr) { return E_POINTER; }
                *name = nullptr;
                if (!Resolve(items) || _spec == nullptr) { return E_NOTIMPL; }
                return ::SHStrDupW(_spec->title.c_str(), name);
            }

            IFACEMETHODIMP GetIcon(IShellItemArray* items, LPWSTR* icon) noexcept override
            {
                if (icon == nullptr) { return E_POINTER; }
                *icon = nullptr;
                if (!Resolve(items) || _spec == nullptr) { return E_NOTIMPL; }
                const std::wstring location = ResolveEffectiveIcon(*_spec);
                if (location.empty()) { return E_NOTIMPL; }
                return ::SHStrDupW(location.c_str(), icon);
            }

            IFACEMETHODIMP GetToolTip(IShellItemArray*, LPWSTR* tooltip) noexcept override
            {
                if (tooltip == nullptr) { return E_POINTER; }
                *tooltip = nullptr;
                return E_NOTIMPL;
            }

            IFACEMETHODIMP GetCanonicalName(GUID* canonicalName) noexcept override
            {
                if (canonicalName == nullptr) { return E_POINTER; }
                *canonicalName = GUID_NULL;
                return S_OK;
            }

            IFACEMETHODIMP GetState(IShellItemArray* items, BOOL, EXPCMDSTATE* state) noexcept override
            {
                if (state == nullptr) { return E_POINTER; }
                *state = ECS_HIDDEN;
                if (!Resolve(items) || _spec == nullptr) { return S_OK; }
                if (!_spec->enabled) { *state = ECS_DISABLED; return S_OK; }
                *state = ECS_ENABLED;
                return S_OK;
            }

            IFACEMETHODIMP Invoke(IShellItemArray*, IBindCtx*) noexcept override
            {
                // 顶级项是子菜单（ECF_HASSUBCOMMANDS）时 shell 不会调用 Invoke；
                // 若被调用（例如配置把顶级项写成叶子命令），就派发它自己的动作。
                try
                {
                    if (_spec == nullptr || _spec->kind == MenuItemKind::Submenu)
                    {
                        return S_OK;
                    }
                    LaunchRequest request;
                    request.action = _spec->action;
                    request.args = _spec->args;
                    request.paths = _paths;
                    return LaunchAction(request) ? S_OK : E_FAIL;
                }
                catch (...)
                {
                    LogLine(L"RootCommand::Invoke 未捕获异常（已吞）");
                    return E_FAIL;
                }
            }

            IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) noexcept override
            {
                if (flags == nullptr) { return E_POINTER; }
                // ECF_SEPARATORBEFORE：让我们的项与系统项之间自动出现分隔线（新版菜单里也生效）。
                *flags = static_cast<EXPCMDFLAGS>(ECF_SEPARATORBEFORE);
                if (_spec != nullptr && _spec->kind == MenuItemKind::Submenu)
                {
                    *flags = static_cast<EXPCMDFLAGS>(ECF_HASSUBCOMMANDS | ECF_SEPARATORBEFORE);
                }
                return S_OK;
            }

            IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** enumerator) noexcept override
            {
                if (enumerator == nullptr) { return E_POINTER; }
                *enumerator = nullptr;
                try
                {
                    if (_spec == nullptr || _spec->kind != MenuItemKind::Submenu || _config == nullptr)
                    {
                        return E_NOTIMPL;
                    }
                    const SelectionContext selection{ _scene, _paths };
                    const auto children = SelectVisibleChildren(*_spec, selection);
                    if (children.empty())
                    {
                        return E_NOTIMPL;
                    }

                    std::vector<ComPtr<IExplorerCommand>> commands;
                    commands.reserve(children.size());
                    for (const auto* child : children)
                    {
                        auto* command = new (std::nothrow)
                            SpecCommand(_config, child, _scene, _paths, selection);
                        if (command == nullptr)
                        {
                            return E_OUTOFMEMORY;
                        }
                        ComPtr<IExplorerCommand> holder;
                        holder.Attach(static_cast<IExplorerCommand*>(command));
                        commands.push_back(std::move(holder));
                    }

                    auto* result = new (std::nothrow) CommandEnumerator(std::move(commands));
                    if (result == nullptr) { return E_OUTOFMEMORY; }
                    result->AddRef();
                    *enumerator = result;
                    return S_OK;
                }
                catch (...)
                {
                    LogLine(L"RootCommand::EnumSubCommands 未捕获异常（已吞）");
                    return E_FAIL;
                }
            }

        private:
            /// <summary>
            /// 解析「本场景第 _slot 个可见顶级项」。带 250ms 记忆：同一次菜单构建内
            /// GetState/GetTitle/GetIcon/EnumSubCommands 共享一次选择集枚举。
            /// </summary>
            bool Resolve(IShellItemArray* items) const noexcept
            {
                try
                {
                    const ULONGLONG now = ::GetTickCount64();
                    if (_memoTick != 0 && now - _memoTick <= kMemoTtlMs &&
                        static_cast<const void*>(items) == _memoItems)
                    {
                        return _spec != nullptr;
                    }

                    _memoTick = now;
                    _memoItems = static_cast<const void*>(items);
                    _spec = nullptr;
                    _config.reset();

                    // 背景场景忽略选择集（Directory\Background 可能传 null 或父文件夹，
                    // 两者都会让基于"选中项数量/扩展名"的谓词产生歧义）。
                    if (_scene == MenuScene::Background)
                    {
                        _paths.clear();
                    }
                    else
                    {
                        ExtractSelectionPaths(items, _paths);
                    }

                    std::wstring error;
                    auto config = LoadConfig(&error);
                    if (!config || !config->extensionEnabled)
                    {
                        return false;
                    }

                    const SelectionContext selection{ _scene, _paths };
                    const auto visible = SelectVisibleItems(*config, selection);
                    if (_slot >= visible.size())
                    {
                        return false;
                    }

                    _config = std::move(config);
                    _spec = visible[_slot];
                    return _spec != nullptr;
                }
                catch (...)
                {
                    _spec = nullptr;
                    return false;
                }
            }

            LiveObjectGuard _guard;
            LONG _refCount = 1;
            MenuScene _scene = MenuScene::Background;
            UINT _slot = 0;

            mutable ULONGLONG _memoTick = 0;
            mutable const void* _memoItems = nullptr;
            mutable std::shared_ptr<const MenuConfig> _config;
            mutable const MenuItemSpec* _spec = nullptr;
            mutable std::vector<std::wstring> _paths;
        };
    }

    // =====================================================================
    // CLSID 映射与工厂
    // =====================================================================

    bool MapExplorerClsid(REFCLSID clsid, MenuScene* scene, UINT* slot) noexcept
    {
        if (scene == nullptr || slot == nullptr)
        {
            return false;
        }
        if (::IsEqualCLSID(clsid, kExplorerFiles))
        {
            *scene = MenuScene::Files;
            *slot = 0;
            return true;
        }
        if (::IsEqualCLSID(clsid, kExplorerDirectory))
        {
            *scene = MenuScene::Directory;
            *slot = 0;
            return true;
        }
        if (::IsEqualCLSID(clsid, kExplorerBg))
        {
            *scene = MenuScene::Background;
            *slot = 0;
            return true;
        }
        return false;
    }

    HRESULT CreateExplorerCommand(REFCLSID clsid, REFIID riid, void** ppv) noexcept
    {
        if (ppv == nullptr)
        {
            return E_POINTER;
        }
        *ppv = nullptr;

        MenuScene scene = MenuScene::Background;
        UINT slot = 0;
        if (!MapExplorerClsid(clsid, &scene, &slot))
        {
            return E_NOINTERFACE;
        }

        // 诊断：把「系统到底以哪个 CLSID 激活了我们」落日志。
        // 新菜单只有三个入口（Type=* / Directory / Directory\Background），各对应一个 CLSID。
        // 查"某场景的项为什么没出现"第一步就看系统有没有请求过对应 CLSID：
        //   没请求 = 清单声明没被采信（ItemType 不匹配）；
        //   请求了 = 问题在我们自己的谓词（Resolve/GetState 把项判成了 HIDDEN）。
        // 没有这条日志时，两种情况从外面完全看不出区别（2026-09-12 桌面背景定位耗时即因此）。
        {
            wchar_t clsidText[64] = {};
            if (::StringFromGUID2(clsid, clsidText, ARRAYSIZE(clsidText)) <= 0)
            {
                clsidText[0] = L'\0';
            }
            LogLine(L"A 路激活: CLSID=%s 场景=%s 槽位=%u", clsidText, SceneName(scene), slot);
        }

        auto* command = new (std::nothrow) RootCommand(scene, slot);
        if (command == nullptr)
        {
            return E_OUTOFMEMORY;
        }

        const HRESULT hr = command->QueryInterface(riid, ppv);
        command->Release(); // QI 成功时已 AddRef
        return hr;
    }
}
