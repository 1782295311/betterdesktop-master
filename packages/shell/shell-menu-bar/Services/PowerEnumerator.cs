// BetterDesktop.Shell.MenuBar — 电源与性能模式枚举（真实系统数据源，零硬编码）。
// 电源信息：优先走 C++ 原生层 PowerCore.dll（GetSystemPowerStatus）。
// 性能模式：优先走 PowerCore.dll（powrprof PowerEnumerate → 友好名称 + 活动方案）；
//   原生层不可用时降级到托管 P/Invoke。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using BetterDesktop.Shell.Status.Native;

namespace BetterDesktop.Shell.MenuBar.Services;

/// <summary>电源（电池/接电）信息快照。</summary>
public sealed record PowerStatusInfo(
    int Percentage,           // 电量百分比 0-100（无电池时 -1）
    string LineStatusText,    // "已接通电源" / "使用电池" / "未检测到电池"
    string StatusText,        // 更细的状态文本（如"电量充满"、"剩余 N 分钟"）
    bool IsPlugged,           // 是否接通电源
    bool HasBattery);         // 是否有电池

/// <summary>性能模式（电源方案）快照。</summary>
public sealed record PowerPlanItem(
    Guid SchemeGuid,          // 方案 GUID
    string DisplayName,       // 方案友好名称（系统本地化文本，如 "平衡" / "高性能" / "卓越性能" / "Turbo"）
    string IconGlyph,         // 图标（Segoe UI Symbol）：性能=涡轮，节能=叶子，平衡=太极/普通
    bool IsActive);           // 是否当前启用

/// <summary>
/// 电源状态 + 性能模式：真实系统数据。
/// </summary>
internal static class PowerEnumerator
{
    public static PowerStatusInfo ReadStatus()
    {
        // 优先 C++ 原生层 PowerCore.dll。
        if (PowerCoreNative.IsAvailable)
        {
            var st = PowerCoreNative.ReadStatus();
            if (st.Percent != 0 || st.BatteryFlag != 0 || st.AcLine != 0 || st.LifeSeconds != 0)
            {
                return BuildStatus(st.Percent, st.AcLine == 1, (st.BatteryFlag & 0x80) == 0,
                    st.AcLine == 1, st.LifeSeconds);
            }
        }

        var raw = BatteryInterop.Read();
        if (raw is null)
        {
            return new PowerStatusInfo(-1, "未检测到电池", "未检测到电池 / 无法读取电源状态", false, false);
        }
        bool plugged = raw.Value.ACLineStatus == 1;
        bool hasBattery = (raw.Value.BatteryFlag & 0x80) == 0; // 0x80 = 无电池
        int percent = raw.Value.BatteryLifePercent;
        if (percent > 100) percent = -1;
        return BuildStatus(percent, plugged, hasBattery, plugged, (int)raw.Value.BatteryLifeTime);
    }

    private static PowerStatusInfo BuildStatus(int percent, bool plugged, bool hasBattery,
        bool isPlugged, int lifeSeconds)
    {
        string lineStatus = plugged ? "已接通电源" : (hasBattery ? "使用电池" : "未检测到电池");
        string statusText;
        if (plugged)
        {
            if (percent == 100) statusText = $"电量充满 {percent}%";
            else if (percent >= 0) statusText = $"已接通电源 {percent}% 电量";
            else statusText = "已接通电源";
        }
        else if (!hasBattery)
        {
            statusText = "未检测到电池（桌面机常见）";
        }
        else
        {
            var minutes = lifeSeconds <= 0 ? -1 : lifeSeconds / 60;
            if (minutes < 0)
            {
                statusText = $"剩余 {percent}%（剩余时长未知）";
            }
            else
            {
                var hrs = minutes / 60;
                var mins = minutes % 60;
                statusText = hrs > 0
                    ? $"剩余 {percent}%，约 {hrs} 小时 {mins} 分钟"
                    : $"剩余 {percent}%，约 {mins} 分钟";
            }
        }

        return new PowerStatusInfo(percent, lineStatus, statusText, isPlugged, hasBattery);
    }

