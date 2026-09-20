// BetterDesktopShellMenu — A 路：Windows 11 新版右键菜单（IExplorerCommand）
//
// 【为什么需要它】Win11 新版菜单只接受「有包标识 + IExplorerCommand」的扩展；传统
// IContextMenu 处理器只会出现在「显示更多选项」（见 MS Learn《将文件资源管理器上下文
// 菜单命令添加到打包的桌面应用》）。要实现"像压缩软件一样一眼可见"，这条路必须走。
//
// 【场景如何区分】IExplorerCommand 的每个方法都拿不到"我挂在哪个 ItemType 上"，因此由
// CLSID 区分：kExplorerFiles / kExplorerDirectory / kExplorerBg 各自固定一个场景 + 槽位。
// 槽位 = 该场景下第 N 个可见顶级项（今日每个场景只用槽位 0；留槽位是为将来加功能时
// 只改清单不改代码）。
//
// 【性能红线】GetTitle / GetIcon / GetState / EnumSubCommands 都在 explorer 的菜单构建
// 路径上被调用。全部只读「已解析的内存配置 + 短 TTL 记忆」，禁止读盘/启动进程。

#pragma once

#include "BdShell.h"
#include "ComPtrLite.h"
#include "MenuModel.h"

#include <shobjidl_core.h>

#include <memory>
#include <string>
#include <vector>

namespace bdshell
{
    /// <summary>CLSID → (场景, 槽位)。未登记返回 false。</summary>
    bool MapExplorerClsid(REFCLSID clsid, MenuScene* scene, UINT* slot) noexcept;

    /// <summary>创建 A 路命令对象（供 ClassFactory 调用；失败返回 E_NOINTERFACE 语义的 nullptr）。</summary>
    HRESULT CreateExplorerCommand(REFCLSID clsid, REFIID riid, void** ppv) noexcept;

    /// <summary>从选择集提取文件系统路径（psiItemArray 可为 null → 空集）。</summary>
    void ExtractSelectionPaths(IShellItemArray* items, std::vector<std::wstring>& out) noexcept;
}
