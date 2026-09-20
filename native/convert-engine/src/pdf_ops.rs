// S7 PDF 操作（C# PdfComposeEngine + PdfSecurityEngine 等价；lopdf 0.34）：
// merge（多 PDF 页合并）/ split（单 PDF 逐页拆分）/ compose（多图片合成）/ decrypt（密码解密）。
// 加密（RC4-128 手写标准层）在 pdf_security.rs；O1 spike 实证 lopdf 0.34 无 encrypt 写入 API。
use std::collections::BTreeMap;
use std::path::Path;

use lopdf::{dictionary, Document, Object, ObjectId, Stream};

use crate::error::Error;

/// 多 PDF 合并 → merged.pdf（C# Merge 语义：逐源页顺序追加）。
pub fn merge_pdfs(sources: &[&Path], product: &Path) -> Result<(), Error> {
    let mut docs: Vec<Document> = Vec::new();
    for s in sources {
        let doc = Document::load(s)
            .map_err(|e| Error::conversion_failed(format!("PDF 打开失败（{}）: {e}", s.display())))?;
        if doc.is_encrypted() {
            return Err(Error::conversion_failed(format!(
                "PDF 已加密（{}），无法合并——请先解密",
                s.display()
            )));
        }
        docs.push(doc);
    }
    let mut out = docs.remove(0);

    for mut doc in docs {
        // 【顺序红线】必须先重编号、再取页引用。
        // renumber_objects_with 只重写 doc.objects 内部的引用，改不到任何更早的克隆：若在重编号前
        // 克隆 Kids（历史 bug），追加进主文档的引用会指向【上一份 PDF 的对象空间】，合并结果里后一份
        // 的页会变前一份的同号页；同时 max_id 不同步时，第三份起重编号起始号与既有对象号重叠，
        // BTreeMap::extend 会静默覆盖对象（页数看着对、内容全错或结构非法）。
        doc.renumber_objects_with(out.max_id + 1);
        out.max_id = doc.max_id; // 必须同步：下一份的起始号依赖它

        // 重编号之后取实际页对象 ID（get_pages 会展开嵌套页树，按页序返回）
        let src_page_ids: Vec<ObjectId> = doc.get_pages().values().copied().collect();
        let src_count = src_page_ids.len() as i64;

        // 对象池并入（ID 已重编号，引用不冲突）
        out.objects.extend(doc.objects);

        // 页树追加：主 doc 的 Pages 树 Kids 合并 + Count 累加
        if !src_page_ids.is_empty() {
            let pages_id = page_tree_id(&out)?;
            let mut main = out.get_dictionary_mut(pages_id)?;
            let existing = main.get(b"Kids").cloned().unwrap_or(Object::Array(vec![]));
            let mut kids_all = match existing {
                Object::Array(a) => a,
                _ => vec![],
            };
            kids_all.extend(src_page_ids.into_iter().map(Object::Reference));
            main.set("Kids", Object::Array(kids_all));
            let count = main.get(b"Count").and_then(|o| o.as_i64()).unwrap_or(0) + src_count;
            main.set("Count", count);
        }
    }
    out.compress();
    out.save(product)
        .map_err(|e| Error::conversion_failed(format!("PDF 保存失败: {e}")))?;
    Ok(())
}

/// 页树根 ID（catalog → Pages）。
fn page_tree_id(doc: &Document) -> Result<ObjectId, Error> {
    let catalog = doc.trailer.get(b"Root").and_then(|o| o.as_reference()).map_err(|_| {
        Error::conversion_failed("PDF 目录（Catalog）缺失".to_string())
    })?;
    let pages_id = doc.get_dictionary(catalog)?.get(b"Pages").and_then(|o| o.as_reference()).map_err(|_| {
        Error::conversion_failed("PDF 页树（Pages）缺失".to_string())
    })?;
    Ok(pages_id)
}

