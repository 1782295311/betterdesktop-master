// BetterDesktopShellMenu — 菜单配置模型 + 选枝（纯逻辑，无 COM、无窗口）
//
// 【设计要点】本 DLL 只做「读配置 → 渲染菜单 → 派发执行」，**不含任何业务判断**：
//   - 「哪些格式可转 / 是否无损 / 引擎是否就绪」全部由宿主（C#）在写 shellmenu.json 时定稿；
//   - 本层只按通用谓词（场景 / 扩展名 / 选择数 / 全扩展名命中）决定「这一项此刻该不该出现」；
//   - 未来新增快捷功能 = 宿主多写一段 items + CLI 多一个 action 分支，本 DLL 不改不重编。
//
// 谓词刻意做成"闭合的通用词汇"，禁止在此层加入任何具体格式/功能名，否则 C++ 侧又长回业务。

#pragma once

#include "BdShell.h"
#include "JsonLite.h"

#include <memory>
#include <vector>

namespace bdshell
{
    enum class MenuItemKind
    {
        Command,
        Submenu,
        Toggle,
        Radio,
    };

    struct MenuItemSpec
    {
        std::wstring id;
        std::wstring title;
        std::wstring icon;        // 可选：图标路径或 "dll,-id" 资源串（IExplorerCommand::GetIcon 用）
        MenuItemKind kind = MenuItemKind::Command;
        bool checked = false;
        bool enabled = true;
        bool highlight = false;   // 加粗（无损转换项）
        bool isDefault = false;   // 默认动作加粗（"打开"）
        std::wstring action;      // CLI 动作标识（--menu-batch 的 action）
        std::vector<std::wstring> args;

        // ---- 通用可见性谓词（见文件头说明）----
        std::vector<MenuScene> scenes;                 // 空 = 所有场景
        std::vector<std::wstring> filterExtensions;    // 空 = 不按扩展名过滤；非空仅 Files 场景生效
        bool filterSameExtension = false;              // 要求全部选中项扩展名一致
        int requiresCountMin = 0;                      // 选中项数量下限（0 = 不限）
        std::vector<std::wstring> requiresAllExtIn;     // 非空 = 全部选中项扩展名都必须落在其中

        std::vector<MenuItemSpec> children;
    };

    struct MenuConfig
    {
        int version = 1;
        bool extensionEnabled = true;
        std::vector<MenuItemSpec> items;
    };

    /// <summary>当前选择上下文（由调用方按 COM 侧实况构造）。</summary>
    struct SelectionContext
    {
        MenuScene scene = MenuScene::Background;
        std::vector<std::wstring> paths; // Background 场景为空
    };

    /// <summary>
    /// 读取配置快照（带 mtime+size 缓存；未变化时直接复用已解析结果）。
    /// 返回 nullptr 表示「当前没有可用配置」——调用方必须**不显示任何项**（宁可不显示也不阻塞 explorer）。
    /// 不抛异常（noexcept）：任何失败都走 error 出参 + 日志。
    /// </summary>
    std::shared_ptr<const MenuConfig> LoadConfig(std::wstring* error) noexcept;

    /// <summary>清空缓存（冒烟测试/诊断用）。</summary>
    void InvalidateConfigCache() noexcept;

    /// <summary>直接解析配置文本（冒烟测试用；不触碰磁盘缓存）。</summary>
    bool ParseConfigText(const std::string& utf8, MenuConfig* out, std::wstring* error) noexcept;

    /// <summary>
    /// 从指定路径读取并解析配置（**不走缓存**；供诊断探针与将来「指定配置文件」场景使用）。
    /// 生产路径请用 LoadConfig()（带 mtime 缓存，菜单构建路径上零解析开销）。
    /// </summary>
    std::shared_ptr<const MenuConfig> LoadConfigFromFile(const std::wstring& path, std::wstring* error) noexcept;

    /// <summary>当前上下文下应出现的顶级项（顺序即配置顺序）。</summary>
    std::vector<const MenuItemSpec*> SelectVisibleItems(
        const MenuConfig& config, const SelectionContext& selection) noexcept;

    /// <summary>某个父项的子项过滤（同一套谓词；父项谓词已由调用方判定通过）。</summary>
    std::vector<const MenuItemSpec*> SelectVisibleChildren(
        const MenuItemSpec& parent, const SelectionContext& selection) noexcept;

    /// <summary>谓词判定（公开供冒烟测试覆盖）。</summary>
    bool MatchesSelection(const MenuItemSpec& spec, const SelectionContext& selection) noexcept;

    /// <summary>取小写扩展名（含点）。无扩展名返回空串。失败返回空串。</summary>
    std::wstring ExtensionOf(const std::wstring& path);
}
