// BetterDesktop.Shell.Desktop — 系统桌面右键菜单注册（自绘桌面开关入口）
// 用户 2026-09-07 拍板：开关要注册到系统右键菜单中——explorer 桌面空白右键常驻一项
// 「切换到自绘桌面」（2026-09-11 表述修正：原名「切换桌面控制」与实际行为不符，见 DisplayName 注释），
// 点击运行 BetterDesktop.Cli.exe --toggle-desktop（有宿主实例走命令桥热切，无实例直写设置；M3.1 起命令入口 = CLI）。
// 剪贴板历史项例外：需宿主完整在线（用户拍板 2026-09-10），命令保持指向 Host.exe。
// 注册位置 HKCU\Software\Classes\Directory\Background\shell（普通用户权限可写，
// HKCU 是 HKCR 合并视图的一部分，explorer 桌面空白右键立即生效；无需管理员提权）。
// 幂等：每次宿主启动重写同值，天然去重。

using System;
using System.IO;
using System.Linq;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.Convert.Contracts;
using BetterDesktop.Shell.Convert.Services;
using BetterDesktop.Shell.Convert.Services.Engines;
using Microsoft.Win32;

namespace BetterDesktop.Shell.Desktop.Services;

/// <summary>注册 explorer 桌面空白右键菜单里的「切换桌面控制」项（幂等）。</summary>
public static class DesktopSystemMenuRegistrar
{
    private const string RegPath = @"Software\Classes\Directory\Background\shell\BetterDesktop.ToggleDesktop";
    // 【2026-09-11 用户拍板 · 表述修正】该项**只做一件事**：切换自绘桌面层（components.desktop）。
    //   不带菜单栏/Dock（各自独立组件各有开关），也不隐藏任务栏（那是「桌面控制 ▸ 隐藏任务栏」的职责）。
    //   原名「切换桌面控制」把"桌面层开关"说成了"总控"，与实际行为不符 → 改为「切换到自绘桌面」。
    private const string DisplayName = "切换到自绘桌面";

    private const string UiRegPath = @"Software\Classes\Directory\Background\shell\BetterDesktop.Ui";
    private const string UiDisplayName = "桌面控制"; // 2026-09-10：子菜单标题去 ▸（用户拍板"不应该加▸，会影响美观"）；自绘桌面→桌面控制

    /// <summary>
    /// 系统文件右键「格式转换」单入口项名（2026-09-11 用户拍板）。
    /// 项名与自绘菜单里的入口名保持一致——用户要求"注册格式转换的快捷功能，与自绘菜单的样式一致"。
    /// </summary>
    private const string ConvertDisplayName = "格式转换";

