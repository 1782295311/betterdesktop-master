// 转换矩阵（C# ConversionMatrix.cs 等价物；hub-spoke 拓扑：新增格式只改表）。
// 数据逐行对照 packages/shell/shell-convert/Services/ConversionMatrix.cs 移植 [verified]。
use std::collections::HashMap;

use crate::constants::{CSV_UTF8_FILTER, TXT_UTF8_FILTER};
use crate::contract::{ConversionTarget, EngineKind, TargetCategory};

/// 图片扩展名全集（webp 目标能否出现由 Image 引擎能力判定）。
pub const IMAGE_EXTENSIONS: [&str; 10] = [
    ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".ico", ".tga",
];

/// HEIC/HEIF/AVIF 输入扩展（WIC，C# HeicEngine.HeicExtensions [verified]）。
pub const HEIC_EXTENSIONS: [&str; 3] = [".heic", ".heif", ".avif"];

/// 相机 RAW 19 种（dcraw/LibRaw，C# RawDecodeEngine.RawExtensions [verified]）。
pub const RAW_EXTENSIONS: [&str; 19] = [
    ".cr2", ".cr3", ".crw", ".nef", ".arw", ".dng", ".raf", ".rw2", ".orf", ".pef", ".srw",
    ".3fr", ".erf", ".fff", ".iiq", ".kdc", ".mef", ".mrw", ".x3f",
];

/// MOBI/AZW 电子书源（calibre，C# CalibreEngine.MobiExtensions [verified]）。
pub const MOBI_EXTENSIONS: [&str; 4] = [".mobi", ".azw", ".azw3", ".prc"];

/// 转换矩阵（行保插入序；查询 O(1)）。
pub struct ConversionMatrix {
    rows: HashMap<String, Vec<ConversionTarget>>,
    order: Vec<String>,
}

impl ConversionMatrix {
    /// 查询某扩展名的全部可转目标（扩展名大小写不敏感；未登记返回空）。
    pub fn get_targets(&self, extension: &str) -> &[ConversionTarget] {
        self.rows
            .get(&extension.to_lowercase())
            .map(|v| v.as_slice())
            .unwrap_or(&[])
    }

    /// 查询精确目标（源扩展 + 目标格式）。
    pub fn find(&self, extension: &str, format: &str) -> Option<&ConversionTarget> {
        self.get_targets(extension)
            .iter()
            .find(|t| t.format == format)
    }

    /// 是否可转换（右键菜单整项显隐的快速判定）。
    pub fn is_convertible(&self, extension: &str) -> bool {
        !self.get_targets(extension).is_empty()
    }

    /// 全部已登记可转输入扩展名（系统右键级联注册用；保持登记顺序）。
    pub fn all_input_extensions(&self) -> Vec<String> {
        self.order.clone()
    }

    /// 全部选中项是否同属图片。
    pub fn all_images(paths: &[String]) -> bool {
        !paths.is_empty() && paths.iter().all(|p| {
            let ext = std::path::Path::new(p)
                .extension()
                .map(|e| format!(".{}", e.to_string_lossy().to_lowercase()))
                .unwrap_or_default();
            IMAGE_EXTENSIONS.contains(&ext.as_str())
        })
    }

    /// 全部选中项是否同属 pdf。
    pub fn all_pdf(paths: &[String]) -> bool {
        !paths.is_empty() && paths.iter().all(|p| {
            std::path::Path::new(p)
                .extension()
                .map(|e| e.eq_ignore_ascii_case("pdf"))
                .unwrap_or(false)
        })
    }
}

impl Default for ConversionMatrix {
    fn default() -> Self {
        Self::build()
    }
}