/// 单 PDF 拆分 → page-N.pdf（C# Split 语义：1 页起；页数 <2 报错）。
pub fn split_pdf(input: &Path, temp_dir: &Path) -> Result<Vec<std::path::PathBuf>, Error> {
    let doc = Document::load(input)
        .map_err(|e| Error::conversion_failed(format!("PDF 打开失败: {e}")))?;
    if doc.is_encrypted() {
        return Err(Error::conversion_failed("PDF 已加密，无法拆分——请先解密".to_string()));
    }
    let pages = doc.get_pages();
    if pages.len() < 2 {
        return Err(Error::conversion_failed("PDF 仅 1 页，无需拆分".to_string()));
    }
    let mut products = Vec::new();
    for (i, (_, page_id)) in pages.iter().enumerate() {
        let page_id = *page_id;
        let sub = page_subgraph(&doc, page_id)?;
        let mut out = Document::new();
        out.objects = sub;
        let root = doc
            .trailer
            .get(b"Root")
            .and_then(|o| o.as_reference())
            .map_err(|_| Error::conversion_failed("PDF 目录缺失".to_string()))?;
        out.trailer.set("Root", root);
        // 页树只留该页
        let pages_id = page_tree_id(&doc)?;
        out.get_dictionary_mut(pages_id)
            .map_err(|e| Error::conversion_failed(format!("页树操作失败: {e}")))?
            .set("Kids", Object::Array(vec![Object::Reference(page_id)]));
        out.get_dictionary_mut(pages_id)
            .map_err(|e| Error::conversion_failed(format!("页树操作失败: {e}")))?
            .set("Count", 1);
        out.compress();
        let product = temp_dir.join(format!("page-{}.pdf", i + 1));
        out.save(&product)
            .map_err(|e| Error::conversion_failed(format!("PDF 保存失败: {e}")))?;
        products.push(product);
    }
    Ok(products)
}

/// 页引用子图（BFS：从页对象出发跟随引用收集闭包；Catalog/Pages 一并纳入，引用 ID 保持原样）。
fn page_subgraph(doc: &Document, page_id: ObjectId) -> Result<BTreeMap<ObjectId, Object>, Error> {
    let mut collected: BTreeMap<ObjectId, Object> = BTreeMap::new();
    let mut queue = vec![page_id];
    if let Ok(root) = doc.trailer.get(b"Root").and_then(|o| o.as_reference()) {
        queue.push(root);
    }
    while let Some(id) = queue.pop() {
        if collected.contains_key(&id) {
            continue;
        }
        let obj = doc
            .get_object(id)
            .map_err(|e| Error::conversion_failed(format!("对象读取失败: {e}")))?
            .clone();
        collect_refs(&obj, &mut queue);
        collected.insert(id, obj);
    }
    Ok(collected)
}

fn collect_refs(obj: &Object, queue: &mut Vec<ObjectId>) {
    match obj {
        Object::Reference(r) => queue.push(*r),
        Object::Array(items) => {
            for it in items {
                collect_refs(it, queue);
            }
        }
        Object::Dictionary(d) => {
            for (_, v) in d.iter() {
                collect_refs(v, queue);
            }
        }
        Object::Stream(s) => {
            for (_, v) in s.dict.iter() {
                collect_refs(v, queue);
            }
        }
        _ => {}
    }
}

