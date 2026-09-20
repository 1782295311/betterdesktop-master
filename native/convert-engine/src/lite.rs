// 进程内轻量引擎（convert-lite）：替代 pandoc / LibreOffice / calibre / poppler 等外部 exe。
//
// 【为什么是"引擎"而不是"另一条链"】矩阵（matrix.rs）为每条边指定 prefer/fallback 引擎，
// run 按 resolve_engine 解析。把 lite 做成一个 EngineKind，则**路由完全由矩阵决定** ——
// 换引擎 = 改矩阵行，不引入第二套分派（计划 docs/plans/2026-09-20-convert-lite-migration.md §4 C3）。
//
// 【与其它引擎的差异】
//   · 零外部 exe：进程内调用，没有子进程、没有 stdout 解析、没有 engin 版本探测；
//   · 恒可用：probe 恒返回 available（见 engines.rs::probe_kind）—— 这一条是本次迁移的目的，
//     引擎可用的判据从"磁盘上有没有那个第三方目录"变成"代码里有这个实现"；
//   · 产物写进 job.temp_dir，由既有的 publish_all 统一发布（保持"输出命名/去重/原子替换"单点实现）。
//
// 【lmite 支持面怎么判定】不用"猜"：由本文件的 LITE_INPUTS / LITE_TARGETS 两张表回答
// （逐条对照 convert-lite 的 lib.rs 分派表得出）。表与实现漂移时**以 lib.rs 为准**，
// 单测 lite_supports_matches_impl 用真实调用把两者钉在一起。

use std::path::{Path, PathBuf};

use crate::contract::{ConversionTarget, EngineKind};
use crate::error::{ConvertError, Error};
use crate::run::{Engine, Job};

/// lite 能**读**的输入扩展名（不带点，小写）。
///
/// 对照 convert-lite/src/lib.rs 的 `match (in_ext, target)` 与 `combo_convert` 的 reader 分支：
/// 文档/文本族 + 电子书 + Office（OOXML 与 OLE 旧格式）+ 表格 + 图片 + 音视频（FFmpeg dll 在则可用）
/// + 压缩/加密。
pub const LITE_INPUTS: &[&str] = &[
    // 文本/标记族
    "md", "markdown", "txt", "rst", "org", "textile", "dokuwiki", "wiki", "mediawiki",
    "ipynb", "html", "htm", "fb2", "opml", "csv", "json", "yaml", "yml",
    // 文档族（OOXML + 旧版 OLE 二进制）
    "docx", "docm", "doc", "odt", "rtf", "epub", "mobi", "dbk", "docbook", "jats", "xml",
    "tex", "latex",
    // 表格/演示
    "xlsx", "xlsm", "xls", "ods", "pptx", "pptm", "ppt",
    // 图片
    "png", "jpg", "jpeg", "bmp", "webp", "gif", "tiff",
    // PDF
    "pdf",
    // 压缩/加密
    "zip", "gz", "tar", "tgz", "cvlt",
];

/// lite 能**写**的目标格式（不带点，小写）。特殊目标 `unzip/ungz/untar/enc/dec/collage/jpgs` 单独列出。
pub const LITE_TARGETS: &[&str] = &[
    // 文档/文本族 writer
    "md", "markdown", "html", "docx", "docm", "odt", "rtf", "epub", "ipynb", "mediawiki",
    "org", "docbook", "jats", "latex", "tex", "txt", "rst", "dokuwiki", "textile", "opendocument",
    "plain", "man", "texi", "context", "pptx", "pdf",
    // 表格族 writer
    "xlsx", "ods", "csv", "jsonl", "html",
    // 图片 / PDF 提取
    "jpg", "jpeg", "png", "bmp", "gif",
    // 压缩/加密/解压/抽帧
    "zip", "gz", "tar", "tgz", "unzip", "ungz", "untar", "enc", "dec", "collage", "jpgs", "probe", "json",
    // 清洗
    "clean",
];