    /// <summary>
    /// ⚠️ 【2026-09-17 起停写 · 无调用方，勿再接线】原「桌面控制」注册表级联子菜单注册。
    /// 桌面控制已改为"原生扩展快照里的单叶子项 → Cli --menu-batch → BetterDesktop.DesktopControl.exe"
    /// （点开时现算勾选态/置灰并自己执行），本方法写出的静态级联是**重复且矛盾**的旧结构：
    /// 无勾选态、无法按"宿主此刻在不在"置灰、Windows 11 新菜单还不渲染注册表级联。
    /// 旧版本写下的键由 <see cref="UnregisterUiControls"/> 清理（DesktopPlugin 关开关时调用）。
    /// 待确认无历史键残留后整体删除本方法（见 docs/plans/2026-09-17-desktop-control-standalone.md）。
    ///
    /// 【2026-09-11 用户拍板 · 内容与条件】
    ///   · 常驻三项：桌面图标显隐 / 隐藏任务栏 / **双击隐藏图标**（desktop.doubleClickHideIcons，
    ///     本次新增：此前"双击功能"没有系统菜单入口）；
    ///   · **条件项**：菜单栏显隐、Dock 显隐——只在对应组件**存在**时注册（用户原话：
    ///     "在有菜单栏和dock栏在时才有其对应的控制"）。
    ///   · 剪贴板历史…保留（2026-09-09 拍板：常驻一项，任何位置右键直达）。
    ///
    /// 【2026-09-11 二次拍板 · 静态一级 + 动态二级】上面那批"常驻项/条件项"现在**不在注册表里**了，
    /// 而是全部搬进 DesktopControlMenu：注册表只留**一条静态项**「桌面控制」，
    /// 点击 → CLI → 命令桥 → 宿主 → 光标处弹出自绘二级菜单（内容代码动态生成：勾选状态 + 条件项）。
    /// 原因：注册表级联是静态机制 + Win11 新菜单根本不渲染级联（用户实测"二级从来没有"）。
    /// </summary>
    public static void EnsureUiTogglesRegistered(bool menuBarPresent = true, bool dockPresent = true,
        bool clipboardPresent = true)
    {
        try
        {
            var exe = MenuCommandPaths.GetCliPath();
            var hostExe = MenuCommandPaths.GetHostPath(); // 剪贴板历史项指向宿主（需宿主完整在线）
            if (string.IsNullOrEmpty(exe))
            {
                DiagnosticLog.Trace("shell.desktop", "系统桌面「桌面控制」项注册跳过：MainModule 为空");
                return;
            }

            using var key = Registry.CurrentUser.CreateSubKey(UiRegPath);
            key.SetValue(null, UiDisplayName);
            key.SetValue("MUIVerb", UiDisplayName);
            // 【2026-09-11 图标修正】Icon 必须指向**宿主 exe**（我们的程序图标所在）；
            // 原先指向 CLI（控制台 exe，只有 .NET 通用图标）→ 用户实测"图标不是我们程序的图标"。
            key.SetValue("Icon", $"\"{IconExe(exe)}\"");

            // 【2026-09-11 四次修正 · 子项必须注册到 CommandStore】
            // 用户实测：加了级联声明后 ✔ 出现 ▸ 但**展开是空的** → 说明 verb 名没解析到实现。
            // 微软文档（Create Cascading Menus with the SubCommands Registry Entry）明确：
            //   父键 SubCommands = 分号分隔的 **verb 名**；verb 的实现注册在
            //   CommandStore（…\Explorer\CommandStore\shell\<verb>\command）。
            //   文档只给 HKLM 路径；HKCU 同名路径同样生效且**无需管理员**（本产品全程 HKCU）。
            // 我们此前把子项放在 <父键>\shell\ 下 → shell 找不到实现 → 空二级菜单（正是那个现象）。
            // 另按文档要求：级联父键**不要设 (Default) 值**（此前设了）。
            key.DeleteValue(string.Empty, throwOnMissingValue: false); // 级联父键不设 (Default)（微软文档要求）
            key.DeleteSubKeyTree("command", throwOnMissingSubKey: false);
            key.DeleteValue("ExtendedSubCommandsKey", throwOnMissingValue: false);
            key.DeleteSubKeyTree("shell", throwOnMissingSubKey: false); // 清历史错误布局

            var verbs = new List<string>
            {
                EnsureCommandStoreVerb("BetterDesktop.Ctl.Icons", "桌面图标显隐", $"\"{exe}\" --toggle-key icons"),
                EnsureCommandStoreVerb("BetterDesktop.Ctl.Taskbar", "隐藏任务栏", $"\"{exe}\" --toggle-key taskbar"),
                EnsureCommandStoreVerb("BetterDesktop.Ctl.DoubleClick", "双击隐藏图标", $"\"{exe}\" --toggle-key doubleclick"),
            };

            // 条件项：组件存在才有对应控制（用户拍板原话："在有菜单栏和dock栏在时才有其对应的控制"）
            if (menuBarPresent)
            {
                verbs.Add(EnsureCommandStoreVerb("BetterDesktop.Ctl.MenuBar", "菜单栏显隐", $"\"{exe}\" --toggle-key menubar"));
            }
            else
            {
                DeleteCommandStoreVerb("BetterDesktop.Ctl.MenuBar");
            }

            if (dockPresent)
            {
                verbs.Add(EnsureCommandStoreVerb("BetterDesktop.Ctl.Dock", "Dock 显隐", $"\"{exe}\" --toggle-key dock"));
            }
            else
            {
                DeleteCommandStoreVerb("BetterDesktop.Ctl.Dock");
            }

            if (clipboardPresent && !string.IsNullOrEmpty(hostExe))
            {
                // 剪贴板历史需宿主完整在线 → 指向宿主 exe（用户拍板 2026-09-10 的边界）。
                verbs.Add(EnsureCommandStoreVerb("BetterDesktop.Ctl.Clipboard", "剪贴板历史…",
                    $"\"{hostExe}\" --menu-cmd clipboard-history"));
            }
            else
            {
                DeleteCommandStoreVerb("BetterDesktop.Ctl.Clipboard");
            }

            key.SetValue("SubCommands", string.Join(";", verbs));

            DiagnosticLog.Trace("shell.desktop",
                $"系统右键项已注册: {UiDisplayName}（级联 {verbs.Count} 项，子项在 CommandStore；"
                + $"菜单栏={(menuBarPresent ? "有" : "无")} / Dock={(dockPresent ? "有" : "无")} / 剪贴板={(clipboardPresent ? "有" : "无")}）");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.desktop", $"系统右键项注册失败（{UiDisplayName}）: {ex.Message}");
        }
    }

