//! convert-lite：轻量化格式转换核心（库接口）。
//!
//! 供两处使用：
//! - `main.rs`（bin）：CLI 薄壳，参数解析后调用 `convert`，自行输出 result NDJSON。
//! - better-desktop-cordis `native/convert-engine`：作为库引用，替代 pandoc/soffice/
//!   poppler/calibre 等外部 exe 调用（零外部二进制，进程内完成）。
//!
//! `convert` 完全静默（无 stdout 副作用），宿主负责自己的进度协议。

pub mod av;
pub mod doc;
pub mod html_md;
pub mod epub_md;
pub mod docbook_md;
pub mod ipynb_md;
pub mod latex;
pub mod jats_md;
pub mod md_ipynb;
pub mod md_mediawiki;
pub mod md_org;
pub mod md_rtf;
pub mod md_odt;
pub mod md_docbook;
pub mod md_jats;
pub mod mediawiki_md;
pub mod office;
pub mod ole2;
pub mod doc_legacy;
pub mod xls_legacy;
pub mod ppt_legacy;
pub mod txt_md;
pub mod fb2_md;
pub mod opml_md;
pub mod rst_md;
pub mod dokuwiki_md;
pub mod textile_md;
pub mod mobi_md;
pub mod pdf_text;
pub mod odt_md;
pub mod org_md;
pub mod rtf_md;
pub mod doc_pdf;
pub mod guard;
pub mod image;
pub mod md_docx;
pub mod md_misc;
pub mod md_pptx;
pub mod ir;
pub mod pdf;
pub mod pptx;
pub mod protocol;
pub mod text;
pub mod xlsx;
pub mod archive;
pub mod crypto;
pub mod clean;
pub mod rename;

use std::path::{Path, PathBuf};

use protocol::{Emitter, Phase};

fn ext_of(p: &Path) -> String {
    p.extension()
        .and_then(|e| e.to_str())
        .unwrap_or("")
        .to_ascii_lowercase()
}

fn default_output(src: &Path, target: &str) -> PathBuf {
    src.with_extension(target)
}

fn is_image(ext: &str) -> bool {
    matches!(ext, "png" | "jpg" | "jpeg" | "bmp" | "webp" | "gif" | "tiff")
}

/// 多图合成 PDF：读每张 JPEG（DCTDecode 直嵌，不重编码），走 Parse→DocIR→Render。
fn images_to_pdf(sources: &[&Path], out: &Path, em: &Emitter) -> Result<(), String> {
    let tmp = guard::tmp_path(out);
    em.progress(
        &sources[0].to_string_lossy(),
        "pdf",
        "pdf-compose",
        Phase::Running,
        None,
        Some("逐张解析 JPEG 帧头，构建 IR（无中间字节进度）"),
    );

    let r = (|| -> Result<(), String> {
        let mut doc = ir::DocIR::new();
        for s in sources {
            let bytes =
                std::fs::read(s).map_err(|e| format!("读取 {} 失败：{e}", s.display()))?;
            let (w, h, jpeg) = match ir::jpeg_size(&bytes) {
                Ok((w, h)) => (w, h, bytes),
                // 非 JPEG（PNG/BMP/WEBP/GIF）：image crate 解码后重编码为 JPEG 再直嵌
                Err(_) => {
                    let img = ::image::open(s).map_err(|e| {
                        format!("{}：图像解码失败（{e}）；PDF 合成接受 JPEG/PNG/BMP/WEBP/GIF", s.display())
                    })?;
                    let (w, h) = (img.width(), img.height());
                    guard::check_pixels(w, h).map_err(|e| e.to_string())?;
                    let mut buf = Vec::new();
                    {
                        let mut cur = std::io::Cursor::new(&mut buf);
                        img.write_to(&mut cur, ::image::ImageFormat::Jpeg)
                            .map_err(|e| format!("{}：图像转 JPEG 失败：{e}", s.display()))?;
                    }
                    (w, h, buf)
                }
            };
            guard::check_pixels(w, h).map_err(|e| e.to_string())?;
            doc.push(ir::Page {
                width_px: w,
                height_px: h,
                jpeg,
            });
        }
        let pdf_bytes = pdf::render_pdf(&doc)?;
        std::fs::write(&tmp, &pdf_bytes).map_err(|e| format!("写出失败：{e}"))?;
        Ok(())
    })();

    match r {
        Ok(()) => {
            em.progress(
                &sources[0].to_string_lossy(),
                "pdf",
                "pdf-compose",
                Phase::Finalizing,
                Some(100),
                Some(&format!("{} 页", sources.len())),
            );
            guard::commit(&tmp, out).map_err(|e| format!("提交产物失败：{e}"))?;
            Ok(())
        }
        Err(e) => {
            guard::discard(&tmp);
            Err(e)
        }
    }
}

