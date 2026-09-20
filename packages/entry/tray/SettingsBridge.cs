using System.Text.Json;
using Microsoft.Win32;

namespace BetterDesktop.Tray;

/// <summary>
/// 设置读取（只读）。
/// 托盘**只读** settings.json 用于显示菜单勾选状态；任何写入都必须经 BetterDesktop.Cli.exe
/// （既有契约：宿主在 → 管道热切；宿主不在 → CLI 直写）——避免两处实现漂移出不一致的写语义。
/// </summary>
internal static class SettingsBridge
{
    /// <summary>读取布尔设置；文件/键缺失或解析失败时返回 <paramref name="fallback"/>（不抛）。</summary>
    public static bool GetBool(string key, bool fallback)
    {
        try
        {
            var path = AppPaths.SettingsFile;
            if (!File.Exists(path))
            {
                return fallback;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty(key, out var value) &&
                value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return value.GetBoolean();
            }
        }
        catch (Exception ex)
        {
            TrayLog.Write($"读取设置失败（{key} 按默认 {fallback}）: {ex.Message}");
        }

        return fallback;
    }
}

/// <summary>
/// 开机自启登记（HKCU）。独立实现而不复用 shell-settings.SystemManagement / Kernel 的注册中心：
/// 托盘零包引用（见 csproj 头注释），只能自持一份。
///
/// 【2026-09-17 双写修正】按技术力 7430（windows-autostart-startupapproved）红线：
///   任务管理器的"禁用"状态**单独记录**在 Explorer\StartupApproved\Run（Byte0 = 3 禁用 / 2 启用），
///   只写 Run 键会被它静默压制 —— 用户看到的就是"装了自启却不开机启动"。
///   故此处与 Kernel.Deployment.AutostartRegistrar 同语义（Run + StartupApproved）：
///   启用时把 Byte0 写回 2，删除时两侧都清。**改一处务必改另一处**。
/// </summary>
internal static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupApprovedKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    /// <summary>自启值名（跨进程契约：安装脚本、Agent 与 CLI --system-integration 都按这个名字找它）。</summary>
    public const string ValueName = "BetterDesktop.Tray";

    private const byte ApprovedEnabled = 2;
    private const byte ApprovedDisabled = 3;
    private const int ApprovedLength = 12;

    public static bool IsEnabled()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            if (run?.GetValue(ValueName) is not string s || s.Length == 0)
            {
                return false;
            }

            // Run 键在，但任务管理器可能把它禁用了（Byte0=3）——那"实际不会自启"，不能报成已启用
            using var approved = Registry.CurrentUser.OpenSubKey(StartupApprovedKeyPath);
            return !(approved?.GetValue(ValueName) is byte[] bytes && bytes.Length > 0
                     && bytes[0] == ApprovedDisabled);
        }
        catch (Exception ex)
        {
            TrayLog.Write($"读取自启项失败: {ex.Message}");
            return false;
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            if (enabled)
            {
                using (var run = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (run is null)
                    {
                        TrayLog.Write("自启项写入失败：Run 键不可写");
                        return;
                    }

                    run.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
                }

                MarkApproved();
                TrayLog.Write($"已登记开机自启: {Environment.ProcessPath}");
            }
            else
            {
                using (var run = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true))
                {
                    run?.DeleteValue(ValueName, throwOnMissingValue: false);
                }

                using (var approved = Registry.CurrentUser.OpenSubKey(StartupApprovedKeyPath, writable: true))
                {
                    approved?.DeleteValue(ValueName, throwOnMissingValue: false);
                }

                TrayLog.Write("已取消开机自启");
            }
        }
        catch (Exception ex)
        {
            TrayLog.Write($"自启项写入失败: {ex.Message}");
        }
    }

    private static void MarkApproved()
    {
        using var key = Registry.CurrentUser.CreateSubKey(StartupApprovedKeyPath);
        if (key is null)
        {
            return;
        }

        var bytes = key.GetValue(ValueName) as byte[];
        if (bytes is null || bytes.Length < ApprovedLength)
        {
            bytes = new byte[ApprovedLength];
        }

        bytes[0] = ApprovedEnabled;
        key.SetValue(ValueName, bytes, RegistryValueKind.Binary);
    }
}