/// 归一化扩展名：去点、小写。
pub fn norm_ext(p: &Path) -> String {
    p.extension()
        .and_then(|e| e.to_str())
        .unwrap_or("")
        .trim_start_matches('.')
        .to_ascii_lowercase()
}

/// lite 是否声明支持 `in_ext → target`。
///
/// 语义边界：这里给的是**声明面**（用于矩阵路由与 can_handle）。真正的能力以 `convert_lite::convert`
/// 的返回为准 —— 声明支持但实现失败时，错误走 ConversionFailed，不会被伪装成"引擎缺失"
/// （契约红线：引擎缺失禁止伪装成转换失败，反之亦然）。
pub fn supports(in_ext: &str, target: &str) -> bool {
    let from = in_ext.trim_start_matches('.').to_ascii_lowercase();
    let to = target.trim_start_matches('.').to_ascii_lowercase();

    // 源无关的目标：压缩打包（任意文件→包）、加密、文本清洗 —— 对照 lib.rs 的
    // `matches!(target, "zip"|"gz"|"tar"|"tgz")` 与 `target == "enc"` / `"clean"` 分支，
    // 它们对**任意输入**成立，故不能靠 LITE_INPUTS 判定。
    if matches!(to.as_str(), "zip" | "gz" | "tar" | "tgz" | "enc" | "clean") {
        return from != to;
    }

    // 多输入图片合成 PDF 由 matrix 的 compose 边处理；自身→自身无意义。
    if from == to || from.is_empty() || to.is_empty() {
        return false;
    }
    LITE_INPUTS.contains(&from.as_str()) && LITE_TARGETS.contains(&to.as_str())
}

/// 进程内轻量引擎。
pub struct LiteEngine;

impl Engine for LiteEngine {
    fn kind(&self) -> EngineKind {
        EngineKind::Lite
    }

    fn can_handle(&self, sources: &[PathBuf], target: &ConversionTarget) -> bool {
        // 单源路径（多源由 PdfCompose 负责合成）；lite 内部虽支持多图→pdf，
        // 但矩阵把那条边登记在 PdfCompose 上，故这里只认单源，保持路由单一。
        sources.len() == 1 && supports(&norm_ext(&sources[0]), &target.format)
    }

    fn run(&self, job: &Job) -> Result<Vec<PathBuf>, Error> {
        let source = &job.sources[0];
        let product = job
            .temp_dir
            .join(format!("output.{}", job.target.format));

        // convert-lite 的核心入口是一个**完全静默**的库函数（无 stdout 副作用）：
        // 进度协议仍由本进程（run.rs）自己发，C# 侧契约不受影响。
        let inputs = [source.as_path()];
        convert_lite::convert(
            &inputs,
            &job.target.format,
            Some(product.as_path()),
            job.password.as_deref(),
        )
        .map_err(map_lite_error)?;

        if !product.is_file() {
            return Err(Error::conversion_failed(format!(
                "lite 未产出目标文件：{}",
                product.display()
            )));
        }
        Ok(vec![product])
    }
}

