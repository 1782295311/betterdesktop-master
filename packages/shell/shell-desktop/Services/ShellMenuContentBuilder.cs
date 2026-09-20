// BetterDesktop.Shell.Desktop — 原生右键扩展的菜单树内容构建
//
// 【为什么在 shell-desktop 而不是 shell-context-menu】
// 依赖方向决定：shell-convert 引用了 shell-context-menu（ConvertMenuService 消费 MenuItemDef），
// 所以 shell-context-menu 不能反向引用 shell-convert 拿 ConversionMatrix / EngineRegistry。
// shell-desktop 同时引用两者，且本来就是"系统右键注册"的唯一应用点（ApplyShellMenuRegistration）。
// 故：模型与序列化在 shell-context-menu（ShellMenuConfigWriter），内容装配在此。
//
// 【职责】产出"菜单里有什么"；"此刻显不显示"由原生侧谓词判定；"点了做什么"由 CLI 执行。
//
// 【2026-09-18 形态统一】用户反馈"桌面控制 / 剪贴板历史 / 切换到自绘桌面 没做到格式转换一样的标准
//（缺图标、缺子菜单）"。原生侧的图标 / 子菜单 / 勾选框能力其实**早已就绪**（MenuModel.cpp 解析 icon、
// ShellMenuHandler 的 ApplyItemIcon、ExplorerCommand::GetIcon、MF_CHECKED / ECS_CHECKBOX），
// 断点只在 C# 快照侧没有 Icon 字段、且「桌面控制」是叶子项。本次补齐：
//   · 所有项统一带图标（BrandIcon）；
//   · 「桌面控制」改成**带图标的开关子菜单**（含剪贴板历史 / 热键侧板 / 自绘桌面等开关）；
//   · 开关的勾选态取自当前设置，由 DesktopPlugin 在相关键变更时重写快照刷新。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BetterDesktop.Shell.ContextMenus.Services;
using BetterDesktop.Shell.Convert.Contracts;
using BetterDesktop.Shell.Settings.Contracts;

namespace BetterDesktop.Shell.Desktop.Services;

/// <summary>原生右键扩展菜单树构建（纯函数：给 settings + 转换服务，产出可序列化的菜单树）。</summary>
internal static class ShellMenuContentBuilder
{
    /// <summary>配置快照里出现「桌面控制」的开关键。</summary>
    public const string DesktopControlsKey = "shellmenu.desktopControls";

    /// <summary>配置快照里出现「格式转换」的开关键。</summary>
    public const string ConvertKey = "shellmenu.convert";

    /// <summary>配置快照里出现「压缩为 ZIP」的开关键（2026-09-17 补 Directory 场景时新增）。</summary>
    public const string ArchiveKey = "shellmenu.archive";

    /// <summary>
    /// 构建顶级菜单项。任一组关闭（或数据不可用）即整组不出现——**不出现半可用菜单**。
    /// </summary>
    public static IReadOnlyList<ShellMenuItem> Build(ISettingsService? settings, IConvertMenuService? convertMenu)
    {
        var items = new List<ShellMenuItem>();

        if (settings?.Get(DesktopControlsKey, true) ?? true)
        {
            items.Add(BuildDesktopControls(settings));
        }

        if (settings?.Get(ConvertKey, true) ?? true)
        {
            var convert = BuildConvert(convertMenu);
            if (convert is not null)
            {
                items.Add(convert);
            }
        }

        // 【2026-09-17 补 Directory 场景】此前快照里的项只有「格式转换」（Files 场景）与「桌面控制」
        // （Background 场景），而 B 路与 A 路都注册了 Directory 场景 → 用户右键**文件夹**时
        // "注册了却什么都没有"。
        if (settings?.Get(ArchiveKey, true) ?? true)
        {
            items.Add(BuildArchive());
        }

        return items;
    }

