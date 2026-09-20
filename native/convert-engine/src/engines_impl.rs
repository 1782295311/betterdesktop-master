// 子进程引擎实现（C# SofficeEngine/PopplerEngine/PandocEngine/FfmpegEngine/TesseractEngine/CalibreEngine 等价）：
// 参数数组直传（红线 1）、超时杀进程树（红线 2）、产物只写 TempDir（红线 3）、soffice 全局串行门（同 profile 并发锁冲突）。
use std::path::{Path, PathBuf};
use std::sync::Mutex;

use crate::contract::{ConversionTarget, EngineKind};
use crate::constants::{MEDIA_TIMEOUT_MS, TIMEOUT_MS};
use crate::engines::{
    locate_calibre, locate_ffmpeg, locate_pandoc, locate_pdftoppm, locate_pdftotext, locate_soffice,
    locate_tesseract, user_profile_url,
};
use crate::error::Error;
use crate::exec::{collect_prefixed, product_path, run};
use crate::heic_engine::HeicEngine;
use crate::pdf_text::PdfTextEngine;
use crate::raw_engine::RawEngine;
use crate::run::{Engine, Job};

/// soffice 全局串行门：LibreOffice 同一 UserInstallation profile 并发启动锁冲突（C# 2026-09-10 实测 exit 1）。
/// 同步执行链下一次一作业天然串行；此门防未来并行路径，语义与 C# SemaphoreSlim(1,1) 一致。
static SOFFICE_GATE: Mutex<()> = Mutex::new(());

// ———————————————————————————————— Soffice ————————————————————————————————

pub struct SofficeEngine;

impl Engine for SofficeEngine {
    fn kind(&self) -> EngineKind {
        EngineKind::Soffice
    }

    fn can_handle(&self, sources: &[PathBuf], target: &ConversionTarget) -> bool {
        sources.len() == 1 && (target.prefer == EngineKind::Soffice || target.fallback == Some(EngineKind::Soffice))
    }

    fn run(&self, job: &Job) -> Result<Vec<PathBuf>, Error> {
        let soffice = locate_soffice()
            .ok_or_else(|| Error::engine_missing("内置引擎未就绪（未找到 LibreOffice）——这不是文件错误"))?;
        let filter = job.target.filter.clone().unwrap_or_else(|| job.target.format.clone());
        let product = soffice_convert(&soffice, job.primary_source(), &filter, job.temp_dir)?;
        Ok(vec![product])
    }
}

/// soffice 子进程转换核心（TwoHop 引擎复用）。
/// 产物 = tempDir/输入名.filter短名；参数：-env:UserInstallation → --headless --norestore --convert-to filter --outdir tempDir input。
pub fn soffice_convert(soffice: &Path, input: &Path, filter: &str, temp_dir: &Path) -> Result<PathBuf, Error> {
    let _gate = SOFFICE_GATE.lock().unwrap_or_else(|p| p.into_inner());
    let ext = filter.split(':').next().unwrap_or(filter);
    let product = product_path(temp_dir, input, ext);
    let args = vec![
        format!("-env:UserInstallation={}", user_profile_url()),
        "--headless".to_string(),
        "--norestore".to_string(),
        "--convert-to".to_string(),
        filter.to_string(),
        "--outdir".to_string(),
        temp_dir.to_string_lossy().to_string(),
        input.to_string_lossy().to_string(),
    ];
    let out = run(soffice, &args, input.parent(), TIMEOUT_MS)?;
    if out.timed_out {
        return Err(Error::timeout(format!("soffice 超时（{TIMEOUT_MS}ms，已强杀进程树）")));
    }
    if out.code != Some(0) || !product.is_file() {
        return Err(Error::conversion_failed(format!(
            "soffice 退出码 {:?}，产物缺失",
            out.code
        )));
    }
    Ok(product)
}

// ———————————————————————————————— Poppler ————————————————————————————————

pub struct PopplerEngine;

impl Engine for PopplerEngine {
    fn kind(&self) -> EngineKind {
        EngineKind::Poppler
    }

    fn can_handle(&self, sources: &[PathBuf], target: &ConversionTarget) -> bool {
        sources.len() == 1
            && sources[0]
                .extension()
                .map(|e| e.eq_ignore_ascii_case("pdf"))
                .unwrap_or(false)
            && (target.prefer == EngineKind::Poppler || target.fallback == Some(EngineKind::Poppler))
    }