    public static IReadOnlyList<PowerPlanItem> EnumeratePlans()
    {
        // 优先 C++ 原生层 PowerCore.dll。
        if (PowerCoreNative.IsAvailable)
        {
            try
            {
                var plans = PowerCoreNative.EnumeratePlans();
                if (plans.Length > 0)
                {
                    return plans
                        .Select(p => new PowerPlanItem(
                            p.Guid,
                            string.IsNullOrEmpty(p.Name) ? "未知方案" : LocalizePlanName(p.Name, p.Guid),
                            ClassifyIcon(p.Name, p.Guid),
                            p.IsActive))
                        .OrderByDescending(PerformanceRank)
                        .ToList();
                }
            }
            catch
            {
                // 原生层异常时降级托管实现。
            }
        }

        try
        {
            return EnumeratePlansCore();
        }
        catch
        {
            return Array.Empty<PowerPlanItem>();
        }
    }

    private static IReadOnlyList<PowerPlanItem> EnumeratePlansCore()
    {
        var result = new List<PowerPlanItem>(capacity: 6);
        IntPtr buffer = IntPtr.Zero;
        try
        {
            // 读当前活动方案
            var active = Guid.Empty;
            {
                IntPtr pActive = IntPtr.Zero;
                if (PowerGetActiveScheme(IntPtr.Zero, out pActive) == 0 && pActive != IntPtr.Zero)
                {
                    active = (Guid)Marshal.PtrToStructure(pActive, typeof(Guid))!;
                    LocalFree(pActive);
                }
            }

            // 枚举全部方案
            uint index = 0;
            while (true)
            {
                var sz = (uint)IntPtr.Size;
                var hr = PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    ACCESS_SCHEME, index, IntPtr.Zero, ref sz);
                if (hr != 0) break;
                buffer = Marshal.AllocHGlobal((int)sz);
                try
                {
                    hr = PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                        ACCESS_SCHEME, index, buffer, ref sz);
                    if (hr != 0) break;
                    var guid = (Guid)Marshal.PtrToStructure(buffer, typeof(Guid))!;
                    var name = ReadSchemeName(ref guid);
                    result.Add(new PowerPlanItem(
                        guid,
                        string.IsNullOrEmpty(name) ? "未知方案" : LocalizePlanName(name, guid),
                        ClassifyIcon(name, guid),
                        guid == active));
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                    buffer = IntPtr.Zero;
                }
                index++;
            }
            return result.OrderByDescending(PerformanceRank).ToList();
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
    }

    private static string ReadSchemeName(ref Guid guid)
    {
        uint size = 0;
        PowerReadFriendlyName(IntPtr.Zero, ref guid, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size);
        if (size == 0) return string.Empty;
        var ptr = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PowerReadFriendlyName(IntPtr.Zero, ref guid, IntPtr.Zero, IntPtr.Zero, ptr, ref size) != 0)
            {
                return string.Empty;
            }
            return Marshal.PtrToStringUni(ptr) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private static string ClassifyIcon(string name, Guid guid)
    {
        // 已知官方 GUID：
        //   平衡 381B4222-F694-41F0-9685-FF5BB260DF2E
        //   高性能 8C5E7FDA-E8BF-4A96-9AC8-A63556C34B13
        //   节能 A1841308-3541-4FAB-BC81-F71556F20B4A
        //   卓越性能 E9A42B00-950E-4A30-9CE0-48EB4AB499C7
        var g = guid.ToString().ToUpperInvariant();
        var n = (name ?? string.Empty).ToUpperInvariant();
        if (g == "A1841308-3541-4FAB-BC81-F71556F20B4A" || n.Contains("节能") || n.Contains("SILENT") || n.Contains("QUIET")) return "\uE74E"; // 叶子
        if (g == "8C5E7FDA-E8BF-4A96-9AC8-A63556C34B13" || n.Contains("高性能") || n.Contains("PERFORMANCE") || n.Contains("TURBO")) return "\uE9D2"; // 火箭/涡轮
        if (g == "E9A42B00-950E-4A30-9CE0-48EB4AB499C7" || n.Contains("卓越") || n.Contains("ULTIMATE")) return "\uE840"; // 闪电
        return "\uE783"; // 平衡：仪表盘
    }

    /// <summary>
    /// 把电源方案名映射为显示名。
    /// 只对 Windows 内建方案（GUID 恒定）映射为标准中文名；
    /// 其余 OEM/自定义方案（如厂商自带的 Performance / Silent / Turbo）保留系统原始名，
    /// 避免把多个不同方案折叠成同一个中文名（例如 Performance 与 Turbo 都被误译为"高性能"）。
    /// </summary>
    private static string LocalizePlanName(string name, Guid guid)
    {
        switch (guid.ToString().ToUpperInvariant())
        {
            // Windows 内建电源方案 GUID → 标准中文名
            case "381B4222-F694-41F0-9685-FF5BB260DF2E": return "平衡";
            case "8C5E7FDA-E8BF-4A96-9AC8-A63556C34B13": return "高性能";
            case "A1841308-3541-4FAB-BC81-F71556F20B4A": return "节能";
            case "E9A42B00-950E-4A30-9CE0-48EB4AB499C7": return "卓越性能";
        }

        // OEM/自定义方案：原样保留系统名称，保证每个方案显示名唯一、不折叠
        return string.IsNullOrWhiteSpace(name) ? name : name.Trim();
    }

    /// <summary>
    /// 按"释放性能由高到低"给电源方案打分，供排序：值越大性能越高。
    /// 优先按已知 GUID，其次按名称启发式，保证不同厂商方案的相对档位稳定。
    /// </summary>
    private static int PerformanceRank(PowerPlanItem plan)
    {
        var g = plan.SchemeGuid.ToString().ToUpperInvariant();
        var n = (plan.DisplayName ?? "").ToUpperInvariant();

        // 已知方案 GUID（含本机厂商 Turbo 档 6fecc5ae-f350-48a5-b669-b472cb895ccf）
        switch (g)
        {
            case "6FECC5AE-F350-48A5-B669-B472CB895CCF": return 100; // 涡轮/极速
            case "E9A42B00-950E-4A30-9CE0-48EB4AB499C7": return 90;  // 卓越性能
            case "8C5E7FDA-E8BF-4A96-9AC8-A63556C34B13": return 80;  // 高性能
            case "381B4222-F694-41F0-9685-FF5BB260DF2E": return 50;  // 平衡
            case "A1841308-3541-4FAB-BC81-F71556F20B4A": return 20;  // 节能
        }

        // 名称兜底：识别常见 OEM 档位名，避免未知方案全部挤在中间
        if (n.Contains("TURBO") || n.Contains("BOOST") || n.Contains("极速") || n.Contains("涡轮")) return 100;
        if (n.Contains("ULTIMATE") || n.Contains("卓越")) return 90;
        if (n.Contains("PERFORMANCE") || n.Contains("高性能") || n.Contains("GAMING") || n.Contains("游戏")) return 80;
        if (n.Contains("BALANCED") || n.Contains("平衡") || n.Contains("AUTO")) return 50;
        if (n.Contains("SILENT") || n.Contains("QUIET") || n.Contains("静音") || n.Contains("静") ||
            n.Contains("POWER SAVER") || n.Contains("节能") || n.Contains("BATTERY")) return 20;

        return 50; // 未知方案按平衡档排序
    }

    /// <summary>把给定电源方案设为当前活动方案。优先原生层，失败降级托管 P/Invoke。</summary>
    public static bool SetActiveScheme(Guid schemeGuid)
    {
        if (schemeGuid == Guid.Empty) return false;
        // 优先 C++ 原生层 PowerCore.dll。
        if (PowerCoreNative.IsAvailable && PowerCoreNative.SetActivePlan(schemeGuid))
        {
            return true;
        }
        try
        {
            return PowerSetActiveScheme(IntPtr.Zero, ref schemeGuid) == 0;
        }
        catch
        {
            return false;
        }
    }

    private const uint ACCESS_SCHEME = 16; // POWER_DATA_ACCESSOR::AccessScheme

    [DllImport("powrprof.dll")]
    private static extern uint PowerEnumerate(IntPtr RootPowerKey, IntPtr SchemeGuid,
        IntPtr SubGroupOfPowerSettingsGuid, uint AccessFlags, uint Index,
        IntPtr Buffer, ref uint BufferSize);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr UserRootPowerKey, out IntPtr ActivePolicyGuid);

    [DllImport("powrprof.dll", CharSet = CharSet.Unicode)]
    private static extern uint PowerReadFriendlyName(IntPtr RootPowerKey, ref Guid SchemeGuid,
        IntPtr SubGroupOfPowerSettingsGuid, IntPtr PowerSettingGuid,
        IntPtr Buffer, ref uint BufferSize);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr UserRootPowerKey, ref Guid SchemeGuid);
}