    /// <summary>
    /// 「桌面控制」子菜单：**带图标的开关组**。
    /// <para>
    /// 【形态变更（2026-09-18）】旧实现是**叶子项**，点击转交独立进程 `DesktopControl.exe --desktop-controls`
    /// 弹菜单——好处是勾选态实时、能按"宿主在不在"置灰；代价是形态与「格式转换」不一致（无子菜单、
    /// 无勾选态、在 Win11 新版菜单里只是个普通命令）。用户要求统一标准后改为子菜单 + 勾选框。
    /// </para>
    /// <para>
    /// 【代价与补偿】快照里的勾选态是静态值（原生菜单点击后不回写），刷新依赖
    /// <c>DesktopPlugin.OnSettingsChanged</c> 在相关键变更时重写快照——那里必须订阅这一组键。
    /// 而"宿主缺席时置灰"快照做不到，故保留最后一项「更多控制（实时状态）…」转交独立进程
    /// （它现读设置、现判宿主在线、能置灰并注明原因）。
    /// </para>
    /// </summary>
    private static ShellMenuItem BuildDesktopControls(ISettingsService? settings) => new()
    {
        Id = "desktopControls",
        Title = "桌面控制",
        Kind = ShellMenuKind.Submenu,
        Scenes = [ShellMenuScene.Background],
        Icon = ResolveHostIcon(),
        Children = BuildControlToggles(settings),
    };

    /// <summary>开关组（顺序 = 菜单里的展示顺序；勾选态与图标都取自当前状态）。</summary>
    private static List<ShellMenuItem> BuildControlToggles(ISettingsService? settings)
    {
        var icon = ResolveHostIcon();
        return
        [
            // invert: "桌面图标"的勾选语义是"显示"，而设置键 desktop.iconsHidden 的值语义是"已隐藏"。
            Toggle("ctlIcons", "桌面图标", settings, "desktop.iconsHidden", def: false, invert: true, cliKey: "icons", icon),
            Toggle("ctlTaskbar", "隐藏任务栏", settings, "components.wintaskbar", def: true, invert: false, cliKey: "taskbar", icon),
            Toggle("ctlDoubleClick", "双击隐藏图标", settings, "desktop.doubleClickHideIcons", def: true, invert: false, cliKey: "doubleclick", icon),
            Toggle("ctlMenuBar", "菜单栏", settings, "components.menubar", def: true, invert: false, cliKey: "menubar", icon),
            Toggle("ctlDock", "底部 Dock", settings, "components.dock", def: true, invert: false, cliKey: "dock", icon),

            // 【用户 2026-09-18 明确要求】剪贴板历史在这里是**功能开关**（原先右键里的
            // 「剪贴板历史…」是"打开面板"的静态注册项，语义与其它开关不一致）；
            // 同时补上一直缺失的「热键侧板」开关。两者都是宿主内插件的 enabled 键。
            Toggle("ctlClipboard", "剪贴板历史", settings, "extensions.clipboard-history.enabled", def: true, invert: false, cliKey: "clipboard", icon),
            Toggle("ctlHotkeyPanel", "热键侧板", settings, "hotkeys-panel.enabled", def: true, invert: false, cliKey: "hotkey-panel", icon),

            // 自绘桌面不是 --toggle-key 键（走独立的 --toggle-desktop），故单独构造。
            new ShellMenuItem
            {
                Id = "ctlToggleDesktop",
                Title = "自绘桌面",
                Kind = ShellMenuKind.Toggle,
                IsChecked = settings?.Get("components.desktop", true) ?? true,
                Action = "toggle-desktop",
                Icon = icon,
            },

            // 兜底入口：独立进程的实时菜单（现读设置 / 现判宿主在线 / 能置灰），
            // 与上面的静态开关互补——宿主缺席时它能明确告诉用户"需先启动 BetterDesktop"。
            new ShellMenuItem
            {
                Id = "desktopControlsLive",
                Title = "更多控制（实时状态）…",
                Action = "desktop-controls",
                Icon = icon,
            },
        ];
    }

