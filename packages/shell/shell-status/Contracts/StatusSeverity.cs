// BetterDesktop.Shell.Status — 语义严重级别
// 采集层只产出裸值，语义层把这些值归一到一档严重级别，UI 据此选配色/图标动效。

namespace BetterDesktop.Shell.Status.Contracts;

/// <summary>状态严重级别（由低到高），供 UI 层映射颜色与强调程度。</summary>
public enum StatusSeverity
{
    /// <summary>正常（默认，无着色）。</summary>
    Normal = 0,

    /// <summary>提示（如正在充电、接电中）。</summary>
    Info = 1,

    /// <summary>警告（如内存偏高、电量偏低、音量静音）。</summary>
    Warning = 2,

    /// <summary>严重（如电量极低、内存已满、采集失败）。</summary>
    Critical = 3
}