impl ConversionMatrix {
    fn build() -> Self {
        let mut rows: HashMap<String, Vec<ConversionTarget>> = HashMap::new();
        let mut order: Vec<String> = Vec::new();

        // Word 族（2026-09-10 架构主线：pandoc 主 + soffice/COM 辅——LibreOffice 26.8.0 内置回归）。
        for ext in [".doc", ".docx", ".docm", ".rtf", ".odt", ".wps", ".wpt", ".wpd"] {
            let mut targets = vec![pdf_via_soffice()];
            if ext != ".docx" {
                let prefer = if matches!(ext, ".docx" | ".rtf" | ".odt") {
                    EngineKind::Pandoc
                } else {
                    EngineKind::Soffice
                };
                targets.push(to("docx", "Word (.docx)", 1, prefer, None, TargetCategory::Document, true));
            }
            if ext != ".odt" {
                let prefer = if matches!(ext, ".docx" | ".rtf") {
                    EngineKind::Pandoc
                } else {
                    EngineKind::Soffice
                };
                targets.push(to("odt", "ODT (.odt)", 1, prefer, None, TargetCategory::Text, true));
            }
            if ext != ".rtf" {
                let prefer = if matches!(ext, ".docx" | ".odt") {
                    EngineKind::Pandoc
                } else {
                    EngineKind::Soffice
                };
                targets.push(to("rtf", "RTF (.rtf)", 1, prefer, None, TargetCategory::Text, true));
            }
            targets.push(to("txt", "纯文本 (.txt)", 1, EngineKind::Soffice, Some(TXT_UTF8_FILTER), TargetCategory::Text, true));
            targets.push(to("html", "网页 (.html)", 1, EngineKind::Soffice, None, TargetCategory::Text, true));
            targets.push(to("epub", "EPUB (.epub)", 1, EngineKind::Pandoc, None, TargetCategory::Document, true));
            // 文本类近善近全：Word 族 → md（docx/odt/rtf pandoc 高质量直转；老格式 soffice→html→md 两跳）
            if matches!(ext, ".docx" | ".odt" | ".rtf") {
                targets.push(to("md", "Markdown (.md)", 1, EngineKind::Pandoc, None, TargetCategory::Text, true));
            } else {
                targets.push(ConversionTarget {
                    format: "md".into(),
                    label: "Markdown (.md)".into(),
                    filter: None,
                    hops: 1,
                    prefer: EngineKind::TwoHop,
                    fallback: None,
                    category: TargetCategory::Text,
                    lossless: false, // 两跳样式有损
                });
            }
            // pandoc 文本系扩展：docx/odt/rtf 直读，语义保真=无损（老格式 soffice 无此能力）
            if matches!(ext, ".docx" | ".odt" | ".rtf") {
                for t in pandoc_text_targets() {
                    if t.format != "rtf" && t.format != "odt" {
                        targets.push(t);
                    }
                }
            }
            push_row(&mut rows, &mut order, ext, targets);
        }

        // Excel 族（LibreOffice 26.8.0 内置回归——xlsx/ods/csv 由 soffice 直转；pdf soffice→COM 兜底）
        for ext in [".xls", ".xlsx", ".xlsm", ".et", ".ett"] {
            let mut targets = vec![pdf_via_soffice()];
            if ext != ".xlsx" {
                targets.push(to("xlsx", "Excel (.xlsx)", 1, EngineKind::Soffice, None, TargetCategory::Spreadsheet, true));
            }
            targets.push(to("ods", "ODS (.ods)", 1, EngineKind::Soffice, None, TargetCategory::Spreadsheet, true));
            targets.push(to("csv", "CSV (.csv)", 1, EngineKind::Soffice, Some(CSV_UTF8_FILTER), TargetCategory::Spreadsheet, true));
            push_row(&mut rows, &mut order, ext, targets);
        }

        // .pptx 源（pandoc 3.x 原生 pptx reader → 文本系全列；pdf soffice→COM；png/jpg 两跳）
        {
            let mut targets = vec![
                pdf_via_soffice(),
                to("odp", "ODP (.odp)", 1, EngineKind::Soffice, None, TargetCategory::Document, true),
                to("html", "网页 (.html)", 1, EngineKind::Pandoc, None, TargetCategory::Text, true),
                to("md", "Markdown (.md)", 1, EngineKind::Pandoc, None, TargetCategory::Text, true),
                to("txt", "纯文本 (.txt)", 1, EngineKind::Pandoc, None, TargetCategory::Text, true),
                to("docx", "Word (.docx)", 1, EngineKind::Pandoc, None, TargetCategory::Document, true),
                to("epub", "EPUB (.epub)", 1, EngineKind::Pandoc, None, TargetCategory::Document, true),
                ConversionTarget {
                    format: "png".into(),
                    label: "PNG 图片 (.png)".into(),
                    filter: None,
                    hops: 2,
                    prefer: EngineKind::TwoHop,
                    fallback: None,
                    category: TargetCategory::Image,
                    lossless: true,
                },
                ConversionTarget {
                    format: "jpg".into(),
                    label: "JPG 图片 (.jpg)".into(),
                    filter: None,
                    hops: 2,
                    prefer: EngineKind::TwoHop,
                    fallback: None,
                    category: TargetCategory::Image,
                    lossless: false, // 重编码
                },
            ];
            targets.extend(pandoc_text_targets());
            push_row(&mut rows, &mut order, ".pptx", targets);
        }

        // 老演示格式（.ppt/.pps/.dps/.dpt）：无 pandoc reader → soffice 直转；pdf soffice→COM；png/jpg 两跳
        for ext in [".ppt", ".pps", ".dps", ".dpt"] {
            let targets = vec![
                pdf_via_soffice(),
                to("pptx", "PowerPoint (.pptx)", 1, EngineKind::Soffice, None, TargetCategory::Document, true),
                to("odp", "ODP (.odp)", 1, EngineKind::Soffice, None, TargetCategory::Document, true),
                to("html", "网页 (.html)", 1, EngineKind::Soffice, None, TargetCategory::Text, true),
                ConversionTarget {
                    format: "png".into(),
                    label: "PNG 图片 (.png)".into(),
                    filter: None,
                    hops: 2,
                    prefer: EngineKind::TwoHop,
                    fallback: None,
                    category: TargetCategory::Image,
                    lossless: true,
                },
                ConversionTarget {
                    format: "jpg".into(),
                    label: "JPG 图片 (.jpg)".into(),
                    filter: None,
                    hops: 2,
                    prefer: EngineKind::TwoHop,
                    fallback: None,
                    category: TargetCategory::Image,
                    lossless: false, // 重编码
                },
            ];
            push_row(&mut rows, &mut order, ext, targets);
        }

        // Markdown（枢纽 IR）：html/txt 纯托管；docx 主=pandoc 兜底=两跳；pdf=两跳
        {
            let mut targets = vec![
                to("html", "网页 (.html)", 1, EngineKind::Managed, None, TargetCategory::Text, true),
                to("txt", "纯文本 (.txt)", 1, EngineKind::Managed, None, TargetCategory::Text, true),
                to("epub", "EPUB (.epub)", 1, EngineKind::Pandoc, None, TargetCategory::Document, true),
                to("pptx", "PowerPoint (.pptx)", 1, EngineKind::Pandoc, None, TargetCategory::Document, true),
                ConversionTarget {
                    format: "docx".into(),
                    label: "Word (.docx)".into(),
                    filter: None,
                    hops: 2,
                    prefer: EngineKind::Pandoc,
                    fallback: Some(EngineKind::TwoHop),
                    category: TargetCategory::Document,
                    lossless: true,
                },
                ConversionTarget {
                    format: "pdf".into(),
                    label: "PDF 文档".into(),
                    filter: Some("pdf".into()),
                    hops: 2,
                    prefer: EngineKind::TwoHop,
                    fallback: None,
                    category: TargetCategory::Document,
                    lossless: false, // 两跳样式有损
                },
                to("csv", "CSV 表格 (.csv)", 1, EngineKind::Managed, None, TargetCategory::Spreadsheet, true),
                to("tsv", "TSV 表格 (.tsv)", 1, EngineKind::Managed, None, TargetCategory::Spreadsheet, true),
            ];
            targets.extend(pandoc_text_targets());
            push_row(&mut rows, &mut order, ".md", targets);
        }

        // 纯文本：md（轻包装）/ html（纯托管）/ pdf（soffice→COM）/ epub（pandoc）+ pandoc 文本系扩展
        let txt_targets = {
            let mut targets = vec![
                to("md", "Markdown (.md)", 1, EngineKind::Managed, None, TargetCategory::Text, true),
                to("html", "网页 (.html)", 1, EngineKind::Managed, None, TargetCategory::Text, true),
                pdf_via_soffice(),
                to("epub", "EPUB (.epub)", 1, EngineKind::Pandoc, None, TargetCategory::Document, true),
            ];
            targets.extend(pandoc_text_targets());
            targets
        };
        push_row(&mut rows, &mut order, ".txt", txt_targets.clone());
        push_row(&mut rows, &mut order, ".log", txt_targets);

        // JSON/XML（结构化文本，2026-09-07 补全）
        push_row(&mut rows, &mut order, ".json", vec![
            to("md", "Markdown (.md)", 1, EngineKind::Managed, None, TargetCategory::Text, true),
            to("html", "网页 (.html)", 1, EngineKind::Managed, None, TargetCategory::Text, true),
            to("txt", "纯文本 (.txt)", 1, EngineKind::Managed, None, TargetCategory::Text, true),
            to("yaml", "YAML (.yaml)", 1, EngineKind::Managed, None, TargetCategory::Text, true),
            to("csv", "CSV 表格 (.csv)", 1, EngineKind::Managed, None, TargetCategory::Spreadsheet, true),
            to("tsv", "TSV 表格 (.tsv)", 1, EngineKind::Managed, None, TargetCategory::Spreadsheet, true),
        ]);
        push_row(&mut rows, &mut order, ".xml", vec![
            to("md", "Markdown (.md)", 1, EngineKind::Managed, None, TargetCategory::Text, true),
            to("html", "网页 (.html)", 1, EngineKind::Managed, None, TargetCategory::Text, true),
            to("txt", "纯文本 (.txt)", 1, EngineKind::Managed, None, TargetCategory::Text, true),
            to("json", "JSON (.json)", 1, EngineKind::Managed, None, TargetCategory::Text, true),
            to("yaml", "YAML (.yaml)", 1, EngineKind::Managed, None, TargetCategory::Text, true),
            pdf_via_soffice(),
        ]);

        // CSV：表格回填 xlsx/ods/pdf（soffice）+ 文本三态 md/txt/html（Managed）
        let csv_targets = vec![
            to("xlsx", "Excel (.xlsx)", 1, EngineKind::Soffice, None, TargetCategory::Spreadsheet, true),
            to("ods", "ODS (.ods)", 1, EngineKind::Soffice, None, TargetCategory::Spreadsheet, true),
            pdf_via_soffice(),
            to("md", "Markdown (.md)", 1, EngineKind::Managed, None, TargetCategory::Text, true),
            to("txt", "纯文本 (.txt)", 1, EngineKind::Managed, None, TargetCategory::Text, true),
            to("html", "网页 (.html)", 1, EngineKind::Managed, None, TargetCategory::Text, true),
            to("json", "JSON (.json)", 1, EngineKind::Managed, None, TargetCategory::Text, true),
            to("yaml", "YAML (.yaml)", 1, EngineKind::Managed, None, TargetCategory::Text, true),
        ];
        push_row(&mut rows, &mut order, ".csv", csv_targets.clone());
        push_row(&mut rows, &mut order, ".tsv", csv_targets); // TSV 同 csv

        // YAML（2026-09-07 补全）
        let yaml_targets = vec![
            to("md", "Markdown (.md)", 1, EngineKind::Managed, None, TargetCategory::Text, true),
            to("html", "网页 (.html)", 1, EngineKind::Managed, None, TargetCategory::Text, true),
            to("txt", "纯文本 (.txt)", 1, EngineKind::Managed, None, TargetCategory::Text, true),
            to("json", "JSON (.json)", 1, EngineKind::Managed, None, TargetCategory::Text, true),
            to("csv", "CSV 表格 (.csv)", 1, EngineKind::Managed, None, TargetCategory::Spreadsheet, true),
            to("tsv", "TSV 表格 (.tsv)", 1, EngineKind::Managed, None, TargetCategory::Spreadsheet, true),
        ];
        push_row(&mut rows, &mut order, ".yaml", yaml_targets.clone());
        push_row(&mut rows, &mut order, ".yml", yaml_targets);

        // .markdown 与 .md 等价（2026-09-07 补全）
        let md_row = rows[".md"].clone();
        push_row(&mut rows, &mut order, ".markdown", md_row);

        // EPUB 源（2026-09-10 收尾：docx/txt/html/md 全改 pandoc 原生 reader；pdf 改 TwoHop）
        {
            let mut targets = vec![
                ConversionTarget {
                    format: "pdf".into(),
                    label: "PDF 文档".into(),
                    filter: Some("pdf".into()),
                    hops: 2,
                    prefer: EngineKind::TwoHop,
                    fallback: None,
                    category: TargetCategory::Document,
                    lossless: true, // 渲染保真（文字可复制）= 无损；中间态透明
                },
                to("docx", "Word (.docx)", 1, EngineKind::Pandoc, None, TargetCategory::Document, true),
                to("txt", "纯文本 (.txt)", 1, EngineKind::Pandoc, None, TargetCategory::Text, true),
                to("html", "网页 (.html)", 1, EngineKind::Pandoc, None, TargetCategory::Text, true),
                to("md", "Markdown (.md)", 1, EngineKind::Pandoc, None, TargetCategory::Text, true),
            ];
            targets.extend(pandoc_text_targets());
            push_row(&mut rows, &mut order, ".epub", targets);
        }

        // HTML：md（托管）/ pdf（soffice→COM）/ epub（pandoc）+ pandoc 文本系扩展
        let html_targets = {
            let mut targets = vec![
                to("md", "Markdown (.md)", 1, EngineKind::Managed, None, TargetCategory::Text, true),
                pdf_via_soffice(),
                to("epub", "EPUB (.epub)", 1, EngineKind::Pandoc, None, TargetCategory::Document, true),
            ];
            targets.extend(pandoc_text_targets());
            targets
        };
        push_row(&mut rows, &mut order, ".html", html_targets.clone());
        push_row(&mut rows, &mut order, ".htm", html_targets);

        // 图片：互转 + 合成 pdf + AVIF + OCR
        for ext in IMAGE_EXTENSIONS {
            let mut targets: Vec<ConversionTarget> = Vec::new();
            for target in IMAGE_EXTENSIONS {
                if target.eq_ignore_ascii_case(ext) {
                    continue;
                }
                if target == ".jpeg" && matches!(ext, ".jpg" | ".jpeg") {
                    continue; // 同义格式去重
                }
                let format = if target == ".jpeg" { "jpg" } else { target.trim_start_matches('.') };
                if targets.iter().any(|t| t.format == format) {
                    continue;
                }
                let engine = if ext == ".tga" || format == "tga" {
                    EngineKind::Ffmpeg
                } else {
                    EngineKind::Image
                };
                targets.push(to(
                    format,
                    label_of(format),
                    1,
                    engine,
                    None,
                    TargetCategory::Image,
                    is_lossless_image_format(format),
                ));
            }
            // 图像合成 PDF = 无损封装
            targets.push(ConversionTarget {
                format: "pdf".into(),
                label: "PDF 文档".into(),
                filter: Some(ConversionTarget::COMPOSE_PDF_MARKER.into()),
                hops: 1,
                prefer: EngineKind::PdfCompose,
                fallback: None,
                category: TargetCategory::Document,
                lossless: true,
            });
            // AVIF 输出（ffmpeg libaom-av1）
            targets.push(to("avif", "AVIF (.avif)", 1, EngineKind::Ffmpeg, None, TargetCategory::Image, false));
            // OCR：图片 → 识别文本（tesseract；识别=有损提取）
            targets.push(to("txt", "识别文本 (OCR) (.txt)", 1, EngineKind::Tesseract, None, TargetCategory::Text, false));
            push_row(&mut rows, &mut order, ext, targets);
        }

        // HEIC/HEIF/AVIF 输入（WIC）
        for ext in HEIC_EXTENSIONS {
            push_row(&mut rows, &mut order, ext, image_input_targets(EngineKind::Heic));
        }

        // 相机 RAW 19 种（dcraw/LibRaw）
        for ext in RAW_EXTENSIONS {
            push_row(&mut rows, &mut order, ext, image_input_targets(EngineKind::Raw));
        }

        // MOBI/AZW 电子书源（calibre）
        for ext in MOBI_EXTENSIONS {
            push_row(&mut rows, &mut order, ext, vec![
                to("epub", "EPUB (.epub)", 1, EngineKind::Calibre, None, TargetCategory::Document, true),
                to("pdf", "PDF 文档", 1, EngineKind::Calibre, Some("pdf".into()), TargetCategory::Document, true),
                to("docx", "Word (.docx)", 1, EngineKind::Calibre, None, TargetCategory::Document, true),
                to("txt", "纯文本 (.txt)", 1, EngineKind::Calibre, None, TargetCategory::Text, true),
                to("html", "网页 (.html)", 1, EngineKind::Calibre, None, TargetCategory::Text, true),
            ]);
        }

        // PDF：→图片/文本（Poppler）+ docx/xlsx（PdfText 文本提取降级）。加密/解密见特殊目标。
        push_row(&mut rows, &mut order, ".pdf", vec![
            to("png", "PNG (.png)", 1, EngineKind::Poppler, None, TargetCategory::Image, false),
            to("jpg", "JPG (.jpg)", 1, EngineKind::Poppler, None, TargetCategory::Image, false),
            to("tiff", "TIFF (.tiff)", 1, EngineKind::Poppler, None, TargetCategory::Image, false),
            to("txt", "纯文本 (.txt)", 1, EngineKind::PdfText, None, TargetCategory::Text, false),
            to("md", "Markdown (.md)（文本提取）", 1, EngineKind::PdfText, None, TargetCategory::Text, false),
            to("html", "网页 (.html)（文本提取）", 1, EngineKind::PdfText, None, TargetCategory::Text, false),
            ConversionTarget {
                format: "docx".into(),
                label: "Word (.docx)（文本提取）".into(),
                filter: None,
                hops: 1,
                prefer: EngineKind::PdfText,
                fallback: None,
                category: TargetCategory::Document,
                lossless: false,
            },
            ConversionTarget {
                format: "xlsx".into(),
                label: "Excel (.xlsx)（文本提取）".into(),
                filter: None,
                hops: 1,
                prefer: EngineKind::PdfText,
                fallback: None,
                category: TargetCategory::Spreadsheet,
                lossless: false,
            },
            ConversionTarget {
                format: "epub".into(),
                label: "电子书 (.epub)（文本提取）".into(),
                filter: None,
                hops: 1,
                prefer: EngineKind::PdfText,
                fallback: None,
                category: TargetCategory::Document,
                lossless: false,
            },
            ConversionTarget {
                format: "odt".into(),
                label: "OpenDocument (.odt)（文本提取）".into(),
                filter: None,
                hops: 1,
                prefer: EngineKind::PdfText,
                fallback: None,
                category: TargetCategory::Document,
                lossless: false,
            },
            ConversionTarget {
                format: "rtf".into(),
                label: "RTF (.rtf)（文本提取）".into(),
                filter: None,
                hops: 1,
                prefer: EngineKind::PdfText,
                fallback: None,
                category: TargetCategory::Document,
                lossless: false,
            },
            ConversionTarget {
                format: "opendocument".into(),
                label: "OpenDocument XML (.xml)（文本提取）".into(),
                filter: None,
                hops: 1,
                prefer: EngineKind::PdfText,
                fallback: None,
                category: TargetCategory::Document,
                lossless: false,
            },
        ]);

        // 视频（FFmpeg）：容器/编码互转 + 抽音轨 + GIF
        for ext in [".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v"] {
            let mut targets = vec![
                ConversionTarget {
                    format: "mp4".into(),
                    label: "MP4 (H.264)".into(),
                    filter: Some(ConversionTarget::VIDEO_ENC_H264_MARKER.into()),
                    hops: 1,
                    prefer: EngineKind::Ffmpeg,
                    fallback: None,
                    category: TargetCategory::Video,
                    lossless: false,
                },
                ConversionTarget {
                    format: "mp4".into(),
                    label: "MP4 (H.265/HEVC)".into(),
                    filter: Some(ConversionTarget::VIDEO_ENC_H265_MARKER.into()),
                    hops: 1,
                    prefer: EngineKind::Ffmpeg,
                    fallback: None,
                    category: TargetCategory::Video,
                    lossless: false,
                },
                ConversionTarget {
                    format: "mp4".into(),
                    label: "MP4 (AV1)".into(),
                    filter: Some(ConversionTarget::VIDEO_ENC_AV1_MARKER.into()),
                    hops: 1,
                    prefer: EngineKind::Ffmpeg,
                    fallback: None,
                    category: TargetCategory::Video,
                    lossless: false,
                },
            ];
            for format in ["mkv", "webm", "mov", "gif"] {
                if format!(".{format}") == ext {
                    continue;
                }
                // mkv/mov = 容器 remux（-c copy 不重编码，无损）；webm/gif = 重编码（有损）
                targets.push(to(
                    format,
                    label_of(format),
                    1,
                    EngineKind::Ffmpeg,
                    None,
                    TargetCategory::Video,
                    format == "mkv" || format == "mov",
                ));
            }
            targets.push(to("mp3", "音频 MP3 (.mp3)（抽音轨）", 1, EngineKind::Ffmpeg, None, TargetCategory::Audio, false));
            push_row(&mut rows, &mut order, ext, targets);
        }

        // 音频（FFmpeg）：编码互转
        for ext in [".mp3", ".wav", ".flac", ".m4a", ".ogg", ".ape", ".aac", ".opus", ".wma"] {
            let mut targets = Vec::new();
            for format in ["mp3", "wav", "flac", "m4a", "aac", "opus", "wma"] {
                if format!(".{format}") == ext {
                    continue;
                }
                targets.push(to(
                    format,
                    label_of(format),
                    1,
                    EngineKind::Ffmpeg,
                    None,
                    TargetCategory::Audio,
                    format == "wav" || format == "flac",
                ));
            }
            push_row(&mut rows, &mut order, ext, targets);
        }

        // 2026-09-20 路由收口（convert-lite 迁移 C3）：把 lite 能做的边改派给 Lite。
        route_through_lite(&mut rows, &mut order);

        Self { rows, order }
    }
}