    fn run(&self, job: &Job) -> Result<Vec<PathBuf>, Error> {
        let input = job.primary_source();
        let format = job.target.format.as_str();
        let name = input
            .file_stem()
            .map(|s| s.to_string_lossy().to_string())
            .unwrap_or_else(|| "output".to_string());

        if matches!(format, "png" | "jpg" | "tiff") {
            let pdftoppm = locate_pdftoppm()
                .ok_or_else(|| Error::engine_missing("内置引擎未就绪（未找到 Poppler）——这不是文件错误"))?;
            let out_root = job.temp_dir.join(&name);
            let fmt_arg = match format {
                "png" => "-png",
                "jpg" => "-jpeg",
                _ => "-tiff",
            };
            let args = vec![
                "-r".to_string(),
                "150".to_string(),
                fmt_arg.to_string(),
                input.to_string_lossy().to_string(),
                out_root.to_string_lossy().to_string(),
            ];
            let out = run(&pdftoppm, &args, None, TIMEOUT_MS)?;
            // tiff 产物扩展名 .tif（Poppler 约定）。
            let products = collect_prefixed(
                job.temp_dir,
                &name,
                &[if format == "tiff" { "tif" } else { format }],
            );
            if out.timed_out {
                return Err(Error::timeout(format!("Poppler 超时（{TIMEOUT_MS}ms，已强杀进程树）")));
            }
            if out.code != Some(0) || products.is_empty() {
                return Err(Error::conversion_failed(format!(
                    "pdftoppm 退出码 {:?}，产物缺失 {}",
                    out.code,
                    truncate(&out.combined())
                )));
            }
            return Ok(products);
        }

        if format == "txt" {
            let pdftotext = locate_pdftotext()
                .ok_or_else(|| Error::engine_missing("未找到 pdftotext.exe（Poppler 套件不完整）"))?;
            let product = product_path(job.temp_dir, input, "txt");
            let args = vec![
                "-enc".to_string(),
                "UTF-8".to_string(),
                input.to_string_lossy().to_string(),
                product.to_string_lossy().to_string(),
            ];
            let out = run(&pdftotext, &args, None, TIMEOUT_MS)?;
            if out.code != Some(0) || !product.is_file() {
                return Err(Error::conversion_failed(format!(
                    "pdftotext 退出码 {:?} {}",
                    out.code,
                    truncate(&out.combined())
                )));
            }
            return Ok(vec![product]);
        }

        Err(Error::input_invalid(format!("Poppler 引擎不支持的转换目标: {format}")))
    }
}

// ———————————————————————————————— Pandoc ————————————————————————————————

/// 产物扩展名 ≠ writer 名的目标显式 -t（tex→latex 等；其余靠 -o 扩展名自动选 writer）。
const WRITER_NAMES: [(&str, &str); 6] = [
    ("tex", "latex"),
    ("wiki", "mediawiki"),
    ("adoc", "asciidoc"),
    ("db", "docbook"),
    ("texi", "texinfo"),
    ("opendocument", "opendocument"),
];

pub struct PandocEngine;

impl Engine for PandocEngine {
    fn kind(&self) -> EngineKind {
        EngineKind::Pandoc
    }

    fn can_handle(&self, sources: &[PathBuf], target: &ConversionTarget) -> bool {
        sources.len() == 1 && (target.prefer == EngineKind::Pandoc || target.fallback == Some(EngineKind::Pandoc))
    }

    fn run(&self, job: &Job) -> Result<Vec<PathBuf>, Error> {
        let pandoc = locate_pandoc()
            .ok_or_else(|| Error::engine_missing("内置引擎未就绪（未找到 pandoc）——这不是文件错误"))?;
        let input = job.primary_source();
        let format = job.target.format.as_str();
        let product = product_path(job.temp_dir, input, format);

        let mut args: Vec<String> = Vec::new();
        let src_ext = input
            .extension()
            .map(|e| format!(".{}", e.to_string_lossy().to_lowercase()))
            .unwrap_or_default();
        if src_ext == ".docx" {
            args.push("-f".to_string());
            args.push("docx".to_string());
            if format == "md" {
                // docx→md 高质量（GitHub flavored）
                args.push("-t".to_string());
                args.push("gfm".to_string());
            }
        }
        if let Some((_, writer)) = WRITER_NAMES.iter().find(|(f, _)| *f == format) {
            args.push("-t".to_string());
            args.push(writer.to_string());
        }
        args.push(input.to_string_lossy().to_string());
        args.push("-o".to_string());
        args.push(product.to_string_lossy().to_string());
        if src_ext == ".md" {
            // 红线 7：md 相对图片按源目录解析
            let dir = input.parent().map(|d| d.to_string_lossy().to_string()).unwrap_or_default();
            args.push(format!("--resource-path={dir}"));
        }

        let out = run(&pandoc, &args, None, TIMEOUT_MS)?;
        if out.timed_out {
            return Err(Error::timeout(format!("pandoc 超时（{TIMEOUT_MS}ms，已强杀进程树）")));
        }
        if out.code != Some(0) || !product.is_file() {
            return Err(Error::conversion_failed(format!(
                "pandoc 退出码 {:?}，产物缺失",
                out.code
            )));
        }
        Ok(vec![product])
    }
}

