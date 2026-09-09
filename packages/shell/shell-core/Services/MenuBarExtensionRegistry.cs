// BetterDesktop.Shell.Core — 菜单栏扩展注册表实现（2026-09-07 P0-3/C2，711 纪律）
// - 线程安全（lock 保护字典；装配快照由 GetAll 返回副本，避免迭代中修改异常）
// - 同 Id 重复注册以新替旧（幂等）
// - 注册表不执行任何扩展方法（Start/OpenPopup 容错由消费方负责，711 生死线 1）

using System;
using System.Collections.Generic;
using BetterDesktop.Shell.Core.Contracts;

namespace BetterDesktop.Shell.Core.Services;

/// <summary>线程安全的 IMenuBarExtension 注册表（登记/解析职责，不含装配）。</summary>
public sealed class MenuBarExtensionRegistry : IMenuBarExtensionRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, IMenuBarExtension> _extensions = new(StringComparer.Ordinal);

    public void Register(IMenuBarExtension extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        lock (_gate)
        {
            _extensions[extension.Id] = extension;
        }
    }

    public bool Unregister(string id)
    {
        if (id is null) return false;
        lock (_gate)
        {
            return _extensions.Remove(id);
        }
    }

    public IMenuBarExtension? Get(string id)
    {
        if (id is null) return null;
        lock (_gate)
        {
            return _extensions.TryGetValue(id, out var ext) ? ext : null;
        }
    }

    public IReadOnlyList<IMenuBarExtension> GetAll()
    {
        lock (_gate)
        {
            // 快照副本：装配方可在迭代中安全使用；顺序 = 注册顺序（Dictionary 保序性对插入序有效，.NET 8）
            return new List<IMenuBarExtension>(_extensions.Values);
        }
    }
}
