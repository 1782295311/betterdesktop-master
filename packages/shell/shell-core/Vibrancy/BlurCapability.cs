// BetterDesktop.Shell.Core.Vibrancy — 无色纯模糊的「可判定」能力探测（2026-09-18）
//
// 【为什么需要】WCA_ACCENT_POLICY（SetWindowCompositionAttribute）有三个特性让"毛玻璃没生效"变成盲区：
//   1) 返回 BOOL 但**失败时不设置 GetLastError**，出错码读不出东西；
//   2) **返回成功 ≠ DWM 真的画了模糊** —— 系统前提不满足时它照样返回成功、什么都不做；
//   3) 模糊只画在窗口的**透明像素**后面，窗口层铺了色就看不见（这是"代码没错但没效果"的第二大原因）。
//   所以出现"毛玻璃没生效"时，日志里什么都没有，只能靠猜。本类把"有没有可能生效"变成一次
//   **可判定、可落盘**的结论，并给出唯一真正的原因（而不是让使用者去猜）。
//
// 【结论的用途】诊断与告知：
//   · 设置中心据此显示真实状态，而不是让用户以为"我们功能坏了"；
//   · 无色主题据此决定"零色"是否安全（模糊不可用时零色会变成整窗透明穿帮）；
//   · 实拍像素级验证（分层/非分层 × 各档配方）见 tools/BlurProbe，本类不做抓屏、零副作用。
//
// 【环境前提（按命中率排序，均为系统级、代码无法绕过）】
//   1) 系统「设置 → 个性化 → 颜色 → 透明效果」= 关（HKCU Themes\Personalize\EnableTransparency=0）
//      → **所有 accent 模糊静默失效**。这是头号原因，且很多镜像/虚拟机默认关闭。
//   2) 省电模式开启 → 系统强制关闭透明效果（同 1）。
//   3) 远程会话（RDP / 部分虚拟机）或 DWM 合成未启用（基础显示驱动）→ 模糊一律无效。
//   4) Windows 10：没有 DWMWA_SYSTEMBACKDROP_TYPE(38)/Mica，只剩 accent 路径可用。

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.Core.Native;
using Microsoft.Win32;

namespace BetterDesktop.Shell.Core.Vibrancy;