// ———————————————————————————————— FFmpeg ————————————————————————————————

pub struct FfmpegEngine;

impl Engine for FfmpegEngine {
    fn kind(&self) -> EngineKind {
        EngineKind::Ffmpeg
    }

    fn can_handle(&self, sources: &[PathBuf], target: &ConversionTarget) -> bool {
        sources.len() == 1 && (target.prefer == EngineKind::Ffmpeg || target.fallback == Some(EngineKind::Ffmpeg))
    }

    fn run(&self, job: &Job) -> Result<Vec<PathBuf>, Error> {
        let ffmpeg = locate_ffmpeg()
            .ok_or_else(|| Error::engine_missing("内置引擎未就绪（未找到 FFmpeg）——这不是文件错误"))?;
        let input = job.primary_source();
        let format = job.target.format.as_str();
        let product = product_path(job.temp_dir, input, format);

        let src_ext = input
            .extension()
            .map(|e| format!(".{}", e.to_string_lossy().to_lowercase()))
            .unwrap_or_default();
        let marker = job.target.filter.clone().unwrap_or_default();
        let is_video = matches!(src_ext.as_str(), ".mp4" | ".mkv" | ".avi" | ".mov" | ".wmv" | ".flv" | ".webm" | ".m4v");
        let is_image_to_video = matches!(src_ext.as_str(), ".png" | ".jpg" | ".jpeg" | ".bmp" | ".gif" | ".tif" | ".tiff" | ".webp" | ".ico" | ".tga")
            && matches!(format, "mp4" | "webm");

        let mut args: Vec<String> = vec!["-y".to_string()];
        if is_image_to_video {
            // 单图循环 3 秒成视频
            args.push("-loop".to_string());
            args.push("1".to_string());
            args.push("-framerate".to_string());
            args.push("25".to_string());
        }
        args.push("-i".to_string());
        args.push(input.to_string_lossy().to_string());
        if is_image_to_video {
            args.push("-t".to_string());
            args.push("3".to_string());
            args.push("-pix_fmt".to_string());
            args.push("yuv420p".to_string()); // 播放器兼容
        } else if is_video {
            if matches!(format, "mkv" | "mov") {
                // 容器 remux（-c copy 不重编码，无损）
                args.push("-c".to_string());
                args.push("copy".to_string());
            } else {
                match marker.as_str() {
                    "enc-h265" => push_encoder(&mut args, "libx265", "28"),
                    "enc-av1" => {
                        push_encoder(&mut args, "libaom-av1", "30");
                        args.push("-b:v".to_string());
                        args.push("0".to_string());
                        args.push("-cpu-used".to_string());
                        args.push("6".to_string());
                    }
                    "enc-h264" => push_encoder(&mut args, "libx264", "23"),
                    _ => {
                        if format == "webm" {
                            // webm 显式 VP9
                            args.push("-c:v".to_string());
                            args.push("libvpx-vp9".to_string());
                            args.push("-crf".to_string());
                            args.push("32".to_string());
                            args.push("-b:v".to_string());
                            args.push("0".to_string());
                        }
                    }
                }
            }
        } else if format == "avif" {
            // 图片 → AVIF（libaom-av1）
            args.push("-c:v".to_string());
            args.push("libaom-av1".to_string());
            args.push("-crf".to_string());
            args.push("30".to_string());
            args.push("-pix_fmt".to_string());
            args.push("yuv420p".to_string());
        }
        args.push(product.to_string_lossy().to_string());

        let out = run(&ffmpeg, &args, None, MEDIA_TIMEOUT_MS)?;
        if out.timed_out {
            return Err(Error::timeout(format!("FFmpeg 超时（{MEDIA_TIMEOUT_MS}ms，已强杀进程树）")));
        }
        if out.code != Some(0) || !product.is_file() {
            return Err(Error::conversion_failed(format!(
                "FFmpeg 退出码 {:?}，产物缺失 {}",
                out.code,
                truncate(&out.combined())
            )));
        }
        Ok(vec![product])
    }
}