/// 多图片合成 PDF → composed.pdf（C# Compose 语义：每图一页，页尺寸=图像像素尺寸；webp 统一转码）。
/// 透明通道合成白底（C# DrawImage 透明区域=页背景白语义）。
pub fn compose_images(sources: &[&Path], product: &Path) -> Result<(), Error> {
    let mut out = Document::with_version("1.5");
    let pages_id = out.new_object_id();
    let mut kids: Vec<Object> = Vec::new();

    for src in sources {
        let img = image::ImageReader::open(src)
            .map_err(|e| Error::conversion_failed(format!("图片读取失败: {e}")))?
            .with_guessed_format()
            .map_err(|e| Error::conversion_failed(format!("图片格式识别失败: {e}")))?
            .decode()
            .map_err(|e| Error::conversion_failed(format!("图片解码失败: {e}")))?;
        let (w, h) = (img.width(), img.height());
        // 合成白底 → RGB → JPEG（DCTDecode 嵌入；C# XImage 原始嵌入的近似，视觉无损可接受）
        let rgb = flatten_white(&img);
        let mut jpeg: Vec<u8> = Vec::new();
        {
            let mut enc = image::codecs::jpeg::JpegEncoder::new_with_quality(&mut jpeg, 90);
            enc.encode(&rgb, w, h, image::ExtendedColorType::Rgb8)
                .map_err(|e| Error::conversion_failed(format!("JPEG 编码失败: {e}")))?;
        }
        let img_stream = Stream::new(
            dictionary! {
                "Type" => "XObject",
                "Subtype" => "Image",
                "Width" => w as i64,
                "Height" => h as i64,
                "ColorSpace" => "DeviceRGB",
                "BitsPerComponent" => 8,
                "Filter" => "DCTDecode",
            },
            jpeg,
        );
        let img_id = out.add_object(img_stream);

        // 页：MediaBox = 图像尺寸（1pt = 1px，C# XUnit.FromPoint 语义）
        let content_id = out.add_object(Stream::new(
            dictionary! {},
            format!("q {w} 0 0 {h} 0 0 cm /X{} Do Q", img_id.0).into_bytes(),
        ));
        let resources_id = out.add_object(dictionary! {
            "XObject" => dictionary! { format!("X{}", img_id.0) => img_id },
        });
        let page_id = out.add_object(dictionary! {
            "Type" => "Page",
            "Parent" => pages_id,
            "MediaBox" => vec![0.into(), 0.into(), (w as f32).into(), (h as f32).into()],
            "Contents" => content_id,
            "Resources" => resources_id,
        });
        kids.push(Object::Reference(page_id));
    }

    let pages = dictionary! {
        "Type" => "Pages",
        "Kids" => Object::Array(kids),
        "Count" => sources.len() as i64,
    };
    out.objects.insert(pages_id, Object::Dictionary(pages));
    let catalog_id = out.add_object(dictionary! {
        "Type" => "Catalog",
        "Pages" => pages_id,
    });
    out.trailer.set("Root", catalog_id);
    out.compress();
    out.save(product)
        .map_err(|e| Error::conversion_failed(format!("PDF 保存失败: {e}")))?;
    Ok(())
}

/// RGBA 合成白底 → RGB 字节（JPEG 无 alpha；透明区域 = 白，对齐 C# 页背景）。
fn flatten_white(img: &image::DynamicImage) -> Vec<u8> {
    let rgba = img.to_rgba8();
    let mut rgb = Vec::with_capacity((rgba.len() / 4) * 3);
    for px in rgba.pixels() {
        let a = px[3] as u32;
        let inv = 255 - a;
        rgb.push(((px[0] as u32 * a + 255 * inv) / 255) as u8);
        rgb.push(((px[1] as u32 * a + 255 * inv) / 255) as u8);
        rgb.push(((px[2] as u32 * a + 255 * inv) / 255) as u8);
    }
    rgb
}

