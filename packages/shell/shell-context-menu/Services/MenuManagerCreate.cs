// BetterDesktop.Shell.ContextMenus — 管理器新建/注入（M2/M3 分片，partial MenuManagerService）
// 写侧一律 HKCU\Software\Classes（合并视图生效，零夺权）。命令桥：宿主 --menu-cmd 单实例转发。

using System;
using BetterDesktop.Kernel.Core;
using Microsoft.Win32;

namespace BetterDesktop.Shell.ContextMenus.Services;

// ── 本文件方法级白话索引（MenuManager 新建/注入分片，白话 → 方法）──
//   "新建一个自定义静态右键菜单项（只写 HKCU，不夺权）" → CreateStaticVerb
//   "给某扩展名注册『新建文件』ShellNew 子项" → CreateShellNew
//   "注入 BetterDesktop 自有菜单项（如压缩/解压，键名前缀 UserMenu）" → InjectBetterDeskItems
// ────────────────────────────────────

public static partial class MenuManagerService
{
    /// <summary>新建静态菜单项（M2）。sceneKey 为 Scenes 之 key。</summary>
    public static MenuItemInfo CreateStaticVerb(string sceneKey, string keyName, string displayText, string command)
    {
        if (string.IsNullOrWhiteSpace(sceneKey) || string.IsNullOrWhiteSpace(keyName) || string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException("场景/键名/命令均不能为空");
        }
        if (keyName.Contains('\\') || keyName.Contains('/') || keyName.Contains('*'))
        {
            throw new ArgumentException("键名含非法字符");
        }
        var scene = System.Linq.Enumerable.FirstOrDefault(Scenes, s => s.Key == sceneKey);
        if (scene.Key is null)
        {
            throw new ArgumentException($"未知场景: {sceneKey}");
        }
        var path = $@"{UserClassesRoot}\{scene.RegPath}\shell\{keyName}";
        Registry.SetValue(path, "MUIVerb", displayText);
        Registry.SetValue(path + @"\command", string.Empty, command);
        DiagnosticLog.Trace("menu-manager", $"新建静态项: {path}");
        return new MenuItemInfo(sceneKey, keyName, "Static", displayText, "HKCU", path, true, string.Empty, command);
    }

    /// <summary>新建 ShellNew（M2；HKCU；NullFile 空模板）。</summary>
    public static void CreateShellNew(string ext)
    {
        if (string.IsNullOrWhiteSpace(ext))
        {
            throw new ArgumentException("扩展名不能为空");
        }
        if (!ext.StartsWith('.'))
        {
            ext = "." + ext;
        }
        Registry.SetValue(UserClassesRoot + @"\" + ext + @"\ShellNew", "NullFile", string.Empty);
        DiagnosticLog.Trace("menu-manager", $"新建 ShellNew: {ext}");
    }

    /// <summary>注入 BetterDesktop 自有菜单项（M3；幂等；全 HKCU）。命令桥 = 宿主 --menu-cmd 单实例转发。</summary>
    public static void InjectBetterDeskItems()
    {
        var hostPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法定位宿主 exe");
        var host = $"\"{hostPath}\"";

        // 桌面背景 → BetterDesktop 子菜单（打开设置）
        var desk = UserClassesRoot + @"\DesktopBackground\shell\BetterDesk";
        Registry.SetValue(desk, "MUIVerb", "BetterDesktop");
        Registry.SetValue(desk + @"\shell\settings", "MUIVerb", "BetterDesktop 设置…");
        Registry.SetValue(desk + @"\shell\settings\command", string.Empty, $"{host} --menu-cmd open-settings");

        // 文件/文件夹场景 → 钉到 Dock
        foreach (var scene in new[] { "*", "AllFilesystemObjects", "Directory" })
        {
            var path = $@"{UserClassesRoot}\{scene}\shell\BetterDeskDock";
            Registry.SetValue(path, "MUIVerb", "钉到 Dock");
            Registry.SetValue(path + @"\command", string.Empty, $"{host} --menu-cmd dock-pin \"%1\"");
        }
        DiagnosticLog.Trace("menu-manager", "BetterDesktop 自有项注入完成（幂等）");
    }
}
