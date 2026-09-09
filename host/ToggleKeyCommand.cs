// BetterDesktop.Host — 自绘UI开关命令（--toggle-key <icons|menubar|dock> 无运行实例时的设置直写）
// 场景：宿主未运行时，用户在 explorer 系统桌面右键「自绘桌面 ▸」点图标/菜单栏/Dock 显隐开关。
//   → 直接翻转 %APPDATA%\BetterDesktop\settings.json 对应键，下次宿主启动生效；
//     不拉起宿主（icons/menubar/dock 的承载窗口随宿主存在，宿主不在即无窗口可即时切换）。
// 宿主已在运行时走 MenuCommandPipe 命令桥（App.xaml.cs 的 --toggle-key 分支先 TrySend）。
// 默认值按键区分：icons(desktop.iconsHidden) 默认 false（未隐藏）；menubar/dock 默认 true（启用）。

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using BetterDesktop.Kernel.Core;

namespace BetterDesktop.Host;

/// <summary>自绘UI开关：无宿主实例时直接读写 settings.json（icons/menubar/dock）。</summary>
public static class ToggleKeyCommand
{
    /// <summary>命令键 → (settings 键, 默认值)。icons 对应 desktop.iconsHidden。</summary>
    private static readonly (string Cmd, string Key, bool Def)[] Map =
    [
        ("icons", "desktop.iconsHidden", false),
        ("menubar", "components.menubar", true),
        ("dock", "components.dock", true),
        ("taskbar", "components.wintaskbar", true),
    ];

    public static void Run(string cmdKey)
    {
        string? settingsKey = null;
        var def = true;
        foreach (var (cmd, key, d) in Map)
        {
            if (string.Equals(cmd, cmdKey, StringComparison.OrdinalIgnoreCase))
            {
                settingsKey = key;
                def = d;
                break;
            }
        }
        if (settingsKey is null)
        {
            DiagnosticLog.Trace("menu-cmd", $"未知 toggle-key: {cmdKey}");
            return;
        }

        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BetterDesktop",
            "settings.json");

        var current = def;
        try
        {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject root &&
                root[settingsKey] is JsonValue v && v.TryGetValue<bool>(out var b))
            {
                current = b;
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("menu-cmd", $"读取设置失败（按默认处理）: {ex.Message}");
        }

        var next = !current;
        try
        {
            var root = new JsonObject();
            if (File.Exists(path))
            {
                try
                {
                    root = (JsonNode.Parse(File.ReadAllText(path)) as JsonObject) ?? new JsonObject();
                }
                catch
                {
                    root = new JsonObject();
                }
            }
            root[settingsKey] = next;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            DiagnosticLog.Trace("menu-cmd", $"切换UI(无实例直写): {settingsKey}={next}");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("menu-cmd", $"写入设置失败: {ex.Message}");
        }
    }
}