/// PDF 解密（lopdf Document::decrypt：RC4/AES 全支持）→ 逐页复制进无加密新文档（C# Decrypt 语义）。
pub fn decrypt_pdf(input: &Path, password: &str, product: &Path) -> Result<(), Error> {
    let mut doc = Document::load(input)
        .map_err(|e| Error::conversion_failed(format!("PDF 打开失败: {e}")))?;
    if !doc.is_encrypted() {
        return Err(Error::conversion_failed("PDF 未加密，无需解密".to_string()));
    }
    doc.decrypt(password.as_bytes())
        .map_err(|e| Error::conversion_failed(format!("密码错误或解密失败: {e}")))?;
    // 清掉 Encrypt 字典（解密后保存不含加密设置）
    if let Ok(enc) = doc.trailer.get(b"Encrypt").and_then(|o| o.as_reference()) {
        doc.objects.remove(&enc);
    }
    doc.trailer.remove(b"Encrypt");
    doc.compress();
    doc.save(product)
        .map_err(|e| Error::conversion_failed(format!("PDF 保存失败: {e}")))?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 生成单页测试 PDF（lopdf 官方模式简化）。
    fn make_pdf(dir: &Path, name: &str) -> std::path::PathBuf {
        let mut doc = Document::with_version("1.5");
        let pages_id = doc.new_object_id();
        let font_id = doc.add_object(dictionary! {
            "Type" => "Font", "Subtype" => "Type1", "BaseFont" => "Courier",
        });
        let resources_id = doc.add_object(dictionary! {
            "Font" => dictionary! { "F1" => font_id },
        });
        let content_id = doc.add_object(Stream::new(
            dictionary! {},
            format!("BT /F1 24 Tf 100 700 Td ({name}) Tj ET").into_bytes(),
        ));
        let page_id = doc.add_object(dictionary! {
            "Type" => "Page", "Parent" => pages_id, "Contents" => content_id,
            "Resources" => resources_id,
            "MediaBox" => vec![0.into(), 0.into(), 595.into(), 842.into()],
        });
        let pages = dictionary! {
            "Type" => "Pages", "Kids" => vec![Object::Reference(page_id)], "Count" => 1,
        };
        doc.objects.insert(pages_id, Object::Dictionary(pages));
        let catalog_id = doc.add_object(dictionary! { "Type" => "Catalog", "Pages" => pages_id });
        doc.trailer.set("Root", catalog_id);
        let p = dir.join(name);
        doc.save(&p).unwrap();
        p
    }

    #[test]
    fn merge_two_pdfs_page_count_2() {
        let dir = std::env::temp_dir().join(format!("bdt-pdf-merge-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let a = make_pdf(&dir, "a.pdf");
        let b = make_pdf(&dir, "b.pdf");
        let out = dir.join("merged.pdf");
        merge_pdfs(&[&a, &b], &out).unwrap();
        let doc = Document::load(&out).unwrap();
        assert_eq!(doc.get_pages().len(), 2);
        std::fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn split_pdf_one_page_fails() {
        let dir = std::env::temp_dir().join(format!("bdt-pdf-split-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let a = make_pdf(&dir, "single.pdf");
        let err = split_pdf(&a, &dir).unwrap_err();
        assert_eq!(err.code.as_str(), "ConversionFailed");
        assert!(err.message.contains("无需拆分"));
        std::fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn compose_images_creates_pdf() {
        let dir = std::env::temp_dir().join(format!("bdt-pdf-comp-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let png = dir.join("a.png");
        image::RgbaImage::from_pixel(10, 20, image::Rgba([255u8, 0, 0, 255])).save(&png).unwrap();
        let out = dir.join("composed.pdf");
        compose_images(&[&png], &out).unwrap();
        let doc = Document::load(&out).unwrap();
        assert_eq!(doc.get_pages().len(), 1);
        std::fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn flatten_white_composites_alpha() {
        let img = image::DynamicImage::ImageRgba8(
            image::RgbaImage::from_pixel(1, 1, image::Rgba([0u8, 0, 0, 0])),
        );
        let rgb = flatten_white(&img);
        assert_eq!(rgb, vec![255, 255, 255]); // 全透明 → 白
    }

    #[test]
    fn page_subgraph_includes_catalog() {
        let dir = std::env::temp_dir().join(format!("bdt-pdf-sub-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let a = make_pdf(&dir, "sub.pdf");
        let doc = Document::load(&a).unwrap();
        let page_id = *doc.get_pages().iter().next().unwrap().1;
        let sub = page_subgraph(&doc, page_id).unwrap();
        // 子图至少含页对象与目录对象
        assert!(sub.len() >= 2);
        std::fs::remove_dir_all(&dir).ok();
    }
}
