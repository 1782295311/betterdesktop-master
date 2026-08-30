// BetterDesktop.Shell.MenuBar — Windows 无线电（Wi‑Fi / 蓝牙）开关统一封装。
// 使用 WinRT Windows.Devices.Radios.Radio，这是 Windows 官方管理 Wi‑Fi/蓝牙/FM 无线电的 API，
// 无需管理员即可切换（与系统设置里的开关同一权限模型）。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Radios;

namespace BetterDesktop.Shell.MenuBar.Services;

/// <summary>无线电状态缓存（避免每次打开控制中心都重新枚举）。</summary>
internal static class RadioInterop
{
    /// <summary>上一次枚举到的无线电列表（可能为 null）。</summary>
    private static IReadOnlyList<Radio>? _cache;

    /// <summary>缓存时间戳（UTC）。</summary>
    private static DateTime _cacheAt;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(2);

    /// <summary>获取所有无线电（带 2s 缓存，避免频繁弹窗打开时重复请求）。</summary>
    public static async Task<IReadOnlyList<Radio>> GetRadiosAsync()
    {
        if (_cache is not null && DateTime.UtcNow - _cacheAt < CacheTtl)
        {
            return _cache;
        }

        try
        {
            _ = await Radio.RequestAccessAsync();
            var radios = await Radio.GetRadiosAsync();
            _cache = radios.ToList();
            _cacheAt = DateTime.UtcNow;
            return _cache;
        }
        catch
        {
            return Array.Empty<Radio>();
        }
    }

    /// <summary>读取指定类型无线电的当前状态。null = 没找到对应无线电。</summary>
    public static async Task<RadioState?> GetStateAsync(RadioKind kind)
    {
        var radios = await GetRadiosAsync();
        var radio = radios.FirstOrDefault(r => r.Kind == kind);
        return radio?.State;
    }

    /// <summary>设置指定类型无线电的开关状态。返回是否成功。</summary>
    public static async Task<bool> SetStateAsync(RadioKind kind, bool on)
    {
        var radios = await GetRadiosAsync();
        var radio = radios.FirstOrDefault(r => r.Kind == kind);
        if (radio is null) return false;

        try
        {
            var target = on ? RadioState.On : RadioState.Off;
            var access = await radio.SetStateAsync(target);
            if (access == RadioAccessStatus.Allowed)
            {
                // 更新缓存中的状态，避免立即重新枚举读到旧值
                if (_cache is not null)
                {
                    _cacheAt = DateTime.MinValue;
                }
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>切换指定类型无线电：开→关，关→开。返回切换后的目标状态或 null（无适配器/失败）。</summary>
    public static async Task<bool?> ToggleAsync(RadioKind kind)
    {
        var current = await GetStateAsync(kind);
        if (current is null) return null;
        bool target = current != RadioState.On;
        var ok = await SetStateAsync(kind, target);
        return ok ? target : null;
    }

    /// <summary>强制刷新缓存（例如切换后想立即重新读真实硬件状态）。</summary>
    public static void InvalidateCache() => _cacheAt = DateTime.MinValue;
}
