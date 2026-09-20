using System.Collections.Generic;
using System.Linq;
using BetterDesktop.Shell.Clipboard.Ipc;
using BetterDesktop.Shell.Hotkeys.Contracts;

namespace BetterDesktop.Shell.HotkeyPanel;

/// <summary>侧板列表行（只读渲染数据，纯逻辑可单测）。</summary>
internal sealed record HotkeyRow(
    string Id,
    string ChordText,
    string Description,
    string Owner,
    bool Conflict,
    string? ConflictReason,
    bool Wired,
    bool Enabled,
    bool Visible,
    HotkeyBinding Binding,
    bool InactiveScope = false,
    string? ScopeBadge = null,
    bool OwnerAlive = true);

/// <summary>
/// 侧板列表模型：把注册表数据（GetActive/GetConflicts）整理成渲染行——
/// 冲突/被接管项置顶+高亮；作用域非活跃的已接线项（如面板未开时的 paste-back）由管理视图展示。
/// </summary>
internal static class HotkeyListModel
{
    /// <summary>构建只读列表：冲突/被接管置顶，其余按功能名排序；隐藏项不计入。</summary>
    public static List<HotkeyRow> BuildRows(
        IReadOnlyList<HotkeyView> active,
        IReadOnlyDictionary<string, HotkeyConflict> conflictsById)
    {
        var rows = active
            .Where(v => v.Visible)
            .Select(v => ToRow(v, conflictsById))
            .ToList();

        // 冲突/被接管置顶（保持相对顺序），其余按 Description 稳定排序
        return rows
            .OrderByDescending(r => r.Conflict)
            .ThenBy(r => r.Description, System.StringComparer.Ordinal)
            .ToList();
    }

    private static HotkeyRow ToRow(HotkeyView view, IReadOnlyDictionary<string, HotkeyConflict> conflictsById)
    {
        HotkeyConflict? conflict = null;
        if (view.OsConflict || view.ShadowedBy)
        {
            conflictsById.TryGetValue(view.Binding.Id, out conflict);
        }

        return new HotkeyRow(
            Id: view.Binding.Id,
            ChordText: HotkeySpec.Pretty(view.Binding.Chord.Spec),
            Description: view.Binding.Description,
            Owner: view.Binding.Owner,
            Conflict: conflict is not null,
            ConflictReason: conflict?.Reason,
            Wired: HotkeyDeclarations.IsWired(view.Binding.Id),
            Enabled: view.Enabled,
            Visible: view.Visible,
            Binding: view.Binding,
            OwnerAlive: HotkeyDeclarations.IsOwnerAlive(view.Binding.Owner));
    }

    /// <summary>
    /// 构建可操作态列表（P0-1）：列出**全部**条目（含非活跃作用域），只读态才按上下文过滤——
    /// 否则已接线的管理能力会被过滤逻辑吃掉（paste-back 仅面板打开时"可用"，但改键/停用任何时候都该可管理）。
    /// 非活跃作用域条目灰显 + 作用域徽标；隐藏项仍由管理浮层单独展示（此处过滤）。
    /// </summary>
    public static List<HotkeyRow> BuildAllRows(
        IReadOnlyList<HotkeyView> all,
        IReadOnlyDictionary<string, HotkeyConflict> conflictsById,
        IReadOnlyCollection<string> activeScopes)
    {
        var rows = all
            .Where(v => v.Visible)
            .Select(v =>
            {
                var row = ToRow(v, conflictsById);
                var scopeId = v.Binding.Scope.Id;
                var isSurface = scopeId.StartsWith("Surface.", System.StringComparison.Ordinal);
                var active = !isSurface || activeScopes.Contains(scopeId);
                return row with
                {
                    InactiveScope = !active,
                    ScopeBadge = isSurface && !active ? scopeId["Surface.".Length..] : null,
                };
            })
            .ToList();

        // 冲突置顶 → 非活跃作用域靠后 → 其余按描述稳定排序
        return rows
            .OrderByDescending(r => r.Conflict)
            .ThenBy(r => r.InactiveScope)
            .ThenBy(r => r.Description, System.StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>已忽略（隐藏）计数：GetAll 中 Visible=false 的条目数（冲突项强制显示，不计为"已忽略"）。</summary>
    public static int HiddenCount(IReadOnlyList<HotkeyView> all)
        => all.Count(v => !v.Visible && !v.OsConflict);

    /// <summary>
    /// 已忽略管理列表（P0-3）：隐藏项（<c>Visible=false</c>，冲突项强制显示不在此列）——
    /// "绝不静默丢失"红线：用户隐藏任何热键后必须在此可恢复（调 <c>SetVisible(id,true)</c>）。
    /// </summary>
    public static List<HotkeyRow> BuildManageRows(
        IReadOnlyList<HotkeyView> all,
        IReadOnlyDictionary<string, HotkeyConflict> conflictsById)
        => all
            .Where(v => !v.Visible && !v.OsConflict)
            .Select(v => ToRow(v, conflictsById))
            .OrderBy(r => r.Description, System.StringComparer.Ordinal)
            .ToList();
}
