// BetterDesktop.Kernel — CapabilityAttribute 能力元数据声明位（M16 前置，ADR-002 D1）
// 标注的服务/命令进入能力目录（P2 由生成器聚合）

namespace BetterDesktop.Kernel.Contracts;

/// <summary>
/// 能力元数据声明位：标注的类型/方法进入 AI 能力目录（ai-control.md M16）。
/// P1 仅声明位；目录聚合与权限执行在 P2 落地。
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class CapabilityAttribute : Attribute
{
    /// <summary>构造：能力唯一 id（约定「域/能力」，如 shell/switch-theme）。</summary>
    public CapabilityAttribute(string id)
    {
        Id = id;
    }

    /// <summary>能力唯一 id。</summary>
    public string Id { get; }

    /// <summary>人类可读描述（进入能力目录）。</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>权限等级（默认 Normal）。</summary>
    public CapabilityPermission Permission { get; set; } = CapabilityPermission.Normal;

    /// <summary>是否可撤销（不可撤销能力在目录中标注并强制确认）。</summary>
    public bool Undoable { get; set; }
}