/// 把"lite 能做的边"改派给 [`EngineKind::Lite`]，并摘掉音视频目标。
///
/// 【为什么不逐行改表】矩阵有几百条边、跨约二十个格式族。逐行改的真正代价不是工作量而是**一致性**：
/// 漏一行就留下一条指向已删引擎的边（`EngineMissing` ⇒ 菜单整项隐藏，用户看到的是"功能少了"），
/// 而且两侧矩阵（本文件 + `ConversionMatrix.cs`）会各漏一次。改成一道路由 pass 之后，
/// "哪些边归 lite"由 [`crate::lite::supports`] **一处**回答 —— 它同时也是 `LiteEngine::can_handle`
/// 的判据，于是路由与执行能力不可能互相漂移。
///
/// 【只收编"外部引擎的边"】`Image` / `PdfCompose` / `PdfSecurity` / `Heic` / `Raw` / `Tesseract` /
/// `Poppler` 各有专职实现，**不许被 lite 抢走**：`Poppler`（PDF）与 `Tesseract`（OCR）正是用户
/// 点名要保留的两棵树，抢走它们等于顺手删功能；`Image`/`PdfCompose`/`PdfSecurity` 是纯托管实现，
/// 与 engines/ 无关，换掉只会白白丢能力。
fn route_through_lite(
    rows: &mut HashMap<String, Vec<ConversionTarget>>,
    order: &mut Vec<String>,
) {
    // 迭代用快照：下面第 (3) 步要 `order.retain(...)` 改它，直接 `order.iter()` 会与可变借用冲突
    // （编译错误 E0502）。order 只有几十项，克隆成本可忽略。
    let exts: Vec<String> = order.clone();
    for ext in exts.iter() {
        let Some(targets) = rows.get_mut(ext) else { continue };
        let in_ext = ext.trim_start_matches('.').to_ascii_lowercase();

        // (1) 音视频：本轮不带 FFmpeg 依赖（计划 §7 D3①）⇒ 摘掉目标，菜单项自然消失。
        //     注意 engines/ffmpeg 目录与 FfmpegEngine 都**保留**（用户点名要留），
        //     将来恢复只需删掉这一句。
        //
        //     判据用**类别**（Audio/Video）而不是"引擎 == Ffmpeg"：后者会连 `png → tga`
        //     这类**图片**边一起摘掉（tga 编解码也走 ffmpeg）—— 那是能力损失，不是"隐藏音视频"。
        //     这条正是被旧测试 `image_interconversion_and_special_targets`（"png 行缺少 tga"）抓出来的。
        targets.retain(|t| !matches!(t.category, TargetCategory::Audio | TargetCategory::Video));

        // (2) lite 能做的边 → 改派 Lite：prefer=Lite、fallback=None（不做双路径）。
        for t in targets.iter_mut() {
            let owned_by_external = matches!(
                t.prefer,
                EngineKind::Pandoc
                    | EngineKind::Soffice
                    | EngineKind::Calibre
                    | EngineKind::TwoHop
                    | EngineKind::ComPdf
            );
            if !owned_by_external {
                continue;
            }
            if !crate::lite::supports(&in_ext, &t.format) {
                // lite 做不了的边（如 .wps/.wpt/.wpd 这类 lite 没有 reader 的源）保持原样：
                // 引擎树被删后它们会 EngineMissing ⇒ 菜单隐藏。这是**诚实的**能力缩减，
                // 不伪装成"能转"（红线：引擎缺失禁止伪装成转换失败）。
                continue;
            }
            t.prefer = EngineKind::Lite;
            t.fallback = None;
        }

        // (3) 摘掉空行：目标被摘光的源（本轮 = 音视频源）不该再出现在 `all_input_extensions()` 里，
        //     否则"系统右键级联注册"会为它建一个没有任何子项的空键，而菜单里又什么都没有。
        let empty: Vec<String> = order
            .iter()
            .filter(|e| rows.get(&e.to_lowercase()).map(|v| v.is_empty()).unwrap_or(false))
            .cloned()
            .collect();
        for e in &empty {
            rows.remove(&e.to_lowercase());
        }
        order.retain(|e| !empty.contains(e));
    }
}

