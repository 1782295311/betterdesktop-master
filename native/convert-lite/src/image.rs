//! 图片互转（image crate，进程内解码/编码，零外部二进制）。
//!
//! 诚实进度：单张静态图，image crate 解码/编码不提供中间回调，
//! 故只推 Running(None) -> Finalizing(100)，绝不在中间编造百分比。

use std::fs::File;
use std::io::Write;
use std::path::Path;

use image::ImageFormat;

use crate::guard::{self, GuardError};
use crate::protocol::{Emitter, Phase};

/// 编码到目标文件（用 DynamicImage::write_to，按格式分发）。
fn encode_to(
    img: &image::DynamicImage,
    out: &Path,
    fmt: ImageFormat,
) -> Result<(), String> {
    let f = File::create(out).map_err(|e| e.to_string())?;
    let mut w = std::io::BufWriter::new(f);
    img.write_to(&mut w, fmt)
        .map_err(|e| format!("图片编码失败：{e}"))?;
    w.flush().map_err(|e| e.to_string())?;
    Ok(())
}

/// 图片互转。
pub fn image_convert(
    src: &Path,
    out: &Path,
    target_ext: &str,
    em: &Emitter,
) -> Result<(), String> {
    let target_fmt = match target_ext.to_ascii_lowercase().as_str() {
        "png" => ImageFormat::Png,
        "jpg" | "jpeg" => ImageFormat::Jpeg,
        "bmp" => ImageFormat::Bmp,
        "webp" => ImageFormat::WebP,
        other => return Err(format!("不支持的图片目标格式：{other}")),
    };

    em.progress(
        &src.to_string_lossy(),
        target_ext,
        "image-rs",
        Phase::Running,
        None,
        Some("进程内解码（单图，无中间进度）"),
    );

    let tmp = guard::tmp_path(out);
    let r = (|| -> Result<(), String> {
        // 先读尺寸做护栏，再全图解码。image::Reader 可以先拿尺寸。
        let reader = image::ImageReader::open(src).map_err(|e| format!("打开图片失败：{e}"))?;
        let (w, h) = reader
            .into_dimensions()
            .map_err(|e| format!("读取图片尺寸失败：{e}"))?;
        guard::check_pixels(w, h).map_err(|e: GuardError| e.to_string())?;

        let img = image::open(src).map_err(|e| format!("图片解码失败：{e}"))?;
        encode_to(&img, &tmp, target_fmt)
    })();

    match r {
        Ok(()) => {
            em.progress(
                &src.to_string_lossy(),
                target_ext,
                "image-rs",
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
