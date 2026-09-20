// BetterDesktop.Host — 自绘UI开关命令（--toggle-key <icons|taskbar|doubleclick|menubar|dock|hotkey-panel> 无运行实例时的设置直写）
// 场景：宿主进程被以 `--toggle-key <key>` 拉起（无运行实例），由用户/系统菜单触发的开关翻转。
//   → 直接翻转 %APPDATA%\BetterDesktop\settings.json 对应键；**并对能被 explorer 原生层直接生效的开关当场生效**
//     （图标 / 任务栏，2026-09-17 桌面控制独立化：无宿主时"只写盘=点了没反应"，用户实测过）。
// 宿主已在运行时走 MenuCommandPipe 命令桥（App.xaml.cs 的 --toggle-key 分支先 TrySend）。
// 默认值按键区分：icons(desktop.iconsHidden) 默认 false（未隐藏）；其余默认 true。
// 【2026-09-11】新增 doubleclick → desktop.doubleClickHideIcons（"双击隐藏图标"开关，默认开）。
// 【2026-09-17】键名/默认值/可执行性改由 DesktopToggleCatalog 单点定义（此前宿主/CLI/Host 三处各写一份，
//   已漂移过一次：CLI 那份漏了 doubleclick）。本文件不再自持映射。

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.Core.DesktopControl;

namespace BetterDesktop.Host;

/// <summary>自绘UI开关：无宿主实例时直接读写 settings.json（+ 可原生生效项当场生效）。</summary>
public static class ToggleKeyCommand
{
    public static void Run(string cmdKey)
    {
        if (!DesktopToggleCatalog.TryGet(cmdKey, out var toggle))
        {
            DiagnosticLog.Trace("menu-cmd", $"未知 toggle-key: {cmdKey}");
            return;
        }

        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BetterDesktop",
            "settings.json");

        var current = toggle.Default;
        try
        {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject root &&
                root[toggle.SettingsKey] is JsonValue v && v.TryGetValue<bool>(out var b))
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
            root[toggle.SettingsKey] = next;

            // 显式留痕：让「桌面控制」的选择压过其它功能的默认隐藏（如 dock 默认隐藏原生任务栏）。
            foreach (var (overrideKey, overrideValue) in DesktopToggleCatalog.ExplicitOverrides(toggle, next))
            {
                root[overrideKey] = overrideValue;
            }

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            // 2026-09-16：改走安全写入口（原子 + 备份 + 防清空守卫）——本进程是短命写者，
            // 直接 File.WriteAllText 整文件覆盖曾把用户配置清空（见 SettingsFileWriter 注释）。
            if (!SettingsFileWriter.TryWrite(path, root))
            {
                DiagnosticLog.Trace("menu-cmd", $"切换UI(无实例直写)被拒绝：{toggle.SettingsKey}（疑似丢键事故，已放弃写入）");
                return;
            }

            DiagnosticLog.Trace("menu-cmd", $"切换UI(无实例直写): {toggle.SettingsKey}={next}");

            // 【2026-09-17】免宿主当场生效：原生层（图标 / 任务栏）。
            // 走到本分支即"宿主不在"，所以写盘没有消费者——不补这一步就是用户实测的"点了没反应"。
            if (toggle.NativeEffective)
            {
                var applied = DesktopControlNative.Apply(toggle.Name, next);
                DiagnosticLog.Trace("menu-cmd", $"原生层立即生效: {toggle.Name} → {next}（applied={applied}）");
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("menu-cmd", $"写入设置失败: {ex.Message}");
        }
    }
}