/// 多输入合并目标（全部为 pdf 且 ≥2 时可合并为一个 pdf）。
pub fn merge_pdf() -> ConversionTarget {
    ConversionTarget {
        format: "pdf".into(),
        label: "合并 PDF".into(),
        filter: Some(ConversionTarget::MERGE_PDF_MARKER.into()),
        hops: 1,
        prefer: EngineKind::PdfCompose,
        fallback: None,
        category: TargetCategory::Document,
        lossless: true,
    }
}

/// 多输入合成目标（全部为图片且 ≥2 时合成一个多页 pdf）。
pub fn compose_pdf() -> ConversionTarget {
    ConversionTarget {
        format: "pdf".into(),
        label: "合成 PDF".into(),
        filter: Some(ConversionTarget::COMPOSE_PDF_MARKER.into()),
        hops: 1,
        prefer: EngineKind::PdfCompose,
        fallback: None,
        category: TargetCategory::Document,
        lossless: true,
    }
}

/// 拆分目标（单 pdf 每页一文件；低频项，菜单走 Shift 扩展位）。
pub fn split_pdf() -> ConversionTarget {
    ConversionTarget {
        format: "pdf".into(),
        label: "拆分 PDF".into(),
        filter: Some(ConversionTarget::SPLIT_PDF_MARKER.into()),
        hops: 1,
        prefer: EngineKind::PdfCompose,
        fallback: None,
        category: TargetCategory::Document,
        lossless: true,
    }
}

