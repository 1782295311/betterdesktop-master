// BetterDesktop.Kernel.Deployment — HKCU 开机自启登记（Run 键 + StartupApproved 双写）
//
// 【为什么必须双写】技术力 74-Windows内部接口逆向/7430-windows-autostart-startupapproved.md 红线 1：
//   任务管理器的"禁用"状态**单独记录**在
//   HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run（Byte0 = 3 禁用 / 2 启用），
//   只写 Run 键会被该状态静默压制 —— 表现为"装好了、Run 键也在，但开机就是不起来"。
//   本仓此前 tray/SettingsBridge.AutoStart 与 shell-settings/SystemManagement 都只写了 Run 键（已知坑）。
//
// 【删除语义】删 Run 值 + 删 StartupApproved 条目；条目/键本就不存在**不算错误**
//   （红线 3：ERROR_FILE_NOT_FOUND 当错误抛 = 误报）。
//
// 【值名是跨进程契约】托盘（零包引用，自行实现同一语义）与安装/卸载脚本都按这些字面量操作；
//   改一处必须改全部（scripts/verify-system-integration.ps1 会校验字面量一致）。

using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace BetterDesktop.Kernel.Deployment;

/// <summary>开机自启登记器（HKCU，免管理员）。</summary>
public static class AutostartRegistrar
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>任务管理器"启用/禁用"状态所在键（Byte0 语义见技术力 7430）。</summary>
    public const string StartupApprovedKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    /// <summary>托盘自启值名（托盘 = 中转站，开机后由它带起 Agent / 桌面服务）。</summary>
    public const string TrayValueName = "BetterDesktop.Tray";

    /// <summary>看门狗自启值名（与 shell-settings/SystemManagement 既有实现同名同义）。</summary>
    public const string WatchdogValueName = "BetterDesktop.Watchdog";

    /// <summary>主程序（壳）自启值名 —— 由用户在设置里决定，安装器默认不写。</summary>
    public const string ShellValueName = "BetterDesktop";

    private const byte ApprovedEnabled = 2;
    private const byte ApprovedDisabled = 3;

    /// <summary>StartupApproved 值长度（实测 12 字节；Byte0 为状态位）。</summary>
    private const int ApprovedLength = 12;

    /// <summary>本程序会管理的全部自启值名（卸载/注销时逐个清）。</summary>
    public static IReadOnlyList<string> KnownValueNames { get; } = new[]
    {
        TrayValueName,
        WatchdogValueName,
        ShellValueName,
    };

    /// <summary>
    /// 登记或取消自启。<paramref name="command"/> 为 null/空白 = 取消（删除 Run 值 + StartupApproved 条目）。
    /// 失败返回 false 并把原因写 <paramref name="error"/>（不抛）。
    /// </summary>
    public static bool Set(string valueName, string? command, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(valueName))
        {
            error = "自启值名不能为空";
            return false;
        }

        if (string.IsNullOrWhiteSpace(command))
        {
            return Remove(valueName, out error);
        }

        try
        {
            using (var run = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                if (run is null)
                {
                    error = $@"无法打开 HKCU\{RunKeyPath}";
                    return false;
                }

                run.SetValue(valueName, Quote(command), RegistryValueKind.String);
            }

            // 双写启用态：清掉可能存在的"任务管理器禁用"（Byte0=3），否则 Run 键写了也不生效。
            MarkApproved(valueName, ApprovedEnabled);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 当前是否**真的会**自启：Run 值存在，且 StartupApproved 未标记为禁用（Byte0 != 3）。
    /// 刻意与用户可见状态一致 —— 只看 Run 键会把"任务管理器里已禁用"报成已启用。
    /// </summary>
    public static bool IsEnabled(string valueName)
    {
        var command = GetCommand(valueName);
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupApprovedKeyPath);
            if (key?.GetValue(valueName) is byte[] bytes && bytes.Length > 0 && bytes[0] == ApprovedDisabled)
            {
                return false;
            }
        }
        catch
        {
            // 读不到状态位时按 Run 键判定（保守：不把"已登记"误报成"未登记"）
        }

        return true;
    }

    /// <summary>Run 值里记录的命令行（未登记返回 null）。</summary>
    public static string? GetCommand(string valueName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(valueName) as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>删除自启（Run + StartupApproved 两侧都清；本就不存在视为成功）。</summary>
    public static bool Remove(string valueName, out string? error)
    {
        error = null;
        try
        {
            using (var run = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true))
            {
                run?.DeleteValue(valueName, throwOnMissingValue: false);
            }

            using (var approved = Registry.CurrentUser.OpenSubKey(StartupApprovedKeyPath, writable: true))
            {
                approved?.DeleteValue(valueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>把 StartupApproved 状态位置为启用（保留既有值的其余字节，避免破坏任务管理器的记录格式）。</summary>
    private static void MarkApproved(string valueName, byte state)
    {
        using var key = Registry.CurrentUser.CreateSubKey(StartupApprovedKeyPath);
        if (key is null)
        {
            return;
        }

        var bytes = key.GetValue(valueName) as byte[];
        if (bytes is null || bytes.Length < ApprovedLength)
        {
            bytes = new byte[ApprovedLength];
        }

        bytes[0] = state;
        key.SetValue(valueName, bytes, RegistryValueKind.Binary);
    }

    /// <summary>与既有 writer 同格式：始终加引号（路径含空格时必须）。</summary>
    private static string Quote(string command)
        => command.StartsWith('"') ? command : $"\"{command}\"";
}
