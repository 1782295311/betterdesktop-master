using System;

namespace BetterDesktop.Shell.Capture.Ocr;

/// <summary>
/// paths-only 档识别决策（D4，红线：**不得静默降级，必须给可读原因**）。
/// <para>
/// 语义：<c>storage-mode=paths-only</c> 时原图不落盘，只能对缩略图识别，精度下降——
/// UI 必须明示这一限制并允许改档；无缩略图可用时直接拒绝识别并给出改档提示。
/// </para>
/// </summary>
/// <param name="Allowed">是否允许发起识别。</param>
/// <param name="Reason">可读原因（UI 明示，绝不静默）。</param>
/// <param name="SuggestSwitchToFull">是否建议用户切换到 full 档。</param>
public sealed record PathsOnlyDecision(bool Allowed, string Reason, bool SuggestSwitchToFull);

/// <summary>存储档位常量（与设置契约 storage-mode 取值一致）。</summary>
public static class StorageModes
{
    public const string Full = "full";
    public const string PathsOnly = "paths-only";
}

/// <summary>paths-only 降级策略（纯函数，单测覆盖三分支）。</summary>
public static class PathsOnlyPolicy
{
    /// <summary>
    /// 评估当前存储档下能否识别。
    /// </summary>
    /// <param name="storageMode">当前存储档（settings 里 storage-mode 的值；null/未知按 full 处理）。</param>
    /// <param name="thumbnailAvailable">是否存在可用缩略图（paths-only 档识别只吃缩略图）。</param>
    public static PathsOnlyDecision Evaluate(string? storageMode, bool thumbnailAvailable)
    {
        if (!string.Equals(storageMode, StorageModes.PathsOnly, StringComparison.OrdinalIgnoreCase))
        {
            return new PathsOnlyDecision(true, "原图已落盘，可全精度识别", SuggestSwitchToFull: false);
        }

        if (thumbnailAvailable)
        {
            return new PathsOnlyDecision(
                true,
                "路径档：原图不落盘，仅对缩略图识别，精度下降；如需全精度请在设置中切换存储档",
                SuggestSwitchToFull: true);
        }

        return new PathsOnlyDecision(
            false,
            "路径档且无可用缩略图，无法识别；请在设置中切换存储档后重试",
            SuggestSwitchToFull: true);
    }
}
