// BetterDesktop.Host — 自绘桌面开关命令（--toggle-desktop 无运行实例时的设置直写）
// 场景：宿主未运行时，用户在 explorer 系统桌面右键菜单点「切换自绘桌面」。
//   → 直接翻转 %APPDATA%\BetterDesktop\settings.json 的 components.desktop；
//   → 若翻到"开"，再拉起宿主完整运行（自绘桌面需要宿主进程承载）；翻到"关"则直接退出。
// 宿主已在运行时走 MenuCommandPipe 命令桥（App.xaml.cs 的 --toggle-desktop 分支先 TrySend）。

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using BetterDesktop.Kernel.Core;

namespace BetterDesktop.Host;

/// <summary>自绘桌面开关：无宿主实例时直接读写 settings.json 并决定是否拉起宿主。</summary>
public static class DesktopToggleCommand
{
    private const string Key = "components.desktop";

    public static void ToggleAndMaybeLaunch()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BetterDesktop",
            "settings.json");

        // 读取当前值（缺失按默认 true 处理，与 ISettingsService.Get 默认一致）
        var current = true;
        try
        {
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path);
                if (JsonNode.Parse(text) is JsonObject root &&
                    root[Key] is JsonValue v &&
                    v.TryGetValue<bool>(out var b))
                {
                    current = b;
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("menu-cmd", $"读取设置失败（按默认处理）: {ex.Message}");
        }

        var next = !current;

        // 写回（宿主未运行无并发；失败不阻断——退出即可，下次宿主启动按旧值）
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
            root[Key] = next;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            DiagnosticLog.Trace("menu-cmd", $"切换自绘桌面(无实例直写): components.desktop={next}");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("menu-cmd", $"写入设置失败: {ex.Message}");
        }

        // 翻到"开"：自绘桌面需要宿主进程承载 → 拉起完整宿主；翻到"关"：无需动作
        if (!next)
        {
            return;
        }

        try
        {
            var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(exe))
            {
                _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                DiagnosticLog.Trace("menu-cmd", "已拉起宿主（自绘桌面开启）");
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("menu-cmd", $"拉起宿主失败: {ex.Message}");
        }
    }
}
