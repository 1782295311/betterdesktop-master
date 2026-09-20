namespace BetterDesktop.Shell.Convert.Contracts;

/// <summary>
/// 转换引擎种类（矩阵按此指派主/兜底引擎；EngineRegistry 据此路由）。
/// P0：Soffice/ComPdf（等价搬迁）；P1：Managed/TwoHop/Image/PdfCompose；
/// P2：Poppler（真实 --version 探测）；P3：Pandoc/Ffmpeg（按需下载，dependency-on-demand）。
/// </summary>
public enum EngineKind
{
    /// <summary>LibreOffice soffice 子进程（Office 文档族互转 / 两跳中转的执行段）。</summary>
    Soffice,

    /// <summary>Office/WPS COM ExportAsFixedFormat（仅 pdf；soffice 缺失时的兜底，STA 专用线程）。</summary>
    ComPdf,

    /// <summary>C# 纯托管链（Markdig/ReverseMarkdown/纯文本）——零外部依赖，必须独立可用。</summary>
    Managed,

    /// <summary>两跳中转（A→html→B；矩阵显式登记 hop=2，禁止隐式超过两跳）。</summary>
    TwoHop,

    /// <summary>图片互转（System.Drawing 常规格式；webp 缺口由 SkiaSharp 补，MIT）。</summary>
    Image,

    /// <summary>PDF 组合（PDFsharp，MIT）：图片合成 PDF / 多 PDF 合并 / 拆分。</summary>
    PdfCompose,

    /// <summary>Poppler 子进程（pdftoppm/pdftotext）：PDF→图片/文本。</summary>
    Poppler,

    /// <summary>pandoc 子进程：docx↔md 高质量、md→epub/pptx（本机已装优先，缺失可按需下载）。</summary>
    Pandoc,

    /// <summary>FFmpeg 子进程：音视频容器/编码互转、抽音轨、图片→视频、tga 图片互转（本机已装优先）。</summary>
    Ffmpeg,

    /// <summary>PDF 文本提取（Poppler pdftotext + 托管 OOXML 生成）：pdf→docx/xlsx 文本降级（无版式）。</summary>
    PdfText,

    /// <summary>Tesseract OCR 子进程：图片→识别文本（tesseract.exe 探测可用才可执行）。</summary>
    Tesseract,

    /// <summary>PDF 安全（PDFsharp，MIT）：加密/解密（密码经菜单输入，不进注册表路由）。</summary>
    PdfSecurity,

    /// <summary>HEIC/HEIF/AVIF 输入（WPF WIC BitmapDecoder：依赖系统 HEIF/AV1 图像扩展，失败给安装指引）。</summary>
    Heic,

    /// <summary>相机 RAW 输入（dcraw/LibRaw 子进程：19 种厂商 RAW → 常规图片）。</summary>
    Raw,

    /// <summary>MOBI/AZW 电子书源（calibre ebook-convert 子进程：→ epub/pdf/docx/txt）。</summary>
    Calibre,

    /// <summary>进程内轻量引擎（convert-lite，2026-09-20）：零外部 exe，替代 pandoc / LibreOffice / calibre / poppler 的文档与表格族；<b>恒可用</b>（可用性不再取决于磁盘上有没有第三方目录，这是本次迁移的目的）。</summary>
    Lite,
}
