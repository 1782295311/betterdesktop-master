//! 图片内容指纹（**像素级**）：图片条目去重的唯一依据。
//!
//! 为什么不能用路径、也不能只用字节：
//! - `image_path` 形如 `clipboard\images\{id}.png`，**含条目自己的 id** → 每次捕获都不同，
//!   拿它当指纹等于永远不命中（2026-09-12 真机 bug：同一次截图出现两条历史）；
//! - 截图工具会在一次复制里连续写入多个剪贴板格式（`CF_PNG` 与 `CF_DIB`，见 capture.rs `set_image_entry`），
//!   引擎两次快照拿到的**字节不同但像素相同**。
//!
//! 故此处解码为 RGBA 像素后哈希 —— 同一张图无论怎么编码/经哪条格式路径进来，指纹恒定。
//! 解码失败回退字节哈希（保证仍有稳定值，不因解码器差异把"能去重"退化成"完全不去重"）。

/// 计算图片内容指纹（SHA256 前 16 hex，与既有指纹风格一致）。
pub fn image_content_hash(png: &[u8]) -> String {
    let payload: Vec<u8> = match image::load_from_memory(png) {
        Ok(img) => {
            // 统一到 RGBA8 + 显式尺寸：避免"同一像素、不同编码元数据/位深"造成差异。
            let rgba = img.to_rgba8();
            let (w, h) = rgba.dimensions();
            let raw = rgba.as_raw();
            let mut v = Vec::with_capacity(raw.len() + 8);
            v.extend_from_slice(&w.to_le_bytes());
            v.extend_from_slice(&h.to_le_bytes());
            v.extend_from_slice(raw);
            v
        }
        Err(_) => png.to_vec(),
    };

    let digest = crate::store::sha256_hex(&payload);
    digest[..16.min(digest.len())].to_string()
}

#[cfg(test)]
mod tests {
    use super::*;
    use image::codecs::png::{CompressionType, FilterType, PngEncoder};
    use image::ImageEncoder;

    /// 生成纯色 PNG，可指定压缩级别（用于构造"像素相同、字节不同"的样本）。
    fn png_of(w: u32, h: u32, rgb: [u8; 3], compression: CompressionType) -> Vec<u8> {
        let mut img = image::RgbImage::new(w, h);
        for p in img.pixels_mut() {
            *p = image::Rgb(rgb);
        }
        let mut out = Vec::new();
        let encoder = PngEncoder::new_with_quality(&mut out, compression, FilterType::Adaptive);
        encoder
            .write_image(img.as_raw(), w, h, image::ExtendedColorType::Rgb8)
            .expect("png encode");
        out
    }

    #[test]
    fn same_pixels_different_compression_share_hash() {
        let a = png_of(8, 6, [1, 2, 3], CompressionType::Fast);
        let b = png_of(8, 6, [1, 2, 3], CompressionType::Best);
        assert_ne!(a, b, "两种压缩级别字节应当不同，否则本用例失去意义");
        assert_eq!(
            image_content_hash(&a),
            image_content_hash(&b),
            "像素相同 → 指纹必须相同（截图工具多格式写入正是此场景）"
        );
    }

    #[test]
    fn different_pixels_differ_in_hash() {
        let a = png_of(8, 6, [1, 2, 3], CompressionType::Fast);
        let b = png_of(8, 6, [9, 9, 9], CompressionType::Fast);
        assert_ne!(image_content_hash(&a), image_content_hash(&b));
    }

    #[test]
    fn size_participates_in_hash() {
        let a = png_of(8, 6, [1, 2, 3], CompressionType::Fast);
        let b = png_of(8, 7, [1, 2, 3], CompressionType::Fast);
        assert_ne!(image_content_hash(&a), image_content_hash(&b));
    }

    #[test]
    fn undecodable_falls_back_to_byte_hash() {
        let garbage = vec![0u8, 1, 2, 3, 4, 5, 6, 7];
        let h1 = image_content_hash(&garbage);
        let h2 = image_content_hash(&garbage);
        assert_eq!(h1, h2, "回退路径也必须是稳定值");
        assert_eq!(h1.len(), 16);
    }
}
