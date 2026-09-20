using System.IO;
using BetterDesktop.Shell.Convert.Contracts;

namespace BetterDesktop.Shell.Convert.Services.Engines;

/// <summary>
/// 演示族两跳引擎（COM→PDF 中间态→Poppler 渲染，2026-09-10 PPT 主线）：
/// 与 TwoHopEngine 同 Kind（EngineKind.TwoHop），注册序在其后——soffice 缺失时按序兜底承接；
/// CanHandle 限定演示族源 + png/jpg，因此 md→pdf 等 soffice 专属两跳不受影响（保持隐藏=可成功契约）。
/// 中间态 PDF 写入 job.TempDir（`.bd-convert-*` 隐藏目录），由 ConversionService finally 递归删除，用户不可见。
/// </summary>
public sealed class ComPdfTwoHopEngine : IConversionEngine
{
    public EngineKind Kind => EngineKind.TwoHop;

    public string Name => "com-two-hop";

    private static readonly HashSet<string> PresentationExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ppt", ".pptx", ".pps", ".dps", ".dpt",
    };

    public bool CanHandle(IReadOnlyList<string> sources, ConversionTarget target) =>
        sources.Count == 1
        && PresentationExts.Contains(Path.GetExtension(sources[0]))
        && target.Format is "png" or "jpg"
        && (target.Prefer == EngineKind.TwoHop || target.Fallback == EngineKind.TwoHop);

    /// <summary>COM（MS Office/WPS）与 Poppler 均就绪才可用（同 TwoHop 的"两跳可用性"语义）。</summary>
    public EngineAvailability Probe() =>
        ConvertEngineLocator.HasComEngine() && PopplerEngine.EnsureProbed().Available
            ? EngineAvailability.Ok("Office/WPS COM + Poppler 两跳")
            : EngineAvailability.Missing;

    public async Task<IReadOnlyList<string>> RunAsync(ConversionJob job, CancellationToken ct)
    {
        // 1) 中间态：COM 导出 PDF（TempDir 内部生成；ConversionService 用后删除，用户不可见）
        var category = ConvertEngineLocator.CategoryOf(Path.GetExtension(job.PrimarySource))
            ?? throw new ConvertException(ConvertError.InputInvalid, $"不支持的类型: {Path.GetExtension(job.PrimarySource)}");
        var progId = ConvertEngineLocator.LocateComProgId(category)
            ?? throw new ConvertException(ConvertError.EngineMissing,
                "内置引擎未就绪（未找到 MS Office/WPS）——这不是文件错误");
        var pdfPath = Path.Combine(job.TempDir, Path.GetFileNameWithoutExtension(job.PrimarySource) + ".pdf");
        await Task.Run(() => OfficeComPdfRunner.ConvertToPdf(job.PrimarySource, pdfPath, category, progId), ct);

        // 2) Poppler 渲染中间态 PDF → 目标图片（复用同一引擎，产物同在 TempDir）
        var pdfJob = new ConversionJob(
            [pdfPath],
            new ConversionTarget(job.Target.Format, job.Target.Label, null, 1, EngineKind.Poppler),
            job.TempDir);
        return await new PopplerEngine().RunAsync(pdfJob, ct);
    }
}