/// docx -> html：解 zip → OOXML → 文档 IR → HTML。
fn docx_to_html(input: &Path, out: &Path, em: &Emitter) -> Result<(), String> {
    let tmp = guard::tmp_path(out);
    em.progress(
        &input.to_string_lossy(),
        "html",
        "docx-ir",
        Phase::Running,
        None,
        Some("解包 docx，解析 OOXML 为文档 IR"),
    );
    let r = (|| -> Result<(), String> {
        let bytes = doc::docx_to_html_bytes(input)?;
        std::fs::write(&tmp, &bytes).map_err(|e| format!("写出失败：{e}"))?;
        Ok(())
    })();
    match r {
        Ok(()) => {
            em.progress(
                &input.to_string_lossy(),
                "html",
                "docx-ir",
                Phase::Finalizing,
                Some(100),
                None,
            );
            guard::commit(&tmp, out).map_err(|e| format!("提交产物失败：{e}"))?;
            Ok(())
        }
        Err(e) => {
            guard::discard(&tmp);
            Err(e)
        }
    }
}

/// xlsx -> csv 或 html：解包 → sharedStrings + worksheet → 二维表 → 渲染。
fn xlsx_to(input: &Path, out: &Path, fmt: &str, em: &Emitter) -> Result<(), String> {
    let tmp = guard::tmp_path(out);
    em.progress(
        &input.to_string_lossy(),
        fmt,
        "xlsx-ir",
        Phase::Running,
        None,
        Some("解包 xlsx，解析 sharedStrings 与 worksheet"),
    );
    let r = (|| -> Result<(), String> {
        let bytes = match fmt {
            "csv" => xlsx::xlsx_to_csv_bytes(input)?,
            "html" => xlsx::xlsx_to_html_bytes(input)?,
            _ => return Err("xlsx 只支持 csv/html 输出".to_string()),
        };
        std::fs::write(&tmp, &bytes).map_err(|e| format!("写出失败：{e}"))?;
        Ok(())
    })();
    match r {
        Ok(()) => {
            em.progress(
                &input.to_string_lossy(),
                fmt,
                "xlsx-ir",
                Phase::Finalizing,
                Some(100),
                None,
            );
            guard::commit(&tmp, out).map_err(|e| format!("提交产物失败：{e}"))?;
            Ok(())
        }
        Err(e) => {
            guard::discard(&tmp);
            Err(e)
        }
    }
}

/// docx -> pdf：解包 → 文档 IR → 流式排版（文本折行+JPEG 直嵌）。
fn docx_to_pdf(input: &Path, out: &Path, em: &Emitter) -> Result<(), String> {
    let tmp = guard::tmp_path(out);
    em.progress(
        &input.to_string_lossy(),
        "pdf",
        "docx-layout",
        Phase::Running,
        None,
        Some("解包 docx，流式排版为 PDF（无中间字节进度）"),
    );
    let r = (|| -> Result<(), String> {
        let bytes = doc_pdf::docx_to_pdf_bytes(input)?;
        std::fs::write(&tmp, &bytes).map_err(|e| format!("写出失败：{e}"))?;
        Ok(())
    })();
    match r {
        Ok(()) => {
            em.progress(
                &input.to_string_lossy(),
                "pdf",
                "docx-layout",
                Phase::Finalizing,
                Some(100),
                None,
            );
            guard::commit(&tmp, out).map_err(|e| format!("提交产物失败：{e}"))?;
            Ok(())
        }
        Err(e) => {
            guard::discard(&tmp);
            Err(e)
        }
    }
}

/// pptx -> html：解包 → 逐页解析文本/图片 → HTML section。
fn pptx_to_html(input: &Path, out: &Path, em: &Emitter) -> Result<(), String> {
    let tmp = guard::tmp_path(out);
    em.progress(
        &input.to_string_lossy(),
        "html",
        "pptx-parse",
        Phase::Running,
        None,
        Some("解包 pptx，逐页解析幻灯片"),
    );
    let r = (|| -> Result<(), String> {
        let bytes = pptx::pptx_to_html_bytes(input)?;
        std::fs::write(&tmp, &bytes).map_err(|e| format!("写出失败：{e}"))?;
        Ok(())
    })();
    match r {
        Ok(()) => {
            em.progress(
                &input.to_string_lossy(),
                "html",
                "pptx-parse",
                Phase::Finalizing,
                Some(100),
                None,
            );
            guard::commit(&tmp, out).map_err(|e| format!("提交产物失败：{e}"))?;
            Ok(())
        }
        Err(e) => {
            guard::discard(&tmp);
            Err(e)
        }
    }
}

/// pptx -> pdf：每页一张幻灯片，16:9，复用字体子集化。
fn pptx_to_pdf(input: &Path, out: &Path, em: &Emitter) -> Result<(), String> {
    let tmp = guard::tmp_path(out);
    em.progress(
        &input.to_string_lossy(),
        "pdf",
        "pptx-pdf",
        Phase::Running,
        None,
        Some("解包 pptx，逐页生成 PDF"),
    );
    let r = (|| -> Result<(), String> {
        let bytes = pptx::pptx_to_pdf_bytes(input)?;
        std::fs::write(&tmp, &bytes).map_err(|e| format!("写出失败：{e}"))?;
        Ok(())
    })();
    match r {
        Ok(()) => {
            em.progress(
                &input.to_string_lossy(),
                "pdf",
                "pptx-pdf",
                Phase::Finalizing,
                Some(100),
                None,
            );
            guard::commit(&tmp, out).map_err(|e| format!("提交产物失败：{e}"))?;
            Ok(())
        }
        Err(e) => {
            guard::discard(&tmp);
            Err(e)
        }
    }
}

