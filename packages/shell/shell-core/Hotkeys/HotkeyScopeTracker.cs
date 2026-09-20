using System;
using System.Collections.Generic;
using System.Linq;
using BetterDesktop.Shell.Hotkeys.Contracts;

namespace BetterDesktop.Shell.Core.Hotkeys;

/// <summary>
/// 作用域聚合器：各表面（面板/菜单栏/通知中心等）在 show/hide 时上报增减，
/// 本类聚合后一次性 <see cref="IHotkeyRegistryService.SetActiveScopes"/>（注册表是**全量覆盖**
/// 语义，多表面并发活跃必须由本类合并，否则互相覆盖）。
/// <para>用法：构造注入注册表；表面显示时 <c>EnterScope</c>、收起时 <c>ExitScope</c>；
/// 任何变化立即推送合并快照。跨进程表面（如面板 exe）经其事件通道回调宿主后调用。</para>
/// </summary>
public sealed class HotkeyScopeTracker : IHotkeyScopeTracker
{
    private readonly IHotkeyRegistryService _registry;
    private readonly HashSet<string> _scopes = new(StringComparer.Ordinal);

    /// <param name="registry">热键注册表（GetActive 的"此刻可用"依据）。</param>
    public HotkeyScopeTracker(IHotkeyRegistryService registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <summary>当前已上报的作用域（调试/快照用）。</summary>
    public IReadOnlyCollection<string> Current => _scopes;

    /// <summary>表面活跃（如面板显示）。幂等。</summary>
    public void EnterScope(string scopeId)
    {
        if (string.IsNullOrWhiteSpace(scopeId))
        {
            return;
        }

        if (_scopes.Add(scopeId))
        {
            Push();
        }
    }

    /// <summary>表面收起（如面板隐藏）。幂等。</summary>
    public void ExitScope(string scopeId)
    {
        if (_scopes.Remove(scopeId))
        {
            Push();
        }
    }

    /// <summary>全量覆盖（宿主启动时可注入初始上下文）。</summary>
    public void SetScopes(IEnumerable<string>? scopeIds)
    {
        var next = new HashSet<string>(scopeIds ?? Array.Empty<string>(), StringComparer.Ordinal);
        if (!next.SetEquals(_scopes))
        {
            _scopes.Clear();
            foreach (var s in next)
            {
                _scopes.Add(s);
            }

            Push();
        }
    }

    private void Push() => _registry.SetActiveScopes(_scopes.OrderBy(s => s, StringComparer.Ordinal).ToList());
}
