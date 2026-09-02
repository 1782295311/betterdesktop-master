// BetterDesktop.Shell.Desktop — 桌面场景菜单模板（消费统一菜单服务 shell-context-menu）
// 两个模板：DesktopBlankTemplate（桌面空白处）/ DesktopIconTemplate（桌面图标）。
// 区块归属照计划 §2.6 mockup：①常用 → ②管理 → ③贡献 → ④系统；高频项一层直达。
// 回退开关：context-menu.migrated=false 时 DesktopIconsControl 走旧自绘路径（不注册模板）。

using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.ContextMenus.Contracts;
using BetterDesktop.Shell.Desktop.Contracts;
using BetterDesktop.Shell.Desktop.Controls;

namespace BetterDesktop.Shell.Desktop.Templates;

/// <summary>图标右键目标（entry + 单元格 + 文本标签；重命名等 UI 操作需要）。</summary>
internal sealed record DesktopIconTarget(BrowserEntry Entry, Border Cell, TextBlock Label);

/// <summary>桌面空白处模板（Scope: Desktop）。</summary>
internal sealed class DesktopBlankTemplate(DesktopIconsControl owner) : IMenuTemplate
{
    public MenuScope Scope => MenuScope.Desktop;

    public void Build(IMenuTemplateBuilder b, MenuRequest request)
    {
        // ① 常用操作组
        b.AddItem(new MenuItemDef
        {
            Id = "desktop.new-folder", Text = "新建文件夹", Group = MenuGroup.Common,
            Command = () => owner.InvokeBrowserNewFolder(),
        });
        b.AddItem(new MenuItemDef
        {
            Id = "desktop.paste", Text = "粘贴", Group = MenuGroup.Common,
            IsEnabled = owner.InvokeCanPaste(),
            Command = () => owner.InvokeBrowserPaste(),
        });
        b.AddItem(new MenuItemDef
        {
            Id = "desktop.refresh", Text = "刷新", Group = MenuGroup.Common,
            Command = () => owner.InvokeBrowserRefresh(),
        });

        // ② 管理组
        b.AddItem(new MenuItemDef
        {
            Id = "desktop.compact", Text = "整理图标", Group = MenuGroup.Manage,
            Command = owner.InvokeCompactLayout,
        });

        // ④ 系统组
        b.AddItem(new MenuItemDef
        {
            Id = "desktop.display", Text = "显示设置", Group = MenuGroup.System,
            Command = () => owner.InvokeOpenSettings("ms-settings:display"),
        });
        b.AddItem(new MenuItemDef
        {
            Id = "desktop.personalize", Text = "个性化", Group = MenuGroup.System,
            Command = () => owner.InvokeOpenSettings("ms-settings:personalization"),
        });
        if (DesktopMenuActions.HasWindowsTerminal)
        {
            b.AddItem(new MenuItemDef
            {
                Id = "desktop.terminal", Text = "在终端中打开", Group = MenuGroup.System,
                Command = () => DesktopMenuActions.OpenTerminal(owner.InvokeDesktopPath()),
            });
        }
    }
}

/// <summary>桌面图标模板（Scope: DesktopIcon；能力过滤经 RequiredCapability 生效，隐藏优先）。</summary>
internal sealed class DesktopIconTemplate(DesktopIconsControl owner) : IMenuTemplate
{
    public MenuScope Scope => MenuScope.DesktopIcon;