    // ===== 级联子动词（CommandStore）=====

    /// <summary>CommandStore 路径（级联子动词的实现注册处；HKCU 无需管理员）。</summary>
    private const string CommandStorePath =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\CommandStore\shell";

    /// <summary>注册一个级联子动词（父键的 SubCommands 用名字引用它），返回 verb 名。</summary>
    private static string EnsureCommandStoreVerb(string verbName, string label, string commandLine)
    {
        using var key = Registry.CurrentUser.CreateSubKey(CommandStorePath + @"\" + verbName);
        key.SetValue("MUIVerb", label);
        key.SetValue("Icon", $"\"{IconExe(MenuCommandPaths.GetCliPath())}\"");
        using var cmd = key.CreateSubKey("command");
        cmd.SetValue(null, commandLine);
        return verbName;
    }

    /// <summary>删除级联子动词（条件项不满足时调用；无引用残留时无害）。</summary>
    private static void DeleteCommandStoreVerb(string verbName)
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(CommandStorePath + @"\" + verbName, throwOnMissingSubKey: false);
        }
        catch
        {
            // 删除失败不影响其余注册
        }
    }

    // 【2026-09-11 已删除】原 EnsureUiSub / DeleteUiSub 把级联子项写在 <父键>\shell\ 下。
    // ⚠️ 这是**错误布局**：SubCommands 里的 verb 名必须在 **CommandStore** 注册实现
    //（…\Explorer\CommandStore\shell\<verb>\command），放在父键 shell 下会导致
    // "父项有 ▸ 但二级展开为空"（用户实测）。子项一律走 EnsureCommandStoreVerb。

    /// <summary>
    /// 菜单图标来源 exe = **宿主自身**（`GetHostPath`）——我们的程序图标在 Host.exe 里。
    /// 命令入口是 CLI，但 CLI 是控制台程序、图标是 .NET 通用图标，指它就会显示成"不是我们程序的图标"。
    /// 取不到宿主路径时退回调用方给的 exe（保证注册不失败）。
    /// </summary>
    private static string IconExe(string fallback) => MenuCommandPaths.GetHostPath() ?? fallback;

    /// <summary>注销「切换到自绘桌面」项（设置开关 shellmenu.toggleDesktop 关闭时调用）。</summary>
    public static void UnregisterToggleDesktop() => DeleteKey(RegPath, DisplayName);

    /// <summary>注销「桌面控制」子菜单项（含全部子项；设置开关 shellmenu.desktopControls 关闭时调用）。</summary>
    public static void UnregisterUiControls() => DeleteKey(UiRegPath, UiDisplayName);

