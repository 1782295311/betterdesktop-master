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

    // 2026-09-20 路由收口（convert-lite 迁移）：装配后过一道路由 pass，见 RouteThroughLite。
    private static readonly Dictionary<string, IReadOnlyList<ConversionTarget>> Rows = RouteThroughLite(BuildRows());

    /// <summary>查询某扩展名的全部可转目标（扩展名大小写不敏感；未登记返回空）。</summary>
    public static IReadOnlyList<ConversionTarget> GetTargets(string extension) =>
        Rows.TryGetValue(extension.ToLowerInvariant(), out var targets) ? targets : [];

    /// <summary>查询精确目标（源扩展 + 目标格式）。</summary>
    public static ConversionTarget? Find(string extension, string format) =>
        GetTargets(extension).FirstOrDefault(t => t.Format == format);

    /// <summary>是否可转换（右键菜单整项显隐的快速判定）。</summary>
    public static bool IsConvertible(string extension) => GetTargets(extension).Count > 0;

    /// <summary>全部已登记可转输入扩展名（系统右键级联注册用：每种类型全量平铺「格式转换 ▸」，引擎缺失项照常显示）。</summary>
    public static IReadOnlyList<string> AllInputExtensions { get; } = new List<string>(Rows.Keys);

    /// <summary>多输入合并目标（全部为 pdf 且 ≥2 时可合并为一个 pdf）。</summary>
    public static ConversionTarget MergePdf { get; } = new(
        "pdf", "合并 PDF", ConversionTarget.MergePdfMarker, 1, EngineKind.PdfCompose,
        Category: TargetCategory.Document, Lossless: true);

    /// <summary>多输入合成目标（全部为图片且 ≥2 时合成一个多页 pdf）。</summary>
    public static ConversionTarget ComposePdf { get; } = new(
        "pdf", "合成 PDF", ConversionTarget.ComposePdfMarker, 1, EngineKind.PdfCompose,
        Category: TargetCategory.Document, Lossless: true);

    /// <summary>拆分目标（单 pdf 每页一文件；低频项，菜单走 Shift 扩展位）。</summary>
    public static ConversionTarget SplitPdf { get; } = new(
        "pdf", "拆分 PDF", ConversionTarget.SplitPdfMarker, 1, EngineKind.PdfCompose,
        Category: TargetCategory.Document, Lossless: true);

    /// <summary>全部选中项是否同属图片。</summary>
    public static bool AllImages(IReadOnlyList<string> paths) =>
        paths.Count > 0 && paths.All(p => ImageExtensions.Contains(Path.GetExtension(p)));

    /// <summary>全部选中项是否同属 pdf。</summary>
    public static bool AllPdf(IReadOnlyList<string> paths) =>
        paths.Count > 0 && paths.All(p => Path.GetExtension(p).Equals(".pdf", StringComparison.OrdinalIgnoreCase));

    /// <summary>PDF 加密目标（不进 GetTargets——密码经菜单输入；特殊分支经 ConvertWithPasswordAsync 执行）。</summary>
    public static ConversionTarget EncryptPdf { get; } = new(
        "pdf", "加密 PDF", ConversionTarget.EncryptPdfMarker, 1, EngineKind.PdfSecurity,
        Category: TargetCategory.Other, Lossless: true);

    /// <summary>PDF 解密目标（同上；解密需原密码，密码经菜单输入）。</summary>
    public static ConversionTarget DecryptPdf { get; } = new(
        "pdf", "解密 PDF", ConversionTarget.DecryptPdfMarker, 1, EngineKind.PdfSecurity,
        Category: TargetCategory.Other, Lossless: true);

    // —— 路由收口（2026-09-20 convert-lite 迁移 C3/C4） ——

    /// <summary>
    /// 把"lite 能做的边"改派给 <see cref="EngineKind.Lite"/>，并摘掉音视频目标。
    /// <para>
    /// 【为什么不逐行改表】矩阵有几百条边、跨约二十个格式族。逐行改的真正代价不是工作量而是**一致性**：
    /// 漏一行就留下一条指向已删引擎的边（<c>IsEngineReady=false</c> ⇒ 菜单整项隐藏，用户看到"功能少了"）。
    /// 收成一道路由 pass 之后，"哪些边归 lite"由 <see cref="LiteCapability.Supports"/> **一处**回答。
    /// </para>
    /// <para>
    /// 【与 Rust 侧同口径】本方法与 <c>native/convert-engine/src/matrix.rs::route_through_lite</c>
    /// **逐一对应**（同一判据、同一三条步骤）。两侧矩阵本就各自组装，口径必须一致 ——
    /// 否则会出现"菜单显示了、执行说引擎缺失"这类跨语言不一致。改一处务必改另一处。
    /// </para>
    /// </summary>
    private static Dictionary<string, IReadOnlyList<ConversionTarget>> RouteThroughLite(
        Dictionary<string, IReadOnlyList<ConversionTarget>> rows)
    {
        var routed = new Dictionary<string, IReadOnlyList<ConversionTarget>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (ext, targets) in rows)
        {
            var kept = new List<ConversionTarget>(targets.Count);

            foreach (var target in targets)
            {
                // (1) 音视频：本轮不带 FFmpeg 依赖（计划 §7 D3①）⇒ 摘掉目标，菜单项随之消失。
                //     判据用**类别**而不是"引擎 == Ffmpeg"：png→tga/avif 也走 ffmpeg，但它们是**图片**边，
                //     摘掉是真丢功能（这条口径是被 Rust 侧旧测试"png 行缺少 tga"纠出来的）。
                if (target.Category is TargetCategory.Audio or TargetCategory.Video)
                {
                    continue;
                }

                // (2) lite 能做的边 → 改派 Lite（Prefer=Lite、Fallback=null：不做双路径）。
                //     只收编原本由外部引擎拥有的边；Image/PdfCompose/PdfSecurity/Heic/Raw/Tesseract/Poppler
                //     各有专职实现，**不许被抢** —— Poppler（PDF）与 Tesseract（OCR）正是点名保留的两棵树，
                //     抢走它们的边等于把"保留"变成"删掉"。
                var ownedByExternal = target.Prefer is EngineKind.Pandoc
                    or EngineKind.Soffice
                    or EngineKind.Calibre
                    or EngineKind.TwoHop
                    or EngineKind.ComPdf;

                if (ownedByExternal && Engines.LiteCapability.Supports(ext, target.Format))
                {
                    kept.Add(target with { Prefer = EngineKind.Lite, Fallback = null });
                    continue;
                }

                // lite 做不了的边（如 .wps/.wpt/.wpd 这类 lite 没有 reader 的源）保持原样：
                // 引擎树被删后它们会 EngineMissing ⇒ 菜单隐藏。这是**诚实的**能力缩减，不伪装成"能转"。
                kept.Add(target);
            }

            // (3) 摘掉空行：目标被摘光的源不进入 Rows ⇒ AllInputExtensions 也不再包含它，
            //     否则系统右键级联会为一个没有子项的源建空键。
            if (kept.Count > 0)
            {
                routed[ext] = kept;
            }
        }

        return routed;
    }

    // —— 表构建（新增格式 = 加一行登记，不动菜单/服务） ——

    private static Dictionary<string, IReadOnlyList<ConversionTarget>> BuildRows()
    {
        var rows = new Dictionary<string, IReadOnlyList<ConversionTarget>>(StringComparer.OrdinalIgnoreCase);

        // Word 族：pdf(soffice→COM) / docx / odt / rtf / txt / html / epub(pandoc)
        // 2026-09-07 补全：+ .wpt（WPS 文字模板）/ .wpd（WordPerfect）
        // Word 族（2026-09-10 架构主线：pandoc 主 + soffice/COM 辅——LibreOffice 26.8.0 内置回归）：
        // 可读源（.docx/.odt/.rtf pandoc 原生 reader）→ 文本系 pandoc 直转；老二进制源（.doc/.docm/.wps/.wpt/.wpd
        // pandoc 读不了）→ soffice 直转 + md 两跳；pdf 全部 soffice→COM 兜底
        foreach (var ext in new[] { ".doc", ".docx", ".docm", ".rtf", ".odt", ".wps", ".wpt", ".wpd" })
        {
            var targets = new List<ConversionTarget> { PdfViaSoffice() };
            if (ext != ".docx") targets.Add(To("docx", "Word (.docx)", 1,
                ext is ".docx" or ".rtf" or ".odt" ? EngineKind.Pandoc : EngineKind.Soffice, category: TargetCategory.Document));
            if (ext != ".odt") targets.Add(To("odt", "ODT (.odt)", 1,
                ext is ".docx" or ".rtf" ? EngineKind.Pandoc : EngineKind.Soffice));
            if (ext != ".rtf") targets.Add(To("rtf", "RTF (.rtf)", 1,
                ext is ".docx" or ".odt" ? EngineKind.Pandoc : EngineKind.Soffice));
            targets.Add(To("txt", "纯文本 (.txt)", 1, EngineKind.Soffice, ConvertServiceConstants.TxtUtf8Filter));
            targets.Add(To("html", "网页 (.html)", 1, EngineKind.Soffice));
            targets.Add(To("epub", "EPUB (.epub)", 1, EngineKind.Pandoc, category: TargetCategory.Document));
            // 文本类近善近全：Word 族 → md（docx/odt/rtf pandoc 高质量直转；老格式 soffice→html→md 两跳）
            if (ext is ".docx" or ".odt" or ".rtf")
            {
                targets.Add(To("md", "Markdown (.md)", 1, EngineKind.Pandoc, category: TargetCategory.Text));
            }
            else
            {
                targets.Add(new ConversionTarget("md", "Markdown (.md)", null, 1, EngineKind.TwoHop,
                    Category: TargetCategory.Text, Lossless: false)); // 两跳样式有损
            }
            // pandoc 文本系扩展：docx/odt/rtf 直读，语义保真=无损（老格式 soffice 无此能力）
            if (ext is ".docx" or ".odt" or ".rtf")
            {
                foreach (var t in PandocTextTargets().Where(t => t.Format is not "rtf" and not "odt"))
                {
                    targets.Add(t);
                }
            }
            rows[ext] = targets;
        }

        // Excel 族（2026-09-10：LibreOffice 26.8.0 内置回归——xlsx/ods/csv 由 soffice 直转；pdf soffice→COM 兜底）
        // csv 走红线 6 UTF-8 filter；+ .ett（WPS 表格模板）
        foreach (var ext in new[] { ".xls", ".xlsx", ".xlsm", ".et", ".ett" })
        {
            var targets = new List<ConversionTarget> { PdfViaSoffice() };
            if (ext != ".xlsx") targets.Add(To("xlsx", "Excel (.xlsx)", 1, EngineKind.Soffice, category: TargetCategory.Spreadsheet));
            targets.Add(To("ods", "ODS (.ods)", 1, EngineKind.Soffice, category: TargetCategory.Spreadsheet));
            targets.Add(To("csv", "CSV (.csv)", 1, EngineKind.Soffice, ConvertServiceConstants.CsvUtf8Filter, TargetCategory.Spreadsheet));
            rows[ext] = targets;
        }

        // .pptx 源（2026-09-10）：pandoc 3.x 原生 pptx reader（实测 3.11 可用）→ 文本系全列；
        // pdf soffice→COM 兜底；odp soffice 直转（LibreOffice 26.8.0 回归）；png/jpg ComPdfTwoHop（COM→pdf 中间态→poppler）
        var pptxTargets = new List<ConversionTarget>
        {
            PdfViaSoffice(),
            To("odp", "ODP (.odp)", 1, EngineKind.Soffice, category: TargetCategory.Document),
            To("html", "网页 (.html)", 1, EngineKind.Pandoc),
            To("md", "Markdown (.md)", 1, EngineKind.Pandoc),
            To("txt", "纯文本 (.txt)", 1, EngineKind.Pandoc),
            To("docx", "Word (.docx)", 1, EngineKind.Pandoc, category: TargetCategory.Document),
            To("epub", "EPUB (.epub)", 1, EngineKind.Pandoc, category: TargetCategory.Document),
            new ConversionTarget("png", "PNG 图片 (.png)", null, 2, EngineKind.TwoHop,
                Category: TargetCategory.Image, Lossless: true),
            new ConversionTarget("jpg", "JPG 图片 (.jpg)", null, 2, EngineKind.TwoHop,
                Category: TargetCategory.Image, Lossless: false), // 重编码
        };
        foreach (var t in PandocTextTargets())
        {
            pptxTargets.Add(t);
        }
        rows[".pptx"] = pptxTargets;

        // 老演示格式（.ppt/.pps/.dps/.dpt）：无 pandoc reader（老二进制）→ pptx/odp/html soffice 直转（LibreOffice 回归）；
        // pdf soffice→COM；png/jpg ComPdfTwoHop（COM→pdf 中间态→poppler）
        foreach (var ext in new[] { ".ppt", ".pps", ".dps", ".dpt" })
        {
            var targets = new List<ConversionTarget> { PdfViaSoffice() };
            targets.Add(To("pptx", "PowerPoint (.pptx)", 1, EngineKind.Soffice, category: TargetCategory.Document));
            targets.Add(To("odp", "ODP (.odp)", 1, EngineKind.Soffice, category: TargetCategory.Document));
            targets.Add(To("html", "网页 (.html)", 1, EngineKind.Soffice));
            targets.Add(new ConversionTarget("png", "PNG 图片 (.png)", null, 2, EngineKind.TwoHop,
                Category: TargetCategory.Image, Lossless: true));
            targets.Add(new ConversionTarget("jpg", "JPG 图片 (.jpg)", null, 2, EngineKind.TwoHop,
                Category: TargetCategory.Image, Lossless: false)); // 重编码
            rows[ext] = targets;
        }

        // Markdown（枢纽 IR）：html/txt 纯托管（零外部依赖，必须独立可用）；
        // docx 主=pandoc(P3 高质量) 兜底=两跳(P1)；pdf=两跳（pandoc 出 pdf 需外部 pdf 引擎，LibreOffice 26.8.0 回归后可用）。
        // pptx 2026-09-10 补全：pandoc 3.x pptx writer 成熟，随内置引擎 3.11 解锁（高亮由 PandocEngine.SupportsPptx 版本门槛控制）。
        // pandoc 文本系扩展 2026-09-10：rtf/odt/tex/rst/org/wiki/adoc/textile/ipynb/db/man/context/texi/opendocument/plain 全量直转
        var mdTargets = new List<ConversionTarget>
        {
            To("html", "网页 (.html)", 1, EngineKind.Managed),
            To("txt", "纯文本 (.txt)", 1, EngineKind.Managed),
            To("epub", "EPUB (.epub)", 1, EngineKind.Pandoc, category: TargetCategory.Document),
            To("pptx", "PowerPoint (.pptx)", 1, EngineKind.Pandoc, category: TargetCategory.Document),
            new ConversionTarget("docx", "Word (.docx)", null, 2, EngineKind.Pandoc, EngineKind.TwoHop,
                TargetCategory.Document, Lossless: true),
            new ConversionTarget("pdf", "PDF 文档", "pdf", 2, EngineKind.TwoHop,
                Category: TargetCategory.Document, Lossless: false), // 两跳样式有损
            // 表格型 md → csv/tsv（2026-09-07：表格语法可逆 → csv↔md 无损往返；非表格 md 转换时报错提示）
            To("csv", "CSV 表格 (.csv)", 1, EngineKind.Managed, category: TargetCategory.Spreadsheet),
            To("tsv", "TSV 表格 (.tsv)", 1, EngineKind.Managed, category: TargetCategory.Spreadsheet),
        };
        foreach (var t in PandocTextTargets())
        {
            mdTargets.Add(t);
        }
        rows[".md"] = mdTargets;

        // 纯文本：md（轻包装）/ html（纯托管）/ pdf（soffice→COM 兜底）/ epub（pandoc）+ pandoc 文本系扩展（2026-09-10）
        var txtTargets = new List<ConversionTarget>
        {
            To("md", "Markdown (.md)", 1, EngineKind.Managed),
            To("html", "网页 (.html)", 1, EngineKind.Managed),
            PdfViaSoffice(),
            To("epub", "EPUB (.epub)", 1, EngineKind.Pandoc, category: TargetCategory.Document),
        };
        foreach (var t in PandocTextTargets())
        {
            txtTargets.Add(t);
        }
        rows[".txt"] = txtTargets;
        rows[".log"] = txtTargets;

        // JSON/XML（结构化文本，2026-09-07 补全）：md/html/txt 轻包装 + json→yaml（文本类近善近全）
        // 2026-09-10 补分类元数据（csv/tsv → Spreadsheet）
        rows[".json"] =
        [
            To("md", "Markdown (.md)", 1, EngineKind.Managed),
            To("html", "网页 (.html)", 1, EngineKind.Managed),
            To("txt", "纯文本 (.txt)", 1, EngineKind.Managed),
            To("yaml", "YAML (.yaml)", 1, EngineKind.Managed),
            To("csv", "CSV 表格 (.csv)", 1, EngineKind.Managed, category: TargetCategory.Spreadsheet),
            To("tsv", "TSV 表格 (.tsv)", 1, EngineKind.Managed, category: TargetCategory.Spreadsheet),
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
        // 2026-09-10 补分类元数据（xlsx/ods → Spreadsheet）
        rows[".csv"] =
        [
            To("xlsx", "Excel (.xlsx)", 1, EngineKind.Soffice, category: TargetCategory.Spreadsheet),
            To("ods", "ODS (.ods)", 1, EngineKind.Soffice, category: TargetCategory.Spreadsheet),
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
            To("csv", "CSV 表格 (.csv)", 1, EngineKind.Managed, category: TargetCategory.Spreadsheet),
            To("tsv", "TSV 表格 (.tsv)", 1, EngineKind.Managed, category: TargetCategory.Spreadsheet),
        ];
        rows[".yml"] = rows[".yaml"];

        // .markdown 与 .md 等价（2026-09-07 补全，飞鼠 textInput 含 markdown 扩展）
        rows[".markdown"] = rows[".md"];

        // EPUB 源（2026-09-07 补全；2026-09-10 收尾修正：soffice 26.8 实测 epub 输入 exit 1 无产物——
        // docx/txt/html/md 全改 pandoc 原生 reader 直转；pdf 改 TwoHop（epub→html 中间态→soffice，中间态 TempDir finally 删除）
        var epubTargets = new List<ConversionTarget>
        {
            new ConversionTarget("pdf", "PDF 文档", "pdf", 2, EngineKind.TwoHop,
                Category: TargetCategory.Document, Lossless: true), // 渲染保真（文字可复制）= 无损；中间态透明
            To("docx", "Word (.docx)", 1, EngineKind.Pandoc, category: TargetCategory.Document),
            To("txt", "纯文本 (.txt)", 1, EngineKind.Pandoc),
            To("html", "网页 (.html)", 1, EngineKind.Pandoc),
            To("md", "Markdown (.md)", 1, EngineKind.Pandoc),
        };
        foreach (var t in PandocTextTargets())
        {
            epubTargets.Add(t);
        }
        rows[".epub"] = epubTargets;

        // HTML：md（ReverseMarkdown 纯托管）/ pdf（soffice→COM 兜底）/ epub（pandoc）+ pandoc 文本系扩展（2026-09-10）
        var htmlTargets = new List<ConversionTarget>
        {
            To("md", "Markdown (.md)", 1, EngineKind.Managed),
            PdfViaSoffice(),
            To("epub", "EPUB (.epub)", 1, EngineKind.Pandoc, category: TargetCategory.Document),
        };
        foreach (var t in PandocTextTargets())
        {
            htmlTargets.Add(t);
        }
        rows[".html"] = htmlTargets;
        rows[".htm"] = htmlTargets;

        // 图片：互转（jpg/jpeg 归一为 jpg 目标；webp 由 Image 引擎能力裁决）+ 合成 pdf（单图=单页 pdf）
        // 2026-09-07 补全：+ .ico（GDI+ 读写+手写封装）/ .tga（ffmpeg 原生——System.Drawing 不支持 tga，
        // 故 tga 源/目标引擎指派 Ffmpeg）；每图 + OCR→txt（Tesseract 探测可用才可执行，菜单置灰兜底）。
        // 2026-09-10 无损规则：png/bmp/tif/tiff/tga 同族无损；jpg/gif/webp/ico/avif 重编码有损
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
                targets.Add(To(format, LabelOf(format), 1, engine,
                    category: TargetCategory.Image, lossless: IsLosslessImageFormat(format)));
            }
            targets.Add(new ConversionTarget("pdf", "PDF 文档", ConversionTarget.ComposePdfMarker, 1, EngineKind.PdfCompose,
                Category: TargetCategory.Document, Lossless: true)); // 图像合成 PDF = 无损封装
            // AVIF 输出（ffmpeg libaom-av1；完整构建需 libaom，2026-09-07 补全——飞鼠 imageTargets 含 avif）
            targets.Add(To("avif", "AVIF (.avif)", 1, EngineKind.Ffmpeg, category: TargetCategory.Image, lossless: false));
            // OCR：图片 → 识别文本（tesseract；依赖本机 Tesseract-OCR，缺失置灰不隐藏；识别=有损提取）
            targets.Add(To("txt", "识别文本 (OCR) (.txt)", 1, EngineKind.Tesseract, lossless: false));
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
        // 2026-09-10 补分类/无损元数据（calibre 语义保真转换=无损）
        foreach (var ext in Engines.CalibreEngine.MobiExtensions)
        {
            rows[ext] =
            [
                To("epub", "EPUB (.epub)", 1, EngineKind.Calibre, category: TargetCategory.Document),
                To("pdf", "PDF 文档", 1, EngineKind.Calibre, "pdf", TargetCategory.Document),
                To("docx", "Word (.docx)", 1, EngineKind.Calibre, category: TargetCategory.Document),
                To("txt", "纯文本 (.txt)", 1, EngineKind.Calibre),
                To("html", "网页 (.html)", 1, EngineKind.Calibre),
            ];
        }

        // PDF：→图片/文本（Poppler，P2）+ docx/xlsx（PdfText 文本提取降级，2026-09-07 补全；
        // 无版式，菜单文本已标注「文本提取」）。加密/解密见 EncryptPdf/DecryptPdf（密码菜单输入）。
        // 2026-09-10 无损规则：渲染/文本提取=有损（不可逆降级）
        rows[".pdf"] =
        [
            // 页面渲染（Poppler pdftoppm，150dpi；多页多产物；重编码=有损；bmp 被 26.09 移除不再登记）
            To("png", "PNG (.png)", 1, EngineKind.Poppler, category: TargetCategory.Image, lossless: false),
            To("jpg", "JPG (.jpg)", 1, EngineKind.Poppler, category: TargetCategory.Image, lossless: false),
            To("tiff", "TIFF (.tiff)", 1, EngineKind.Poppler, category: TargetCategory.Image, lossless: false),
            // 文本提取（pdftotext → 托管生成；无版式，诚实标注「文本提取」=有损）
            To("txt", "纯文本 (.txt)", 1, EngineKind.PdfText, lossless: false),
            To("md", "Markdown (.md)（文本提取）", 1, EngineKind.PdfText, lossless: false),
            To("html", "网页 (.html)（文本提取）", 1, EngineKind.PdfText, lossless: false),
            new ConversionTarget("docx", "Word (.docx)（文本提取）", null, 1, EngineKind.PdfText,
                Category: TargetCategory.Document, Lossless: false),
            new ConversionTarget("xlsx", "Excel (.xlsx)（文本提取）", null, 1, EngineKind.PdfText,
                Category: TargetCategory.Spreadsheet, Lossless: false),
            new ConversionTarget("epub", "电子书 (.epub)（文本提取）", null, 1, EngineKind.PdfText,
                Category: TargetCategory.Document, Lossless: false),
            new ConversionTarget("odt", "OpenDocument (.odt)（文本提取）", null, 1, EngineKind.PdfText,
                Category: TargetCategory.Document, Lossless: false),
            new ConversionTarget("rtf", "RTF (.rtf)（文本提取）", null, 1, EngineKind.PdfText,
                Category: TargetCategory.Document, Lossless: false),
            new ConversionTarget("opendocument", "OpenDocument XML (.xml)（文本提取）", null, 1, EngineKind.PdfText,
                Category: TargetCategory.Document, Lossless: false),
        ];

        // 视频（FFmpeg，P3）：容器/编码互转 + 抽音轨 + GIF
        // 2026-09-07 补全：MP4 编码选择（H.264/H.265/AV1 三个目标，Filter 承载 marker）、
        // + .m4v 源（飞鼠 videoInput 含 m4v）、+ mov 目标（飞鼠 mediaVideoTargets 含 mov）
        // 2026-09-10：重编码=有损（全 false）；mp3 抽音轨=Audio 类有损
        foreach (var ext in new[] { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v" })
        {
            var targets = new List<ConversionTarget>();
            targets.Add(new ConversionTarget("mp4", "MP4 (H.264)", ConversionTarget.VideoEncH264Marker, 1, EngineKind.Ffmpeg,
                Category: TargetCategory.Video, Lossless: false));
            targets.Add(new ConversionTarget("mp4", "MP4 (H.265/HEVC)", ConversionTarget.VideoEncH265Marker, 1, EngineKind.Ffmpeg,
                Category: TargetCategory.Video, Lossless: false));
            targets.Add(new ConversionTarget("mp4", "MP4 (AV1)", ConversionTarget.VideoEncAv1Marker, 1, EngineKind.Ffmpeg,
                Category: TargetCategory.Video, Lossless: false));
            foreach (var format in new[] { "mkv", "webm", "mov", "gif" })
            {
                if ($".{format}" == ext) continue;
                // 2026-09-10：mkv/mov = 容器 remux（-c copy 不重编码，无损）；webm/gif = 重编码（有损）
                targets.Add(To(format, LabelOf(format), 1, EngineKind.Ffmpeg, category: TargetCategory.Video,
                    lossless: format is "mkv" or "mov"));
            }
            targets.Add(To("mp3", "音频 MP3 (.mp3)（抽音轨）", 1, EngineKind.Ffmpeg, category: TargetCategory.Audio, lossless: false));
            rows[ext] = targets;
        }

        // 音频（FFmpeg，P3）：编码互转（2026-09-07 补全 aac/opus/wma 源与目标）
        // 2026-09-10 无损规则：wav/flac 无损编码；mp3/m4a/aac/opus/wma 有损重编码
        foreach (var ext in new[] { ".mp3", ".wav", ".flac", ".m4a", ".ogg", ".ape", ".aac", ".opus", ".wma" })
        {
            var targets = new List<ConversionTarget>();
            foreach (var format in new[] { "mp3", "wav", "flac", "m4a", "aac", "opus", "wma" })
            {
                if ($".{format}" == ext) continue;
                targets.Add(To(format, LabelOf(format), 1, EngineKind.Ffmpeg,
                    category: TargetCategory.Audio, lossless: format is "wav" or "flac"));
            }
            rows[ext] = targets;
        }

        return rows;
    }

    /// <summary>HEIC/RAW 输入行的常规图片目标集（8 种；不含 pdf 合成——PdfCompose 依赖 GDI+ 解码源，诚实不跨解码链）。
    /// 2026-09-10：png/bmp/tif/tiff 无损；jpg/gif/webp/ico 重编码有损。</summary>
    private static IReadOnlyList<ConversionTarget> ImageInputTargets(EngineKind kind)
    {
        var targets = new List<ConversionTarget>();
        foreach (var format in new[] { "png", "jpg", "bmp", "gif", "tif", "tiff", "webp", "ico" })
        {
            targets.Add(To(format, LabelOf(format), 1, kind,
                category: TargetCategory.Image, lossless: IsLosslessImageFormat(format)));
        }
        return targets;
    }

    /// <summary>图像无损格式判定（同族不重编码：png/bmp/tif/tiff/tga；jpg/gif/webp/ico/avif 重编码有损）。</summary>
    private static bool IsLosslessImageFormat(string format) =>
        format is "png" or "bmp" or "tif" or "tiff" or "tga";

    /// <summary>
    /// pandoc 文本系扩展目标（2026-09-10 用户拍板"当然要加"）：pandoc 直转，文本语义保真 = 无损。
    /// Format = 产物扩展名；PandocEngine.RunAsync 按 §12 writer 映射补显式 -t（扩展名≠writer 名的：tex→latex、
    /// wiki→mediawiki、adoc→asciidoc、db→docbook、texi→texinfo、opendocument）。
    /// </summary>
    private static IReadOnlyList<ConversionTarget> PandocTextTargets() =>
    [
        To("rtf", "RTF (.rtf)", 1, EngineKind.Pandoc),
        To("odt", "ODT (.odt)", 1, EngineKind.Pandoc),
        To("tex", "LaTeX (.tex)", 1, EngineKind.Pandoc),
        To("rst", "reStructuredText (.rst)", 1, EngineKind.Pandoc),
        To("org", "Org-mode (.org)", 1, EngineKind.Pandoc),
        To("wiki", "MediaWiki (.wiki)", 1, EngineKind.Pandoc),
        To("adoc", "AsciiDoc (.adoc)", 1, EngineKind.Pandoc),
        To("textile", "Textile (.textile)", 1, EngineKind.Pandoc),
        To("ipynb", "Jupyter Notebook (.ipynb)", 1, EngineKind.Pandoc),
        To("db", "DocBook XML (.db)", 1, EngineKind.Pandoc),
        To("man", "Man page (.man)", 1, EngineKind.Pandoc),
        To("context", "ConTeXt (.context)", 1, EngineKind.Pandoc),
        To("texi", "Texinfo (.texi)", 1, EngineKind.Pandoc),
        To("opendocument", "OpenDocument XML (.opendocument)", 1, EngineKind.Pandoc),
        To("plain", "纯文本 (.plain)", 1, EngineKind.Pandoc),
    ];

    /// <summary>
    /// PDF 目标：soffice 直转（LibreOffice 26.8.0 内置回归）→ COM 兜底（Office/WPS 渲染保真）。
    /// 非 Office 源（md/txt/xml/csv/epub/html）soffice 可读 → pdf 可用；COM 只兜 Office 源。
    /// </summary>
    private static ConversionTarget PdfViaSoffice() =>
        new("pdf", "PDF 文档", "pdf", 1, EngineKind.Soffice, EngineKind.ComPdf,
            TargetCategory.Document, Lossless: true); // 渲染保真（文字可复制）= 无损

    /// <summary>文本系直连目标（pandoc/Managed；文本互转语义保真 = 无损）。</summary>
    private static ConversionTarget To(string format, string label, int hops, EngineKind prefer,
        string? filter = null, TargetCategory category = TargetCategory.Text, bool lossless = true) =>
        new(format, label, filter, hops, prefer, Category: category, Lossless: lossless);

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
