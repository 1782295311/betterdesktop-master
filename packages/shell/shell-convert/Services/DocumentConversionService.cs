using System.Diagnostics;
using System.IO;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Kernel.Core;

namespace BetterDesktop.Shell.Convert.Services;

/// <summary>
/// 文档转 PDF 服务（安全输出照 pdf-edit-safe-output 适配：
/// 输入校验 → 引擎写入**同卷临时目录** → 成功后 File.Move 原子发布 → 失败清理临时目录）。
/// 用户拍板修订：目标已存在**不覆盖**，自动追加序号 (2)(3)——比 OUTPUT_EXISTS 拒绝更强（零覆盖）。
/// </summary>
public sealed class DocumentConversionService(IEventBus? events)
{
    /// <summary>子进程超时（ms；红线：长任务必须超时控制）。</summary>
    internal const int TimeoutMs = 120_000;

    private static readonly HashSet<string> InFlight = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>异步转 PDF（fire-and-forget 友好；结果经 IEventBus convert/* 通知）。</summary>
    public async Task ConvertToPdfAsync(string sourcePath)
    {
        var startedAt = Stopwatch.GetTimestamp();
        if (!InFlight.Add(sourcePath))
        {
            DiagnosticLog.Trace("shell-convert", $"已在转换中，忽略重复请求: {sourcePath}");
            return;
        }

        var engine = "soffice";
        try
        {
            await EmitAsync("convert/started", sourcePath, engine, null, 0);

            var input = ValidateInput(sourcePath);
            var target = await RunConversionAsync(input);
            var elapsed = ElapsedMs(startedAt);
            DiagnosticLog.Trace("shell-convert", $"convert result=ok input={input} target={target} engine={engine} elapsed={elapsed}ms");
            await EmitAsync("convert/finished", input, engine, target, elapsed);
        }
        catch (Exception ex)
        {
            var elapsed = ElapsedMs(startedAt);
            DiagnosticLog.Trace("shell-convert", $"convert result=failed input={sourcePath} error={ex.Message} elapsed={elapsed}ms");
            await EmitAsync("convert/failed", sourcePath, engine, null, elapsed, ex.Message);
        }
        finally
        {
            InFlight.Remove(sourcePath);
        }
    }

    private static long ElapsedMs(long timestamp) => Stopwatch.GetElapsedTime(timestamp).Milliseconds;

    private async Task EmitAsync(string name, string source, string engine, string? target, long elapsed, string? error = null)
    {
        try
        {
            if (events is not null)
                await events.EmitAsync(name, new ConvertEventPayload(source, target, engine, error, elapsed));
        }
        catch (Exception ex)
        {
            // 事件通知失败不阻断转换结果（M10）
            DiagnosticLog.Trace("shell-convert", $"事件 {name} 发布失败: {ex.Message}");
        }
    }

    /// <summary>输入校验（pdf-edit-safe-output 红线 1 适配：非空/无\0/存在/支持类型）。</summary>
    private static string ValidateInput(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0'))
            throw new ConvertException(ConvertError.InputInvalid, "输入路径非法");
        if (!File.Exists(path))
            throw new ConvertException(ConvertError.InputInvalid, "输入文件不存在");
        if (ConvertEngineLocator.CategoryOf(Path.GetExtension(path)) is null)
            throw new ConvertException(ConvertError.InputInvalid, $"不支持的类型: {Path.GetExtension(path)}");
        return Path.GetFullPath(path);
    }

    private async Task<string> RunConversionAsync(string input)
    {
        var extension = Path.GetExtension(input);
        var category = ConvertEngineLocator.CategoryOf(extension)!.Value;
        var soffice = ConvertEngineLocator.LocateSoffice();
        if (soffice is not null)
        {
            return await RunSofficeAsync(soffice, input);
        }

        var progId = ConvertEngineLocator.LocateComProgId(category);
        if (progId is not null)
        {
            var target = UniqueTarget(input, ".pdf");
            return await Task.Run(() => OfficeComPdfRunner.ConvertToPdf(input, target, category, progId));
        }

        throw new ConvertException(ConvertError.EngineMissing,
            "内置引擎未就绪（未找到 LibreOffice/MS Office/WPS）——这不是文件错误");
    }

    /// <summary>
    /// soffice 子进程转换（local-engine-orchestration 标准实现适配）：
    /// UseShellExecute=false + ArgumentList 逐个传参（execFile 等价，禁止 shell 拼串）；
    /// 输出到**同卷临时目录**（安全输出红线：禁止直接写目标），成功后原子发布。
    /// </summary>
    private static async Task<string> RunSofficeAsync(string soffice, string input)
    {
        var directory = Path.GetDirectoryName(input)!;
        var tempDir = Path.Combine(directory, $".bd-convert-{Environment.ProcessId}");
        var pdfName = Path.GetFileNameWithoutExtension(input) + ".pdf";
        var tempPdf = Path.Combine(tempDir, pdfName);

        Directory.CreateDirectory(tempDir);
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = soffice,
                UseShellExecute = false,          // 不经 shell；参数数组直传（execFile 等价）
                CreateNoWindow = true,
                WorkingDirectory = directory,
            };
            process.StartInfo.ArgumentList.Add("--headless");
            process.StartInfo.ArgumentList.Add("--norestore");
            process.StartInfo.ArgumentList.Add("--convert-to");
            process.StartInfo.ArgumentList.Add("pdf");
            process.StartInfo.ArgumentList.Add("--outdir");
            process.StartInfo.ArgumentList.Add(tempDir);
            process.StartInfo.ArgumentList.Add(input);

            process.Start();
            using var timeoutCts = new CancellationTokenSource(TimeoutMs);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                throw new ConvertException(ConvertError.Timeout, $"soffice 超时（{TimeoutMs}ms，已强杀进程树）");
            }

            if (process.ExitCode != 0 || !File.Exists(tempPdf))
            {
                throw new ConvertException(ConvertError.ConversionFailed,
                    $"soffice 退出码 {process.ExitCode}，产物缺失");
            }

            // 原子发布：同卷 Move；目标已存在 → 重名序号（不覆盖，用户拍板）
            var target = Publish(tempPdf, input);
            return target;
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch { /* 清理失败不阻断 */ }
        }
    }

    /// <summary>发布：临时产物 → 最终目标（存在则 (2)(3) 序号；绝不覆盖）。</summary>
    private static string Publish(string tempPdf, string input)
    {
        var directory = Path.GetDirectoryName(input)!;
        var name = Path.GetFileNameWithoutExtension(input);
        var target = Path.Combine(directory, name + ".pdf");
        for (var i = 2; File.Exists(target); i++)
        {
            target = Path.Combine(directory, $"{name} ({i}).pdf");
        }
        File.Move(tempPdf, target); // 同卷同目录 Move = 原子发布（临时文件随 finally 清理目录删除）
        return target;
    }

    /// <summary>占位目标计算（COM 分支预解析目标路径，含重名序号）。</summary>
    private static string UniqueTarget(string input, string extension)
    {
        var directory = Path.GetDirectoryName(input)!;
        var name = Path.GetFileNameWithoutExtension(input);
        var target = Path.Combine(directory, name + extension);
        for (var i = 2; File.Exists(target); i++)
        {
            target = Path.Combine(directory, $"{name} ({i}){extension}");
        }
        return target;
    }

    private static void Kill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch { /* 进程已退出 */ }
    }

    /// <summary>稳定错误（携带分类，禁止把引擎缺失伪装成转换失败——local-engine-orchestration 红线）。</summary>
    public sealed class ConvertException(ConvertError error, string message) : Exception(message)
    {
        public ConvertError Error { get; } = error;
    }
}

