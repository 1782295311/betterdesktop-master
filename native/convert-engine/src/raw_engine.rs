// Raw 引擎（S9.5 补全：相机 RAW 19 种输入——矩阵引用 152 处但执行层未注册，与 PdfText 同类的隐性缺口）。
// 语义锚定 C# RawDecodeEngine.cs：dcraw（LibRaw）子进程 -T -q 3 -o 0 输出高质量 TIFF
// → convert_image 重编码为目标图片格式。dcraw 缺失 = EngineMissing（不是文件错误）。
use std::path::{Path, PathBuf};

use crate::constants::TIMEOUT_MS;
use crate::contract::{ConversionTarget, EngineKind};
use crate::engines::locate_dcraw;
use crate::error::Error;
use crate::exec;
use crate::image_engine::convert_image;
use crate::matrix::RAW_EXTENSIONS;
use crate::run::{Engine, Job};

/// 相机 RAW 解码引擎（dcraw/LibRaw；与 C# RawDecodeEngine 逐参数对齐）。
pub struct RawEngine;

impl Engine for RawEngine {
    fn kind(&self) -> EngineKind {
        EngineKind::Raw
    }

    fn can_handle(&self, sources: &[PathBuf], target: &ConversionTarget) -> bool {
        sources.len() == 1
            && sources[0]
                .extension()
                .map(|e| {
                    let ext = format!(".{}", e.to_string_lossy().to_lowercase());
                    RAW_EXTENSIONS.contains(&ext.as_str())
                })
                .unwrap_or(false)
            && (target.prefer == EngineKind::Raw || target.fallback == Some(EngineKind::Raw))
    }

    fn run(&self, job: &Job) -> Result<Vec<PathBuf>, Error> {
        let dcraw = locate_dcraw().ok_or_else(|| {
            Error::engine_missing(
                "内置引擎未就绪（未找到 dcraw.exe）。下载 LibRaw 官方 dcraw 放入 engines\\dcraw 或设 \
                 BETTERDESKTOP_DCRAW_PATH 环境变量后重试——这不是文件错误",
            )
        })?;
        let input = job.primary_source();
        let format = job.target.format.as_str();
        let name = input
            .file_stem()
            .map(|s| s.to_string_lossy().to_string())
            .unwrap_or_else(|| "output".to_string());

        // dcraw：-T TIFF 输出；-q 3 高质量去马赛克；-o 0 相机白平衡；-O 指定输出文件
        let tiff = job.temp_dir.join(format!("{name}-raw.tiff"));
        let mut args = vec!["-T".to_string(), "-q".to_string(), "3".to_string(), "-o".to_string(), "0".to_string(), "-O".to_string(), tiff.to_string_lossy().to_string()];
        args.push(input.to_string_lossy().to_string());
        let out = exec::run(&dcraw, &args, None, TIMEOUT_MS)?;
        if out.code != Some(0) || !tiff.is_file() {
            return Err(Error::conversion_failed(format!(
                "dcraw 退出码 {} {}（RAW 可能不受支持或文件损坏）",
                out.code.map(|c| c.to_string()).unwrap_or_else(|| "-".to_string()),
                truncate(&out.stderr)
            )));
        }

        let product = job.temp_dir.join(format!("{name}.{format}"));
        convert_image(&tiff, &product, format)?;
        Ok(vec![product])
    }
}

fn truncate(text: &str) -> &str {
    if text.len() > 200 { &text[..200] } else { text }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn can_handle_accepts_raw_source_only() {
        let engine = RawEngine;
        let target = crate::contract::ConversionTarget {
            format: "png".into(),
            label: "PNG".into(),
            filter: None,
            hops: 1,
            prefer: EngineKind::Raw,
            fallback: None,
            category: crate::contract::TargetCategory::Image,
            lossless: true,
        };
        for ext in [".cr2", ".nef", ".arw", ".dng", ".x3f"] {
            assert!(engine.can_handle(&[PathBuf::from(format!("a{ext}"))], &target), "{ext} 应命中");
        }
        assert!(!engine.can_handle(&[PathBuf::from("a.jpg")], &target));
        assert!(!engine.can_handle(&[PathBuf::from("a.nef"), PathBuf::from("b.nef")], &target));
    }
}