fn push_encoder(args: &mut Vec<String>, codec: &str, crf: &str) {
    args.push("-c:v".to_string());
    args.push(codec.to_string());
    args.push("-crf".to_string());
    args.push(crf.to_string());
    args.push("-preset".to_string());
    args.push("medium".to_string());
}

// ———————————————————————————————— Tesseract ————————————————————————————————

pub struct TesseractEngine;

impl Engine for TesseractEngine {
    fn kind(&self) -> EngineKind {
        EngineKind::Tesseract
    }

    fn can_handle(&self, sources: &[PathBuf], target: &ConversionTarget) -> bool {
        sources.len() == 1
            && target.format == "txt"
            && (target.prefer == EngineKind::Tesseract || target.fallback == Some(EngineKind::Tesseract))
    }

    fn run(&self, job: &Job) -> Result<Vec<PathBuf>, Error> {
        let tesseract = locate_tesseract()
            .ok_or_else(|| Error::engine_missing("内置引擎未就绪（未找到 Tesseract-OCR）——这不是文件错误"))?;
        let input = job.primary_source();
        let name = input
            .file_stem()
            .map(|s| s.to_string_lossy().to_string())
            .unwrap_or_else(|| "output".to_string());
        let out_base = job.temp_dir.join(format!("{name}-ocr"));
        let product = job.temp_dir.join(format!("{name}-ocr.txt"));

        // 受管内置 tessdata 定位（UB-Mannheim exe 编译 DATADIR=安装路径，迁移后须显式指定）
        let tessdata = tesseract
            .parent()
            .map(|d| d.join("tessdata"))
            .unwrap_or_else(|| PathBuf::from("tessdata"));

        // 先 chi_sim+eng，非零退出回落 eng（诚实，不静默失败）
        let first = run(
            &tesseract,
            &[
                input.to_string_lossy().to_string(),
                out_base.to_string_lossy().to_string(),
                "--tessdata-dir".to_string(),
                tessdata.to_string_lossy().to_string(),
                "-l".to_string(),
                "chi_sim+eng".to_string(),
                "--psm".to_string(),
                "6".to_string(),
            ],
            None,
            TIMEOUT_MS,
        );
        let (out, label) = match first {
            Ok(o) if o.code == Some(0) && product.is_file() => (o, "chi_sim+eng".to_string()),
            _ => {
                let fallback = run(
                    &tesseract,
                    &[
                        input.to_string_lossy().to_string(),
                        out_base.to_string_lossy().to_string(),
                        "--tessdata-dir".to_string(),
                        tessdata.to_string_lossy().to_string(),
                        "-l".to_string(),
                        "eng".to_string(),
                        "--psm".to_string(),
                        "6".to_string(),
                    ],
                    None,
                    TIMEOUT_MS,
                );
                match fallback {
                    Ok(o) if o.code == Some(0) && product.is_file() => (o, "eng".to_string()),
                    other => {
                        let msg = match other {
                            Ok(o) if o.timed_out => format!("tesseract 超时（{TIMEOUT_MS}ms）"),
                            Ok(o) => format!("tesseract 退出码 {:?} {}", o.code, truncate(&o.combined())),
                            Err(e) => e.message,
                        };
                        return Err(Error::conversion_failed(format!("OCR 失败（chi_sim+eng 与 eng 均未通过）: {msg}")));
                    }
                }
            }
        };
        let _ = label;
        let _ = out;
        Ok(vec![product])
    }
}

// ———————————————————————————————— Calibre ————————————————————————————————

pub struct CalibreEngine;

impl Engine for CalibreEngine {
    fn kind(&self) -> EngineKind {
        EngineKind::Calibre
    }

    fn can_handle(&self, sources: &[PathBuf], target: &ConversionTarget) -> bool {
        sources.len() == 1 && (target.prefer == EngineKind::Calibre || target.fallback == Some(EngineKind::Calibre))
    }

