using System.IO;
using BetterDesktop.Shell.Convert.Contracts;

namespace BetterDesktop.Shell.Convert.Services.Engines;

/// <summary>
/// 进程内轻量引擎（convert-lite，2026-09-20）：零外部 exe，替代 pandoc / LibreOffice / calibre / poppler
/// 负责的文档与表格族。<b>恒可用</b> —— 引擎可用性从"磁盘上有没有那个第三方目录"变成"代码里有这个实现"，
/// 于是删掉 <c>engines/</c> 之后菜单不再整项隐藏（这正是本次迁移的目的）。
/// <para>
/// 【职责边界：只管可用性与路由，不重复实现转换】菜单的 <c>ConvertMenuService.IsEngineReady</c> 消费
/// <see cref="Probe"/>；路由由矩阵的 <c>Prefer/Fallback</c> 决定（见 <c>ConversionMatrix.RouteThroughLite</c>）。
/// 真正的**执行**由 <c>convert-engine.exe</c>（Rust）承担 —— <c>ConversionService</c> 的正常路径直接调
/// <c>RustConvertRunner</c>（S9 起执行核心已全部下沉 Rust）。故 <see cref="RunAsync"/> 按同一路径委托，
/// 避免万一被旧路径走到时行为与主路径不一致。
/// </para>
/// </summary>
public sealed class LiteEngine : IConversionEngine
{
    public EngineKind Kind => EngineKind.Lite;

    public string Name => "lite";

    /// <summary>单源 + 矩阵把该边派给 Lite + lite 声明能读能写。</summary>
    public bool CanHandle(IReadOnlyList<string> sources, ConversionTarget target) =>
        sources.Count == 1
        && (target.Prefer == EngineKind.Lite || target.Fallback == EngineKind.Lite)
        && LiteCapability.Supports(Path.GetExtension(sources[0]), target.Format);

    /// <summary>恒可用：没有外部依赖可探测（这正是它与其它引擎的本质差别）。</summary>
    public EngineAvailability Probe() => EngineAvailability.Ok("Rust convert-lite（进程内，零外部 exe）");

    public async Task<IReadOnlyList<string>> RunAsync(ConversionJob job, CancellationToken ct)
    {
        // 执行委托给 Rust 引擎（与 ConversionService 主路径同一入口，不另造一条链）。
        var result = await new RustConvertRunner(null)
            .RunAsync(job.Sources, job.Target.Format, null, Name, 0, ct)
            .ConfigureAwait(false);
        return result.Output is null ? [] : [result.Output];
    }
}
