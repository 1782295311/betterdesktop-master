// BetterDesktop.Shell.Status — PowerCore.dll 的 C# Interop 薄封装（纯转发，无业务逻辑）。
using System;
using System.Runtime.InteropServices;

namespace BetterDesktop.Shell.Status.Native;

/// <summary>电源状态原生快照。</summary>
public readonly record struct PowerStatusNative(
    int AcLine,
    int BatteryFlag,
    int Percent,
    int LifeSeconds);

/// <summary>性能方案原生快照。</summary>
public readonly record struct PowerPlanNative(Guid Guid, string Name, bool IsActive);

/// <summary>电池详细信息原生快照（IOCTL 直读）。</summary>
public readonly record struct BatteryDetailNative(
    bool HasBattery,
    bool AcOnline,
    bool Charging,
    int Percent,           // 0-100，-1=未知
    int CurrentCapacityMwh,
    int FullCapacityMwh,
    int DesignCapacityMwh,
    int HealthPercent,     // 0-100，-1=未知
    int RateMw,            // 正=放电，负=充电，0=空闲/未知
    int RemainingSeconds,  // -1=未知
    int CycleCount,        // -1=未知
    int TemperatureC);     // 摄氏度*10（如 325=32.5°C），-1=未知

/// <summary>PowerCore.dll 的薄封装。仅转发，不做任何业务判断。</summary>
public static class PowerCoreNative
{
    private delegate int PowerReadStatus(out int acLine, out int batteryFlag, out int percent, out int lifeSeconds);
    private delegate int PowerEnumeratePlans(out int planCount);
    private delegate int PowerGetPlan(int index, out Guid guid, [Out] ushort[] name, int nameCch, out int isActive);
    private delegate int PowerSetActivePlan(ref Guid guid);

    private delegate int PowerReadBatteryDetail(
        out int hasBattery, out int acOnline, out int charging, out int percent,
        out int currentCapacity, out int fullCapacity, out int designCapacity,
        out int healthPercent, out int rateMw, out int remainingSeconds,
        out int cycleCount, out int temperatureC);

    private static readonly PowerReadStatus? _readStatus;
    private static readonly PowerEnumeratePlans? _enumerate;
    private static readonly PowerGetPlan? _getPlan;
    private static readonly PowerSetActivePlan? _setActivePlan;
    private static readonly PowerReadBatteryDetail? _readBatteryDetail;

    static PowerCoreNative()
    {
        _readStatus = NativeLoader.GetExport<PowerReadStatus>("PowerCore.dll", "Power_ReadStatus");
        _enumerate = NativeLoader.GetExport<PowerEnumeratePlans>("PowerCore.dll", "Power_EnumeratePlans");
        _getPlan = NativeLoader.GetExport<PowerGetPlan>("PowerCore.dll", "Power_GetPlan");
        _setActivePlan = NativeLoader.GetExport<PowerSetActivePlan>("PowerCore.dll", "Power_SetActivePlan");
        _readBatteryDetail = NativeLoader.GetExport<PowerReadBatteryDetail>("PowerCore.dll", "Power_ReadBatteryDetail");
    }

    public static bool IsAvailable => _readStatus is not null && _enumerate is not null && _getPlan is not null;

    /// <summary>读电源状态。失败时 AllZero。</summary>
    public static PowerStatusNative ReadStatus()
    {
        if (_readStatus is null)
        {
            return default;
        }
        try
        {
            int hr = _readStatus(out int ac, out int flag, out int percent, out int life);
            if (hr != 0)
            {
                return default;
            }
            return new PowerStatusNative(ac, flag, percent, life);
        }
        catch
        {
            return default;
        }
    }

    /// <summary>枚举全部性能方案。失败时返回空数组。</summary>
    public static PowerPlanNative[] EnumeratePlans()
    {
        if (_enumerate is null || _getPlan is null)
        {
            return Array.Empty<PowerPlanNative>();
        }
        try
        {
            if (_enumerate(out int count) != 0 || count <= 0 || count > 64)
            {
                return Array.Empty<PowerPlanNative>();
            }
            var result = new PowerPlanNative[count];
            for (int i = 0; i < count; i++)
            {
                var name = new ushort[256];
                if (_getPlan(i, out Guid guid, name, name.Length, out int isActive) != 0)
                {
                    continue;
                }
                var sb = new System.Text.StringBuilder();
                foreach (var c in name)
                {
                    if (c == '\0') break;
                    sb.Append((char)c);
                }
                result[i] = new PowerPlanNative(guid, sb.ToString(), isActive != 0);
            }
            return result;
        }
        catch
        {
            return Array.Empty<PowerPlanNative>();
        }
    }

    /// <summary>将指定方案设为当前活动方案。原生层不可用时返回 false。</summary>
    public static bool SetActivePlan(Guid schemeGuid)
    {
        if (_setActivePlan is null)
        {
            return false;
        }
        try
        {
            return _setActivePlan(ref schemeGuid) == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>读取电池详细信息（IOCTL 直读）。原生层不可用或读取失败时返回默认值（HasBattery=false）。</summary>
    public static BatteryDetailNative ReadBatteryDetail()
    {
        if (_readBatteryDetail is null)
        {
            return default;
        }
        try
        {
            int hr = _readBatteryDetail(
                out var hasBattery, out var acOnline, out var charging, out var percent,
                out var cur, out var full, out var design,
                out var health, out var rate, out var remain, out var cycles, out var temp);
            if (hr != 0)
            {
                return default;
            }
            return new BatteryDetailNative(
                HasBattery: hasBattery != 0,
                AcOnline: acOnline != 0,
                Charging: charging != 0,
                Percent: percent,
                CurrentCapacityMwh: cur,
                FullCapacityMwh: full,
                DesignCapacityMwh: design,
                HealthPercent: health,
                RateMw: rate,
                RemainingSeconds: remain,
                CycleCount: cycles,
                TemperatureC: temp);
        }
        catch
        {
            return default;
        }
    }
}