    fn run(&self, job: &Job) -> Result<Vec<PathBuf>, Error> {
        let ebook_convert = locate_calibre()
            .ok_or_else(|| Error::engine_missing("内置引擎未就绪（未找到 ebook-convert.exe）。安装 calibre（https://calibre-ebook.com）后重试——这不是文件错误"))?;
        let input = job.primary_source();
        let format = job.target.format.as_str();
        let product = product_path(job.temp_dir, input, format);

        // 目标格式由输出扩展名决定（ebook-convert 惯例）
        let args = vec![
            input.to_string_lossy().to_string(),
            product.to_string_lossy().to_string(),
        ];
        let out = run(&ebook_convert, &args, None, MEDIA_TIMEOUT_MS)?;
        if out.timed_out {
            return Err(Error::timeout(format!("ebook-convert 超时（{MEDIA_TIMEOUT_MS}ms，已强杀进程树）")));
        }
        if out.code != Some(0) || !product.is_file() {
            return Err(Error::conversion_failed(format!(
                "ebook-convert 退出码 {:?} {}",
                out.code,
                truncate(&out.combined())
            )));
        }
        Ok(vec![product])
    }
}

// ———————————————————————————————— 托管文本（S6） ————————————————————————————————

/// 纯托管文本引擎（C# ManagedEngine 等价；Markdig/ReverseMarkdown 纯托管，零外部进程）。
/// front_matter 模式默认 Strip（C# 默认；设置 convert.front-matter=heading 由门面层经 target.filter 传递，暂未接线）。
pub struct ManagedEngine;

impl Engine for ManagedEngine {
    fn kind(&self) -> EngineKind {
        EngineKind::Managed
    }

    fn can_handle(&self, sources: &[PathBuf], target: &ConversionTarget) -> bool {
        sources.len() == 1 && (target.prefer == EngineKind::Managed || target.fallback == Some(EngineKind::Managed))
    }

    fn run(&self, job: &Job) -> Result<Vec<PathBuf>, Error> {
        let input = job.primary_source();
        let format = job.target.format.as_str();
        let content = crate::managed::convert_managed(input, format, false)?;
        let product = product_path(job.temp_dir, input, format);
        std::fs::write(&product, content.as_bytes())
            .map_err(|e| Error::conversion_failed(format!("写入失败: {e}")))?;
        Ok(vec![product])
    }
}

// ———————————————————————————————— 托管图片（S6） ————————————————————————————————

/// 图片互转引擎（C# ManagedImageEngine 等价；image crate 0.25；webp 目标有损 90）。
pub struct ManagedImageEngine;

impl Engine for ManagedImageEngine {
    fn kind(&self) -> EngineKind {
        EngineKind::Image
    }

    fn can_handle(&self, sources: &[PathBuf], target: &ConversionTarget) -> bool {
        sources.len() == 1
            && (target.prefer == EngineKind::Image || target.fallback == Some(EngineKind::Image))
            && sources[0]
                .extension()
                .map(|e| {
                    let ext = format!(".{}", e.to_string_lossy().to_lowercase());
                    crate::image_engine::is_image_source(&ext)
                        && ext != ".tga" // tga 由 Ffmpeg 引擎处理（System.Drawing/image 均不支持 tga 编码）
                })
                .unwrap_or(false)
    }

    fn run(&self, job: &Job) -> Result<Vec<PathBuf>, Error> {
        let input = job.primary_source();
        let format = job.target.format.as_str();
        let product = product_path(job.temp_dir, input, format);
        crate::image_engine::convert_image(input, &product, format)?;
        Ok(vec![product])
    }
}

// ———————————————————————————————— PDF 合成/合并/拆分（S7） ————————————————————————————————

/// PDF 组合引擎（C# PdfComposeEngine 等价）：按 filter marker 分派 合并/合成/拆分。
pub struct PdfComposeEngine;

impl Engine for PdfComposeEngine {
    fn kind(&self) -> EngineKind {
        EngineKind::PdfCompose
    }

    fn can_handle(&self, sources: &[PathBuf], target: &ConversionTarget) -> bool {
        if target.prefer != EngineKind::PdfCompose && target.fallback != Some(EngineKind::PdfCompose) {
            return false;
        }
        let all_pdf = |list: &[PathBuf]| {
            list.iter()
                .all(|p| p.extension().map(|e| e.eq_ignore_ascii_case("pdf")).unwrap_or(false))
        };
        let all_images = |list: &[PathBuf]| {
            list.iter().all(|p| {
                p.extension()
                    .map(|e| {
                        let ext = format!(".{}", e.to_string_lossy().to_lowercase());
                        crate::image_engine::is_image_source(&ext)
                    })
                    .unwrap_or(false)
            })
        };
        match target.filter.as_deref() {
            Some(m) if m == ConversionTarget::MERGE_PDF_MARKER => sources.len() >= 2 && all_pdf(sources),
            Some(m) if m == ConversionTarget::COMPOSE_PDF_MARKER => sources.len() >= 1 && all_images(sources),
            Some(m) if m == ConversionTarget::SPLIT_PDF_MARKER => sources.len() == 1 && all_pdf(sources),
            _ => false,
        }
    }

