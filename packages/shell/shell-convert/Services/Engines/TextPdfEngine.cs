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
        && target.Format is "txt" or "md" or "html" or "docx" or "xlsx" or "epub" or "odt" or "rtf" or "opendocument";

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

        // 2) 按目标格式生成（纯托管；无版式，诚实文本提取）
        var product = Path.Combine(job.TempDir, name + "." + ExtensionOf(format));
        switch (format)
        {
            case "docx":
                WriteMinimalDocx(product, text);
                break;
            case "xlsx":
                WriteMinimalXlsx(product, text);
                break;
            case "txt":
            case "md":
                await File.WriteAllTextAsync(product, text, new UTF8Encoding(false), ct);
                break;
            case "html":
                await File.WriteAllTextAsync(product, BuildHtml(text), new UTF8Encoding(false), ct);
                break;
            case "epub":
                WriteEpub(product, name, text);
                break;
            case "odt":
                WriteOdt(product, text);
                break;
            case "rtf":
                await File.WriteAllTextAsync(product, BuildRtf(text), new UTF8Encoding(false), ct);
                break;
            case "opendocument":
                await File.WriteAllTextAsync(product, BuildOpenDocumentXml(text), new UTF8Encoding(false), ct);
                break;
            default:
                throw new ConvertException(ConvertError.InputInvalid, $"TextPdf 引擎不支持的转换目标: {format}");
        }
        return [product];
    }

    // —— 目标格式扩展名（opendocument 输出单 XML） ——

    private static string ExtensionOf(string format) => format switch
    {
        "md" => "md",
        "html" => "html",
        "epub" => "epub",
        "odt" => "odt",
        "rtf" => "rtf",
        "opendocument" => "xml",
        _ => format,
    };

    // —— html（转义段落，CJK 友好） ——

    private static string BuildHtml(string text)
    {
        var body = string.Concat(text.Replace("\r\n", "\n").Split('\n')
            .Select(line => "<p>" + EscapeXml(line) + "</p>\n"));
        return "<!DOCTYPE html>\n<html lang=\"zh-CN\">\n<head><meta charset=\"utf-8\"/></head>\n<body>\n"
            + body + "</body>\n</html>\n";
    }

    // —— rtf（\rtf1 单段落序列；\n 转 \par） ——

    private static string BuildRtf(string text)
    {
        var escaped = text
            .Replace("\\", "\\\\")
            .Replace("{", "\\{")
            .Replace("}", "\\}")
            .Replace("\r\n", "\n")
            .Replace("\n", "\\par\n");
        return "{\\rtf1\\ansi\\deff0{\\fonttbl{\\f0 Courier New;}}\\f0\\fs24\n" + escaped + "\n}";
    }

    // —— opendocument（单 XML，pandoc -t opendocument 同形态：office:text 内容流） ——

    private static string BuildOpenDocumentXml(string text)
    {
        var paragraphs = string.Concat(text.Replace("\r\n", "\n").Split('\n')
            .Select(line => "<text:p>" + EscapeXml(line) + "</text:p>\n"));
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"
            + "<office:document xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" "
            + "xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\">\n"
            + "<office:body><office:text>\n" + paragraphs + "</office:text></office:body>\n</office:document>\n";
    }

    // —— odt（zip：mimetype + content.xml，office:text 内容流） ——

    private static void WriteOdt(string path, string text)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(archive, "mimetype", "application/vnd.oasis.opendocument.text");
        var paragraphs = string.Concat(text.Replace("\r\n", "\n").Split('\n')
            .Select(line => "<text:p>" + EscapeXml(line) + "</text:p>\n"));
        WriteEntry(archive, "content.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"
            + "<office:document-content xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" "
            + "xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\">\n"
            + "<office:body><office:text>\n" + paragraphs + "</office:text></office:body>\n</office:document-content>\n");
    }

    // —— epub（最小单章包：mimetype/container.opf/xhtml） ——

    private static void WriteEpub(string path, string title, string text)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(archive, "mimetype", "application/epub+zip");
        WriteEntry(archive, "META-INF/container.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"
            + "<container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\">"
            + "<rootfiles><rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/>"
            + "</rootfiles></container>");
        WriteEntry(archive, "OEBPS/content.opf",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"
            + "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"uid\">"
            + "<metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\">"
            + "<dc:identifier id=\"uid\">betterdt-pdf-text</dc:identifier>"
            + "<dc:title>" + EscapeXml(title) + "</dc:title>"
            + "<dc:language>zh-CN</dc:language></metadata>"
            + "<manifest><item id=\"c1\" href=\"chapter.xhtml\" media-type=\"application/xhtml+xml\"/></manifest>"
            + "<spine><itemref idref=\"c1\"/></spine></package>");
        var body = string.Concat(text.Replace("\r\n", "\n").Split('\n')
            .Select(line => "<p>" + EscapeXml(line) + "</p>\n"));
        WriteEntry(archive, "OEBPS/chapter.xhtml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"
            + "<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>"
            + EscapeXml(title) + "</title></head><body>\n" + body + "</body></html>");
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
            RedirectStandardOutput = true, // 2026-09-10：读流必须重定向
            RedirectStandardError = true,
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