    /// <summary>删注册键树（幂等）+ 记日志。</summary>
    private static void DeleteKey(string regPath, string displayName)
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(regPath, throwOnMissingSubKey: false);
            DiagnosticLog.Trace("shell.desktop", $"系统右键项已注销: {displayName}");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.desktop", $"系统右键项注销失败（{displayName}）: {ex.Message}");
        }
    }

    /// <summary>确保系统桌面右键菜单项存在（宿主启动时调用，幂等）。</summary>
    public static void EnsureRegistered()
    {
        try
        {
            var exe = MenuCommandPaths.GetCliPath();
            if (string.IsNullOrEmpty(exe))
            {
                DiagnosticLog.Trace("shell.desktop", "系统右键菜单注册跳过：MainModule 为空");
                return;
            }

            using var key = Registry.CurrentUser.CreateSubKey(RegPath);
            key.SetValue(null, DisplayName);
            key.SetValue("Icon", $"\"{IconExe(exe)}\""); // 图标 = 宿主 exe（我们的程序图标）

            using var cmd = key.CreateSubKey("command");
            cmd.SetValue(null, $"\"{exe}\" --toggle-desktop");

            DiagnosticLog.Trace("shell.desktop", $"系统右键菜单已注册: {DisplayName} → {exe} --toggle-desktop");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.desktop", $"系统右键菜单注册失败: {ex.Message}");
        }
    }

    // 直转可用性：首选与兜底引擎都在纯托管白名单（无需外部进程，缺依赖也能转）。
    private static bool IsUsable(ConversionTarget t) =>
        t.Prefer is EngineKind.Managed or EngineKind.Image or EngineKind.PdfText
        && (t.Fallback is null
            or EngineKind.Managed or EngineKind.Image or EngineKind.PdfText);

    /// <summary>注册系统文件右键转换入口（2026-09-09 全扩展版）：矩阵全部可转输入扩展各自注册
    /// 专属级联「格式转换 ▸」（子命令 = 矩阵全量，打开即所有选项，引擎缺失项照常显示、点击时宿主检测反馈）。
    /// 旧通配「更多格式…」（*\shell\BetterDesktopMore）已移除——全量级联不再需要兜底入口。
    /// 幂等：每次启动先清旧键树再重建（旧版 *\shell\BetterDesktopConvert 级联结构自动迁移）。
    /// </summary>
    public static void EnsureConvertRegistered()
    {
        try
        {
            var exe = MenuCommandPaths.GetCliPath();
            if (string.IsNullOrEmpty(exe))
            {
                DiagnosticLog.Trace("shell.desktop", "系统转换菜单注册跳过：MainModule 为空");
                return;
            }

            // 【2026-09-11 五次修正 · 格式转换也做成级联（悬停展开目标列表）】
            // 用户要求：「格式转换」也要有 ▸、悬停直接出可选项（此前是"单命令项 → 点击弹自绘菜单"，
            // 所以没有 ▸）。现按扩展名分别注册级联父项，子项 = 该扩展名的可转目标，
            // 且子项实现注册在 **CommandStore**（见 EnsureCommandStoreVerb——上一版把子项放在
            // <父键>\shell\ 下导致"有 ▸ 但展开为空"）。
            // 这样也顺带修掉"PDF 看不到格式转换"：逐扩展注册，PDF 有自己的入口与目标列表。
            var convertVerbCount = 0;
            foreach (var ext in BetterDesktop.Shell.Convert.Services.ConversionMatrix.AllInputExtensions)
            {
                var regPath = @"Software\Classes\" + ext + @"\shell\BetterDesktopConvert";
                Registry.CurrentUser.DeleteSubKeyTree(regPath, throwOnMissingSubKey: false); // 幂等重建

                var targets = BetterDesktop.Shell.Convert.Services.ConversionMatrix.GetTargets(ext);
                if (targets.Count == 0)
                {
                    continue;
                }

                var verbs = new List<string>();
                foreach (var t in targets)
                {
                    // verb 名唯一到 (扩展名, 目标格式)：命令只需传目标格式，源文件由 %1 带入。
                    var verb = "BetterDesktop.Convert" + ext.Replace(".", "_") + "." + t.Format;
                    EnsureCommandStoreVerb(verb, t.Label, $"\"{exe}\" --menu-cmd convert-to-{t.Format} \"%1\"");
                    if (!verbs.Contains(verb))
                    {
                        verbs.Add(verb);
                        convertVerbCount++;
                    }
                }

                using var key = Registry.CurrentUser.CreateSubKey(regPath);
                key.SetValue("MUIVerb", ConvertDisplayName);
                key.SetValue("Icon", $"\"{IconExe(exe)}\"");
                key.SetValue("SubCommands", string.Join(";", verbs));
            }

            // 清理上一版/历史键：单入口通配项 + 历史「更多格式…」
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\*\shell\BetterDesktopConvert", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\*\shell\BetterDesktopMore", throwOnMissingSubKey: false);

            DiagnosticLog.Trace("shell.desktop",
                $"系统转换菜单已注册: 逐扩展级联「{ConvertDisplayName} ▸」（{convertVerbCount} 个子动词走 CommandStore）");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.desktop", $"系统转换菜单注册失败: {ex.Message}");
        }
    }

    /// <summary>注销系统转换菜单（功能管理开关）：删全部扩展专属级联。</summary>
    public static void UnregisterConvert()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\*\shell\BetterDesktopMore", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\*\shell\BetterDesktopConvert", throwOnMissingSubKey: false);
            foreach (var ext in BetterDesktop.Shell.Convert.Services.ConversionMatrix.AllInputExtensions)
            {
                Registry.CurrentUser.DeleteSubKeyTree(
                    @"Software\Classes\" + ext + @"\shell\BetterDesktopConvert", throwOnMissingSubKey: false);
            }
            DiagnosticLog.Trace("shell.desktop", "系统转换菜单已注销（全扩展级联）");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.desktop", $"系统转换菜单注销失败: {ex.Message}");
        }
    }

    /// <summary>系统转换菜单当前是否已注册（功能管理开关的 IsChecked 状态源；2026-09-09 起以任一扩展专属级联为准）。</summary>
    public static bool IsConvertRegistered()
    {
        try
        {
            foreach (var ext in BetterDesktop.Shell.Convert.Services.ConversionMatrix.AllInputExtensions)
            {
                if (Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + ext + @"\shell\BetterDesktopConvert") is not null)
                {
                    return true;
                }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    // ===== 压缩 / 解压（2026-09-07） =====

    /// <summary>
    /// 注册系统文件右键归档入口：
    /// ① 通配「压缩到 ▸」（*\shell\BetterDesktopCompress，任意文件/文件夹）：子命令 compress-zip（内置）/ compress-7z（7-Zip）/ compress-rar（Rar.exe），按引擎可用注册；
    /// ② .zip 扩展级联「解压到 ▸」（unzip-here 当前文件夹 / unzip-to 以文件名命名的子文件夹）；
    /// ③ .7z/.rar 扩展级联同款——WinRAR 或 7-Zip 任一可用时注册。
    /// 幂等：每次启动先清旧键树再重建（旧版 BetterDesktopArchive 单命令键自动迁移删除）。
    ///
    /// 【2026-09-11 停用】用户拍板：压缩/解压**不再注册到系统右键菜单**。
    /// DesktopPlugin 启动/设置变更时调用 <see cref="UnregisterArchive"/> 清理历史键；
    /// 本方法已无调用方（保留实现供将来复用——若重新启用，请同时接到设置开关 shellmenu.* 上）。
    /// </summary>
    public static void EnsureArchiveRegistered()
    {
        try
        {
            var exe = MenuCommandPaths.GetCliPath();
            if (string.IsNullOrEmpty(exe))
            {
                DiagnosticLog.Trace("shell.desktop", "系统归档菜单注册跳过：MainModule 为空");
                return;
            }

            EnsureCompressSubmenu(exe);
            EnsureUnzipSubmenu(exe, ".zip");
            if (BetterDesktop.Shell.Convert.Services.ArchiveService.ProbeWinRar()
                || BetterDesktop.Shell.Convert.Services.ArchiveService.ProbeSevenZip())
            {
                EnsureUnzipSubmenu(exe, ".7z");
                EnsureUnzipSubmenu(exe, ".rar");
            }

            DiagnosticLog.Trace("shell.desktop",
                $"系统归档菜单已注册: 压缩到 ▸(通配级联) + .zip 解压级联" +
                (BetterDesktop.Shell.Convert.Services.ArchiveService.ProbeWinRar()
                    || BetterDesktop.Shell.Convert.Services.ArchiveService.ProbeSevenZip()
                    ? " + .7z/.rar 解压级联(WinRAR/7-Zip)" : "（无解压引擎，7z/rar 不注册）"));
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.desktop", $"系统归档菜单注册失败: {ex.Message}");
        }
    }

    /// <summary>通配「压缩到 ▸」：HKCU\Software\Classes\*\shell\BetterDesktopCompress 级联 → 命令桥 compress-zip/7z/rar。</summary>
    private static void EnsureCompressSubmenu(string exe)
    {
        // 旧版单命令键迁移清理（* → 压缩到 ▸ 级联）
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\*\shell\BetterDesktopArchive", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\*\shell\BetterDesktopCompress", throwOnMissingSubKey: false);

        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\*\shell\BetterDesktopCompress");
        key.SetValue(null, "压缩到"); // 2026-09-10：子菜单标题去 ▸（用户拍板"不应该加▸，会影响美观"）
        key.SetValue("MUIVerb", "压缩到");
        key.SetValue("Icon", $"\"{IconExe(exe)}\"");
        // SubCommands 必须填子命令名列表（微软文档为分号分隔）；空值 = 级联菜单无法展开（Win11 实测）。
        var compressCmds = new List<string> { "compress-zip" };
        if (BetterDesktop.Shell.Convert.Services.ArchiveService.ProbeSevenZip())
        {
            compressCmds.Add("compress-7z");
        }
        if (BetterDesktop.Shell.Convert.Services.ArchiveService.ProbeRar())
        {
            compressCmds.Add("compress-rar");
        }
        key.SetValue("SubCommands", string.Join(";", compressCmds));

        // zip：内置引擎，永远注册
        using (var zip = key.CreateSubKey(@"shell\compress-zip"))
        {
            zip.SetValue("MUIVerb", "压缩为 .zip");
            using var cmd = zip.CreateSubKey("command");
            cmd.SetValue(null, $"\"{exe}\" --menu-cmd compress-zip \"%1\"");
        }

        // 7z：7-Zip 引擎探测到才注册（缺失置灰不隐藏原则在自绘菜单体现；系统注册只放真能力）
        if (BetterDesktop.Shell.Convert.Services.ArchiveService.ProbeSevenZip())
        {
            using var s7z = key.CreateSubKey(@"shell\compress-7z");
            s7z.SetValue("MUIVerb", "压缩为 .7z");
            using var cmd7z = s7z.CreateSubKey("command");
            cmd7z.SetValue(null, $"\"{exe}\" --menu-cmd compress-7z \"%1\"");
        }

        // rar：Rar.exe 探测到才注册
        if (BetterDesktop.Shell.Convert.Services.ArchiveService.ProbeRar())
        {
            using var rar = key.CreateSubKey(@"shell\compress-rar");
            rar.SetValue("MUIVerb", "压缩为 .rar");
            using var cmdRar = rar.CreateSubKey("command");
            cmdRar.SetValue(null, $"\"{exe}\" --menu-cmd compress-rar \"%1\"");
        }
    }

    /// <summary>扩展级联「解压到 ▸」：子命令 = 当前文件夹 / 以文件名命名的子文件夹。</summary>
    private static void EnsureUnzipSubmenu(string exe, string ext)
    {
        var regPath = @"Software\Classes\" + ext + @"\shell\BetterDesktopUnzip";
        Registry.CurrentUser.DeleteSubKeyTree(regPath, throwOnMissingSubKey: false);
        using var key = Registry.CurrentUser.CreateSubKey(regPath);
        key.SetValue(null, "解压到"); // 2026-09-10：子菜单标题去 ▸（用户拍板"不应该加▸，会影响美观"）
        key.SetValue("MUIVerb", "解压到");
        key.SetValue("Icon", $"\"{IconExe(exe)}\"");
        // SubCommands 必须填子命令名列表（微软文档为分号分隔）；空值 = 级联菜单无法展开（Win11 实测）。
        key.SetValue("SubCommands", "unzip-here;unzip-to");

        using (var here = key.CreateSubKey(@"shell\unzip-here"))
        {
            here.SetValue("MUIVerb", "解压到当前文件夹");
            using var hereCmd = here.CreateSubKey("command");
            hereCmd.SetValue(null, $"\"{exe}\" --menu-cmd unzip-here \"%1\"");
        }

        using (var named = key.CreateSubKey(@"shell\unzip-to"))
        {
            named.SetValue("MUIVerb", "解压到以文件名命名的子文件夹");
            using var namedCmd = named.CreateSubKey("command");
            namedCmd.SetValue(null, $"\"{exe}\" --menu-cmd unzip-to \"%1\"");
        }

        DiagnosticLog.Trace("shell.desktop", $"「解压到 ▸」已注册: {ext}");
    }

    /// <summary>注销系统归档菜单（功能管理开关）。</summary>
    public static void UnregisterArchive()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\*\shell\BetterDesktopArchive", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\*\shell\BetterDesktopCompress", throwOnMissingSubKey: false);
            foreach (var ext in new[] { ".zip", ".7z", ".rar" })
            {
                Registry.CurrentUser.DeleteSubKeyTree(
                    @"Software\Classes\" + ext + @"\shell\BetterDesktopUnzip", throwOnMissingSubKey: false);
            }
            DiagnosticLog.Trace("shell.desktop", "系统归档菜单已注销（压缩 + 解压）");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.desktop", $"系统归档菜单注销失败: {ex.Message}");
        }
    }

    /// <summary>系统归档菜单当前是否已注册（以通配压缩入口为准）。</summary>
    public static bool IsArchiveRegistered()
    {
        try
        {
            return Registry.CurrentUser.OpenSubKey(@"Software\Classes\*\shell\BetterDesktopCompress") is not null;
        }
        catch
        {
            return false;
        }
    }
}
