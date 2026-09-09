using System.IO;
using BetterDesktop.Shell.Convert.Contracts;

namespace BetterDesktop.Shell.Convert.Services;

/// <summary>
/// 转换矩阵（hub-spoke 拓扑纪律：新增格式只改表；两跳链显式登记 hop=2，禁止隐式超过两跳）。
/// 静态表 + 运行期「引擎可用」过滤由菜单层/服务层经 EngineRegistry 执行（隐藏优先）。
/// </summary>
public static class ConversionMatrix
{
    /// <summary>图片扩展名全集（webp 目标能否出现由 Image 引擎能力判定）。</summary>
    public static readonly IReadOnlySet<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp",
        ".ico", ".tga", // 2026-09-07 补全：ICO（GDI+ 读写+手写封装）、TGA（ffmpeg 原生）
    };

    private static readonly Dictionary<string, IReadOnlyList<ConversionTarget>> Rows = BuildRows();

    /// <summary>查询某扩展名的全部可转目标（扩展名大小写不敏感；未登记返回空）。</summary>
    public static IReadOnlyList<ConversionTarget> GetTargets(string extension) =>
        Rows.TryGetValue(extension.ToLowerInvariant(), out var targets) ? targets : [];

    /// <summary>查询精确目标（源扩展 + 目标格式）。</summary>
    public static ConversionTarget? Find(string extension, string format) =>
        GetTargets(extension).FirstOrDefault(t => t.Format == format);

    /// <summary>是否可转换（右键菜单整项显隐的快速判定）。</summary>
    public static bool IsConvertible(string extension) => GetTargets(extension).Count > 0;

    /// <summary>多输入合并目标（全部为 pdf 且 ≥2 时可合并为一个 pdf）。</summary>
    public static ConversionTarget MergePdf { get; } = new(
        "pdf", "合并 PDF", ConversionTarget.MergePdfMarker, 1, EngineKind.PdfCompose);

    /// <summary>多输入合成目标（全部为图片且 ≥2 时合成一个多页 pdf）。</summary>
    public static ConversionTarget ComposePdf { get; } = new(
        "pdf", "合成 PDF", ConversionTarget.ComposePdfMarker, 1, EngineKind.PdfCompose);

    /// <summary>拆分目标（单 pdf 每页一文件；低频项，菜单走 Shift 扩展位）。</summary>
    public static ConversionTarget SplitPdf { get; } = new(
        "pdf", "拆分 PDF", ConversionTarget.SplitPdfMarker, 1, EngineKind.PdfCompose);

    /// <summary>全部选中项是否同属图片。</summary>
    public static bool AllImages(IReadOnlyList<string> paths) =>
        paths.Count > 0 && paths.All(p => ImageExtensions.Contains(Path.GetExtension(p)));

    /// <summary>全部选中项是否同属 pdf。</summary>
    public static bool AllPdf(IReadOnlyList<string> paths) =>
        paths.Count > 0 && paths.All(p => Path.GetExtension(p).Equals(".pdf", StringComparison.OrdinalIgnoreCase));

    /// <summary>PDF 加密目标（不进 GetTargets——密码经菜单输入；特殊分支经 ConvertWithPasswordAsync 执行）。</summary>
    public static ConversionTarget EncryptPdf { get; } = new(
        "pdf", "加密 PDF", ConversionTarget.EncryptPdfMarker, 1, EngineKind.PdfSecurity);

    /// <summary>PDF 解密目标（同上；解密需原密码，密码经菜单输入）。</summary>
    public static ConversionTarget DecryptPdf { get; } = new(
        "pdf", "解密 PDF", ConversionTarget.DecryptPdfMarker, 1, EngineKind.PdfSecurity);

    // —— 表构建（新增格式 = 加一行登记，不动菜单/服务） ——

    private static Dictionary<string, IReadOnlyList<ConversionTarget>> BuildRows()
    {
        var rows = new Dictionary<string, IReadOnlyList<ConversionTarget>>(StringComparer.OrdinalIgnoreCase);

        // Word 族：pdf(soffice→COM) / docx / odt / rtf / txt / html / epub(pandoc)
        // 2026-09-07 补全：+ .wpt（WPS 文字模板）/ .wpd（WordPerfect）
        foreach (var ext in new[] { ".doc", ".docx", ".docm", ".rtf", ".odt", ".wps", ".wpt", ".wpd" })
        {
            var targets = new List<ConversionTarget> { PdfViaSoffice() };
            if (ext != ".docx") targets.Add(To("docx", "Word (.docx)", 1, EngineKind.Soffice));
            if (ext != ".odt") targets.Add(To("odt", "ODT (.odt)", 1, EngineKind.Soffice));
            if (ext != ".rtf") targets.Add(To("rtf", "RTF (.rtf)", 1, EngineKind.Soffice));
            targets.Add(To("txt", "纯文本 (.txt)", 1, EngineKind.Soffice, ConvertServiceConstants.TxtUtf8Filter));
            targets.Add(To("html", "网页 (.html)", 1, EngineKind.Soffice));
            targets.Add(To("epub", "EPUB (.epub)", 1, EngineKind.Pandoc));
            // 文本类近善近全（2026-09-07）：Word 族 → md（docx/odt/rtf pandoc 高质量直转；doc/docm/wps/wpt/wpd 走 soffice→html→md 两跳）
            if (ext is ".docx" or ".odt" or ".rtf")
            {
                targets.Add(new ConversionTarget("md", "Markdown (.md)", null, 1, EngineKind.Pandoc, EngineKind.TwoHop));
            }
            else
            {
                targets.Add(new ConversionTarget("md", "Markdown (.md)", null, 1, EngineKind.TwoHop));
            }
            rows[ext] = targets;
        }

        // Excel 族：pdf(soffice→COM) / xlsx / ods / csv（csv 走红线 6 UTF-8 filter）
        // 2026-09-07 补全：+ .ett（WPS 表格模板）
        foreach (var ext in new[] { ".xls", ".xlsx", ".xlsm", ".et", ".ett" })
        {
            var targets = new List<ConversionTarget> { PdfViaSoffice() };
            if (ext != ".xlsx") targets.Add(To("xlsx", "Excel (.xlsx)", 1, EngineKind.Soffice));
            targets.Add(To("ods", "ODS (.ods)", 1, EngineKind.Soffice));
            targets.Add(To("csv", "CSV (.csv)", 1, EngineKind.Soffice, ConvertServiceConstants.CsvUtf8Filter));
            rows[ext] = targets;
        }

        // Presentation 族：pdf(soffice→COM) / pptx / odp / html（soffice 直转）
        // + png/jpg（两跳：soffice→pdf→poppler；2026-09-07 补全）
        // 2026-09-07 补全：+ .dpt（WPS 演示模板）
        foreach (var ext in new[] { ".ppt", ".pptx", ".pps", ".dps", ".dpt" })
        {
            var targets = new List<ConversionTarget> { PdfViaSoffice() };
            if (ext != ".pptx") targets.Add(To("pptx", "PowerPoint (.pptx)", 1, EngineKind.Soffice));
            targets.Add(To("odp", "ODP (.odp)", 1, EngineKind.Soffice));
            targets.Add(To("html", "网页 (.html)", 1, EngineKind.Soffice));
            targets.Add(new ConversionTarget("png", "PNG 图片 (.png)", null, 2, EngineKind.TwoHop));
            targets.Add(new ConversionTarget("jpg", "JPG 图片 (.jpg)", null, 2, EngineKind.TwoHop));
            rows[ext] = targets;
        }

        // Markdown（枢纽 IR）：html/txt 纯托管（零外部依赖，必须独立可用）；
        // docx 主=pandoc(P3 高质量) 兜底=两跳(P1)；pdf=两跳（pandoc 出 pdf 需外部 pdf 引擎，不做不可靠承诺）。
        rows[".md"] =
        [
            To("html", "网页 (.html)", 1, EngineKind.Managed),
            To("txt", "纯文本 (.txt)", 1, EngineKind.Managed),
            To("epub", "EPUB (.epub)", 1, EngineKind.Pandoc),
            new ConversionTarget("docx", "Word (.docx)", null, 2, EngineKind.Pandoc, EngineKind.TwoHop),
            new ConversionTarget("pdf", "PDF 文档", "pdf", 2, EngineKind.TwoHop),
            // 表格型 md → csv/tsv（2026-09-07：表格语法可逆 → csv↔md 无损往返；非表格 md 转换时报错提示）
            To("csv", "CSV 表格 (.csv)", 1, EngineKind.Managed),
            To("tsv", "TSV 表格 (.tsv)", 1, EngineKind.Managed),
        ];

        // 纯文本：md（轻包装）/ html（纯托管）/ pdf（soffice）/ epub（pandoc；2026-09-07 补全）
        rows[".txt"] = [To("md", "Markdown (.md)", 1, EngineKind.Managed), To("html", "网页 (.html)", 1, EngineKind.Managed), PdfViaSoffice(), To("epub", "EPUB (.epub)", 1, EngineKind.Pandoc)];
        rows[".log"] = [To("md", "Markdown (.md)", 1, EngineKind.Managed), To("html", "网页 (.html)", 1, EngineKind.Managed), PdfViaSoffice(), To("epub", "EPUB (.epub)", 1, EngineKind.Pandoc)];

        // JSON/XML（结构化文本，2026-09-07 补全）：md/html/txt 轻包装 + json→yaml（文本类近善近全）
        rows[".json"] =
        [
            To("md", "Markdown (.md)", 1, EngineKind.Managed),
            To("html", "网页 (.html)", 1, EngineKind.Managed),
            To("txt", "纯文本 (.txt)", 1, EngineKind.Managed),
            To("yaml", "YAML (.yaml)", 1, EngineKind.Managed),
            To("csv", "CSV 表格 (.csv)", 1, EngineKind.Managed),
            To("tsv", "TSV 表格 (.tsv)", 1, EngineKind.Managed),
        ];
        rows[".xml"] =
        [
            To("md", "Markdown (.md)", 1, EngineKind.Managed),
            To("html", "网页 (.html)", 1, EngineKind.Managed),
            To("txt", "纯文本 (.txt)", 1, EngineKind.Managed),
            To("json", "JSON (.json)", 1, EngineKind.Managed),
            To("yaml", "YAML (.yaml)", 1, EngineKind.Managed),
            PdfViaSoffice(),
        ];

        // CSV（2026-09-07 补全）：表格回填 xlsx/ods/pdf（soffice）+ 文本三态 md/txt/html（Managed 纯托管）
        rows[".csv"] =
        [
            To("xlsx", "Excel (.xlsx)", 1, EngineKind.Soffice),
            To("ods", "ODS (.ods)", 1, EngineKind.Soffice),
            PdfViaSoffice(),
            To("md", "Markdown (.md)", 1, EngineKind.Managed),
            To("txt", "纯文本 (.txt)", 1, EngineKind.Managed),
            To("html", "网页 (.html)", 1, EngineKind.Managed),
            To("json", "JSON (.json)", 1, EngineKind.Managed),
            To("yaml", "YAML (.yaml)", 1, EngineKind.Managed),
        ];

        // TSV（2026-09-07 补全，飞鼠 spreadsheetInput 含 tsv）：同 csv
        rows[".tsv"] = rows[".csv"];

        // YAML（2026-09-07 补全，飞鼠 textInput 含 yaml/yml）：md/html/txt 轻包装 + json（YamlDotNet 双向）
        rows[".yaml"] =
        [
            To("md", "Markdown (.md)", 1, EngineKind.Managed),
            To("html", "网页 (.html)", 1, EngineKind.Managed),
            To("txt", "纯文本 (.txt)", 1, EngineKind.Managed),
            To("json", "JSON (.json)", 1, EngineKind.Managed),
            To("csv", "CSV 表格 (.csv)", 1, EngineKind.Managed),
            To("tsv", "TSV 表格 (.tsv)", 1, EngineKind.Managed),
        ];
        rows[".yml"] = rows[".yaml"];

        // .markdown 与 .md 等价（2026-09-07 补全，飞鼠 textInput 含 markdown 扩展）
        rows[".markdown"] = rows[".md"];

        // EPUB 源（2026-09-07 补全）：→ pdf/docx/txt/html（soffice）+ md（pandoc）
        rows[".epub"] = [PdfViaSoffice(), To("docx", "Word (.docx)", 1, EngineKind.Soffice), To("txt", "纯文本 (.txt)", 1, EngineKind.Soffice), To("html", "网页 (.html)", 1, EngineKind.Soffice), To("md", "Markdown (.md)", 1, EngineKind.Pandoc)];

        // HTML：md（ReverseMarkdown 纯托管）/ pdf（soffice）/ epub（pandoc；2026-09-07 补全）
        rows[".html"] = [To("md", "Markdown (.md)", 1, EngineKind.Managed), PdfViaSoffice(), To("epub", "EPUB (.epub)", 1, EngineKind.Pandoc)];
        rows[".htm"] = [To("md", "Markdown (.md)", 1, EngineKind.Managed), PdfViaSoffice(), To("epub", "EPUB (.epub)", 1, EngineKind.Pandoc)];

        // 图片：互转（jpg/jpeg 归一为 jpg 目标；webp 由 Image 引擎能力裁决）+ 合成 pdf（单图=单页 pdf）
        // 2026-09-07 补全：+ .ico（GDI+ 读写+手写封装）/ .tga（ffmpeg 原生——System.Drawing 不支持 tga，
        // 故 tga 源/目标引擎指派 Ffmpeg）；每图 + OCR→txt（Tesseract 探测可用才可执行，菜单置灰兜底）。
        foreach (var ext in ImageExtensions)
        {
            var targets = new List<ConversionTarget>();
            foreach (var target in ImageExtensions)
            {
                if (target.Equals(ext, StringComparison.OrdinalIgnoreCase)) continue;
                if (target is ".jpeg" && ext is ".jpg" or ".jpeg") continue; // 同义格式去重
                var format = target is ".jpeg" ? "jpg" : target.TrimStart('.');
                if (targets.Any(t => t.Format == format)) continue;
                var engine = (ext == ".tga" || format == "tga") ? EngineKind.Ffmpeg : EngineKind.Image;
                targets.Add(To(format, LabelOf(format), 1, engine));
            }
            targets.Add(new ConversionTarget("pdf", "PDF 文档", ConversionTarget.ComposePdfMarker, 1, EngineKind.PdfCompose));
            // AVIF 输出（ffmpeg libaom-av1；完整构建需 libaom，2026-09-07 补全——飞鼠 imageTargets 含 avif）
            targets.Add(To("avif", "AVIF (.avif)", 1, EngineKind.Ffmpeg));
            // OCR：图片 → 识别文本（tesseract；依赖本机 Tesseract-OCR，缺失置灰不隐藏）
            targets.Add(To("txt", "识别文本 (OCR) (.txt)", 1, EngineKind.Tesseract));
            rows[ext] = targets;
        }

        // HEIC/HEIF/AVIF 输入（WIC，2026-09-07 集成）：→ 8 种常规图片（可用性由系统 HEIF/AV1 扩展决定）
        foreach (var ext in Engines.HeicEngine.HeicExtensions)
        {
            rows[ext] = ImageInputTargets(EngineKind.Heic);
        }

        // 相机 RAW 19 种（dcraw/LibRaw，2026-09-07 集成）：→ 8 种常规图片（缺 dcraw 置灰）
        foreach (var ext in Engines.RawDecodeEngine.RawExtensions)
        {
            rows[ext] = ImageInputTargets(EngineKind.Raw);
        }

        // MOBI/AZW 电子书源（calibre，2026-09-07 集成）：→ epub/pdf/docx/txt/html（缺 calibre 置灰；html→md 可间接）
        foreach (var ext in Engines.CalibreEngine.MobiExtensions)
        {
            rows[ext] =
            [
                To("epub", "EPUB (.epub)", 1, EngineKind.Calibre),
                To("pdf", "PDF 文档", 1, EngineKind.Calibre, "pdf"),
                To("docx", "Word (.docx)", 1, EngineKind.Calibre),
                To("txt", "纯文本 (.txt)", 1, EngineKind.Calibre),
                To("html", "网页 (.html)", 1, EngineKind.Calibre),
            ];
        }

        // PDF：→图片/文本（Poppler，P2）+ docx/xlsx（PdfText 文本提取降级，2026-09-07 补全；
        // 无版式，菜单文本已标注「文本提取」）。加密/解密见 EncryptPdf/DecryptPdf（密码菜单输入）。
        rows[".pdf"] =
        [
            To("png", "PNG (.png)", 1, EngineKind.Poppler),
            To("jpg", "JPG (.jpg)", 1, EngineKind.Poppler),
            To("txt", "纯文本 (.txt)", 1, EngineKind.Poppler),
            new ConversionTarget("docx", "Word (.docx)（文本提取）", null, 1, EngineKind.PdfText),
            new ConversionTarget("xlsx", "Excel (.xlsx)（文本提取）", null, 1, EngineKind.PdfText),
        ];

        // 视频（FFmpeg，P3）：容器/编码互转 + 抽音轨 + GIF
        // 2026-09-07 补全：MP4 编码选择（H.264/H.265/AV1 三个目标，Filter 承载 marker）、
        // + .m4v 源（飞鼠 videoInput 含 m4v）、+ mov 目标（飞鼠 mediaVideoTargets 含 mov）
        foreach (var ext in new[] { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v" })
        {
            var targets = new List<ConversionTarget>();
            targets.Add(new ConversionTarget("mp4", "MP4 (H.264)", ConversionTarget.VideoEncH264Marker, 1, EngineKind.Ffmpeg));
            targets.Add(new ConversionTarget("mp4", "MP4 (H.265/HEVC)", ConversionTarget.VideoEncH265Marker, 1, EngineKind.Ffmpeg));
            targets.Add(new ConversionTarget("mp4", "MP4 (AV1)", ConversionTarget.VideoEncAv1Marker, 1, EngineKind.Ffmpeg));
            foreach (var format in new[] { "mkv", "webm", "mov", "gif" })
            {
                if ($".{format}" == ext) continue;
                targets.Add(To(format, LabelOf(format), 1, EngineKind.Ffmpeg));
            }
            targets.Add(To("mp3", "音频 MP3 (.mp3)（抽音轨）", 1, EngineKind.Ffmpeg));
            rows[ext] = targets;
        }

        // 音频（FFmpeg，P3）：编码互转（2026-09-07 补全 aac/opus/wma 源与目标）
        foreach (var ext in new[] { ".mp3", ".wav", ".flac", ".m4a", ".ogg", ".ape", ".aac", ".opus", ".wma" })
        {
            var targets = new List<ConversionTarget>();
            foreach (var format in new[] { "mp3", "wav", "flac", "m4a", "aac", "opus", "wma" })
            {
                if ($".{format}" == ext) continue;
                targets.Add(To(format, LabelOf(format), 1, EngineKind.Ffmpeg));
            }
            rows[ext] = targets;
        }

        return rows;
    }

    /// <summary>HEIC/RAW 输入行的常规图片目标集（8 种；不含 pdf 合成——PdfCompose 依赖 GDI+ 解码源，诚实不跨解码链）。</summary>
    private static IReadOnlyList<ConversionTarget> ImageInputTargets(EngineKind kind)
    {
        var targets = new List<ConversionTarget>();
        foreach (var format in new[] { "png", "jpg", "bmp", "gif", "tif", "tiff", "webp", "ico" })
        {
            targets.Add(To(format, LabelOf(format), 1, kind));
        }
        return targets;
    }

    private static ConversionTarget PdfViaSoffice() =>
        new("pdf", "PDF 文档", "pdf", 1, EngineKind.Soffice, EngineKind.ComPdf);

    private static ConversionTarget To(string format, string label, int hops, EngineKind prefer, string? filter = null) =>
        new(format, label, filter, hops, prefer);

    private static string LabelOf(string format) => format switch
    {
        "png" => "PNG (.png)",
        "jpg" => "JPG (.jpg)",
        "bmp" => "BMP (.bmp)",
        "gif" => "GIF (.gif)",
        "tif" => "TIFF (.tif)",
        "webp" => "WebP (.webp)",
        "ico" => "ICO 图标 (.ico)",
        "tga" => "TGA (.tga)",
        _ => $"{format.ToUpperInvariant()} (.{format})",
    };
}