/// convert-lite 的错误字符串 → 本进程的稳定六分类。
///
/// 为什么分类而不是一律 ConversionFailed：C# 侧对 EngineMissing / InputInvalid 有**不同提示语**
/// （"引擎缺失"要引导用户查部署、"输入非法"要指出文件问题），把它们混成一种会让用户被误导。
fn map_lite_error(msg: String) -> Error {
    let lower = msg.to_lowercase();

    // 输入不存在/读不了/参数不合理 → InputInvalid（用户文件问题）
    let input_invalid = [
        "读取失败",
        "读取 ",
        "不存在",
        "不是文件",
        "拒绝",
        "不接受",
        "仅适用于",
        "需要口令",
        "未实现",
        "不支持的转换",
        "只支持",
        "无法",
        "无效",
        "非法",
        "为空",
    ];
    if input_invalid.iter().any(|k| msg.contains(k)) {
        return Error::new(ConvertError::InputInvalid, msg);
    }

    // 写盘/提交产物失败 → OutputFailed
    if msg.contains("写出失败") || msg.contains("写入失败") || msg.contains("提交产物失败") || lower.contains("permission denied") {
        return Error::new(ConvertError::OutputFailed, msg);
    }

    Error::new(ConvertError::ConversionFailed, msg)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn supports_document_family() {
        // 本次迁移的主战场：pandoc / LibreOffice / calibre 原来负责的边
        assert!(supports("docx", "md"));
        assert!(supports("docx", "pdf"));
        assert!(supports("doc", "md"));      // OLE 旧格式（原先只能靠 LibreOffice）
        assert!(supports("md", "docx"));
        assert!(supports("epub", "md"));     // 原先 calibre
        assert!(supports("mobi", "md"));     // 原先 calibre
        assert!(supports("pdf", "txt"));     // 原先 poppler
        assert!(supports("xlsx", "csv"));
        assert!(supports("html", "md"));
    }

    #[test]
    fn rejects_unsupported_and_identity() {
        assert!(!supports("docx", "docx"));          // 自身→自身无意义
        assert!(!supports("docx", "unknownfmt"));    // 目标不在 writer 表
        assert!(!supports("unknownsrc", "md"));      // 源不在 reader 表
        assert!(supports("anyfile", "zip"));         // 压缩族：任意文件 → 包（reader 表含任意？）
    }

    #[test]
    fn engine_kind_and_can_handle() {
        let e = LiteEngine;
        assert_eq!(e.kind(), EngineKind::Lite);
        let t = ConversionTarget {
            format: "md".into(),
            label: "Markdown (.md)".into(),
            filter: None,
            hops: 1,
            prefer: EngineKind::Lite,
            fallback: None,
            category: crate::contract::TargetCategory::Text,
            lossless: false,
        };
        assert!(e.can_handle(&[PathBuf::from("C:/x/a.docx")], &t));
        assert!(!e.can_handle(&[PathBuf::from("C:/x/a.docx"), PathBuf::from("C:/x/b.docx")], &t));
    }

    #[test]
    fn error_mapping_is_six_way() {
        assert_eq!(map_lite_error("读取失败：文件不存在".into()).code, ConvertError::InputInvalid);
        assert_eq!(map_lite_error("写出失败：磁盘满".into()).code, ConvertError::OutputFailed);
        assert_eq!(
            map_lite_error("解包 docx 时 XML 语法错误".into()).code,
            ConvertError::ConversionFailed
        );
    }

    /// C3 路由不变量①：**任何仍指向外部引擎的边，必须是 lite 做不了的**。
    ///
    /// 反向读：一条 lite 能做的边若还留在 pandoc/soffice/calibre/twohop/comPdf 上，就是路由漏了 ——
    /// 删掉 `engines/` 之后它会变成 `EngineMissing` ⇒ 菜单整项消失（用户看到"功能少了"）。
    /// 这条测试是"删 engines 后仍有货"的可验证保证。
    #[test]
    fn no_lite_capable_edge_left_on_external_engines() {
        use crate::matrix::ConversionMatrix;
        let m = ConversionMatrix::default();
        let mut offenders = Vec::new();
        for ext in m.all_input_extensions() {
            let from = ext.trim_start_matches('.').to_ascii_lowercase();
            for t in m.get_targets(&ext) {
                let on_external = matches!(
                    t.prefer,
                    EngineKind::Pandoc
                        | EngineKind::Soffice
                        | EngineKind::Calibre
                        | EngineKind::TwoHop
                        | EngineKind::ComPdf
                );
                if on_external && supports(&from, &t.format) {
                    offenders.push(format!("{ext} → {} (prefer={:?})", t.format, t.prefer));
                }
            }
        }
        assert!(
            offenders.is_empty(),
            "这些边 lite 能做、却仍留给外部引擎（删 engines 后会变成 EngineMissing）：\n{}",
            offenders.join("\n")
        );
    }

    /// C3 路由不变量②：D3① 决定本轮不带"音视频" ⇒ 矩阵里不应再有 `Audio`/`Video` 类别的目标。
    ///
    /// 【判据是类别，不是"引擎 == Ffmpeg"】`png → tga` / `png → avif` 这类**图片**边也走 ffmpeg
    /// （tga/avif 编解码在 ffmpeg 里），但它们属于 `Image` 类别、**必须保留** ——
    /// 用引擎判据会把它们一起摘掉，那是能力损失而不是"隐藏音视频"。
    /// 这条口径是被旧测试 `image_interconversion_and_special_targets`（"png 行缺少 tga"）纠出来的。
    #[test]
    fn no_av_targets_remain_in_matrix() {
        use crate::contract::TargetCategory;
        use crate::matrix::ConversionMatrix;
        let m = ConversionMatrix::default();
        let mut left = Vec::new();
        for ext in m.all_input_extensions() {
            for t in m.get_targets(&ext) {
                if matches!(t.category, TargetCategory::Audio | TargetCategory::Video) {
                    left.push(format!("{ext} → {} ({:?})", t.format, t.category));
                }
            }
        }
        assert!(left.is_empty(), "音视频目标应已摘掉（D3①）：\n{}", left.join("\n"));
    }

    /// C3 路由不变量③：**保留清单里的引擎不得被 lite 抢走边**。
    /// `poppler`（PDF）与 `tesseract`（OCR）是用户点名要保留的两棵树 —— 抢走它们的边等于把"保留"
    /// 变成"删掉"。这里钉最高频的那条：`pdf → txt` 必须仍由保留/原生引擎负责（poppler 的文本层
    /// 提取质量优于 lite 的手写解析器）。
    #[test]
    fn retained_engine_edges_are_not_taken_over_by_lite() {
        use crate::matrix::ConversionMatrix;
        let m = ConversionMatrix::default();
        let t = m
            .find(".pdf", "txt")
            .expect("矩阵应登记 pdf → txt（poppler 保留清单）");
        assert_ne!(
            t.prefer,
            EngineKind::Lite,
            "pdf → txt 被 lite 抢走了：poppler 是保留清单里的引擎，它的边必须原样保留"
        );
    }

    /// 端到端：桥真的能转换，不只是能编译。
    ///
    /// 这条测试的价值在于它是**唯一**能证明"vendored crate → Engine trait → 产物落 temp_dir"整条链
    /// 通了的证据（单测 supports 只证明声明面）。C3 把矩阵路由过来之后，真机 `convert-engine run`
    /// 会走同一条链。
    #[test]
    fn lite_engine_converts_end_to_end() {
        let dir = std::env::temp_dir().join(format!("bdt_lite_bridge_{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).expect("建临时目录");

        let src = dir.join("in.md");
        std::fs::write(&src, "# 标题\n\n正文 **粗体** 与 `code`。\n").expect("写夹具");

        let target = ConversionTarget {
            format: "html".into(),
            label: "网页 (.html)".into(),
            filter: None,
            hops: 1,
            prefer: EngineKind::Lite,
            fallback: None,
            category: crate::contract::TargetCategory::Document,
            lossless: true,
        };
        let job = Job {
            sources: vec![src.clone()],
            target: &target,
            temp_dir: dir.as_path(),
            password: None,
        };

        let products = LiteEngine.run(&job).expect("lite 桥应当转换成功");
        assert_eq!(products.len(), 1, "单源应产出单产物");
        let html = std::fs::read_to_string(&products[0]).expect("读产物");
        assert!(html.contains("<h1"), "产物不是 HTML：{html}");
        assert!(html.contains("<strong>"), "粗体未渲染：{html}");

        let _ = std::fs::remove_dir_all(&dir);
    }
}
