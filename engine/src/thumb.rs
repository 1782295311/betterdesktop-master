//! 缩略图生成：PNG 原图 → 最长边 ≤480px；有 alpha 用 PNG、无 alpha 用 JPEG（省空间）。
//! 引擎生成缩略图，UI 永不解码原图（信息架构定稿：图片条目显示整图缩略图）。

use image::imageops::FilterType;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ThumbFormat {
    Png,
    Jpeg,
}

/// 生成缩略图（原图 ≤ 目标宽时原样重编码；返回字节 + 格式）。
pub fn generate(png: &[u8], max_width: u32) -> Option<(Vec<u8>, ThumbFormat)> {
    let img = image::load_from_memory(png).ok()?;
    let width = img.width();
    let height = img.height();
    if width == 0 || height == 0 {
        return None;
    }

    // 等比缩放（只缩小不放大）
    let scaled = if width > max_width {
        let new_height = ((height as u64 * max_width as u64) / width as u64) as u32;
        img.resize(max_width, new_height.max(1), FilterType::Lanczos3)
    } else {
        img
    };

    // alpha 判定：存在任一 alpha<255 → 保留透明（PNG）；否则 JPEG
    let rgba = scaled.to_rgba8();
    let has_alpha = rgba.pixels().any(|p| p.0[3] != 255);
    let format = if has_alpha { ThumbFormat::Png } else { ThumbFormat::Jpeg };

    let mut out = Vec::new();
    let encode_result = {
        let dyn_img = image::DynamicImage::ImageRgba8(rgba);
        if has_alpha {
            dyn_img.write_to(
                &mut std::io::Cursor::new(&mut out),
                image::ImageFormat::Png,
            )
        } else {
            dyn_img.write_to(
                &mut std::io::Cursor::new(&mut out),
                image::ImageFormat::Jpeg,
            )
        }
    };
    match encode_result {
        Ok(()) => Some((out, format)),
        Err(e) => {
            crate::log::error(format!("thumbnail encode failed: {e}"));
            None
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn make_test_png(width: u32, height: u32, alpha: u8) -> Vec<u8> {
        let mut img = image::RgbaImage::new(width, height);
        for (x, y, p) in img.enumerate_pixels_mut() {
            *p = image::Rgba([(x * 7) as u8, (y * 13) as u8, 200, alpha]);
        }
        let mut out = Vec::new();
        image::DynamicImage::ImageRgba8(img)
            .write_to(&mut std::io::Cursor::new(&mut out), image::ImageFormat::Png)
            .unwrap();
        out
    }

    #[test]
    fn downscales_large_image() {
        let png = make_test_png(2000, 1000, 255);
        let (thumb, fmt) = generate(&png, 480).expect("generate");
        assert_eq!(fmt, ThumbFormat::Jpeg); // 不透明 → JPEG
        let decoded = image::load_from_memory(&thumb).expect("decode");
        assert_eq!(decoded.width(), 480);
        assert_eq!(decoded.height(), 240);
    }

    #[test]
    fn small_image_not_upscaled() {
        let png = make_test_png(100, 100, 255);
        let (thumb, _) = generate(&png, 480).expect("generate");
        let decoded = image::load_from_memory(&thumb).expect("decode");
        assert_eq!(decoded.width(), 100);
    }

    #[test]
    fn alpha_kept_as_png() {
        let png = make_test_png(320, 240, 128);
        let (thumb, fmt) = generate(&png, 480).expect("generate");
        assert_eq!(fmt, ThumbFormat::Png);
        let decoded = image::load_from_memory(&thumb).expect("decode");
        assert_eq!(decoded.width(), 320);
    }
}
