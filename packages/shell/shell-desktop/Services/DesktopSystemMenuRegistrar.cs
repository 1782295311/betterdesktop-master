// BetterDesktop.Shell.Desktop — 系统桌面右键菜单注册（自绘桌面开关入口）
// 用户 2026-09-07 拍板："开启/关闭自绘桌面"要注册到系统右键菜单中——
// 即 explorer 桌面空白右键菜单里常驻一项「切换自绘桌面」，点击运行
// BetterDesktop.Host.exe --toggle-desktop（有宿主实例走命令桥热切，无实例直写设置并按需拉起）。
// 注册位置 HKCU\Software\Classes\Directory\Background\shell（普通用户权限可写，
// HKCU 是 HKCR 合并视图的一部分，explorer 桌面空白右键立即生效；无需管理员提权）。
// 幂等：每次宿主启动重写同值，天然去重。

using System;
using System.Diagnostics;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.Convert.Contracts;
using BetterDesktop.Shell.Convert.Services;
using Microsoft.Win32;

namespace BetterDesktop.Shell.Desktop.Services;

/// <summary>注册 explorer 桌面空白右键菜单里的「切换自绘桌面」项（幂等）。</summary>
public static class DesktopSystemMenuRegistrar
{
    private const string RegPath = @"Software\Classes\Directory\Background\shell\BetterDesktop.ToggleDesktop";
    private const string DisplayName = "切换自绘桌面";

    private const string UiRegPath = @"Software\Classes\Directory\Background\shell\BetterDesktop.Ui";
    private const string UiDisplayName = "自绘桌面 ▸";

    /// <summary>
    /// 注册系统桌面右键「自绘桌面 ▸」级联子菜单（2026-09-07 用户拍板）：
    /// 桌面图标显隐（双击隐藏的注册入口）/ 菜单栏显隐 / Dock 显隐。
    /// 子命令 = BetterDesktop.Host.exe --toggle-key &lt;icons|menubar|dock&gt;；
    /// 有宿主实例走命令桥热切（Bootstrap dispatch toggle-key），无实例由 App 分支直写 settings.json。
    /// </summary>
    public static void EnsureUiTogglesRegistered()
    {
        try
        {
            var exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exe))
            {
                DiagnosticLog.Trace("shell.desktop", "系统UI开关菜单注册跳过：MainModule 为空");
                return;
            }

            using var key = Registry.CurrentUser.CreateSubKey(UiRegPath);
            key.SetValue(null, UiDisplayName);
            key.SetValue("MUIVerb", UiDisplayName);
            key.SetValue("Icon", $"\"{exe}\"");
            // SubCommands 必须填子命令名列表（逗号分隔）；空值 = 级联菜单无法展开（Win11 实测）。
            key.SetValue("SubCommands", "toggle-clipboard,toggle-icons,toggle-menubar,toggle-dock,toggle-taskbar");

            EnsureUiSub(exe, key, "toggle-icons", "桌面图标显隐");
            EnsureUiSub(exe, key, "toggle-menubar", "菜单栏显隐");
            EnsureUiSub(exe, key, "toggle-dock", "Dock 显隐");
            EnsureUiSub(exe, key, "toggle-taskbar", "任务栏显隐");

            // 剪贴板历史显式入口（2026-09-09）：HKCU 4 场景右键项在 Win11 会被折叠进
            // 「显示更多选项」（经典菜单）；这里在自绘桌面 ▸ 子菜单常驻一项，任何位置右键都直达。
            using (var cb = key.CreateSubKey(@"shell\toggle-clipboard"))
            {
                cb.SetValue("MUIVerb", "剪贴板历史…");
                using var cbCmd = cb.CreateSubKey("command");
                cbCmd.SetValue(null, $"\"{exe}\" --menu-cmd clipboard-history");
            }