/// 核心转换入口（库接口）：完全静默，无 stdout 副作用。
/// 语义与 CLI 一致：`<input...> --to <target> [-o <output>]`。
pub fn convert(
    inputs: &[&Path],
    target: &str,
    output: Option<&Path>,
    key: Option<&str>,
) -> Result<Option<PathBuf>, String> {
    let em = Emitter::silent();
    let target = target.trim_start_matches('.').to_ascii_lowercase();
    let input = inputs[0];
    let in_ext = ext_of(input);

    // 文档清洗：任意文本 -> clean
    if target == "clean" {
        let raw = std::fs::read(input).map_err(|e| format!("读取失败：{e}"))?;
        let text = String::from_utf8_lossy(&raw);
        let cleaned = clean::clean_text(&text);
        let out = match output {
            Some(o) => o.to_path_buf(),
            None => default_output(input, &in_ext),
        };
        std::fs::write(&out, cleaned).map_err(|e| format!("写入失败：{e}"))?;
        return Ok(Some(out));
    }

    em.progress(
        &input.to_string_lossy(),
        &target,
        "guard",
        Phase::Probing,
        None,
        Some("探测输入"),
    );

    // 多输入：图片合成 PDF
    if target == "pdf" && inputs.iter().all(|p| is_image(&ext_of(p))) {
        let out = match output {
            Some(o) => o.to_path_buf(),
            None => default_output(input, "pdf"),
        };
        let total: u64 = inputs.iter().map(|p| guard::probe_input(p).unwrap_or(0)).sum();
        return match images_to_pdf(inputs, &out, &em) {
            Ok(()) => {
                em.progress(
                    &input.to_string_lossy(),
                    "pdf",
                    "done",
                    Phase::Done,
                    None,
                    Some(&format!("{total} bytes, {} 页", inputs.len())),
                );
                Ok(Some(out))
            }
            Err(e) => Err(e),
        };
    }

    // 单输入路径（其余格式）
    let total_bytes = match guard::probe_input(input) {
        Ok(n) => n,
        Err(e) => {
            let msg = e.to_string();
            em.progress(
                &input.to_string_lossy(),
                &target,
                "guard",
                Phase::Failed,
                None,
                Some(&msg),
            );
            return Err(msg);
        }
    };

    let out = match output {
        Some(o) => o.to_path_buf(),
        None => default_output(input, &target),
    };

    // 音频提取：mp4/mp3/aac -> wav（FFmpeg 解码 -> PCM -> 手写 WAV）
    // PDF 文字提取：pdf -> txt
    if in_ext == "pdf" && target == "txt" {
        let out = match output { Some(o) => o.to_path_buf(), None => default_output(input, "txt") };
        let text = pdf_text::pdf_to_text(input)?;
        std::fs::write(&out, &text).map_err(|e| e.to_string())?;
        em.progress(&input.to_string_lossy(), &target, "pdf-extract", Phase::Finalizing, Some(100), None);
        return Ok(Some(out));
    }

    // 缩略图网格：mp4 -> 3x3 网格 JPEG
    if crate::guard::is_av_ext(in_ext.as_str()) && target == "collage" {
        let out = match output { Some(o) => o.to_path_buf(), None => default_output(input, "jpg") };
        av::av_to_collage(input, &out)?;
        em.progress(&input.to_string_lossy(), &target, "collage", Phase::Finalizing, Some(100), None);
        return Ok(Some(out));
    }

    if crate::guard::is_av_ext(in_ext.as_str()) && target == "wav" {
        let out = match output { Some(o) => o.to_path_buf(), None => default_output(input, "wav") };
        av::av_to_wav(input, &out)?;
        em.progress(&input.to_string_lossy(), &target, "audio-wav", Phase::Finalizing, Some(100), None);
        return Ok(Some(out));
    }

    // 视频转码：mp4 -> gif（H.264 解码→RGB→GIF 动画编码）
    if crate::guard::is_av_ext(in_ext.as_str()) && target == "gif" {
        let out = match output { Some(o) => o.to_path_buf(), None => default_output(input, "gif") };
        av::av_to_gif(input, &out, 8)?;
        em.progress(&input.to_string_lossy(), &target, "transcode-gif", Phase::Finalizing, Some(100), None);
        return Ok(Some(out));
    }

    // 音视频转码：mp4 -> mp4（H.264 解码→重编码→封装）
    if crate::guard::is_av_ext(in_ext.as_str()) && in_ext == "mp4" && target == "mp4" {
        let out = match output { Some(o) => o.to_path_buf(), None => default_output(input, "mp4") };
        av::av_transcode_mp4(input, &out)?;
        em.progress(&input.to_string_lossy(), &target, "transcode", Phase::Finalizing, Some(100), None);
        return Ok(Some(out));
    }

    // 视频抽多帧：mp4/mkv -> jpgs（首/中/尾 3 帧）
    if crate::guard::is_av_ext(in_ext.as_str()) && target == "jpgs" {
        let out = match output { Some(o) => o.to_path_buf(), None => default_output(input, "jpg") };
        let outs = av::av_extract_frames(input, &out)?;
        em.progress(&input.to_string_lossy(), &target, "extract-frames", Phase::Finalizing, Some(100), None);
        return Ok(Some(outs[0].clone()));
    }

    // 视频抽帧：mp4/mkv -> jpg/png（第一帧）
    if crate::guard::is_av_ext(in_ext.as_str()) && (target == "jpg" || target == "jpeg" || target == "png") {
        let bytes = av::av_extract_frame(input)?;
        let out = match output { Some(o) => o.to_path_buf(), None => default_output(input, "jpg") };
        std::fs::write(&out, &bytes).map_err(|e| format!("写出失败：{e}"))?;
        em.progress(&input.to_string_lossy(), &target, "extract-frame", Phase::Finalizing, Some(100), None);
        return Ok(Some(out));
    }

    // 音视频元数据探测：任意音视频 -> json/probe
    if crate::guard::is_av_ext(in_ext.as_str()) && (target == "json" || target == "probe") {
        let v = av::av_probe(input)?;
        let out = match output { Some(o) => o.to_path_buf(), None => default_output(input, "json") };
        std::fs::write(&out, serde_json::to_string_pretty(&v).map_err(|e| e.to_string())?)
            .map_err(|e| format!("写出失败：{e}"))?;
        em.progress(&input.to_string_lossy(), "json", "av-probe", Phase::Finalizing, Some(100), None);
        return Ok(Some(out));
    }

    // 旧版 Office 格式桥：.doc/.xls/.ppt -> 现代格式（docx/xlsx/pptx）-> 主链
    if office::is_legacy_office(in_ext.as_str())
        && !office::is_native_supported(in_ext.as_str(), target.as_str())
    {
        let mid = office::to_modern(input, &in_ext)?;
        if office::is_modern_equivalent(in_ext.as_str(), target.as_str()) {
            std::fs::copy(&mid, &out).map_err(|e| {
                let msg = format!("复制桥产物失败：{e}");
                msg
            })?;
            let _ = std::fs::remove_file(&mid);
            em.progress(&input.to_string_lossy(), &target, "office-bridge", Phase::Finalizing, Some(100), None);
            return Ok(Some(out));
        }
        em.progress(&input.to_string_lossy(), &target, "office-bridge", Phase::Finalizing, Some(100), None);
        let out2 = out;
        let r = convert(&[&mid], &target, Some(&out2), key);
        let _ = std::fs::remove_file(&mid);
        return r;
    }

    // 压缩域：任意文件 -> zip/gz/tar/tgz（单文件打包，进程内毫秒级）
    if matches!(target.as_str(), "zip" | "gz" | "tar" | "tgz") && in_ext != target {
        let b = archive::compress(input, &target)?;
        let out = match output { Some(o) => o.to_path_buf(), None => default_output(input, &target) };
        std::fs::write(&out, &b).map_err(|e| format!("写出失败：{e}"))?;
        return Ok(Some(out));
    }
    // 解压域：zip/gz/tar/tgz -> unzip/ungz/untar（单条目解到目标文件，多条目解到目录）
    if matches!(target.as_str(), "unzip" | "ungz" | "untar") {
        let need = match target.as_str() {
            "unzip" => "zip",
            "ungz" => "gz",
            _ => "tar/tgz",
        };
        if !matches!(
            (target.as_str(), in_ext.as_str()),
            ("unzip", "zip") | ("ungz", "gz") | ("untar", "tar") | ("untar", "tgz")
        ) {
            return Err(format!(
                "目标 {target} 仅适用于 {need} 输入（当前输入为 .{in_ext}）"
            ));
        }
        let out = match output { Some(o) => o.to_path_buf(), None => default_output(input, &target) };
        match target.as_str() {
            "unzip" => archive::decompress_zip(input, &out)?,
            "ungz" => archive::decompress_gz(input, &out)?,
            _ => archive::decompress_tar(input, &out)?,
        }
        return Ok(Some(out));
    }
    // 加密域：任意文件 -> enc（AES-256-GCM，口令 -k/--key 或环境变量 CONVERT_LITE_KEY）
    if target == "enc" {
        let pass: &str = match key {
            Some(k) => k,
            None => {
                let env = std::env::var("CONVERT_LITE_KEY").map_err(|_| {
                    "加密需要口令：-k <pass> 或设置 CONVERT_LITE_KEY".to_string()
                })?;
                Box::leak(env.into_boxed_str())
            }
        };
        let out = match output { Some(o) => o.to_path_buf(), None => default_output(input, "enc") };
        crypto::encrypt_file(input, &out, pass)?;
        return Ok(Some(out));
    }
    // 解密域：.cvlt -> dec
    if in_ext == "cvlt" && target == "dec" {
        let pass: &str = match key {
            Some(k) => k,
            None => {
                let env = std::env::var("CONVERT_LITE_KEY").map_err(|_| {
                    "解密需要口令：-k <pass> 或设置 CONVERT_LITE_KEY".to_string()
                })?;
                Box::leak(env.into_boxed_str())
            }
        };
        let out = match output { Some(o) => o.to_path_buf(), None => default_output(input, "dec") };
        crypto::decrypt_file(input, &out, pass)?;
        return Ok(Some(out));
    }

    let result: Result<(), String> = match (in_ext.as_str(), target.as_str()) {
        (s, t) if is_image(s) && is_image(t) => image::image_convert(input, &out, &target, &em),
        ("json", "yaml") | ("yaml", "json") => {
            text::json_yaml(input, &out, in_ext == "json", &em)
        }
        ("csv", "jsonl") => text::csv_to_jsonl(input, &out, &em),
        // 第十二轮：soffice 替代链 csv -> xlsx
        ("csv", "xlsx") => {
            let b = xlsx::csv_to_xlsx_bytes(input)?;
            std::fs::write(&out, &b).map_err(|e| e.to_string())?;
            Ok(())
        }
        // 第十二轮：soffice 替代链 csv -> ods
        ("csv", "ods") => {
            let b = xlsx::csv_to_ods_bytes(input)?;
            std::fs::write(&out, &b).map_err(|e| e.to_string())?;
            Ok(())
        }
        // 第九轮：TXT/CSV 表格/FB2/OPML + 宏格式别名
        ("txt", "md") | ("txt", "markdown") => {
            let md = txt_md::txt_to_md(input)?;
            std::fs::write(&out, md.as_bytes()).map_err(|e| e.to_string())?;
            Ok(())
        }
                // 第十二轮：Pandoc 收尾 小格式 writer（opendocument/plain/man/texi/context）
        ("md", "opendocument") => {
            md_misc::md_to_opendocument(input, &out)?;
            Ok(())
        }
        ("md", "plain") => {
            md_misc::md_to_plain(input, &out)?;
            Ok(())
        }
        ("md", "man") => {
            md_misc::md_to_man(input, &out)?;
            Ok(())
        }
        ("md", "texi") => {
            md_misc::md_to_texi(input, &out)?;
            Ok(())
        }
                ("md", "pptx") => {
            md_pptx::md_to_pptx(input, &out)?;
            Ok(())
        }
("md", "context") => {
            md_misc::md_to_context(input, &out)?;
            Ok(())
        }
("md", "txt") | ("markdown", "txt") => {
            let txt = txt_md::md_to_txt(input)?;
            std::fs::write(&out, txt.as_bytes()).map_err(|e| e.to_string())?;
            Ok(())
        }
        ("csv", "md") | ("csv", "markdown") => {
            let md = txt_md::csv_to_md(input)?;
            std::fs::write(&out, md.as_bytes()).map_err(|e| e.to_string())?;
            Ok(())
        }
        ("fb2", "md") | ("fb2", "markdown") => {
            let md = fb2_md::fb2_to_md(input)?;
            std::fs::write(&out, md.as_bytes()).map_err(|e| e.to_string())?;
            Ok(())
        }
        ("opml", "md") | ("opml", "markdown") => {
            let md = opml_md::opml_to_md(input)?;
            std::fs::write(&out, md.as_bytes()).map_err(|e| e.to_string())?;
            Ok(())
        }
        // 第十轮：RST/DokuWiki/Textile
        ("rst", "md") | ("rst", "markdown") => {
            let md = rst_md::rst_to_md(input)?;
            std::fs::write(&out, md.as_bytes()).map_err(|e| e.to_string())?;
            Ok(())
        }
        ("md", "rst") | ("markdown", "rst") => {
            let rst = rst_md::md_to_rst(input)?;
            std::fs::write(&out, rst.as_bytes()).map_err(|e| e.to_string())?;
            Ok(())
        }
        ("dokuwiki", "md") | ("dokuwiki", "markdown") => {
            let md = dokuwiki_md::dokuwiki_to_md(input)?;
            std::fs::write(&out, md.as_bytes()).map_err(|e| e.to_string())?;
            Ok(())
        }
        ("md", "dokuwiki") | ("markdown", "dokuwiki") => {
            let dw = dokuwiki_md::md_to_dokuwiki(input)?;
            std::fs::write(&out, dw.as_bytes()).map_err(|e| e.to_string())?;
            Ok(())
        }
        ("textile", "md") | ("textile", "markdown") => {
            let md = textile_md::textile_to_md(input)?;
            std::fs::write(&out, md.as_bytes()).map_err(|e| e.to_string())?;
            Ok(())
        }
        ("md", "textile") | ("markdown", "textile") => {
            let tx = textile_md::md_to_textile(input)?;
            std::fs::write(&out, tx.as_bytes()).map_err(|e| e.to_string())?;
            Ok(())
        }
        // 第十一轮：MOBI 输入 / PDF 文本层提取
        ("mobi", "md") | ("mobi", "markdown") => {
            let md = mobi_md::mobi_to_md(input)?;
            std::fs::write(&out, md.as_bytes()).map_err(|e| e.to_string())?;
            Ok(())
        }
        ("pdf", "txt") => {
            let txt = pdf_text::pdf_to_text(input)?;
            std::fs::write(&out, txt.as_bytes()).map_err(|e| e.to_string())?;
            Ok(())
        }
        ("docm", "md") | ("docm", "markdown") => {
            let md = doc::docx_to_markdown(input)?;
            std::fs::write(&out, md.as_bytes()).map_err(|e| e.to_string())?;
            Ok(())
        }
        ("xlsm", "csv") => xlsx_to(input, &out, "csv", &em),
        ("xlsm", "html") => xlsx_to(input, &out, "html", &em),
        ("pptm", "html") => pptx_to_html(input, &out, &em),
        ("pptm", "pdf") => pptx_to_pdf(input, &out, &em),
        // 第七轮原生层：旧版 Office 二进制（OLE2 复合文档）直接解析
        ("doc", "md") | ("doc", "markdown") => {
            let md = doc_legacy::doc_to_md(input)?;
            std::fs::write(&out, md.as_bytes()).map_err(|e| e.to_string())?;
            Ok(())
        }
        ("xls", "csv") => {
            let csv = xls_legacy::xls_to_csv(input)?;
            std::fs::write(&out, csv.as_bytes()).map_err(|e| e.to_string())?;
            Ok(())
        }
        // 第十二轮：soffice 替代链 xls -> xlsx（两跳：xls -> csv(临时) -> xlsx）
        ("xls", "xlsx") => {
            let csv = xls_legacy::xls_to_csv(input)?;
            let tmp = std::env::temp_dir().join(format!(
                "convert_lite_xlsx_{}.csv",
                std::process::id()
            ));
            std::fs::write(&tmp, csv.as_bytes()).map_err(|e| e.to_string())?;
            let r = (|| -> Result<(), String> {
                let b = xlsx::csv_to_xlsx_bytes(&tmp)?;
                std::fs::write(&out, &b).map_err(|e| e.to_string())?;
                Ok(())
            })();
            let _ = std::fs::remove_file(&tmp);
            r?;
            Ok(())
        }
        // 第十三轮：表格族互通 xls -> pdf（xls -> csv(内存) -> 表格渲染）
        ("xls", "pdf") => {
            let b = xlsx::xls_to_pdf_bytes(input)?;
            std::fs::write(&out, &b).map_err(|e| e.to_string())?;
            Ok(())
        }
        // 第十三轮：表格族互通 xls -> ods（两跳 xls -> csv(临时) -> ods）
        ("xls", "ods") => {
            let csv = xls_legacy::xls_to_csv(input)?;
            let tmp = std::env::temp_dir().join(format!(
                "convert_lite_ods_{}.csv",
                std::process::id()
            ));
            std::fs::write(&tmp, csv.as_bytes()).map_err(|e| e.to_string())?;
            let r = (|| -> Result<(), String> {
                let b = xlsx::csv_to_ods_bytes(&tmp)?;
                std::fs::write(&out, &b).map_err(|e| e.to_string())?;
                Ok(())
            })();
            let _ = std::fs::remove_file(&tmp);
            r?;
            Ok(())
        }
        ("ppt", "md") | ("ppt", "markdown") => {
            let md = ppt_legacy::ppt_to_md(input)?;
            std::fs::write(&out, md.as_bytes()).map_err(|e| e.to_string())?;
            Ok(())
        }
        // 第十二轮：PPT 族补链（soffice 替代链）pptx -> md
        ("pptx", "md") | ("pptx", "markdown") => {
            let md = pptx::pptx_to_md(input)?;
            std::fs::write(&out, md.as_bytes()).map_err(|e| e.to_string())?;
            Ok(())
        }
        ("md", "html") | ("markdown", "html") => text::md_to_html(input, &out, &em),
        ("epub", "md") | ("epub", "markdown") => {
            let md = epub_md::epub_to_md(input)?;
            std::fs::write(&out, md.as_bytes()).map_err(|e| e.to_string())?;
            Ok(())
        },
        ("html", "md") | ("html", "markdown") => {
            let md = html_md::html_to_md(input)?;
            std::fs::write(&out, md.as_bytes()).map_err(|e| e.to_string())?;
            Ok(())
        },
        ("ipynb", "md") | ("ipynb", "markdown") => {
            let md = ipynb_md::ipynb_to_md(input)?;
            std::fs::write(&out, md.as_bytes()).map_err(|e| e.to_string())?;
            Ok(())
        },
        ("wiki", "md") | ("mediawiki", "md") | ("wiki", "markdown") | ("mediawiki", "markdown") => {
            let md = mediawiki_md::mediawiki_to_md(input)?;
            std::fs::write(&out, md.as_bytes()).map_err(|e| e.to_string())?;
            Ok(())
        },
        ("org", "md") | ("org", "markdown") => {
            let md = org_md::org_to_md(input)?;
            std::fs::write(&out, md.as_bytes()).map_err(|e| e.to_string())?;
            Ok(())
        },
        ("rtf", "md") | ("rtf", "markdown") => {
            let md = rtf_md::rtf_to_md(input)?;
            std::fs::write(&out, md.as_bytes()).map_err(|e| e.to_string())?;
            Ok(())
        },
        ("odt", "md") | ("odt", "markdown") => {
            let md = odt_md::odt_to_md(input)?;
            std::fs::write(&out, md.as_bytes()).map_err(|e| e.to_string())?;
            Ok(())
        },
        ("dbk", "md") | ("docbook", "md") => {
            let md = docbook_md::docbook_to_md(input)?;
            std::fs::write(&out, md.as_bytes()).map_err(|e| e.to_string())?;
            Ok(())
        },
        ("jats", "md") | ("xml", "md") => {
            let md = jats_md::jats_to_md(input)?;
            std::fs::write(&out, md.as_bytes()).map_err(|e| e.to_string())?;
            Ok(())
        },
        ("md", "mediawiki") | ("markdown", "mediawiki") => {
            let wiki = md_mediawiki::md_to_mediawiki(input)?;
            std::fs::write(&out, wiki.as_bytes()).map_err(|e| e.to_string())?;
            Ok(())
        },
        ("md", "org") | ("markdown", "org") => {
            let org = md_org::md_to_org(input)?;
            std::fs::write(&out, org.as_bytes()).map_err(|e| e.to_string())?;
            Ok(())
        },
        ("md", "rtf") | ("markdown", "rtf") => {
            let rtf = md_rtf::md_to_rtf(input)?;
            std::fs::write(&out, rtf.as_bytes()).map_err(|e| e.to_string())?;
            Ok(())
        },
        ("md", "odt") | ("markdown", "odt") => {
            md_odt::md_to_odt(input, &out)?;
            Ok(())
        },
        ("md", "docbook") | ("markdown", "docbook") => {
            let db = md_docbook::md_to_docbook(input)?;
            std::fs::write(&out, db.as_bytes()).map_err(|e| e.to_string())?;
            Ok(())
        },
        ("md", "jats") | ("markdown", "jats") => {
            let j = md_jats::md_to_jats(input)?;
            std::fs::write(&out, j.as_bytes()).map_err(|e| e.to_string())?;
            Ok(())
        },
        ("md", "latex") | ("markdown", "latex") | ("md", "tex") | ("markdown", "tex") => {
            let tex = latex::md_to_latex(input)?;
            std::fs::write(&out, tex.as_bytes()).map_err(|e| e.to_string())?;
            Ok(())
        },
        ("latex", "md") | ("tex", "md") | ("latex", "markdown") | ("tex", "markdown") => {
            let md = latex::latex_to_md(input)?;
            std::fs::write(&out, md.as_bytes()).map_err(|e| e.to_string())?;
            Ok(())
        },
        ("md", "ipynb") | ("markdown", "ipynb") => {
            let nb = md_ipynb::md_to_ipynb(input)?;
            std::fs::write(&out, nb.as_bytes()).map_err(|e| e.to_string())?;
            Ok(())
        },
        ("md", "docx") | ("markdown", "docx") => md_docx::md_to_docx(input, &out),
        ("md", "epub") | ("markdown", "epub") => md_docx::md_to_epub(input, &out),
        // 第十二轮：soffice 替代链 md -> pdf（两跳：md -> docx(临时) -> pdf，进程内）
        ("md", "pdf") | ("markdown", "pdf") => {
            let tmp = std::env::temp_dir().join(format!(
                "convert_lite_mdpdf_{}.docx",
                std::process::id()
            ));
            md_docx::md_to_docx(input, &tmp)?;
            let r = (|| -> Result<(), String> {
                let bytes = doc_pdf::docx_to_pdf_bytes(&tmp)?;
                std::fs::write(&out, &bytes).map_err(|e| format!("写出失败：{e}"))?;
                Ok(())
            })();
            let _ = std::fs::remove_file(&tmp);
            r?;
            Ok(())
        }
        ("docx", "md") | ("docx", "markdown") => {
            let md = doc::docx_to_markdown(input)?;
            std::fs::write(&out, md.as_bytes()).map_err(|e| e.to_string())?;
            Ok(())
        },
        ("docx", "html") => docx_to_html(input, &out, &em),
        ("docx", "pdf") => docx_to_pdf(input, &out, &em),
        ("pptx", "html") => pptx_to_html(input, &out, &em),
        ("pptx", "pdf") => pptx_to_pdf(input, &out, &em),
        ("xlsx", "csv") => xlsx_to(input, &out, "csv", &em),
        ("xlsx", "html") => xlsx_to(input, &out, "html", &em),
        // 第十二轮：soffice 替代链 xlsx -> pdf（表格渲染）
        ("xlsx", "pdf") => {
            let b = xlsx::xlsx_to_pdf_bytes(input)?;
            std::fs::write(&out, &b).map_err(|e| e.to_string())?;
            Ok(())
        }
        // 第十三轮：表格族互通 xlsx -> ods
        ("xlsx", "ods") => {
            let b = xlsx::xlsx_to_ods_bytes(input)?;
            std::fs::write(&out, &b).map_err(|e| e.to_string())?;
            Ok(())
        }
        (s, t) => match combo_convert(input, s, t, &out, &em) {
            Ok(true) => Ok(()),
            Ok(false) => Err(format!(
                "不支持的转换：{s} -> {t}（本实验为轻量化子集，未编排外部引擎）"
            )),
            Err(e) => Err(e),
        },
    };

    match result {
        Ok(()) => {
            em.progress(
                &input.to_string_lossy(),
                &target,
                "done",
                Phase::Done,
                None,
                Some(&format!("输入 {total_bytes} bytes")),
            );
            Ok(Some(out))
        }
        Err(e) => {
            em.progress(
                &input.to_string_lossy(),
                &target,
                "failed",
                Phase::Failed,
                None,
                Some(&e),
            );
            Err(e)
        }
    }
}

