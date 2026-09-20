using System;
using System.Collections.Generic;
using System.Linq;
using BetterDesktop.Shell.Clipboard.Ipc;
using BetterDesktop.Shell.Hotkeys.Contracts;

namespace BetterDesktop.Shell.Core.Hotkeys;

/// <summary>
/// 热键冲突判定与作用域过滤（**纯函数，无窗口无钩子**，单测直接覆盖）。
/// <para>冲突策略（用户可管理，见热键实施计划 §5）：同作用域同键位 → 拒绝（fail-closed）；
/// 跨作用域同键位 → 允许但必须显式声明 <c>Shadows</c> 接管；内置键（引擎三枚全局热键）→ 保护不抢。</para>
/// </summary>
public static class HotkeyConflictPolicy
{
    /// <summary>
    /// 键位等价比较（大小写/别名/顺序不敏感）：两边都经 <see cref="HotkeySpec"/> 归一为规范串。
    /// 解析失败按原串比较（宁可判冲突也不静默放行）。
    /// </summary>
    public static bool ChordEquals(HotkeyChord a, HotkeyChord b)
    {
        if (a is null || b is null)
        {
            return false;
        }

        return string.Equals(Canonical(a), Canonical(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>键位规范串（HotkeySpec 唯一解析；解析失败回退原串）。</summary>
    public static string Canonical(HotkeyChord chord)
    {
        if (chord is null || string.IsNullOrWhiteSpace(chord.Spec))
        {
            return string.Empty;
        }

        if (HotkeySpec.TryParse(chord.Spec, out _, out _, out var canonical) && canonical.Length > 0)
        {
            return canonical;
        }

        return chord.Spec.Trim();
    }

    /// <summary>
    /// 同作用域同键位、不同属主 → 冲突（无论先后，注册期拒绝）。
    /// 与 <paramref name="other"/> 为同一对象或 Id 相同时不构成"两个绑定"的冲突。
    /// </summary>
    public static bool IsSameScopeDuplicate(HotkeyBinding binding, HotkeyBinding other)
    {
        if (ReferenceEquals(binding, other) || string.Equals(binding.Id, other.Id, StringComparison.Ordinal))
        {
            return false;
        }

        return string.Equals(binding.Scope.Id, other.Scope.Id, StringComparison.Ordinal)
               && ChordEquals(binding.Chord, other.Chord);
    }

    /// <summary>
    /// 跨作用域同键位是否构成冲突：新绑定必须显式声明 <c>Shadows</c> 接管，否则即冲突。
    /// <paramref name="incoming"/> 为待注册绑定，<paramref name="existing"/> 为已注册绑定。
    /// </summary>
    public static bool IsUndeclaredCrossScopeConflict(HotkeyBinding incoming, HotkeyBinding existing)
    {
        if (ReferenceEquals(incoming, existing) || string.Equals(incoming.Id, existing.Id, StringComparison.Ordinal))
        {
            return false;
        }

        if (string.Equals(incoming.Scope.Id, existing.Scope.Id, StringComparison.Ordinal))
        {
            return false; // 同作用域走 IsSameScopeDuplicate
        }

        if (!ChordEquals(incoming.Chord, existing.Chord))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(incoming.Shadows);
    }

    /// <summary>与引擎内置三枚全局热键冲突？（引擎占用保护：抢了要么注册失败、要么顶掉引擎键。）</summary>
    public static bool ConflictsWithBuiltIn(HotkeyBinding binding)
        => binding?.Chord is not null && HotkeySpec.ConflictsWithBuiltIn(binding.Chord.Spec);

    /// <summary>
    /// 作用域过滤：显示集合 = 全部 <c>Global</c> ∪ 当前上下文栈上的 <c>Surface.*</c>；
    /// <c>Surface.Any</c> = 任一 BetterDesktop 表面活跃即生效。没有任何表面活跃时只显示全局键。
    /// </summary>
    public static bool IsActive(HotkeyBinding binding, IReadOnlyCollection<string> activeScopes)
    {
        if (binding is null)
        {
            return false;
        }

        if (binding.Scope == HotkeyScope.Global)
        {
            return true;
        }

        if (binding.Scope == HotkeyScope.Any)
        {
            return activeScopes is not null
                   && activeScopes.Any(s => s.StartsWith("Surface.", StringComparison.Ordinal));
        }

        return activeScopes is not null && activeScopes.Contains(binding.Scope.Id);
    }

    /// <summary>
    /// 某绑定是否被另一条（同键位、显式声明 Shadows 的）绑定接管。
    /// 接管判据：另一方 Shadows 显式指向本 Id，或本绑定是 <c>Global</c> 且另一方是表面作用域并声明了 Shadows。
    /// </summary>
    public static HotkeyBinding? FindShadowing(HotkeyBinding target, IEnumerable<HotkeyBinding> all)
    {
        foreach (var other in all)
        {
            if (ReferenceEquals(target, other) || string.Equals(target.Id, other.Id, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(other.Shadows) || !ChordEquals(target.Chord, other.Chord))
            {
                continue;
            }

            var explicitId = string.Equals(other.Shadows, target.Id, StringComparison.Ordinal);
            var globalTaken = target.Scope == HotkeyScope.Global
                              && other.Scope != HotkeyScope.Global
                              && other.Scope != HotkeyScope.Any;
            if (explicitId || globalTaken)
            {
                return other;
            }
        }

        return null;
    }
}
