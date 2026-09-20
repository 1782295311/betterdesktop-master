namespace BetterDesktop.Shell.Convert.Contracts;

/// <summary>
/// 目标格式描述（ConversionMatrix 值对象；新增格式只改表、不动菜单与服务）。
/// </summary>
/// <param name="Format">目标扩展名（小写、不带点，如 "pdf"）。</param>
/// <param name="Label">菜单显示文本（中文，含格式名）。</param>
/// <param name="Filter">soffice 输出 filter（仅 Soffice/TwoHop 段消费；null = 用 Format 短名）。</param>
/// <param name="Hops">跳数（1 = 直连；2 = 矩阵显式登记的两跳链）。</param>
/// <param name="Prefer">首选引擎。</param>
/// <param name="Fallback">兜底引擎（首选缺失时自动切换；pdf 主链=soffice 兜底=COM，md→docx 主=pandoc 兜底=两跳）。</param>
/// <param name="Category">目标类别（菜单分组依据，2026-09-10 新增）。</param>
/// <param name="Lossless">无损转换标记（无损=菜单高亮；有损=常规显示+点击风险确认；系统级联只注册无损项，2026-09-10 新增）。</param>
public sealed record ConversionTarget(
    string Format,
    string Label,
    string? Filter,
    int Hops,
    EngineKind Prefer,
    EngineKind? Fallback = null,
    TargetCategory Category = TargetCategory.Text,
    bool Lossless = true)
{
    /// <summary>多输入操作标记（Filter 承载操作语义；仅 PdfCompose 类目标使用）。</summary>
    public const string MergePdfMarker = "pdf-merge";
    public const string ComposePdfMarker = "pdf-compose";
    public const string SplitPdfMarker = "pdf-split";
    public const string EncryptPdfMarker = "pdf-encrypt";
    public const string DecryptPdfMarker = "pdf-decrypt";

    /// <summary>MP4 编码选择（Filter 承载；2026-09-07：H.264 / H.265 / AV1）。</summary>
    public const string VideoEncH264Marker = "enc-h264";
    public const string VideoEncH265Marker = "enc-h265";
    public const string VideoEncAv1Marker = "enc-av1";
}