            DiagnosticLog.Trace("shell.desktop", $"系统UI开关菜单已注册: {UiDisplayName}（图标/菜单栏/Dock/任务栏/剪贴板历史 五项）");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.desktop", $"系统UI开关菜单注册失败: {ex.Message}");
        }
    }

    private static void EnsureUiSub(string exe, RegistryKey parent, string name, string label)
    {
        using var sub = parent.CreateSubKey(@"shell\" + name);
        sub.SetValue("MUIVerb", label);
        using var cmd = sub.CreateSubKey("command");
        cmd.SetValue(null, $"\"{exe}\" --toggle-key {name.Replace("toggle-", string.Empty)}");
    }

    /// <summary>确保系统桌面右键菜单项存在（宿主启动时调用，幂等）。</summary>
    public static void EnsureRegistered()
    {
        try
        {
            var exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exe))
            {
                DiagnosticLog.Trace("shell.desktop", "系统右键菜单注册跳过：MainModule 为空");
                return;
            }

            using var key = Registry.CurrentUser.CreateSubKey(RegPath);
            key.SetValue(null, DisplayName);
            key.SetValue("Icon", $"\"{exe}\"");

            using var cmd = key.CreateSubKey("command");
            cmd.SetValue(null, $"\"{exe}\" --toggle-desktop");

            DiagnosticLog.Trace("shell.desktop", $"系统右键菜单已注册: {DisplayName} → {exe} --toggle-desktop");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.desktop", $"系统右键菜单注册失败: {ex.Message}");
        }
    }

    /// <summary>文本/数据类常见扩展（2026-09-09 精简显示）：各自注册专属级联「格式转换 ▸」，
    /// 子命令 = 该类型矩阵全量（引擎缺失项照常显示、点击时宿主检测反馈）。
    /// epub/mobi/Office/图片/PDF 等外部引擎依赖类型无系统级联，由自绘右键「格式转换」置灰全量承载。</summary>
    private static readonly string[] QuickExtensions =
        [".txt", ".log", ".md", ".markdown", ".html", ".htm", ".json", ".xml", ".yaml", ".yml", ".csv", ".tsv"];

    /// <summary>直转可用性：首选与兜底引擎都在纯托管白名单（无需外部进程，缺依赖也能转）。</summary>
    private static bool IsUsable(ConversionTarget t) =>
        t.Prefer is EngineKind.Managed or EngineKind.Image or EngineKind.PdfText
        && (t.Fallback is null
            or EngineKind.Managed or EngineKind.Image or EngineKind.PdfText);

    /// <summary>
    /// 注册系统文件右键转换入口（2026-09-09 精简版）：
    /// 文本/数据 12 扩展各注册专属级联「格式转换 ▸」（子命令 = 矩阵全量，打开即所有选项，
    /// 引擎缺失项照常显示、点击时宿主检测反馈）。
    /// 旧通配「更多格式…」（*\shell\BetterDesktopMore）已移除——全量级联不再需要兜底入口。
    /// 幂等：每次启动先清旧键树再重建（旧版 *\shell\BetterDesktopConvert 级联结构自动迁移）。
    /// </summary>
    public static void EnsureConvertRegistered()
    {
        try
        {
            var exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exe))
            {
                DiagnosticLog.Trace("shell.desktop", "系统转换菜单注册跳过：MainModule 为空");
                return;
            }

            // 注销历史通配入口（2026-09-09 移除；旧键残留一并清理）。
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\*\shell\BetterDesktopMore", throwOnMissingSubKey: false);
            foreach (var ext in QuickExtensions)
            {
                EnsureQuickSubmenu(exe, ext);
            }

            DiagnosticLog.Trace("shell.desktop",
                $"系统转换菜单已注册: {QuickExtensions.Length} 类扩展专属级联「格式转换 ▸」（全量）");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.desktop", $"系统转换菜单注册失败: {ex.Message}");
        }
    }

    /// <summary>文本/数据扩展专属级联「格式转换 ▸」：子命令 = 矩阵全部目标（含引擎缺失项——
    /// 打开即为所有选项，可用性由点击时宿主检测/反馈，与自绘菜单置灰同哲学）。</summary>
    private static void EnsureQuickSubmenu(string exe, string ext)
    {
        var regPath = @"Software\Classes\" + ext + @"\shell\BetterDesktopConvert";
        var targets = ConversionMatrix.GetTargets(ext);
        if (targets.Count == 0)
        {
            return; // 该类型无任何可转目标 → 只保留通配「更多格式…」兜底
        }

        Registry.CurrentUser.DeleteSubKeyTree(regPath, throwOnMissingSubKey: false);
        using var key = Registry.CurrentUser.CreateSubKey(regPath);
        key.SetValue(null, "格式转换 ▸");
        key.SetValue("MUIVerb", "格式转换 ▸");
        key.SetValue("Icon", $"\"{exe}\"");
        // SubCommands 必须填子命令名列表（逗号分隔）；空值 = 级联菜单无法展开（Win11 实测）。
        // 层级红线：级联内不再放「更多格式…」convert-more（点击又弹完整自绘菜单 = 三级跳层），
        // 完整菜单由通配「更多格式…」（BetterDesktopMore）单入口承载，级联保持纯二级。
        key.SetValue("SubCommands", string.Join(",", targets.Select(t => "convert-to-" + t.Format)));

        foreach (var t in targets)
        {
            var cmdName = "convert-to-" + t.Format;
            using var sub = key.CreateSubKey("shell\\" + cmdName);
            sub.SetValue("MUIVerb", t.Label);
            using var cmd = sub.CreateSubKey("command");
            cmd.SetValue(null, $"\"{exe}\" --menu-cmd {cmdName} \"%1\"");
        }

        DiagnosticLog.Trace("shell.desktop", $"「格式转换 ▸」已注册: {ext} → {targets.Count} 子命令");
    }

    /// <summary>注销系统转换菜单（功能管理开关）：删通配入口 + 全部扩展专属级联。</summary>
    public static void UnregisterConvert()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\*\shell\BetterDesktopMore", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\*\shell\BetterDesktopConvert", throwOnMissingSubKey: false);
            foreach (var ext in QuickExtensions)
            {
                Registry.CurrentUser.DeleteSubKeyTree(
                    @"Software\Classes\" + ext + @"\shell\BetterDesktopConvert", throwOnMissingSubKey: false);
            }
            DiagnosticLog.Trace("shell.desktop", "系统转换菜单已注销（通配 + 扩展专属）");
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
            foreach (var ext in QuickExtensions)
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
    /// </summary>
    public static void EnsureArchiveRegistered()
    {
        try
        {
            var exe = Process.GetCurrentProcess().MainModule?.FileName;
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
        key.SetValue(null, "压缩到 ▸");
        key.SetValue("MUIVerb", "压缩到 ▸");
        key.SetValue("Icon", $"\"{exe}\"");
        // SubCommands 必须填子命令名列表（逗号分隔）；空值 = 级联菜单无法展开（Win11 实测）。
        var compressCmds = new List<string> { "compress-zip" };
        if (BetterDesktop.Shell.Convert.Services.ArchiveService.ProbeSevenZip())
        {
            compressCmds.Add("compress-7z");
        }
        if (BetterDesktop.Shell.Convert.Services.ArchiveService.ProbeRar())
        {
            compressCmds.Add("compress-rar");
        }
        key.SetValue("SubCommands", string.Join(",", compressCmds));

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
        key.SetValue(null, "解压到 ▸");
        key.SetValue("MUIVerb", "解压到 ▸");
        key.SetValue("Icon", $"\"{exe}\"");
        // SubCommands 必须填子命令名列表（逗号分隔）；空值 = 级联菜单无法展开（Win11 实测）。
        key.SetValue("SubCommands", "unzip-here,unzip-to");

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