/// <summary>
/// 无色纯模糊的能力探测结果（不可变快照；结论只描述"环境是否允许"，不代表某一档配方一定成功）。
/// </summary>
/// <param name="CompositionEnabled">DWM 合成是否启用（基础主题/远程会话可能为 false）。</param>
/// <param name="TransparencyEnabled">系统「透明效果」开关（0 = 关闭 → 所有 accent 模糊失效）。</param>
/// <param name="RemoteSession">是否远程会话（SM_REMOTESESSION）。</param>
/// <param name="PowerSaverOn">是否处于省电模式（会强制关闭透明效果）。</param>
/// <param name="OsBuild">Windows build（&lt; 22621 = Win10/Win11 21H2，无 DWM 系统材质）。</param>
/// <param name="ColorlessBlurExpected">综合判断：环境是否允许"无色纯模糊"生效。</param>
/// <param name="Reason">拦路原因（可读，直接落日志/上屏；为空串表示未发现拦路条件）。</param>
public sealed record BlurCapability(
    bool CompositionEnabled,
    bool TransparencyEnabled,
    bool RemoteSession,
    bool PowerSaverOn,
    int OsBuild,
    bool ColorlessBlurExpected,
    string Reason)
{
    private const string PersonalizeKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string TransparencyValueName = "EnableTransparency";

    /// <summary>settings.json 里的"先试哪一档模糊"键（排障/实验用；见 <see cref="ReadBlurPreference"/>）。</summary>
    private const string BlurImplementationKey = "appearance.material.blur";

    /// <summary>Win11 22H2（build 22621）起才有 DWMWA_SYSTEMBACKDROP_TYPE 系统材质。</summary>
    private const int Win11_22H2_Build = 22621;

    private static readonly object Gate = new();
    private static BlurCapability? _cached;

    /// <summary>当前探测结果（首次访问时探测一次并落日志；之后走缓存）。</summary>
    public static BlurCapability Current
    {
        get
        {
            lock (Gate)
            {
                return _cached ??= ProbeAndLog();
            }
        }
    }

    /// <summary>作废缓存（收到 WM_DWMCOMPOSITIONCHANGED / 透明效果被改动后调用）。</summary>
    public static void Invalidate()
    {
        lock (Gate)
        {
            _cached = null;
        }
    }

    /// <summary>重新探测并返回（作废缓存 + 立即探测）。</summary>
    public static BlurCapability Refresh()
    {
        Invalidate();
        return Current;
    }

    /// <summary>是否具备 DWM 系统材质（Mica/Acrylic backdrop）能力 —— 需要 Win11 22H2+ 且合成启用。</summary>
    public bool SupportsSystemBackdrop => OsBuild >= Win11_22H2_Build && CompositionEnabled;

    /// <summary>一行可读摘要（落日志 / 上屏）。</summary>
    public string Describe()
        => $"合成={CompositionEnabled} 透明效果={TransparencyEnabled} 远程会话={RemoteSession} " +
           $"省电模式={PowerSaverOn} build={OsBuild} → 无色模糊可用={ColorlessBlurExpected}" +
           (Reason.Length > 0 ? $"（拦路：{Reason}）" : string.Empty);

    private static BlurCapability ProbeAndLog()
    {
        var capability = Probe();
        DiagnosticLog.Trace("Vibrancy", "能力探测: " + capability.Describe());
        if (!capability.ColorlessBlurExpected)
        {
            DiagnosticLog.Trace("Vibrancy",
                "无色纯模糊在当前环境不可能生效（非代码问题）：" + capability.Reason +
                "。代码侧表现 = 窗口只剩主题托盘色（无色模式为近透明），不会出现模糊。");
        }

        return capability;
    }

    /// <summary>只读探测（无副作用、不抓屏）。</summary>
    public static BlurCapability Probe()
    {
        var composition = ReadCompositionEnabled();
        var transparency = ReadTransparencyEnabled();
        var remote = SafeGetSystemMetrics(NativeMethods.SM_REMOTESESSION) != 0;
        var powerSaver = ReadPowerSaverOn();
        var build = Environment.OSVersion.Version.Build;

        string reason;
        if (!composition)
        {
            reason = "DWM 合成未启用（基础显示驱动 / 部分远程会话）";
        }
        else if (remote)
        {
            reason = "当前是远程会话（RDP）";
        }
        else if (powerSaver)
        {
            reason = "省电模式已开启（系统强制关闭透明效果）";
        }
        else if (!transparency)
        {
            reason = "系统「透明效果」被关闭（设置 → 个性化 → 颜色 → 透明效果）";
        }
        else
        {
            reason = string.Empty;
        }

        return new BlurCapability(
            CompositionEnabled: composition,
            TransparencyEnabled: transparency,
            RemoteSession: remote,
            PowerSaverOn: powerSaver,
            OsBuild: build,
            ColorlessBlurExpected: reason.Length == 0,
            Reason: reason);
    }

    private static bool ReadCompositionEnabled()
    {
        try
        {
            return NativeMethods.DwmIsCompositionEnabled(out var enabled) == 0 && enabled;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>读系统「透明效果」开关；键不存在按"开启"处理（默认值就是开）。</summary>
    public static bool ReadTransparencyEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath);
            var raw = key?.GetValue(TransparencyValueName);
            return raw is not int value || value != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return true; // 读不到按"开启"处理：宁可报可用，也不误判成"功能坏了"
        }
    }

    private static bool ReadPowerSaverOn()
    {
        try
        {
            var status = default(NativeMethods.SYSTEM_POWER_STATUS);
            if (!NativeMethods.GetSystemPowerStatus(ref status))
            {
                return false;
            }

            // SystemStatusFlag 第 0 位 = 省电模式（Windows 10+）。
            return (status.SystemStatusFlag & 0x01) != 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// 读"先试哪一档模糊"（用于按机器 A/B：同一档在不同 build/虚拟机上并不一致）。
    /// <para>
    /// 取值来源（前者优先）：环境变量 <c>BETTERDESKTOP_BLUR_IMPL</c> → settings.json 的
    /// <c>appearance.material.blur</c>；取值 <c>blurbehind</c>（默认）/ <c>acrylic0</c> / <c>mica</c>。
    /// </para>
    /// <para>
    /// 刻意用"直接扫 settings.json"的轻量读法：shell-core **不能**引用 shell-settings
    ///（依赖方向相反），而这只是一个排障/实验开关，不值得为它引入跨层依赖
    ///（与 IndexEngineLauncher / watchdog 的标记读法同源）。
    /// </para>
    /// </summary>
    internal static DwmHelper.BlurPreference ReadBlurPreference()
    {
        if (TryParsePreference(Environment.GetEnvironmentVariable("BETTERDESKTOP_BLUR_IMPL"), out var fromEnv))
        {
            return fromEnv;
        }

        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BetterDesktop",
                "settings.json");
            if (!File.Exists(path))
            {
                return DwmHelper.BlurPreference.Auto;
            }

            var text = File.ReadAllText(path);
            var match = Regex.Match(
                text,
                "\"" + Regex.Escape(BlurImplementationKey) + "\"\\s*:\\s*\"(?<v>[^\"]*)\"");
            if (match.Success && TryParsePreference(match.Groups["v"].Value, out var fromSettings))
            {
                return fromSettings;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 读不到就用默认顺序：绝不因为设置文件异常而让毛玻璃不生效。
        }

        return DwmHelper.BlurPreference.Auto;
    }

    private static bool TryParsePreference(string? raw, out DwmHelper.BlurPreference preference)
    {
        preference = DwmHelper.BlurPreference.Auto;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        switch (raw.Trim().ToLowerInvariant())
        {
            case "blurbehind":
            case "blur":
                preference = DwmHelper.BlurPreference.BlurBehind;
                return true;
            case "acrylic0":
            case "acrylic":
                preference = DwmHelper.BlurPreference.AcrylicZeroTint;
                return true;
            case "mica":
                preference = DwmHelper.BlurPreference.Mica;
                return true;
            default:
                return false;
        }
    }

    private static int SafeGetSystemMetrics(int index)
    {
        try
        {
            return NativeMethods.GetSystemMetrics(index);
        }
        catch (DllNotFoundException)
        {
            return 0;
        }
    }

    /// <summary>
    /// 一键开启系统「透明效果」（写 HKCU + 广播 WM_SETTINGCHANGE 让 DWM 立即重读）。
    /// <para>
    /// 这是"模糊不可用"里**唯一能被我们修好**的一类（关闭者多为系统镜像/优化工具，用户自己不知道）。
    /// 其余前提（远程会话、DWM 未启用、省电模式）代码无法绕过，只能如实告知。
    /// </para>
    /// </summary>
    /// <param name="message">结果说明（可直接上屏）。</param>
    public static bool TryEnableSystemTransparency(out string message)
    {
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(PersonalizeKeyPath, writable: true))
            {
                if (key is null)
                {
                    message = "无法打开注册表键（可能被策略锁定），请手动开启：设置 → 个性化 → 颜色 → 透明效果。";
                    return false;
                }

                key.SetValue(TransparencyValueName, 1, RegistryValueKind.DWord);
            }

            BroadcastImmersiveColorSet();
            Invalidate();

            if (ReadTransparencyEnabled())
            {
                message = "已开启系统「透明效果」。若窗口仍无模糊，请再关闭省电模式。";
                return true;
            }

            message = "已写入设置，但系统未接受（可能被组策略/省电模式覆盖）。请手动检查：设置 → 个性化 → 颜色 → 透明效果。";
            return false;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            message = $"开启失败：{ex.Message}。请手动开启：设置 → 个性化 → 颜色 → 透明效果。";
            return false;
        }
    }

    /// <summary>广播 WM_SETTINGCHANGE("ImmersiveColorSet")：通知系统个性化设置已变更（透明效果即时生效）。</summary>
    private static void BroadcastImmersiveColorSet()
    {
        const uint SmtoAbortIfHung = 0x0002;
        const int BroadcastTimeoutMs = 1000;
        var hwndBroadcast = new IntPtr(0xFFFF);
        var payload = Marshal.StringToHGlobalUni("ImmersiveColorSet");
        try
        {
            _ = NativeMethods.SendMessageTimeout(
                hwndBroadcast,
                (uint)NativeMethods.WM_SETTINGCHANGE,
                IntPtr.Zero,
                payload,
                SmtoAbortIfHung,
                BroadcastTimeoutMs,
                out _);
        }
        finally
        {
            Marshal.FreeHGlobal(payload);
        }
    }
}