    private static ShellMenuItem Toggle(
        string id,
        string title,
        ISettingsService? settings,
        string key,
        bool def,
        bool invert,
        string cliKey,
        string icon)
    {
        var value = settings?.Get(key, def) ?? def;
        return new ShellMenuItem
        {
            Id = id,
            Title = title,
            Kind = ShellMenuKind.Toggle,
            IsChecked = invert ? !value : value,
            Action = "toggle-key",
            Args = [cliKey],
            Icon = icon,
        };
    }

    /// <summary>
    /// 「格式转换」子菜单：只含**无损 + 引擎就绪**目标（红线 8：原生菜单无法置灰/弹风险确认，
    /// 出现即承诺可无损转成功）。每个子项带自己的可用源扩展名过滤（原生侧按当前选中扩展名筛）。
    /// 父项过滤 = 全部可转源扩展名并集 + 要求扩展名一致（混合类型不给批量转换）。
    /// </summary>
    private static ShellMenuItem? BuildConvert(IConvertMenuService? convertMenu)
    {
        var targets = convertMenu?.BuildSystemMenuTargets();
        if (targets is null || targets.Count == 0)
        {
            return null;
        }

        var icon = ResolveHostIcon();
        var children = new List<ShellMenuItem>(targets.Count);
        var allSources = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in targets)
        {
            if (target.SourceExtensions.Count == 0)
            {
                continue;
            }
            foreach (var extension in target.SourceExtensions)
            {
                allSources.Add(extension);
            }

            children.Add(new ShellMenuItem
            {
                Id = "convert-to-" + target.Format,
                Title = target.Label,
                Action = "convert-to",
                Args = [target.Format],
                // 全部无损（红线）→ 全部高亮，语义与自绘菜单一致
                Highlight = true,
                FilterExtensions = target.SourceExtensions,
                Icon = icon,
            });
        }

        if (children.Count == 0)
        {
            return null;
        }

        return new ShellMenuItem
        {
            Id = "convert",
            Title = "格式转换",
            Kind = ShellMenuKind.Submenu,
            Scenes = [ShellMenuScene.Files],
            FilterExtensions = [.. allSources],
            FilterSameExtension = true,
            Children = children,
            Icon = icon,
        };
    }

    /// <summary>
    /// 「压缩为 ZIP」叶子项：作用于选中的文件或文件夹（<c>ArchiveService.CompressZipCore</c> 明确接受
    /// <c>Directory.Exists</c> 的输入），多选也支持（批协议全量传 paths）。
    /// <para>
    /// 只挂 ZIP、不挂 7z/RAR：ZIP 是内置实现**永远可用**；7z/RAR 依赖外部引擎，而原生菜单
    /// 无法置灰或提示"引擎缺失"——出现即承诺可成功（红线）。
    /// </para>
    /// </summary>
    private static ShellMenuItem BuildArchive() => new()
    {
        Id = "compressZip",
        Title = "压缩为 ZIP",
        Kind = ShellMenuKind.Command,
        Scenes = [ShellMenuScene.Files, ShellMenuScene.Directory],
        Action = "compress-zip",
        Icon = ResolveHostIcon(),
    };

    /// <summary>
    /// 各项统一使用的品牌图标：宿主 exe 的 0 号图标（= host/Assets/BetterDesktop.ico）。
    /// <para>
    /// 【为什么显式写而不是依赖原生回退】原生侧对空 icon 会回退到同一个宿主图标，但那是"兜底"；
    /// 显式写出来语义明确，也便于将来按项细化（例如给转换目标用文件类型图标）。
    /// </para>
    /// <para>
    /// 【找不到宿主时返回空串】绝不写一个不存在的路径——错路径会让菜单项显示成空白图标，
    /// 比让原生回退更糟（原生回退还能拿到正确的部署路径）。
    /// </para>
    /// </summary>
    private static string ResolveHostIcon()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 4 && directory is not null; depth++)
        {
            var candidate = Path.Combine(directory.FullName, "BetterDesktop.Host.exe");
            if (File.Exists(candidate))
            {
                return candidate + ", 0";
            }

            directory = directory.Parent;
        }

        return string.Empty;
    }
}