/// PDF 加密目标（密码经 stdin JSON 传入，不进 argv）。
pub fn encrypt_pdf() -> ConversionTarget {
    ConversionTarget {
        format: "pdf".into(),
        label: "加密 PDF".into(),
        filter: Some(ConversionTarget::ENCRYPT_PDF_MARKER.into()),
        hops: 1,
        prefer: EngineKind::PdfSecurity,
        fallback: None,
        category: TargetCategory::Other,
        lossless: true,
    }
}

/// PDF 解密目标（同上；解密需原密码）。
pub fn decrypt_pdf() -> ConversionTarget {
    ConversionTarget {
        format: "pdf".into(),
        label: "解密 PDF".into(),
        filter: Some(ConversionTarget::DECRYPT_PDF_MARKER.into()),
        hops: 1,
        prefer: EngineKind::PdfSecurity,
        fallback: None,
        category: TargetCategory::Other,
        lossless: true,
    }
}

// —— 内部辅助 ——

fn push_row(rows: &mut HashMap<String, Vec<ConversionTarget>>, order: &mut Vec<String>, ext: &str, targets: Vec<ConversionTarget>) {
    order.push(ext.to_string());
    rows.insert(ext.to_lowercase(), targets);
}

/// PDF 目标：soffice 直转 → COM 兜底（Office/WPS 渲染保真）。非 Office 源 soffice 可读 → pdf 可用。
fn pdf_via_soffice() -> ConversionTarget {
    ConversionTarget {
        format: "pdf".into(),
        label: "PDF 文档".into(),
        filter: Some("pdf".into()),
        hops: 1,
        prefer: EngineKind::Soffice,
        fallback: Some(EngineKind::ComPdf),
        category: TargetCategory::Document,
        lossless: true, // 渲染保真（文字可复制）= 无损
    }
}

