// Heic 引擎（S9.5 补全：HEIC/HEIF/AVIF 输入——矩阵引用 24 处但执行层未注册，与 PdfText 同类的隐性缺口）。
// 语义锚定 C# HeicEngine.cs：WIC BitmapDecoder 解码（宿主 Windows 自带 WIC，零下载）→ PNG 中转
// → convert_image 重编码为目标图片格式。依赖系统图像扩展（HEIF/AV1），解码失败给安装指引（EngineMissing）。
// 无法无样本预探测 → Probe 恒 Ok（对齐 C#：不假装能力，也不误置灰已装扩展的机器）。
use std::path::{Path, PathBuf};

use crate::contract::{ConversionTarget, EngineKind};
use crate::error::Error;
use crate::image_engine::convert_image;
use crate::run::{Engine, Job};

use windows::core::{GUID, PCWSTR};
use windows::Win32::Foundation::GENERIC_READ;
use windows::Win32::Graphics::Imaging::{
    CLSID_WICImagingFactory, IWICBitmapDecoder, IWICImagingFactory, WICDecodeMetadataCacheOnDemand,
};
use windows::Win32::System::Com::{CoCreateInstance, CoInitializeEx, CLSCTX_INPROC_SERVER, COINIT_MULTITHREADED};

const HEIC_EXTS: [&str; 3] = [".heic", ".heif", ".avif"];

/// HEIC/HEIF/AVIF 输入引擎（WIC 解码 → PNG 中转；与 C# HeicEngine 同解码链）。
pub struct HeicEngine;

impl Engine for HeicEngine {
    fn kind(&self) -> EngineKind {
        EngineKind::Heic
    }

    fn can_handle(&self, sources: &[PathBuf], target: &ConversionTarget) -> bool {
        sources.len() == 1
            && sources[0]
                .extension()
                .map(|e| {
                    let ext = format!(".{}", e.to_string_lossy().to_lowercase());
                    HEIC_EXTS.contains(&ext.as_str())
                })
                .unwrap_or(false)
            && (target.prefer == EngineKind::Heic || target.fallback == Some(EngineKind::Heic))
    }

    fn run(&self, job: &Job) -> Result<Vec<PathBuf>, Error> {
        let input = job.primary_source();
        let format = job.target.format.as_str();
        let name = input
            .file_stem()
            .map(|s| s.to_string_lossy().to_string())
            .unwrap_or_else(|| "output".to_string());

        // WIC 解码 → PNG 中转（对齐 C#：WIC → PNG 中转 → 目标编码）
        let png = job.temp_dir.join(format!("{name}-wic.png"));
        wic_decode_to_png(input, &png)?;

        let product = job.temp_dir.join(format!("{name}.{format}"));
        convert_image(&png, &product, format)?;
        Ok(vec![product])
    }
}

/// WIC 解码 HEIC/HEIF/AVIF 为 PNG（CopyPixels 32bppBGRA → RGBA → image crate 编码）。
fn wic_decode_to_png(input: &Path, png: &Path) -> Result<(), Error> {
    // 线程初始化 COM（已初始化则忽略返回码；MTA 兼容 WIC 工厂）
    unsafe {
        let _ = CoInitializeEx(None, COINIT_MULTITHREADED);
    }
    unsafe {
        let factory: IWICImagingFactory =
            CoCreateInstance(&CLSID_WICImagingFactory, None, CLSCTX_INPROC_SERVER)
                .map_err(|e| engine_missing_wic(&e))?;

        let wide: Vec<u16> = input.to_string_lossy().encode_utf16().chain(std::iter::once(0)).collect();
        let decoder: IWICBitmapDecoder = factory
            .CreateDecoderFromFilename(
                PCWSTR::from_raw(wide.as_ptr()),
                None,
                GENERIC_READ,
                WICDecodeMetadataCacheOnDemand,
            )
            .map_err(|e| engine_missing_wic(&e))?;

        let frame = decoder.GetFrame(0).map_err(|e| engine_missing_wic(&e))?;
        let mut width = 0u32;
        let mut height = 0u32;
        frame.GetSize(&mut width, &mut height).map_err(|e| engine_missing_wic(&e))?;
        if width == 0 || height == 0 {
            return Err(Error::conversion_failed("WIC 未返回图像帧"));
        }

        // CopyPixels 32bppBGRA（帧默认格式）；stride = width * 4（BGRA 每像素 4 字节）
        let stride = width * 4;
        let mut bgra = vec![0u8; (stride * height) as usize];
        frame
            .CopyPixels(std::ptr::null(), stride, bgra.as_mut_slice())
            .map_err(|e| engine_missing_wic(&e))?;

        // BGRA → RGBA（image crate 无 BGRA 编码路径，转一次）
        let mut rgba = Vec::with_capacity(bgra.len());
        for px in bgra.chunks_exact(4) {
            rgba.extend_from_slice(&[px[2], px[1], px[0], px[3]]);
        }
        let img = image::RgbaImage::from_raw(width, height, rgba)
            .ok_or_else(|| Error::conversion_failed("WIC 像素缓冲尺寸不符"))?;
        img.save(png)
            .map_err(|e| Error::conversion_failed(format!("PNG 中转编码失败: {e}")))?;
    }
    Ok(())
}

/// 对齐 C# 语义：任何 WIC 解码失败 → EngineMissing + 安装指引（最常见根因：未装 HEIF/AV1 图像扩展）。
fn engine_missing_wic(e: &windows::core::Error) -> Error {
    Error::engine_missing(format!(
        "HEIC/AVIF 解码失败（HRESULT 0x{:08X}）。请安装微软商店免费扩展：『HEIF 图像扩展』（HEIC）或『AV1 视频扩展』（AVIF）后重试——这不是文件错误",
        e.code().0 as u32
    ))
}

/// 探测对齐 C#：恒 Ok（无法无样本预探测；能力取决于系统扩展）。
pub fn heic_availability() -> crate::engines::EngineAvailability {
    crate::engines::EngineAvailability::ok("WIC（需系统 HEIF/AV1 图像扩展）")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn can_handle_accepts_heic_source_only() {
        let engine = HeicEngine;
        let target = crate::contract::ConversionTarget {
            format: "jpg".into(),
            label: "JPG".into(),
            filter: None,
            hops: 1,
            prefer: EngineKind::Heic,
            fallback: None,
            category: crate::contract::TargetCategory::Image,
            lossless: false,
        };
        for ext in [".heic", ".heif", ".avif"] {
            assert!(engine.can_handle(&[PathBuf::from(format!("a{ext}"))], &target), "{ext} 应命中");
        }
        assert!(!engine.can_handle(&[PathBuf::from("a.png")], &target));
        assert!(!engine.can_handle(&[PathBuf::from("a.heic"), PathBuf::from("b.heic")], &target));
    }

    #[test]
    fn wic_error_hints_extension_install() {
        // 不存在的文件 → WIC 解码错误 → EngineMissing（含安装指引），而非 ConversionFailed
        let e = wic_decode_guard();
        assert_eq!(e.code, crate::error::ConvertError::EngineMissing, "解码失败应归 EngineMissing（给安装指引），实际 {e:?}");
    }

    fn wic_decode_guard() -> Error {
        let dir = std::env::temp_dir().join("bdt-heic-nonexistent.heic");
        let png = std::env::temp_dir().join("bdt-heic-nonexistent.png");
        wic_decode_to_png(&dir, &png)
            .err()
            .unwrap_or_else(|| Error::conversion_failed("不应成功"))
    }
}
