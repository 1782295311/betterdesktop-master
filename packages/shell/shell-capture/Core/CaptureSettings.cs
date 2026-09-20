using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BetterDesktop.Shell.Capture.Core;

/// <summary>
/// capture 设置（%LOCALAPPDATA%\BetterDesktop\capture\settings.json）。
/// 键：hotkey（热键，HotKeyManager 读）、stickerTopmost（贴图默认置顶，2026-09-15 新增）。
/// 读取失败一律回退默认值——设置绝不阻塞截图主流程。
/// </summary>
public static class CaptureSettings
{
    /// <summary>贴图窗口默认是否置顶（托盘菜单/settings.json 可改，下次贴图生效）。</summary>
    public static bool StickerTopmost => ReadBool("stickerTopmost", true);

    /// <summary>写入贴图默认置顶（合并写回，保留 hotkey 等其他键；失败仅告警）。</summary>
    public static void SetStickerTopmost(bool value)
    {
        try
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string path = Path.Combine(root, "BetterDesktop", "capture", "settings.json");
            JsonNode doc;
            if (File.Exists(path))
            {
                doc = JsonNode.Parse(File.ReadAllText(path)) ?? new JsonObject();
            }
            else
            {
                doc = new JsonObject();
            }
            doc["stickerTopmost"] = value;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            CaptureLog.Warn($"写贴图置顶设置失败：{ex.Message}");
        }
    }

    private static bool ReadBool(string key, bool defaultValue)
    {
        try
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string path = Path.Combine(root, "BetterDesktop", "capture", "settings.json");
            if (!File.Exists(path))
            {
                return defaultValue;
            }
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty(key, out var el)
                && (el.ValueKind == JsonValueKind.True || el.ValueKind == JsonValueKind.False)
                ? el.GetBoolean()
                : defaultValue;
        }
        catch
        {
            return defaultValue;
        }
    }
}