/// 文本系直连目标（pandoc/Managed；文本互转语义保真 = 无损）。
fn to(
    format: &str,
    label: impl Into<String>,
    hops: u8,
    prefer: EngineKind,
    filter: Option<&str>,
    category: TargetCategory,
    lossless: bool,
) -> ConversionTarget {
    ConversionTarget {
        format: format.into(),
        label: label.into(),
        filter: filter.map(str::to_string),
        hops,
        prefer,
        fallback: None,
        category,
        lossless,
    }
}

/// HEIC/RAW 输入行的常规图片目标集（8 种；不含 pdf 合成）。
fn image_input_targets(kind: EngineKind) -> Vec<ConversionTarget> {
    ["png", "jpg", "bmp", "gif", "tif", "tiff", "webp", "ico"]
        .map(|format| {
            to(
                format,
                label_of(format),
                1,
                kind,
                None,
                TargetCategory::Image,
                is_lossless_image_format(format),
            )
        })
        .to_vec()
}

/// 图像无损格式判定（同族不重编码：png/bmp/tif/tiff/tga；jpg/gif/webp/ico/avif 重编码有损）。
fn is_lossless_image_format(format: &str) -> bool {
    matches!(format, "png" | "bmp" | "tif" | "tiff" | "tga")
}

/// pandoc 文本系扩展目标（15 个）：pandoc 直转，文本语义保真 = 无损。
fn pandoc_text_targets() -> Vec<ConversionTarget> {
    [
        ("rtf", "RTF (.rtf)"),
        ("odt", "ODT (.odt)"),
        ("tex", "LaTeX (.tex)"),
        ("rst", "reStructuredText (.rst)"),
        ("org", "Org-mode (.org)"),
        ("wiki", "MediaWiki (.wiki)"),
        ("adoc", "AsciiDoc (.adoc)"),
        ("textile", "Textile (.textile)"),
        ("ipynb", "Jupyter Notebook (.ipynb)"),
        ("db", "DocBook XML (.db)"),
        ("man", "Man page (.man)"),
        ("context", "ConTeXt (.context)"),
        ("texi", "Texinfo (.texi)"),
        ("opendocument", "OpenDocument XML (.opendocument)"),
        ("plain", "纯文本 (.plain)"),
    ]
    .map(|(format, label)| to(format, label, 1, EngineKind::Pandoc, None, TargetCategory::Text, true))
    .to_vec()
}