/// 交叉边组合转换：输入可读为 md 且目标可由 md 写出时，走 A -> md(临时) -> B。
/// 覆盖 epub→docx、mediawiki→org、org→mediawiki、docx→mediawiki、jats→docx
/// 等所有未直接编排的 reader×writer 组合；中间 md 用后即删。
fn combo_convert(
    input: &Path,
    in_ext: &str,
    target: &str,
    out: &Path,
    em: &Emitter,
) -> Result<bool, String> {
    if in_ext == target || in_ext == "md" || in_ext == "markdown" {
        return Ok(false);
    }
    let md: String = match in_ext {
        "epub" => epub_md::epub_to_md(input)?,
        "html" => html_md::html_to_md(input)?,
        "ipynb" => ipynb_md::ipynb_to_md(input)?,
        "wiki" | "mediawiki" => mediawiki_md::mediawiki_to_md(input)?,
        "org" => org_md::org_to_md(input)?,
        "rtf" => rtf_md::rtf_to_md(input)?,
        "odt" => odt_md::odt_to_md(input)?,
        "dbk" | "docbook" => docbook_md::docbook_to_md(input)?,
        "jats" | "xml" => jats_md::jats_to_md(input)?,
        "latex" | "tex" => latex::latex_to_md(input)?,
        "docx" | "docm" => doc::docx_to_markdown(input)?,
        // 第十二轮：soffice 替代链旧格式 reader（.doc/.ppt -> md -> B）
        "doc" => doc_legacy::doc_to_md(input)?,
        "ppt" => ppt_legacy::ppt_to_md(input)?,
        "pptx" => pptx::pptx_to_md(input)?,
        "txt" => txt_md::txt_to_md(input)?,
        "fb2" => fb2_md::fb2_to_md(input)?,
        "opml" => opml_md::opml_to_md(input)?,
        "rst" => rst_md::rst_to_md(input)?,
        "dokuwiki" => dokuwiki_md::dokuwiki_to_md(input)?,
        "textile" => textile_md::textile_to_md(input)?,
        "mobi" => mobi_md::mobi_to_md(input)?,
        _ => return Ok(false),
    };
    let tmp = std::env::temp_dir().join(format!(
        "convert_lite_combo_{}.md",
        std::process::id()
    ));
    std::fs::write(&tmp, md.as_bytes()).map_err(|e| e.to_string())?;
    let r: Result<(), String> = match target {
        "html" => text::md_to_html(&tmp, out, em),
        "docx" => md_docx::md_to_docx(&tmp, out),
        "epub" => md_docx::md_to_epub(&tmp, out),
        "ipynb" => {
            let nb = md_ipynb::md_to_ipynb(&tmp)?;
            std::fs::write(out, nb.as_bytes()).map_err(|e| e.to_string())
        }
        "mediawiki" => {
            let w = md_mediawiki::md_to_mediawiki(&tmp)?;
            std::fs::write(out, w.as_bytes()).map_err(|e| e.to_string())
        }
        "org" => {
            let o = md_org::md_to_org(&tmp)?;
            std::fs::write(out, o.as_bytes()).map_err(|e| e.to_string())
        }
        "rtf" => {
            let r = md_rtf::md_to_rtf(&tmp)?;
            std::fs::write(out, r.as_bytes()).map_err(|e| e.to_string())
        }
        "odt" => md_odt::md_to_odt(&tmp, out),
        "docbook" => {
            let d = md_docbook::md_to_docbook(&tmp)?;
            std::fs::write(out, d.as_bytes()).map_err(|e| e.to_string())
        }
        "jats" => {
            let j = md_jats::md_to_jats(&tmp)?;
            std::fs::write(out, j.as_bytes()).map_err(|e| e.to_string())
        }
        "latex" | "tex" => {
            let t = latex::md_to_latex(&tmp)?;
            std::fs::write(out, t.as_bytes()).map_err(|e| e.to_string())
        }
        // 第十二轮：soffice 替代链 pdf writer（md -> docx(临时) -> pdf）
        "pdf" => {
            let docx_tmp = std::env::temp_dir().join(format!(
                "convert_lite_combo_{}.docx",
                std::process::id()
            ));
            md_docx::md_to_docx(&tmp, &docx_tmp)?;
            let r = (|| -> Result<(), String> {
                let b = doc_pdf::docx_to_pdf_bytes(&docx_tmp)?;
                std::fs::write(out, &b).map_err(|e| e.to_string())?;
                Ok(())
            })();
            let _ = std::fs::remove_file(&docx_tmp);
            r?;
            Ok(())
        }
        // 第十三轮：组合面全连接（md 全部 writer 目标均可作 combo 目标）
        "txt" => {
            let t = txt_md::md_to_txt(&tmp)?;
            std::fs::write(out, t.as_bytes()).map_err(|e| e.to_string())
        }
        "rst" => {
            let t = rst_md::md_to_rst(&tmp)?;
            std::fs::write(out, t.as_bytes()).map_err(|e| e.to_string())
        }
        "dokuwiki" => {
            let t = dokuwiki_md::md_to_dokuwiki(&tmp)?;
            std::fs::write(out, t.as_bytes()).map_err(|e| e.to_string())
        }
        "textile" => {
            let t = textile_md::md_to_textile(&tmp)?;
            std::fs::write(out, t.as_bytes()).map_err(|e| e.to_string())
        }
        "pptx" => md_pptx::md_to_pptx(&tmp, out),
        "opendocument" => md_misc::md_to_opendocument(&tmp, out),
        "plain" => md_misc::md_to_plain(&tmp, out),
        "man" => md_misc::md_to_man(&tmp, out),
        "texi" => md_misc::md_to_texi(&tmp, out),
        "context" => md_misc::md_to_context(&tmp, out),
        _ => {
            let _ = std::fs::remove_file(&tmp);
            return Ok(false);
        }
    };
    let _ = std::fs::remove_file(&tmp);
    r?;
    Ok(true)
}
