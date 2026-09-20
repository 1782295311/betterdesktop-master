using System.Text.Json;

namespace BetterDesktop.Tray;

/// <summary>
/// 读取更新器写出的状态文件（%LOCALAPPDATA%\BetterDesktop\update-status.json）。
/// 与更新器解耦：只读 message/hasUpdate/remote 三个字段，字段缺失按"无结果"处理。
/// </summary>
internal static class UpdateStatusReader
{
    public static string? LastMessage()
    {
        try
        {
            var path = AppPaths.UpdateStatusFile;
            if (!File.Exists(path))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            var message = root.TryGetProperty("message", out var m) ? m.GetString() : null;
            var hasUpdate = root.TryGetProperty("hasUpdate", out var h) && h.ValueKind == JsonValueKind.True;
            var remote = root.TryGetProperty("remote", out var r) ? r.GetString() : null;

            if (string.IsNullOrWhiteSpace(message))
            {
                return hasUpdate ? $"发现新版本 {remote}" : null;
            }

            return message;
        }
        catch (Exception ex)
        {
            TrayLog.Write($"读取更新状态失败: {ex.Message}");
            return null;
        }
    }
}