fn label_of(format: &str) -> String {
    match format {
        "png" => "PNG (.png)".into(),
        "jpg" => "JPG (.jpg)".into(),
        "bmp" => "BMP (.bmp)".into(),
        "gif" => "GIF (.gif)".into(),
        "tif" => "TIFF (.tif)".into(),
        "webp" => "WebP (.webp)".into(),
        "ico" => "ICO 图标 (.ico)".into(),
        "tga" => "TGA (.tga)".into(),
        _ => format!("{} (.{format})", format.to_uppercase()),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn matrix() -> ConversionMatrix {
        ConversionMatrix::default()
    }

    #[test]
    fn all_input_extensions_matches_csharp_order() {
        // C# BuildRows 的登记顺序（AllInputExtensions = Rows.Keys 枚举序）。
        let expected_prefix = [
            ".doc", ".docx", ".docm", ".rtf", ".odt", ".wps", ".wpt", ".wpd",
            ".xls", ".xlsx", ".xlsm", ".et", ".ett",
            ".pptx", ".ppt", ".pps", ".dps", ".dpt",
            ".md", ".txt", ".log", ".json", ".xml", ".csv", ".tsv", ".yaml", ".yml", ".markdown",
            ".epub", ".html", ".htm",
        ];
        let m = matrix();
        let exts = m.all_input_extensions();
        // 2026-09-20 迁移：音视频源被摘掉（D3①）⇒ 尾部不再有 +8（视频）/ +9（音频）两段。
        assert_eq!(exts.len(), expected_prefix.len() + IMAGE_EXTENSIONS.len() + HEIC_EXTENSIONS.len() + RAW_EXTENSIONS.len() + MOBI_EXTENSIONS.len() + 1);
        for (i, e) in expected_prefix.iter().enumerate() {
            assert_eq!(&exts[i], e, "第 {i} 个扩展登记顺序不一致");
        }
        // 图片/HEIC/RAW/MOBI/.pdf 在尾部（登记顺序一致）
        assert_eq!(&exts[expected_prefix.len()], ".png");
        assert!(exts.contains(&".pdf".to_string()));
        assert!(!exts.contains(&".mp4".to_string()), "音视频源应已摘掉（D3①）");
        assert!(!exts.contains(&".mp3".to_string()), "音视频源应已摘掉（D3①）");
    }

    #[test]
    fn word_family_docx_row() {
        let m = matrix();
        let t = m.get_targets(".DOCX");
        // pdf 第一；无 docx 自转；含 odt/rtf/txt/html/epub/md（pandoc）+ 文本系（除 rtf/odt）
        let formats: Vec<&str> = t.iter().map(|x| x.format.as_str()).collect();
        assert_eq!(formats[0], "pdf");
        assert!(!formats.contains(&"docx"));
        for f in ["odt", "rtf", "txt", "html", "epub", "md", "tex", "rst", "org"] {
            assert!(formats.contains(&f), "docx 行缺少 {f}");
        }
        // md 走 lite（原 pandoc；2026-09-20 迁移：纯 Rust 进程内直转）
        let md = t.iter().find(|x| x.format == "md").unwrap();
        assert_eq!(md.prefer, EngineKind::Lite);
        assert!(md.lossless);
        // pdf 走 lite（原 soffice 主 + COM 兜底；engines/libreoffice 已删）
        let pdf = &t[0];
        assert_eq!(pdf.prefer, EngineKind::Lite);
        assert_eq!(pdf.fallback, None);
        assert!(pdf.lossless);
        // txt 带 UTF8 filter（红线 6）
        let txt = t.iter().find(|x| x.format == "txt").unwrap();
        assert_eq!(txt.filter.as_deref(), Some(TXT_UTF8_FILTER));
    }

    #[test]
    fn legacy_word_md_is_twohop_lossy() {
        let m = matrix();
        let t = m.get_targets(".wps");
        let md = t.iter().find(|x| x.format == "md").unwrap();
        assert_eq!(md.prefer, EngineKind::TwoHop);
        assert!(!md.lossless);
        // 老格式无 pandoc 文本系扩展
        assert!(!t.iter().any(|x| x.format == "tex"));
    }

    #[test]
    fn excel_family_csv_filter() {
        let m = matrix();
        let t = m.get_targets(".xlsx");
        let csv = t.iter().find(|x| x.format == "csv").unwrap();
        assert_eq!(csv.filter.as_deref(), Some(CSV_UTF8_FILTER));
        assert_eq!(csv.category, TargetCategory::Spreadsheet);
        // xlsx 无自转
        assert!(!t.iter().any(|x| x.format == "xlsx"));
    }

    #[test]
    fn pdf_row_has_poppler_and_text_extraction() {
        let m = matrix();
        let t = m.get_targets(".pdf");
        let formats: Vec<&str> = t.iter().map(|x| x.format.as_str()).collect();
        for f in ["png", "jpg", "tiff", "txt", "md", "html", "docx", "xlsx", "epub", "odt", "rtf", "opendocument"] {
            assert!(formats.contains(&f), "pdf 行缺少 {f}");
        }
        let png = t.iter().find(|x| x.format == "png").unwrap();
        assert_eq!(png.prefer, EngineKind::Poppler);
        assert!(!png.lossless);
        let docx = t.iter().find(|x| x.format == "docx").unwrap();
        assert_eq!(docx.prefer, EngineKind::PdfText);
        assert!(!docx.lossless);
    }

    #[test]
    fn image_interconversion_and_special_targets() {
        let m = matrix();
        let t = m.get_targets(".png");
        let formats: Vec<&str> = t.iter().map(|x| x.format.as_str()).collect();
        // png 源：jpg/bmp/gif/tif/tiff/webp/ico/tga（无 png 自转、无 jpeg 归一）
        for f in ["jpg", "bmp", "gif", "tif", "tiff", "webp", "ico", "tga"] {
            assert!(formats.contains(&f), "png 行缺少 {f}");
        }
        assert!(!formats.contains(&"jpeg"));
        // pdf 合成 + avif + OCR
        let pdf = t.iter().find(|x| x.format == "pdf").unwrap();
        assert_eq!(pdf.filter.as_deref(), Some(ConversionTarget::COMPOSE_PDF_MARKER));
        assert_eq!(pdf.prefer, EngineKind::PdfCompose);
        assert!(formats.contains(&"avif"));
        let ocr = t.iter().find(|x| x.format == "txt").unwrap();
        assert_eq!(ocr.prefer, EngineKind::Tesseract);
        assert!(!ocr.lossless);
        // 无损规则：bmp/tif/tiff/tga 无损，jpg/webp 有损
        assert!(t.iter().find(|x| x.format == "bmp").unwrap().lossless);
        assert!(t.iter().find(|x| x.format == "tga").unwrap().lossless);
        assert!(!t.iter().find(|x| x.format == "jpg").unwrap().lossless);
        assert!(!t.iter().find(|x| x.format == "webp").unwrap().lossless);
    }

    #[test]
    fn tga_uses_ffmpeg_engine() {
        // tga 源/目标引擎指派 Ffmpeg（System.Drawing 不支持 tga）
        let m = matrix();
        let t = m.get_targets(".tga");
        assert!(t.iter().all(|x| {
            x.format == "pdf" || x.format == "avif" || x.format == "txt" || x.prefer == EngineKind::Ffmpeg
        }));
        let png = t.iter().find(|x| x.format == "png").unwrap();
        assert_eq!(png.prefer, EngineKind::Ffmpeg);
        // 其它图源转 tga 也用 ffmpeg
        let src = m.get_targets(".jpg");
        let tga = src.iter().find(|x| x.format == "tga").unwrap();
        assert_eq!(tga.prefer, EngineKind::Ffmpeg);
    }

    #[test]
    fn video_audio_rows_removed_in_this_round() {
        // 2026-09-20 迁移：本轮不带 FFmpeg 依赖（计划 §7 D3①）⇒ 音视频目标从矩阵摘掉、菜单项随之消失。
        // engines/ffmpeg 目录与 FfmpegEngine **都还留着** —— 恢复时删掉 route_through_lite 的第 (1) 步即可。
        let m = matrix();
        assert!(m.get_targets(".mp4").is_empty(), ".mp4 的转换目标应已摘掉");
        assert!(m.get_targets(".mp3").is_empty(), ".mp3 的转换目标应已摘掉");
        assert!(
            !m.all_input_extensions().iter().any(|e| e == ".mp4" || e == ".mp3"),
            "空行必须从 all_input_extensions 摘掉，否则右键级联会为一个没有子项的源建空键"
        );
        // 图片边不受影响：png→tga 仍走 ffmpeg（tga 编解码也在 ffmpeg 里，但它是图片、不是音视频）。
        // 这条正是旧测试 image_interconversion_and_special_targets（"png 行缺少 tga"）抓出的口径问题。
        let png = m.get_targets(".png");
        assert_eq!(
            png.iter().find(|x| x.format == "tga").unwrap().prefer,
            EngineKind::Ffmpeg
        );
    }

    #[test]
    fn markdown_row() {
        let m = matrix();
        let t = m.get_targets(".md");
        let formats: Vec<&str> = t.iter().map(|x| x.format.as_str()).collect();
        for f in ["html", "txt", "epub", "pptx", "docx", "pdf", "csv", "tsv", "tex", "ipynb"] {
            assert!(formats.contains(&f), "md 行缺少 {f}");
        }
        // md→html/txt 纯托管
        assert_eq!(t.iter().find(|x| x.format == "html").unwrap().prefer, EngineKind::Managed);
        // docx 走 lite（原 pandoc 主 + twohop 兜底；迁移后单跳直转，fallback 归 None）
        let docx = t.iter().find(|x| x.format == "docx").unwrap();
        assert_eq!(docx.prefer, EngineKind::Lite);
        assert_eq!(docx.fallback, None);
        assert_eq!(docx.hops, 2);
        // pdf 两跳有损
        let pdf = t.iter().find(|x| x.format == "pdf").unwrap();
        assert_eq!(pdf.hops, 2);
        assert!(!pdf.lossless);
    }

    #[test]
    fn special_targets_markers() {
        assert_eq!(merge_pdf().filter.as_deref(), Some("pdf-merge"));
        assert_eq!(compose_pdf().filter.as_deref(), Some("pdf-compose"));
        assert_eq!(split_pdf().filter.as_deref(), Some("pdf-split"));
        assert_eq!(encrypt_pdf().filter.as_deref(), Some("pdf-encrypt"));
        assert_eq!(decrypt_pdf().filter.as_deref(), Some("pdf-decrypt"));
        assert_eq!(encrypt_pdf().category, TargetCategory::Other);
        assert!(encrypt_pdf().lossless);
    }

    #[test]
    fn json_xml_yaml_rows() {
        let m = matrix();
        let j = m.get_targets(".json");
        assert!(j.iter().any(|x| x.format == "yaml"));
        assert!(j.iter().any(|x| x.format == "csv"));
        let x = m.get_targets(".xml");
        assert!(x.iter().any(|x| x.format == "json"));
        assert!(x.iter().any(|x| x.format == "pdf"));
        let y = m.get_targets(".yml");
        let ya = m.get_targets(".yaml");
        assert_eq!(y.len(), ya.len());
    }

    #[test]
    fn find_is_convertible() {
        let m = matrix();
        assert!(m.is_convertible(".docx"));
        assert!(!m.is_convertible(".xyz"));
        assert!(m.find(".PDF", "png").is_some());
        assert!(m.find(".pdf", "docx").is_some());
        assert!(m.find(".docx", "docx").is_none());
    }

    #[test]
    fn all_images_all_pdf() {
        assert!(ConversionMatrix::all_images(&["a.PNG".into(), "b.jpg".into()]));
        assert!(!ConversionMatrix::all_images(&["a.png".into(), "b.txt".into()]));
        assert!(ConversionMatrix::all_pdf(&["a.PDF".into(), "b.pdf".into()]));
        assert!(!ConversionMatrix::all_pdf(&["a.pdf".into(), "b.png".into()]));
        assert!(!ConversionMatrix::all_images(&[]));
    }

    #[test]
    fn epub_and_calibre_rows() {
        let m = matrix();
        let e = m.get_targets(".epub");
        let formats: Vec<&str> = e.iter().map(|x| x.format.as_str()).collect();
        for f in ["pdf", "docx", "txt", "html", "md"] {
            assert!(formats.contains(&f), "epub 行缺少 {f}");
        }
        let pdf = e.iter().find(|x| x.format == "pdf").unwrap();
        assert_eq!(pdf.hops, 2);
        assert_eq!(pdf.prefer, EngineKind::Lite); // 原 TwoHop（soffice→html→md）；lite 进程内 epub→md→pdf
        assert!(pdf.lossless);
        let mo = m.get_targets(".mobi");
        let mo_f: Vec<&str> = mo.iter().map(|x| x.format.as_str()).collect();
        for f in ["epub", "pdf", "docx", "txt", "html"] {
            assert!(mo_f.contains(&f), "mobi 行缺少 {f}");
        }
        assert!(mo.iter().all(|x| x.prefer == EngineKind::Lite)); // 原 Calibre（已删）
    }

    #[test]
    fn heic_raw_rows_8_image_targets() {
        let m = matrix();
        for ext in HEIC_EXTENSIONS {
            let t = m.get_targets(ext);
            assert_eq!(t.len(), 8, "{ext} 行应 8 个图片目标");
            assert!(t.iter().all(|x| x.prefer == EngineKind::Heic));
        }
        for ext in [".cr2", ".nef", ".dng"] {
            let t = m.get_targets(ext);
            assert_eq!(t.len(), 8);
            assert!(t.iter().all(|x| x.prefer == EngineKind::Raw));
        }
    }
}