    public void Build(IMenuTemplateBuilder b, MenuRequest request)
    {
        if (request.Target is not DesktopIconTarget target) return;
        var entry = target.Entry;
        var kind = request.File?.Kind ?? FileKind.File;

        // ① 常用操作组
        if (kind == FileKind.Unknown)
        {
            // 未知格式定版菜单集：打开方式…(openas) 而非默认"打开"
            b.AddItem(new MenuItemDef
            {
                Id = "icon.openas", Text = "打开方式…", Group = MenuGroup.Common, IsDefault = true,
                Command = () => DesktopMenuActions.OpenWithDialog(entry.Path),
            });
        }
        else
        {
            b.AddItem(new MenuItemDef
            {
                Id = "icon.open", Text = "打开", Group = MenuGroup.Common, IsDefault = true,
                Command = () => owner.InvokeOpenEntry(entry),
            });
        }
        if (!entry.IsShellNamespace)
        {
            b.AddItem(new MenuItemDef
            {
                Id = "icon.runas", Text = "以管理员运行", Group = MenuGroup.Common,
                RequiredCapability = FileCapabilities.RunAsAdmin,
                Command = () => DesktopMenuActions.RunAsAdmin(entry.Path),
            });
            b.AddItem(new MenuItemDef
            {
                Id = "icon.location", Text = "打开文件位置", Group = MenuGroup.Common,
                RequiredCapability = FileCapabilities.OpenFileLocation,
                Command = () => DesktopMenuActions.OpenContainingFolder(entry.Path),
            });

            // ② 管理组（只读：删除/重命名置灰带说明，非隐藏——定版例外规则）
            b.AddItem(new MenuItemDef
            {
                Id = "icon.cut", Text = "剪切", Group = MenuGroup.Manage,
                RequiredCapability = FileCapabilities.Cut, IsEnabled = request.File?.IsReadOnly != true,
                Command = () => owner.InvokeBrowserCut(entry.Path),
            });
            b.AddItem(new MenuItemDef
            {
                Id = "icon.copy", Text = "复制", Group = MenuGroup.Manage,
                RequiredCapability = FileCapabilities.Copy,
                Command = () => owner.InvokeBrowserCopy(entry.Path),
            });
            b.AddItem(new MenuItemDef
            {
                Id = "icon.delete", Text = request.File?.IsReadOnly == true ? "删除（只读）" : "删除",
                Group = MenuGroup.Manage,
                RequiredCapability = FileCapabilities.Delete, IsEnabled = request.File?.IsReadOnly != true,
                Command = () => owner.InvokeBrowserDelete(entry.Path),
            });
            b.AddItem(new MenuItemDef
            {
                Id = "icon.rename", Text = "重命名", Group = MenuGroup.Manage,
                RequiredCapability = FileCapabilities.Rename, IsEnabled = request.File?.IsReadOnly != true,
                Command = () => owner.InvokeStartRename(target.Cell, target.Label, entry.Path),
            });
        }

        // ④ 系统组
        b.AddItem(new MenuItemDef
        {
            Id = "icon.properties", Text = "属性", Group = MenuGroup.System,
            RequiredCapability = FileCapabilities.Properties,
            Command = () => owner.InvokeShowProperties(entry.Path),
        });
    }
}

/// <summary>跨模板共享的静态动作（ShellExecute 变体 + 终端检测）。</summary>
internal static class DesktopMenuActions
{
    /// <summary>以管理员运行（UAC 弹窗由 ShellExecute runas 触发；用户取消抛异常静默）。</summary>
    public static void RunAsAdmin(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas" });
        }
        catch (OperationCanceledException)
        {
            // UAC 取消，正常流程
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.desktop", $"以管理员运行失败 {path}: {ex.Message}");
        }
    }

    /// <summary>打开方式对话框（openas verb；Unknown 格式定版行为）。</summary>
    public static void OpenWithDialog(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "openas" });
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.desktop", $"打开方式失败 {path}: {ex.Message}");
        }
    }

    /// <summary>打开文件位置（资源管理器选中定位）。</summary>
    public static void OpenContainingFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.desktop", $"打开文件位置失败 {path}: {ex.Message}");
        }
    }

    /// <summary>在终端中打开（Windows Terminal 存在时才显示该菜单项，隐藏优先）。</summary>
    public static bool HasWindowsTerminal =>
        File.Exists(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "wt.exe"));

    public static void OpenTerminal(string workingDir)
    {
        try
        {
            var wt = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WindowsApps", "wt.exe");
            Process.Start(new ProcessStartInfo(wt) { UseShellExecute = true, WorkingDirectory = workingDir });
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.desktop", $"打开终端失败: {ex.Message}");
        }
    }
}