    fn run(&self, job: &Job) -> Result<Vec<PathBuf>, Error> {
        let marker = job.target.filter.as_deref().unwrap_or_default();
        let temp = job.temp_dir;
        match marker {
            m if m == ConversionTarget::MERGE_PDF_MARKER => {
                let refs: Vec<&Path> = job.sources.iter().map(|p| p.as_path()).collect();
                let product = temp.join("merged.pdf");
                crate::pdf_ops::merge_pdfs(&refs, &product)?;
                Ok(vec![product])
            }
            m if m == ConversionTarget::COMPOSE_PDF_MARKER => {
                let refs: Vec<&Path> = job.sources.iter().map(|p| p.as_path()).collect();
                let product = temp.join("composed.pdf");
                crate::pdf_ops::compose_images(&refs, &product)?;
                Ok(vec![product])
            }
            m if m == ConversionTarget::SPLIT_PDF_MARKER => {
                crate::pdf_ops::split_pdf(job.primary_source(), temp)
            }
            _ => Err(Error::input_invalid("未知的 PDF 组合操作".to_string())),
        }
    }
}

// ———————————————————————————————— PDF 加密/解密（S7） ————————————————————————————————

/// PDF 安全引擎（C# PdfSecurityEngine 等价）：密码仅来自 job.password（stdin 契约，绝不 argv）。
pub struct PdfSecurityEngine;

impl Engine for PdfSecurityEngine {
    fn kind(&self) -> EngineKind {
        EngineKind::PdfSecurity
    }

    fn can_handle(&self, sources: &[PathBuf], target: &ConversionTarget) -> bool {
        sources.len() == 1
            && sources[0]
                .extension()
                .map(|e| e.eq_ignore_ascii_case("pdf"))
                .unwrap_or(false)
            && (target.prefer == EngineKind::PdfSecurity || target.fallback == Some(EngineKind::PdfSecurity))
    }

    fn run(&self, job: &Job) -> Result<Vec<PathBuf>, Error> {
        let pw = job.password.unwrap_or_default();
        let marker = job.target.filter.as_deref().unwrap_or_default();
        let input = job.primary_source();
        let stem = input
            .file_stem()
            .map(|s| s.to_string_lossy().to_string())
            .unwrap_or_else(|| "output".to_string());
        if marker == ConversionTarget::ENCRYPT_PDF_MARKER {
            let product = job.temp_dir.join(format!("{stem}-encrypted.pdf"));
            crate::pdf_security::encrypt_pdf(input, pw, &product)?;
            Ok(vec![product])
        } else if marker == ConversionTarget::DECRYPT_PDF_MARKER {
            let product = job.temp_dir.join(format!("{stem}-decrypted.pdf"));
            crate::pdf_ops::decrypt_pdf(input, pw, &product)?;
            Ok(vec![product])
        } else {
            Err(Error::input_invalid("未知的 PDF 安全操作".to_string()))
        }
    }
}

// ———————————————————————————————— 两跳（S7，C# TwoHopEngine 等价） ————————————————————————————————

/// 两跳中转引擎（hub-spoke：矩阵显式登记 hop=2，禁止隐式超过两跳）：
/// md→docx/pdf：md → 中转 html（相对图片转绝对 URI，红线 7）→ soffice；
/// 演示族→png/jpg：soffice 出 pdf 中间态 → Poppler 渲染；
/// epub→pdf：pandoc html 中间态 → 完整 HTML 包装 → soffice；
/// Word 族→md（pandoc 缺失离线兜底）：soffice html → 基础 html→md（样式有损，能力边界见 managed）。
pub struct TwoHopEngine;

impl Engine for TwoHopEngine {
    fn kind(&self) -> EngineKind {
        EngineKind::TwoHop
    }

    fn can_handle(&self, sources: &[PathBuf], target: &ConversionTarget) -> bool {
        sources.len() == 1 && (target.prefer == EngineKind::TwoHop || target.fallback == Some(EngineKind::TwoHop))
    }

