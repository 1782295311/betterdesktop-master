using System.IO;
using BetterDesktop.Shell.Convert.Contracts;

namespace BetterDesktop.Shell.Convert.Services.Engines;

/// <summary>
/// Office/WPS COM 引擎（仅 pdf；从旧 DocumentConversionService/OfficeComPdfRunner 等价搬迁）：
/// STA 专用线程 + 逐层 ReleaseComObject（M1）；
/// 【红线适配注记】COM 在同进程 STA 后台线程执行，Join 超时即报告 Timeout 并记录诊断；
/// 线程为 IsBackground，进程退出自然回收。跨进程强杀语义 M2 以独立转换代理进程实现（计划开放问题）。
/// </summary>
public sealed class ComPdfEngine : IConversionEngine
{
    public EngineKind Kind => EngineKind.ComPdf;

    public string Name => "office-com";

    public bool CanHandle(IReadOnlyList<string> sources, ConversionTarget target) =>
        sources.Count == 1
        && target.Format == "pdf"
        && (target.Prefer == EngineKind.ComPdf || target.Fallback == EngineKind.ComPdf);

    public EngineAvailability Probe() => ConvertEngineLocator.HasComEngine()
        ? EngineAvailability.Ok("Office/WPS COM ProgID")
        : EngineAvailability.Missing;

    public Task<IReadOnlyList<string>> RunAsync(ConversionJob job, CancellationToken ct)
    {
        var input = job.PrimarySource;
        var category = ConvertEngineLocator.CategoryOf(Path.GetExtension(input))
            ?? throw new ConvertException(ConvertError.InputInvalid, $"不支持的类型: {Path.GetExtension(input)}");
        var progId = ConvertEngineLocator.LocateComProgId(category)
            ?? throw new ConvertException(ConvertError.EngineMissing,
                "内置引擎未就绪（未找到 MS Office/WPS）——这不是文件错误");

        var productName = Path.GetFileNameWithoutExtension(input) + ".pdf";
        var target = Path.Combine(job.TempDir, productName);
        return Task.Run(() =>
        {
            OfficeComPdfRunner.ConvertToPdf(input, target, category, progId);
            IReadOnlyList<string> products = [target];
            return products;
        }, ct);
    }
}

/// <summary>
/// Office/WPS COM → PDF 执行体（STA；逻辑与旧 OfficeComPdfRunner 逐行等价搬迁）。
/// </summary>
internal static class OfficeComPdfRunner
{
    public static void ConvertToPdf(
        string input, string target,
        ConvertEngineLocator.FileCategory category, string progId)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                ConvertOnSta(input, target, category, progId);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
            Name = "bd-convert-com",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(ConvertServiceConstants.TimeoutMs);
        if (thread.IsAlive)
        {
            throw new ConvertException(ConvertError.Timeout,
                $"COM 转换超时（{ConvertServiceConstants.TimeoutMs}ms）——STA 线程将随进程退出回收（M2 改独立代理进程）");
        }
        if (failure is not null)
        {
            throw new ConvertException(ConvertError.ConversionFailed, failure.Message);
        }
    }

    private static void ConvertOnSta(
        string input, string target,
        ConvertEngineLocator.FileCategory category, string progId)
    {
        var type = Type.GetTypeFromProgID(progId)
            ?? throw new ConvertException(ConvertError.EngineMissing, $"ProgID 不可用: {progId}");
        dynamic app = Activator.CreateInstance(type)!;
        try
        {
            app.DisplayAlerts = false;
            switch (category)
            {
                case ConvertEngineLocator.FileCategory.Word:
                case ConvertEngineLocator.FileCategory.Spreadsheet:
                    // Word/Excel 支持隐藏窗口
                    app.Visible = false;
                    break;
                case ConvertEngineLocator.FileCategory.Presentation:
                    // PowerPoint COM 不允许 Visible=false（抛 "Hiding the application window is not allowed"）；
                    // 改最小化窗口（ppWindowMinimized=2），转换短暂闪任务栏可接受。
                    app.WindowState = 2;
                    break;
            }
            switch (category)
            {
                case ConvertEngineLocator.FileCategory.Word:
                    {
                        dynamic doc = app.Documents.Open(input, ReadOnly: true);
                        try { doc.ExportAsFixedFormat(target, 17); } // 17 = wdExportFormatPDF
                        finally { doc.Close(SaveChanges: false); Release(doc); }
                        break;
                    }
                case ConvertEngineLocator.FileCategory.Spreadsheet:
                    {
                        dynamic book = app.Workbooks.Open(input, ReadOnly: true);
                        try { book.ExportAsFixedFormat(0, target); } // 0 = xlTypePDF
                        finally { book.Close(SaveChanges: false); Release(book); }
                        break;
                    }
                case ConvertEngineLocator.FileCategory.Presentation:
                    {
                        dynamic deck = app.Presentations.Open(input, ReadOnly: true, Untitled: false, WithWindow: false);
                        try
                        {
                            // SaveAs(Path, 32=ppSaveAsPDF)：2 参数简单可靠（ExportAsFixedFormat 的 PrintRange
                            // 对象参数在 dynamic COM 绑定下报 "Could not convert argument 6"）。
                            deck.SaveAs(target, 32);
                        }
                        finally { deck.Close(); Release(deck); }
                        break;
                    }
            }
        }
        finally
        {
            try { app.Quit(); } catch { /* 退出失败不阻断 */ }
            Release(app);
        }
    }

    private static void Release(object o)
    {
        try { System.Runtime.InteropServices.Marshal.ReleaseComObject(o); }
        catch { /* 释放失败不阻断 */ }
    }
}
