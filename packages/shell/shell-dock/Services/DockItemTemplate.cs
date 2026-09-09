// BetterDesktop.Shell.Dock — Dock 项统一右键菜单模板（M3 计划：Scope=DockItem）
// 原 ShowItemContextMenu 裸 items 列表迁入模板：获得统一外观（三列/图标/助记）+
// FileIdentity 能力过滤（M2/C7 注册表与内置贡献项自动并入：打开方式/压缩/注册表 verb…）。
// 目标身份：DockItemData.TargetPath（优先）或 ShortcutPath；虚拟/异常目标降级无 File 区。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.ContextMenus.Contracts;
using BetterDesktop.Shell.Dock.Models;

namespace BetterDesktop.Shell.Dock.Services;

public sealed class DockItemTemplate(IDockAppsService apps) : IMenuTemplate
{
    public MenuScope Scope => MenuScope.DockItem;

    public void Build(IMenuTemplateBuilder b, MenuRequest request)
    {
        if (request.Target is not DockItemData item)
        {
            return;
        }

        // ① 常用操作组：启动（dock 核心语义；运行中窗口激活语义由 DockWindow.Launch 注入——
        //    单实例/已运行激活是任务栏级行为，模板不做简化复制品）
        b.AddItem(new MenuItemDef
        {
            Id = "dockitem.launch",
            Text = "启动",
            Group = MenuGroup.Common,
            IsDefault = true,
            Command = () => (Launch ?? LaunchAppFallback)(item),
        });

        // 文件能力区（TargetPath 可分类时；能力过滤经 RequiredCapability 隐藏优先）。
        // lnk/exe 目标 → 打开方式…/打开文件位置/复制文件地址（explorer 同款）；目录目标跳过。
        if (request.File is { } id && id.Kind is not (FileKind.InRecycleBin or FileKind.ShellNamespace))
        {
            var path = id.Path;
            if (!string.IsNullOrEmpty(path))
            {
                b.AddItem(new MenuItemDef
                {
                    Id = "dockitem.openas",
                    Text = "打开方式…",
                    Group = MenuGroup.Common,
                    RequiredCapability = FileCapabilities.OpenWith,
                    Command = () => RunVerb(path, "openas"),
                });
                b.AddItem(new MenuItemDef
                {
                    Id = "dockitem.dir",
                    Text = "打开所在目录",
                    Group = MenuGroup.Common,
                    RequiredCapability = FileCapabilities.OpenFileLocation,
                    Command = () => OpenContainingDirectory(item),
                });
                b.AddItem(new MenuItemDef
                {
                    Id = "dockitem.copypath",
                    Text = "复制文件地址",
                    Group = MenuGroup.Manage,
                    Command = () =>
                    {
                        try { System.Windows.Clipboard.SetText(path); }
                        catch (Exception ex) { DiagnosticLog.Trace("shell.dock", $"复制路径失败 {path}: {ex.Message}"); }
                    },
                });
            }
        }

        // ② 管理组：从 Dock 移除
        b.AddItem(new MenuItemDef
        {
            Id = "dockitem.remove",
            Text = "从 Dock 移除",
            Group = MenuGroup.Manage,
            Command = () => { apps.RemoveById(item.Id); apps.Save(); },
        });

        // ④ 系统组：开始菜单 / 应用提取器（dock 自身语义，原裸菜单迁入）
        b.AddItem(new MenuItemDef
        {
            Id = "dockitem.startmenu",
            Text = "开始菜单",
            Group = MenuGroup.System,
            Command = () => ToggleStartMenu?.Invoke(),
        });
        b.AddItem(new MenuItemDef
        {
            Id = "dockitem.appgrabber",
            Text = "应用提取器",
            Group = MenuGroup.System,
            Command = () => ShowAppGrabber?.Invoke(),
        });
    }

    /// <summary>启动动作（DockWindow 装配时注入：含"已运行=激活"完整语义）。</summary>
    public Action<DockItemData>? Launch { get; set; }

    /// <summary>开始菜单 toggle（DockPlugin/DockWindow 装配时注入；与原 ShowStartContextMenu 同源）。</summary>
    public Action? ToggleStartMenu { get; set; }

    /// <summary>应用提取器懒打开（DockWindow 装配时注入）。</summary>
    public Action? ShowAppGrabber { get; set; }

    private static void LaunchAppFallback(DockItemData item)
    {
        try
        {
            var path = !string.IsNullOrWhiteSpace(item.TargetPath) ? item.TargetPath : item.ShortcutPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            // 启动失败不阻断（M10）
        }
    }

    private static void OpenContainingDirectory(DockItemData item)
    {
        try
        {
            var path = !string.IsNullOrWhiteSpace(item.TargetPath) ? item.TargetPath : item.ShortcutPath;
            var dir = string.IsNullOrWhiteSpace(path) ? null : System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
            }
        }
        catch
        {
            // M10
        }
    }

    private static void RunVerb(string path, string verb)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = verb });
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.dock", $"openas 失败 {path}: {ex.Message}");
        }
    }
}
