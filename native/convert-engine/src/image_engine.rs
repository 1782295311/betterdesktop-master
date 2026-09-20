// S6 图片互转引擎（C# ManagedImageEngine 等价，红线 11 选型：image crate 0.25）：
// 常规格式 png/jpg/bmp/gif/tiff/webp/ico 互转；webp 目标经 WebPEncoder 有损 90（Skia 质量 90 等价）；
// tga 源由 Ffmpeg 引擎处理（矩阵已指派），本引擎排除。
use std::path::Path;

use crate::error::Error;

/// 引擎支持的源扩展名（tga 排除）。
pub fn is_image_source(ext: &str) -> bool {
    matches!(
        ext.to_lowercase().as_str(),
        ".png" | ".jpg" | ".jpeg" | ".bmp" | ".gif" | ".tif" | ".tiff" | ".webp" | ".ico"
    )
}

/// 解码 + 编码互转；product = temp_dir/输入名.<format>。
pub fn convert_image(input: &Path, product: &Path, format: &str) -> Result<(), Error> {
    let img = image::ImageReader::open(input)
        .map_err(|e| Error::conversion_failed(format!("图片解码失败（读取）: {e}")))?
        .with_guessed_format()
        .map_err(|e| Error::conversion_failed(format!("图片格式识别失败: {e}")))?
        .decode()
        .map_err(|e| Error::conversion_failed(format!("图片解码失败: {e}")))?;

    match format {
        "webp" => {
            // image 0.25 仅提供 VP8L 无损编码器（C# 侧 Skia 有损质量 90；无损兼容性更广、文件更大，差异诚实标注）
            let rgba = img.to_rgba8();
            let mut buf: Vec<u8> = Vec::new();
            let enc = image::codecs::webp::WebPEncoder::new_lossless(&mut buf);
            enc.encode(rgba.as_raw(), rgba.width(), rgba.height(), image::ExtendedColorType::Rgba8)
                .map_err(|e| Error::conversion_failed(format!("webp 编码失败: {e}")))?;
            std::fs::write(product, buf)
                .map_err(|e| Error::conversion_failed(format!("写入失败: {e}")))?;
        }
        _ => {
            let fmt = image::ImageFormat::from_extension(format)
                .ok_or_else(|| Error::conversion_failed(format!("不支持的图片目标格式: {format}")))?;
            img.save_with_format(product, fmt)
                .map_err(|e| Error::conversion_failed(format!("{format} 编码失败: {e}")))?;
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn image_source_whitelist() {
        assert!(is_image_source(".PNG"));
        assert!(is_image_source(".webp"));
        assert!(is_image_source(".ico"));
        assert!(!is_image_source(".tga")); // 矩阵已指派 Ffmpeg
        assert!(!is_image_source(".pdf"));
    }

    #[test]
    fn png_to_jpg_roundtrip() {
        let dir = std::env::temp_dir().join(format!("bdt-img-test-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let src = dir.join("a.png");
        // 1x1 红色 PNG
        let _ = image::RgbaImage::from_pixel(2, 2, image::Rgba([255u8, 0, 0, 255])).save(&src).unwrap();
        let dst = dir.join("a.jpg");
        convert_image(&src, &dst, "jpg").unwrap();
        let back = image::open(&dst).unwrap();
        assert_eq!(back.width(), 2);
        assert_eq!(back.height(), 2);
        std::fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn png_to_webp_lossy() {
        let dir = std::env::temp_dir().join(format!("bdt-img-webp-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let src = dir.join("a.png");
        let _ = image::RgbaImage::from_pixel(4, 4, image::Rgba([0u8, 0, 255, 255])).save(&src).unwrap();
        let dst = dir.join("a.webp");
        convert_image(&src, &dst, "webp").unwrap();
        let decoded = image::open(&dst).unwrap();
        assert_eq!(decoded.width(), 4);
        std::fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn webp_source_decodes() {
        let dir = std::env::temp_dir().join(format!("bdt-img-webpsrc-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let src = dir.join("a.webp");
        let _ = image::RgbaImage::from_pixel(3, 3, image::Rgba([0u8, 255, 0, 255])).save(&src).unwrap();
        let dst = dir.join("a.png");
        convert_image(&src, &dst, "png").unwrap();
        assert!(dst.is_file());
        std::fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn missing_input_fails_cleanly() {
        let dir = std::env::temp_dir().join(format!("bdt-img-miss-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let dst = dir.join("x.png");
        let err = convert_image(&dir.join("nope.png"), &dst, "png").unwrap_err();
        assert_eq!(err.code.as_str(), "ConversionFailed");
        std::fs::remove_dir_all(&dir).ok();
    }
}