/// <summary>
/// Office/WPS COM → PDF（STA 专用线程 + 逐层 ReleaseComObject）。
/// 【红线适配注记】M1 COM 在同进程 STA 后台线程执行，Join 超时即报告 Timeout 并记录诊断；
/// 线程为 IsBackground，进程退出自然回收。跨进程强杀语义 M2 以独立转换代理进程实现（计划开放问题）。
/// </summary>
internal static class OfficeComPdfRunner
{
    public static string ConvertToPdf(
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
        thread.Join(DocumentConversionService.TimeoutMs);
        if (thread.IsAlive)
        {
            throw new DocumentConversionService.ConvertException(ConvertError.Timeout,
                $"COM 转换超时（{DocumentConversionService.TimeoutMs}ms）——STA 线程将随进程退出回收（M2 改独立代理进程）");
        }
        if (failure is not null)
        {
            throw new DocumentConversionService.ConvertException(ConvertError.ConversionFailed, failure.Message);
        }
        return target;
    }

    private static void ConvertOnSta(
        string input, string target,
        ConvertEngineLocator.FileCategory category, string progId)
    {
        var type = Type.GetTypeFromProgID(progId)
            ?? throw new DocumentConversionService.ConvertException(ConvertError.EngineMissing, $"ProgID 不可用: {progId}");
        dynamic app = Activator.CreateInstance(type)!;
        try
        {
            app.Visible = false;
            app.DisplayAlerts = false;
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
                    try { deck.ExportAsFixedFormat(target, 2); } // 2 = ppFixedFormatTypePDF
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