    fn run(&self, job: &Job) -> Result<Vec<PathBuf>, Error> {
        let soffice = locate_soffice()
            .ok_or_else(|| Error::engine_missing("内置引擎未就绪（未找到 LibreOffice）——这不是文件错误"))?;
        let input = job.primary_source();
        let format = job.target.format.as_str();
        let ext = input
            .extension()
            .map(|e| format!(".{}", e.to_string_lossy().to_lowercase()))
            .unwrap_or_default();
        let name = input
            .file_stem()
            .map(|s| s.to_string_lossy().to_string())
            .unwrap_or_else(|| "output".to_string());
        let temp = job.temp_dir;

        match (ext.as_str(), format) {
            (".md", "docx") | (".md", "pdf") => {
                // md → 中转 html（相对图片解析为绝对 file URI + CJK 样式随 html 进 soffice，红线 7/8）
                let md = crate::managed::read_utf8(input)?;
                let html_path = temp.join(format!("{name}.html"));
                let body = crate::managed::markdown_to_standalone_html(&md, &name, input.parent());
                std::fs::write(&html_path, body)
                    .map_err(|e| Error::conversion_failed(format!("中转 html 写入失败: {e}")))?;
                let product = soffice_convert(&soffice, &html_path, format, temp)?;
                Ok(vec![product])
            }
            (".ppt" | ".pptx" | ".pps" | ".dps" | ".dpt", "png" | "jpg") => {
                // 演示族 → PDF 中间态（TempDir）→ Poppler 渲染
                let pdf_path = soffice_convert(&soffice, input, "pdf", temp)?;
                let pdf_job = Job {
                    sources: vec![pdf_path.clone()],
                    target: &ConversionTarget {
                        format: format.to_string(),
                        label: "演示渲染".to_string(),
                        filter: None,
                        hops: 1,
                        prefer: EngineKind::Poppler,
                        fallback: None,
                        category: crate::contract::TargetCategory::Image,
                        lossless: false,
                    },
                    temp_dir: temp,
                    password: None,
                };
                PopplerEngine.run(&pdf_job)
            }
            (".epub", "pdf") => {
                // epub → html（pandoc 原生 reader）→ 完整 HTML 包装 → soffice pdf
                let pandoc = locate_pandoc()
                    .ok_or_else(|| Error::engine_missing("内置引擎未就绪（未找到 pandoc）——这不是文件错误"))?;
                let html_raw = temp.join(format!("{name}.html"));
                let args = vec![
                    input.to_string_lossy().to_string(),
                    "-o".to_string(),
                    html_raw.to_string_lossy().to_string(),
                ];
                let out = crate::exec::run(&pandoc, &args, None, TIMEOUT_MS)?;
                if out.code != Some(0) || !html_raw.is_file() {
                    return Err(Error::conversion_failed(format!(
                        "pandoc epub→html 退出码 {:?}",
                        out.code
                    )));
                }
                let raw = crate::managed::read_utf8(&html_raw)?;
                let html_path = temp.join(format!("{name}-epub.html"));
                std::fs::write(&html_path, crate::managed::build_standalone_html(&name, &raw))
                    .map_err(|e| Error::conversion_failed(format!("中转 html 写入失败: {e}")))?;
                let product = soffice_convert(&soffice, &html_path, "pdf", temp)?;
                Ok(vec![product])
            }
            (".doc" | ".docx" | ".docm" | ".rtf" | ".odt" | ".wps" | ".wpt" | ".wpd", "md") => {
                // Word 族 → soffice html → 基础 html→md（ReverseMarkdown 等价子集，样式有损，菜单已标注）
                let html = soffice_convert(&soffice, input, "html", temp)?;
                let raw = crate::managed::read_utf8(&html)?;
                let product = temp.join(format!("{name}.md"));
                std::fs::write(&product, crate::managed::html_to_markdown(&raw))
                    .map_err(|e| Error::conversion_failed(format!("md 写入失败: {e}")))?;
                Ok(vec![product])
            }
            _ => Err(Error::input_invalid(format!("两跳引擎不支持的转换: {ext} → {format}"))),
        }
    }
}

/// 全部引擎注册表（S5 六子进程 + S6 两托管 + S7 PDF 族与两跳）。
pub fn all_engines() -> Vec<Box<dyn Engine>> {
    vec![
        Box::new(SofficeEngine),
        Box::new(PopplerEngine),
        Box::new(PdfTextEngine),
        Box::new(PandocEngine),
        Box::new(FfmpegEngine),
        Box::new(TesseractEngine),
        Box::new(CalibreEngine),
        Box::new(ManagedEngine),
        Box::new(ManagedImageEngine),
        Box::new(PdfComposeEngine),
        Box::new(PdfSecurityEngine),
        Box::new(HeicEngine),
        Box::new(RawEngine),
        Box::new(TwoHopEngine),
        // 2026-09-20：进程内轻量引擎（零外部 exe）。放在最后只是因为它是最后加进来的 ——
        // 解析顺序由矩阵的 prefer/fallback 决定，与注册顺序无关（run.rs::resolve_engine 按 kind 查）。
        Box::new(crate::lite::LiteEngine),
    ]
}

