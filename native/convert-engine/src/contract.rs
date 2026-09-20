// 转换契约类型（与 packages/api/Convert/ 的 C# 契约一一对应；capabilities 输出 C# 枚举名供门面映射）。
use serde::Serialize;

/// 转换引擎种类（C# EngineKind 等价物；按矩阵指派主/兜底引擎）。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum EngineKind {
    /// LibreOffice soffice 子进程（Office 文档族互转 / 两跳中转的执行段）。
    Soffice,
    /// Office/WPS COM ExportAsFixedFormat（仅 pdf；soffice 缺失时的兜底）。
    ComPdf,
    /// 纯托管链（md→html/txt、结构化文本变换）——零外部依赖。
    Managed,
    /// 两跳中转（A→html→B；矩阵显式登记 hop=2）。
    TwoHop,
    /// 图片互转（Rust image crate）。
    Image,
    /// PDF 组合（lopdf）：图片合成 PDF / 多 PDF 合并 / 拆分。
    PdfCompose,
    /// Poppler 子进程（pdftoppm/pdftotext）：PDF→图片/文本。
    Poppler,
    /// pandoc 子进程：docx↔md、md→epub/pptx、文本系直转。
    Pandoc,
    /// FFmpeg 子进程：音视频容器/编码互转、抽音轨、图片→视频、tga 互转。
    Ffmpeg,
    /// PDF 文本提取（pdftotext + 托管 OOXML 生成）：pdf→docx/xlsx 文本降级。
    PdfText,
    /// Tesseract OCR 子进程：图片→识别文本。
    Tesseract,
    /// PDF 安全（lopdf）：加密/解密（密码经 stdin JSON 传入）。
    PdfSecurity,
    /// HEIC/HEIF/AVIF 输入（WIC 系统组件）。
    Heic,
    /// 相机 RAW 输入（dcraw/LibRaw 子进程）。
    Raw,
    /// MOBI/AZW 电子书源（calibre ebook-convert 子进程）。
    Calibre,
    /// 进程内轻量引擎（convert-lite，2026-09-20）：零外部 exe，替代 pandoc / LibreOffice /
    /// calibre / poppler 的文档与表格族；恒可用（可用性不再取决于磁盘上有没有第三方目录）。
    Lite,
}

impl EngineKind {
    /// C# 枚举名原文（门面层按此映射 EngineKind.TryParse）。
    pub fn as_str(&self) -> &'static str {
        match self {
            EngineKind::Soffice => "Soffice",
            EngineKind::ComPdf => "ComPdf",
            EngineKind::Managed => "Managed",
            EngineKind::TwoHop => "TwoHop",
            EngineKind::Image => "Image",
            EngineKind::PdfCompose => "PdfCompose",
            EngineKind::Poppler => "Poppler",
            EngineKind::Pandoc => "Pandoc",
            EngineKind::Ffmpeg => "Ffmpeg",
            EngineKind::Lite => "Lite",
            EngineKind::PdfText => "PdfText",
            EngineKind::Tesseract => "Tesseract",
            EngineKind::PdfSecurity => "PdfSecurity",
            EngineKind::Heic => "Heic",
            EngineKind::Raw => "Raw",
            EngineKind::Calibre => "Calibre",
        }
    }
}

/// 目标类别（菜单分组依据；C# TargetCategory 等价物）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TargetCategory {
    Text,
    Document,
    Spreadsheet,
    Image,
    Audio,
    Video,
    Other,
}

impl TargetCategory {
    pub fn as_str(&self) -> &'static str {
        match self {
            TargetCategory::Text => "Text",
            TargetCategory::Document => "Document",
            TargetCategory::Spreadsheet => "Spreadsheet",
            TargetCategory::Image => "Image",
            TargetCategory::Audio => "Audio",
            TargetCategory::Video => "Video",
            TargetCategory::Other => "Other",
        }
    }
}

/// 目标格式描述（C# ConversionTarget 等价物）。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ConversionTarget {
    /// 目标扩展名（小写、不带点，如 "pdf"）。
    pub format: String,
    /// 菜单显示文本（中文，含格式名）。
    pub label: String,
    /// soffice 输出 filter（仅 Soffice/TwoHop 段消费；None = 用 Format 短名）。
    pub filter: Option<String>,
    /// 跳数（1 = 直连；2 = 矩阵显式登记的两跳链）。
    pub hops: u8,
    /// 首选引擎。
    pub prefer: EngineKind,
    /// 兜底引擎（首选缺失时自动切换）。
    pub fallback: Option<EngineKind>,
    /// 目标类别（菜单分组依据）。
    pub category: TargetCategory,
    /// 无损转换标记（无损=菜单高亮；有损=常规显示+风险确认；系统级联只注册无损项）。
    pub lossless: bool,
}

impl ConversionTarget {
    /// 多输入操作标记（Filter 承载操作语义；仅 PdfCompose 类目标使用）。
    pub const MERGE_PDF_MARKER: &'static str = "pdf-merge";
    pub const COMPOSE_PDF_MARKER: &'static str = "pdf-compose";
    pub const SPLIT_PDF_MARKER: &'static str = "pdf-split";
    pub const ENCRYPT_PDF_MARKER: &'static str = "pdf-encrypt";
    pub const DECRYPT_PDF_MARKER: &'static str = "pdf-decrypt";
    /// MP4 编码选择（Filter 承载；H.264 / H.265 / AV1）。
    pub const VIDEO_ENC_H264_MARKER: &'static str = "enc-h264";
    pub const VIDEO_ENC_H265_MARKER: &'static str = "enc-h265";
    pub const VIDEO_ENC_AV1_MARKER: &'static str = "enc-av1";
}

/// capabilities 输出用的目标序列化结构（枚举以 C# 名字符串输出）。
#[derive(Serialize)]
pub struct TargetJson<'a> {
    pub format: &'a str,
    pub label: &'a str,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub filter: Option<&'a str>,
    pub hops: u8,
    pub prefer: &'a str,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub fallback: Option<&'a str>,
    pub category: &'a str,
    pub lossless: bool,
}

impl<'a> From<&'a ConversionTarget> for TargetJson<'a> {
    fn from(t: &'a ConversionTarget) -> Self {
        TargetJson {
            format: &t.format,
            label: &t.label,
            filter: t.filter.as_deref(),
            hops: t.hops,
            prefer: t.prefer.as_str(),
            fallback: t.fallback.map(|f| f.as_str()),
            category: t.category.as_str(),
            lossless: t.lossless,
        }
    }
}
