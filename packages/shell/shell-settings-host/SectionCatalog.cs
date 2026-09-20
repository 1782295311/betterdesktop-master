// BetterDesktop.Settings — 设置分区目录（独立进程的"分区来源"）
//
// 【为什么需要这份目录】分区原本由各插件在 LoadAsync 里注册；独立进程不能加载插件（会建菜单栏/Dock/自绘桌面
// 等重窗口 = 白搭"省内存"的初衷），于是改成"引用程序集 + 直接 new 分区"。
// 三项特殊依赖按用户 2026-09-17 的裁决处理（记录修改，等下次该功能启动时应用）：
//   · 菜单栏 / 灵动岛：构造函数回调可空 → 传空 = 只持久化（不再假装即时生效）；
//   · 任务栏外观 / 开始菜单 / Dock 固定项：Build 内经静态桥取服务，未绑定→占位；
//   · 热键：硬依赖 IHotkeyRegistryService（无静态桥、null 即抛）→ 本进程暂不提供（见文件末说明）。
//
// ⚠️ 新增分区时：① 在此登记；② 若该 Section 是 internal，需在对应 csproj 加
//    `<InternalsVisibleTo Include="BetterDesktop.Settings" />`。

using System;
using System.Collections.Generic;
using BetterDesktop.Shell.Settings.Contracts;

namespace BetterDesktop.Shell.SettingsHost;

/// <summary>独立设置进程的分区清单（顺序 = 设置窗左侧导航顺序）。</summary>
internal static class SectionCatalog
{
    /// <summary>把全部可独立构建的分区登记进注册表；单个分区构建失败只记日志，不影响其它分区。</summary>
    public static void RegisterAll(ISettingsSectionRegistry registry, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var failures = new List<string>();

        void Add(string name, Func<ISettingsSection> factory)
        {
            try
            {
                registry.Register(factory());
            }
            catch (Exception ex)
            {
                failures.Add(name);
                log($"分区「{name}」构建失败（已跳过）：{ex.Message}");
            }
        }

        // —— 设置中心自带（系统管理 / 主题 / 左 Dock）——
        Add("系统", () => new Settings.Sections.SystemSection());
        Add("主题", () => new Settings.Sections.ThemeSection());
        Add("左 Dock", () => new Settings.Sections.LeftDockSection());

        // —— 桌面与壳面 ——
        Add("桌面", () => new Desktop.Sections.DesktopSection());
        Add("菜单栏", () => new MenuBar.Sections.MenuBarSection()); // 空回调 = 只持久化（下次宿主启动生效）
        Add("灵动岛", () => new Island.Sections.IslandSection());   // 同上
        Add("任务栏外观", () => new Taskbar.Sections.TaskbarAppearanceSection());
        Add("开始菜单", () => new StartMenu.Sections.StartMenuSection());
        Add("Dock 固定项", () => new Dock.Sections.DockPinnedSection());

        // —— 功能域 ——
        Add("右键菜单", () => new ContextMenus.Sections.MenuManagerSection());
        // 系统集成（2026-09-17 安装器级）：装在哪 / 右键两路注册状态 / 开机自启 / 注册·修复·注销。
        // 与托盘「系统集成」菜单、安装脚本共用同一实现（SystemIntegrationRegistrar）。
        Add("系统集成", () => new ContextMenus.Sections.SystemIntegrationSection());
        Add("剪贴板历史", () => new Clipboard.Sections.ClipboardSection());
        Add("应用提取器", () => new AppSource.Sections.AppSourceSection());

        // 【热键分区为何缺席】HotkeySettingsSection 的构造签名是 (IHotkeyRegistryService registry)
        // 且 null 即抛（无静态桥、无 Null 实现）——该服务由内核 hotkeys 插件在 LoadAsync 里 Provide，
        // 独立进程不加载插件就拿不到。取舍：本进程不显示「热键」分区（点了也不会崩、不会假装能改键）；
        // 需要改热键时用宿主内的设置窗（那里有完整注册表），M3「热键面板独立」后注册表随该进程可用，
        // 届时把本分区接上（那时它才是真正"改完立即生效"的形态）。

        log(failures.Count == 0
            ? "分区目录构建完成（全部成功）"
            : $"分区目录构建完成，失败 {failures.Count} 个：{string.Join('、', failures)}");
    }
}