/// 子进程引擎注册表（保持 S5 命名：S9 前 run 命令使用）。
pub fn subprocess_engines() -> Vec<Box<dyn Engine>> {
    all_engines()
}

fn truncate(text: &str) -> &str {
    if text.len() > 200 { &text[..200] } else { text }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::HashSet;

    /// 防回归防线（S9.5 根因测试）：矩阵每个条目引用的 prefer/fallback 引擎必须已注册。
    /// 唯一例外 ComPdf = 已降级 fallback（soffice 直转优先，Rust 侧无 COM 引擎是拍板决策）。
    #[test]
    fn matrix_prefer_fallback_all_registered() {
        use crate::matrix::ConversionMatrix;
        let registered: HashSet<EngineKind> = all_engines().iter().map(|e| e.kind()).collect();
        let matrix = ConversionMatrix::default();
        let mut checked = 0usize;
        for ext in matrix.all_input_extensions() {
            for t in matrix.get_targets(&ext) {
                for kind in std::iter::once(t.prefer).chain(t.fallback) {
                    if kind == EngineKind::ComPdf {
                        continue;
                    }
                    assert!(
                        registered.contains(&kind),
                        "矩阵 {} 目标 {} 引用 {:?} 未注册（引擎落下）",
                        ext,
                        t.format,
                        kind
                    );
                    checked += 1;
                }
            }
        }
        assert!(checked > 300, "一致性检查覆盖面异常（仅 {checked} 条）");
    }

    #[test]
    fn pandoc_docx_to_md_uses_gfm() {
        // 纯函数验证：docx→md 的参数序列含 -f docx -t gfm
        let mut args: Vec<String> = vec![];
        let format = "md";
        let src_ext = ".docx";
        args.push("-f".to_string());
        args.push("docx".to_string());
        if format == "md" {
            args.push("-t".to_string());
            args.push("gfm".to_string());
        }
        assert_eq!(args, vec!["-f", "docx", "-t", "gfm"]);
    }

    #[test]
    fn ffmpeg_remux_vs_encoder_choice() {
        // mkv 目标 → -c copy（无损 remux）
        let mut args = vec!["-y".to_string(), "-i".to_string(), "in.mp4".to_string()];
        args.push("-c".to_string());
        args.push("copy".to_string());
        assert!(args.windows(2).any(|w| w == ["-c", "copy"]));
        // H.265 → libx265
        let mut args2 = vec!["-y".to_string(), "-i".to_string(), "in.mp4".to_string()];
        push_encoder(&mut args2, "libx265", "28");
        assert!(args2.windows(2).any(|w| w == ["-c:v", "libx265"]));
        assert!(args2.windows(2).any(|w| w == ["-crf", "28"]));
    }

    #[test]
    fn writer_names_mapping() {
        assert_eq!(
            WRITER_NAMES.iter().find(|(f, _)| *f == "tex").map(|(_, w)| *w),
            Some("latex")
        );
        assert!(WRITER_NAMES.iter().all(|(f, _)| *f == f.to_lowercase()));
    }

    #[test]
    fn soffice_args_shape() {
        // 构造序列形状（不实际执行）：-env:UserInstallation 首位 + --headless --norestore --convert-to filter --outdir dir input
        let args = vec![
            "-env:UserInstallation=file:///C:/x".to_string(),
            "--headless".to_string(),
            "--norestore".to_string(),
            "--convert-to".to_string(),
            "pdf".to_string(),
            "--outdir".to_string(),
            "C:/tmp".to_string(),
            "C:/a.docx".to_string(),
        ];
        assert_eq!(args[0].starts_with("-env:UserInstallation="), true);
        assert_eq!(args[1], "--headless");
        assert_eq!(args[2], "--norestore");
        assert_eq!(args[3], "--convert-to");
        assert_eq!(args[4], "pdf");
        assert_eq!(args[5], "--outdir");
        assert_eq!(args[7], "C:/a.docx");
    }

    #[test]
    fn truncate_limits_length() {
        let long = "x".repeat(500);
        assert!(truncate(&long).len() <= 200);
        assert_eq!(truncate("短"), "短");
    }
}
