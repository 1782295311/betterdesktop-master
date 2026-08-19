// BetterDesktop.Kernel — CapabilityPermission 枚举（M16 前置，ADR-002 D1）
// 能力权限等级，与 docs/ai-control.md 四级对应

namespace BetterDesktop.Kernel.Contracts;

/// <summary>能力权限等级（M16 控制面，与 ai-control.md L0–L3 对应）。</summary>
public enum CapabilityPermission
{
    /// <summary>L0 只读：默认放行（仍入审计）。</summary>
    Read = 0,

    /// <summary>L1 常规：AI 身份可执行。</summary>
    Normal = 1,

    /// <summary>L2 敏感：需用户确认（人在环）。</summary>
    Sensitive = 2,

    /// <summary>L3 高危：AI 默认禁止。</summary>
    HighRisk = 3
}
