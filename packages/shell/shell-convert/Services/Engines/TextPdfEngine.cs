// BetterDesktop.Shell.Convert — PDF 文本提取引擎（2026-09-07 补全，对齐 flyingmouse PDF→Word/Excel，诚实降级为文本提取）
// Poppler pdftotext 提取 → 托管生成最小 docx/xlsx（无版式，菜单文本已标注「文本提取」）。零新增依赖。

using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using BetterDesktop.Shell.Convert.Contracts;

namespace BetterDesktop.Shell.Convert.Services.Engines;

/// <summary>
/// PDF 文本提取引擎：pdf→docx/xlsx（pdftotext + 托管 OOXML；版式不还原，诚实标注「文本提取」）。
/// docx：逐行段落；xlsx：-layout 保留对齐，按 2+ 连续空格拆列。Probe 与 Poppler 套件共用。
/// </summary>
public sealed class TextPdfEngine : IConversionEngine
{
    public EngineKind Kind => EngineKind.PdfText;

    public string Name => "pdf-text";

    public bool CanHandle(IReadOnlyList<string> sources, ConversionTarget target) =>
        sources.Count == 1
        && Path.GetExtension(sources[0]).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
        && (target.Prefer == EngineKind.PdfText || target.Fallback == EngineKind.PdfText)
        && target.Format is "docx" or "xlsx";

    public EngineAvailability Probe() =>
        PopplerEngine.LocatePdftotext() is not null
            ? EngineAvailability.Ok("pdftotext + 托管 OOXML")
            : EngineAvailability.Missing;

    public async Task<IReadOnlyList<string>> RunAsync(ConversionJob job, CancellationToken ct)
    {
        var pdftotext = PopplerEngine.LocatePdftotext()
            ?? throw new ConvertException(ConvertError.EngineMissing,
                "未找到 pdftotext（Poppler 套件缺失）——这不是文件错误");
        var input = job.PrimarySource;
        var format = job.Target.Format;
        var name = Path.GetFileNameWithoutExtension(input);

        // 1) pdftotext 提取（xlsx 用 -layout 保留对齐以便拆列）
        var textFile = Path.Combine(job.TempDir, name + ".raw.txt");
        var args = format == "xlsx"
            ? new[] { "-layout", "-enc", "UTF-8", input, textFile }
            : new[] { "-enc", "UTF-8", input, textFile };
        var (code, _, stderr) = await RunCaptureAsync(pdftotext, args, ct);
        if (code != 0 || !File.Exists(textFile))
        {
            throw new ConvertException(ConvertError.ConversionFailed,
                $"pdftotext 退出码 {code} {Truncate(stderr)}");
        }
        var text = await File.ReadAllTextAsync(textFile, Encoding.UTF8, ct);

        // 2) 生成最小 OOXML（纯托管）
        var product = Path.Combine(job.TempDir, name + "." + format);
        if (format == "docx")
        {
            WriteMinimalDocx(product, text);
        }
        else
        {
            WriteMinimalXlsx(product, text);
        }
        return [product];
    }

    // —— docx 最小包（段落文本） ——

    private static void WriteMinimalDocx(string path, string text)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(archive, "[Content_Types].xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n"
            + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
            + "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>"
            + "<Default Extension=\"xml\" ContentType=\"application/xml\"/>"
            + "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>"
            + "</Types>");
        WriteEntry(archive, "_rels/.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n"
            + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
            + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>"
            + "</Relationships>");

        var paragraphs = string.Concat(text.Replace("\r\n", "\n").Split('\n')
            .Select(line => "<w:p><w:r><w:t xml:space=\"preserve\">" + EscapeXml(line) + "</w:t></w:r></w:p>"));
        WriteEntry(archive, "word/document.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n"
            + "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">"
            + "<w:body>" + paragraphs + "<w:sectPr/></w:body></w:document>");
    }

    // —— xlsx 最小包（-layout 行按 2+ 空格拆列；inlineStr 单元格） ——

    private static void WriteMinimalXlsx(string path, string text)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(archive, "[Content_Types].xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n"
            + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
            + "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>"
            + "<Default Extension=\"xml\" ContentType=\"application/xml\"/>"
            + "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>"
            + "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>"
            + "</Types>");
        WriteEntry(archive, "_rels/.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n"
            + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
            + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>"
            + "</Relationships>");
        WriteEntry(archive, "xl/workbook.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n"
            + "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" "
            + "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">"
            + "<sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
        WriteEntry(archive, "xl/_rels/workbook.xml.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n"
            + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
            + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>"
            + "</Relationships>");

        var rowsXml = string.Concat(text.Replace("\r\n", "\n").Split('\n')
            .Select((line, rowIdx) =>
            {
                var cells = System.Text.RegularExpressions.Regex.Split(line, @" {2,}");
                var cellsXml = string.Concat(cells.Select((cell, colIdx) =>
                    "<c r=\"" + ColumnName(colIdx) + (rowIdx + 1) + "\" t=\"inlineStr\"><is><t xml:space=\"preserve\">"
                    + EscapeXml(cell) + "</t></is></c>"));
                return "<row r=\"" + (rowIdx + 1) + "\">" + cellsXml + "</row>";
            }));
        WriteEntry(archive, "xl/worksheets/sheet1.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n"
            + "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">"
            + "<sheetData>" + rowsXml + "</sheetData></worksheet>");
    }

    /// <summary>列名（A…Z, AA…AZ…）——26 进制，与 Excel 单元格坐标一致。</summary>
    private static string ColumnName(int index)
    {
        var name = string.Empty;
        var n = index;
        do
        {
            name = (char)('A' + n % 26) + name;
            n = n / 26 - 1;
        }
        while (n >= 0);
        return name;
    }

    private static string EscapeXml(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");
    }

    private static void WriteEntry(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string Truncate(string text) => text.Length > 200 ? text[..200] : text;

    private static async Task<(int Code, string Stdout, string Stderr)> RunCaptureAsync(
        string exe, string[] args, CancellationToken ct)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false, // 红线 1：参数数组直传
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }
        process.Start();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ConvertServiceConstants.TimeoutMs);
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(timeoutCts.Token);
            return (process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
            throw new ConvertException(ConvertError.Timeout, $"pdftotext 超时（{ConvertServiceConstants.TimeoutMs}ms）");
        }
    }
}
