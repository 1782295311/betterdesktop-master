// BetterDesktop.Shell.Desktop — 文件管理器窗口（FolderBrowserWindow）菜单模板
// Scope=ShellFile；轻量操作集（该窗口仅浏览/打开，无剪切/粘贴队列——重操作走桌面场景）。

using System.Windows;
using System.Windows.Controls;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.ContextMenus.Contracts;
using BetterDesktop.Shell.ContextMenus.Services;
using BetterDesktop.Shell.Desktop.Windows;

namespace BetterDesktop.Shell.Desktop.Templates;

/// <summary>文件管理器条目目标。</summary>
internal sealed record FolderItemTarget(string Path, bool IsDirectory);

/// <summary>文件管理器模板（Scope: ShellFile）。</summary>
internal sealed class FolderMenuTemplate(FolderBrowserWindow owner) : IMenuTemplate
{
    public MenuScope Scope => MenuScope.ShellFile;

    public void Build(IMenuTemplateBuilder b, MenuRequest request)
    {
        if (request.Target is not FolderItemTarget target) return;
        var path = target.Path;

        // ① 常用操作组
        b.AddItem(new MenuItemDef
        {
            Id = "folder.open", Text = "打开", Group = MenuGroup.Common, IsDefault = true,
            Command = () => owner.InvokeOpenEntry(path, target.IsDirectory),
        });
        if (request.File?.Caps.HasFlag(FileCapabilities.RunAsAdmin) == true)
        {
            b.AddItem(new MenuItemDef
            {
                Id = "folder.runas", Text = "以管理员运行", Group = MenuGroup.Common,
                Command = () => DesktopMenuActions.RunAsAdmin(path),
            });
        }
        if (request.File?.Caps.HasFlag(FileCapabilities.OpenFileLocation) == true)
        {
            b.AddItem(new MenuItemDef
            {
                Id = "folder.location", Text = "打开文件位置", Group = MenuGroup.Common,
                Command = () => DesktopMenuActions.OpenContainingFolder(path),
            });
        }

        // 终端回退链（计划 D2）：wt → pwsh → powershell → cmd；仅目录显示
        var terminal = TerminalLocator.Resolve();
        if (terminal is not null && target.IsDirectory)
        {
            b.AddItem(new MenuItemDef
            {
                Id = "folder.terminal", Text = "在终端中打开", Group = MenuGroup.Common,
                Command = () => DesktopMenuActions.OpenTerminalExe(terminal, path),
            });
        }

        // ② 管理组
        b.AddItem(new MenuItemDef
        {
            Id = "folder.copypath", Text = "复制文件路径", Group = MenuGroup.Manage,
            Command = () =>
            {
                try { Clipboard.SetText(path); }
                catch (Exception ex) { DiagnosticLog.Trace("shell.desktop", $"复制路径失败: {ex.Message}"); }
            },
        });
        if (!target.IsDirectory)
        {
            b.AddItem(new MenuItemDef
            {
                Id = "folder.delete", Text = "删除", Group = MenuGroup.Manage,
                IsEnabled = request.File?.IsReadOnly != true,
                Command = () => owner.InvokeDeleteToRecycleBin(path),
            });
        }

        // ④ 系统组
        b.AddItem(new MenuItemDef
        {
            Id = "folder.properties", Text = "属性", Group = MenuGroup.System,
            Command = () => DesktopMenuActions.ShowProperties(path),
        });
    }
}
