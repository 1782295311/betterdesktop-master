// BetterDesktopShellMenu — B 路：经典菜单 / Win11「显示更多选项」处理器
//
// 实现 IShellExtInit + IContextMenu（不做 owner-draw，故不需要 IContextMenu2/3）。
// A 路（IExplorerCommand）是独立实现（见 ExplorerCommand.cpp），两条路共用同一份配置
// 模型与同一个派发出口（Launcher），保证"同一功能在两张菜单里行为一致"。
//
// 【进程内红线】本对象由 explorer.exe 直接加载，任何异常/长时间阻塞都会拖垮用户桌面：
//   - 所有 COM 方法体一律 try/catch，绝不向外抛；
//   - QueryContextMenu 只做「读内存缓存 + 建 HMENU」，不读盘、不启动进程；
//   - InvokeCommand 只写小文件 + CreateProcessW，立即返回。
//
// 菜单项 ID 分配：idCmdFirst 起顺序编号；ID 与「动作描述符」按下标一一对应，
// InvokeCommand 的 lpVerb 低字即 id - idCmdFirst。

#pragma once

#include "BdShell.h"
#include "MenuModel.h"

#include <shlobj.h>
#include <shobjidl.h>

#include <string>
#include <vector>

namespace bdshell
{
    /// <summary>一条可派发菜单项的描述符（菜单构建时生成，InvokeCommand 时按 ID 取回）。</summary>
    struct CommandDescriptor
    {
        std::wstring id;
        std::wstring title;
        std::wstring icon;
        std::wstring action;
        std::vector<std::wstring> args;
        // A 路（IExplorerCommand）渲染勾选态/灰显用；B 路直接用 HMENU 标志位表达。
        bool isToggle = false;
        bool checked = false;
        bool enabled = true;
    };

    /// <summary>MenuItemSpec → 可派发描述符（B 路与 A 路共用，保证两条路径动作一致）。</summary>
    CommandDescriptor ToDescriptor(const MenuItemSpec& spec);

    /// <summary>
    /// 菜单项最终使用的图标位置（"path,index" 形式；可能为空）。
    ///
    /// 配置里带 icon 就用配置的；否则**回退到自家程序图标**（<安装目录>\BetterDesktop.Host.exe,0，
    /// 其 ApplicationIcon = Assets\BetterDesktop.ico）。
    ///
    /// 为什么要有回退：菜单项裸奔在系统项中间非常突兀，而"我们的项长得像我们的产品"属于
    /// 展示契约；它不该取决于宿主是否刚好把 icon 写进了配置快照——快照由宿主生成，
    /// 宿主没跑过、或跑的是旧版本，菜单就会全部没图标。
    /// </summary>
    std::wstring ResolveEffectiveIcon(const MenuItemSpec& spec);

    class ShellMenuHandler final : public IShellExtInit, public IContextMenu
    {
    public:
        ShellMenuHandler() noexcept;
        ~ShellMenuHandler() noexcept;

        // ---- IUnknown ----
        IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) noexcept override;
        IFACEMETHODIMP_(ULONG) AddRef() noexcept override;
        IFACEMETHODIMP_(ULONG) Release() noexcept override;

        // ---- IShellExtInit ----
        IFACEMETHODIMP Initialize(PCIDLIST_ABSOLUTE pidlFolder, IDataObject* pdtobj, HKEY hkeyProgID) noexcept override;

        // ---- IContextMenu ----
        IFACEMETHODIMP QueryContextMenu(HMENU hmenu, UINT indexMenu, UINT idCmdFirst, UINT idCmdLast, UINT uFlags) noexcept override;
        IFACEMETHODIMP InvokeCommand(CMINVOKECOMMANDINFO* pici) noexcept override;
        IFACEMETHODIMP GetCommandString(UINT_PTR idCmd, UINT uFlags, UINT* pwReserved, LPSTR pszName, UINT cchMax) noexcept override;

    private:
        LiveObjectGuard _guard; // 存活计数（DllCanUnloadNow 依据）；必须是第一个成员（构造即 +1）
        LONG _refCount = 1;

        MenuScene _scene = MenuScene::Background;
        std::vector<std::wstring> _paths;

        // QueryContextMenu 产生的 ID → 动作映射（下标 = id - idCmdFirst）。
        std::vector<CommandDescriptor> _descriptors;

        void InferSceneFromSelection() noexcept;
        bool InvokeByIndex(size_t index) noexcept;
        bool InsertItemRecursive(HMENU parent, UINT position, const MenuItemSpec& spec,
            const SelectionContext& selection, UINT idCmdFirst, UINT idCmdLast,
            UINT* nextId, bool* inserted) noexcept;
    };
}
