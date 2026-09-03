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
        // ① 常用操作组：新建▶（集合语义子菜单，第一菜单原则允许）
        b.AddItem(new MenuItemDef
        {
            Id = "desktop.new", Text = "新建", Group = MenuGroup.Common, Kind = MenuItemKind.Submenu,
            Children =
            [
                new MenuItemDef { Id = "desktop.new-folder", Text = "文件夹",
                    Command = () => owner.InvokeBrowserNewFolder() },
                new MenuItemDef { Id = "desktop.new-text", Text = "文本文档",
                    Command = () => owner.InvokeCreateTextFile() },
                new MenuItemDef { Id = "desktop.new-shortcut", Text = "快捷方式",
                    Command = () => DesktopMenuActions.OpenNewShortcutWizard(owner.InvokeDesktopPath()) },
            ],
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

        // ② 管理组：查看▶ / 排序▶（集合语义子菜单）+ 整理图标
        b.AddItem(new MenuItemDef
        {
            Id = "desktop.view", Text = "查看", Group = MenuGroup.Manage, Kind = MenuItemKind.Submenu,
            Children = ViewChildren(),
        });
        b.AddItem(new MenuItemDef
        {
            Id = "desktop.sort", Text = "排序方式", Group = MenuGroup.Manage, Kind = MenuItemKind.Submenu,
            Children = SortChildren(),
        });
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

    /// <summary>查看子菜单：图标大小三档（Radio，落 desktop.iconSize）+ 自动排列/对齐网格（Toggle）。</summary>
    private List<MenuItemDef> ViewChildren()
    {
        var iconSize = owner.InvokeGetDouble("desktop.iconSize", 44d);
        MenuItemDef Radio(string text, double value) => new()
        {
            Id = $"desktop.view-size-{value}", Text = text, Kind = MenuItemKind.Radio,
            IsChecked = Math.Abs(iconSize - value) < 1,
            Command = () => owner.InvokeSetDouble("desktop.iconSize", value),
        };
        return
        [
            Radio("大图标", 64),
            Radio("中等图标", 44),
            Radio("小图标", 30),
            new MenuItemDef
            {
                Id = "desktop.view-autoarrange", Text = "自动排列图标", Kind = MenuItemKind.Toggle,
                IsChecked = owner.InvokeGetBool("desktop.autoArrange", false),
                Command = () => owner.InvokeSetBool("desktop.autoArrange",
                    !owner.InvokeGetBool("desktop.autoArrange", false)),
            },
            new MenuItemDef
            {
                Id = "desktop.view-snap", Text = "将图标与网格对齐", Kind = MenuItemKind.Toggle,
                IsChecked = owner.InvokeGetBool("desktop.snapToGrid", true),
                Command = () => owner.InvokeSetBool("desktop.snapToGrid",
                    !owner.InvokeGetBool("desktop.snapToGrid", true)),
            },
        ];
    }

    /// <summary>排序子菜单（Radio，落 desktop.sortKey；默认=智能排序）。</summary>
    private List<MenuItemDef> SortChildren()
    {
        var current = owner.InvokeSortKey();
        (string Text, string? Key)[] options =
        [
            ("默认", null),
            ("名称", "name"),
            ("大小", "size"),
            ("项目类型", "type"),
            ("修改日期", "modified"),
        ];
        return options.Select(o => new MenuItemDef
        {
            Id = $"desktop.sort-{o.Key ?? "default"}", Text = o.Text, Kind = MenuItemKind.Radio,
            IsChecked = current == o.Key,
            Command = () => owner.InvokeSetSort(o.Key),
        }).ToList();
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

        // ① 常用操作组。回收站内对象不显示"打开"（对象已删除，打开必然失败；审查 P2-3）
        if (kind == FileKind.Unknown)
        {
            // 未知格式定版菜单集：打开方式…(openas) 而非默认"打开"
            b.AddItem(new MenuItemDef
            {
                Id = "icon.openas", Text = "打开方式…", Group = MenuGroup.Common, IsDefault = true,
                Command = () => DesktopMenuActions.OpenWithDialog(entry.Path),
            });
        }
        else if (kind != FileKind.InRecycleBin)
        {
            b.AddItem(new MenuItemDef
            {
                Id = "icon.open", Text = "打开", Group = MenuGroup.Common, IsDefault = true,
                Command = () => owner.InvokeOpenEntry(entry),
            });
            // shell 命名空间虚拟项（此电脑/回收站/网络）也走"打开"（explorer 语义：进入该位置）
            if (entry.IsShellNamespace)
            {
                return;
            }

            // 关联文件也提供"打开方式…"（explorer 同款；Unknown 场景已在上方定版）
            b.AddItem(new MenuItemDef
            {
                Id = "icon.openas", Text = "打开方式…", Group = MenuGroup.Common,
                Command = () => DesktopMenuActions.OpenWithDialog(entry.Path),
            });
            // 文件夹/驱动器：在新窗口中打开（explorer 同款）
            if (kind is FileKind.Folder or FileKind.Drive)
            {
                b.AddItem(new MenuItemDef
                {
                    Id = "icon.newwindow", Text = "在新窗口中打开", Group = MenuGroup.Common,
                    Command = () => DesktopMenuActions.OpenInNewWindow(entry.Path),
                });
            }
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
            b.AddItem(new MenuItemDef
            {
                Id = "icon.copypath", Text = "复制文件地址", Group = MenuGroup.Manage,
                Command = () => DesktopMenuActions.CopyPath(entry.Path),
            });

            // ④ 系统组：发送到▶（枚举 SendTo 文件夹，集合语义子菜单）。
            // 只对文件系统对象开放：shell 命名空间项（回收站/此电脑，::{CLSID}）不是可发送的
            // 文件路径，SendTo 目标程序收到 ::{} 参数无法处理（审查 P2-3）。
            var sendTo = DesktopMenuActions.EnumerateSendToLinks();
            if (sendTo.Count > 0)
            {
                b.AddItem(new MenuItemDef
                {
                    Id = "icon.sendto", Text = "发送到", Group = MenuGroup.System, Kind = MenuItemKind.Submenu,
                    Children = [.. sendTo.Select(kv => new MenuItemDef
                    {
                        Id = $"icon.sendto-{kv.Key}",
                        Text = kv.Key,
                        Command = () => DesktopMenuActions.SendTo(kv.Value, entry.Path),
                    })],
                });
            }
        }

        // ④ 系统组：属性
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

    /// <summary>在新窗口中打开（explorer 语义：文件夹/驱动器进入独立窗口）。</summary>
    public static void OpenInNewWindow(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.desktop", $"新窗口打开失败 {path}: {ex.Message}");
        }
    }

    /// <summary>复制完整路径到剪贴板（explorer"复制文件地址"同款）。</summary>
    public static void CopyPath(string path)
    {
        try
        {
            Clipboard.SetText(path);
        }
        catch (Exception ex)
        {
            // 剪贴板被占用等场景静默（M10）
            DiagnosticLog.Trace("shell.desktop", $"复制地址失败 {path}: {ex.Message}");
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

    /// <summary>新建快捷方式：Windows 内置"创建快捷方式"向导（rundll32 NewLinkHere，落盘到指定目录）。</summary>
    public static void OpenNewShortcutWizard(string directory)
    {
        try
        {
            Process.Start(new ProcessStartInfo("rundll32.exe", $"appwiz.cpl,NewLinkHere \"{directory}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.desktop", $"打开快捷方式向导失败: {ex.Message}");
        }
    }

    /// <summary>枚举用户「发送到」文件夹的快捷方式（显示名 → lnk 全路径；按显示名排序）。</summary>
    public static List<KeyValuePair<string, string>> EnumerateSendToLinks()
    {
        var result = new List<KeyValuePair<string, string>>();
        try
        {
            var sendTo = Environment.GetFolderPath(Environment.SpecialFolder.SendTo);
            if (string.IsNullOrEmpty(sendTo) || !Directory.Exists(sendTo)) return result;
            foreach (var lnk in Directory.EnumerateFiles(sendTo, "*.lnk"))
            {
                var name = Path.GetFileNameWithoutExtension(lnk);
                result.Add(new KeyValuePair<string, string>(name, lnk));
            }
            result.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.CurrentCulture));
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.desktop", $"枚举发送到失败: {ex.Message}");
        }
        return result;
    }

    /// <summary>Win32 属性页（ShellExecute properties verb；无需宿主 hwnd）。</summary>
    public static void ShowProperties(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "properties" });
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.desktop", $"打开属性失败 {path}: {ex.Message}");
        }
    }

    /// <summary>经 SendTo 快捷方式发送目标路径（lnk 目标程序接收路径参数）。</summary>
    public static void SendTo(string lnkPath, string targetPath)
    {
        try
        {
            Process.Start(new ProcessStartInfo(lnkPath)
            {
                UseShellExecute = true,
                Arguments = $"\"{targetPath}\"",
            });
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.desktop", $"发送到失败 {lnkPath}: {ex.Message}");
        }
    }
}
