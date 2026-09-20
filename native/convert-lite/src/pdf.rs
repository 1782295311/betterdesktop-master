//! DocIR -> 多页 PDF（手写最小 PDF，零依赖）。
//!
//! 关键：JPEG 在 PDF 中本就是原生支持的图像编码（Filter /DCTDecode），
//! 直接把 JPEG 字节塞进图像流，无需解码再编码——比 image crate 转码快且无损。
//! 代价：只接受 JPEG；PNG 等需先转 JPEG，本 spike 不做（见 README 边界）。

use std::path::Path;
use crate::ir::DocIR;

/// 屏幕像素按 96 DPI 换算成 PDF pt。
const PX_TO_PT: f32 = 72.0 / 96.0;
/// 页面尺寸上限 6000pt（≈8.3 inch），防爆页。
const MAX_PAGE_PT: f32 = 6000.0;

fn px_to_pt(px: u32) -> f32 {
    (px as f32 * PX_TO_PT).min(MAX_PAGE_PT)
}

/// 把 DocIR 渲染成完整 PDF 字节流（含 xref/trailer/startxref）。
pub fn render_pdf(ir: &DocIR) -> Result<Vec<u8>, String> {
    if ir.is_empty() {
        return Err("空文档，无法生成 PDF".into());
    }
    let n = ir.len();
    // 对象布局：1=Catalog, 2=Pages，从 3 起每页 3 个对象
    //   页 k(0-based)：Page=3+3k, Contents=4+3k, ImageXObject=5+3k
    let total_objs = 2 + 3 * n;

    let mut out: Vec<u8> = Vec::new();
    let mut offsets: Vec<usize> = Vec::with_capacity(total_objs + 1);

    out.extend(b"%PDF-1.3\n");

    // obj 1 Catalog
    offsets.push(out.len());
    out.extend(b"1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

    // obj 2 Pages
    let mut kids = String::from("[");
    for k in 0..n {
        if k > 0 {
            kids.push(' ');
        }
        kids.push_str(&format!("{} 0 R", 3 + 3 * k));
    }
    kids.push(']');
    offsets.push(out.len());
    out.extend(
        format!("2 0 obj\n<< /Type /Pages /Count {n} /Kids {kids} >>\nendobj\n").into_bytes(),
    );

    for (k, page) in ir.pages.iter().enumerate() {
        let page_obj = 3 + 3 * k;
        let cont_obj = 4 + 3 * k;
        let img_obj = 5 + 3 * k;

        let w_pt = px_to_pt(page.width_px);
        let h_pt = px_to_pt(page.height_px);
        // 内容流：把整页图像画满 MediaBox
        let content = format!("q {w_pt:.2} 0 0 {h_pt:.2} 0 0 cm /Im0 Do Q\n");
        let jpeg_len = page.jpeg.len();

        // Page 对象
        offsets.push(out.len());
        out.extend(
            format!(
                "{page_obj} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {w_pt:.2} {h_pt:.2}] /Resources << /XObject << /Im0 {img_obj} 0 R >> >> /Contents {cont_obj} 0 R >>\nendobj\n"
            )
            .into_bytes(),
        );

        // Contents 对象
        offsets.push(out.len());
        out.extend(
            format!(
                "{cont_obj} 0 obj\n<< /Length {} >>\nstream\n{content}endstream\nendobj\n",
                content.len()
            )
            .into_bytes(),
        );

        // Image XObject：DCTDecode 直接嵌入 JPEG 字节
        offsets.push(out.len());
        out.extend(
            format!(
                "{img_obj} 0 obj\n<< /Type /XObject /Subtype /Image /Width {} /Height {} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg_len} >>\nstream\n",
                page.width_px, page.height_px
            )
            .into_bytes(),
        );
        out.extend_from_slice(&page.jpeg);
        out.extend(b"\nendstream\nendobj\n");
    }

    // xref 表
    let xref_start = out.len();
    out.extend(format!("xref\n0 {}\n", total_objs + 1).into_bytes());
    // 第 0 项
    out.extend(b"0000000000 65535 f \n");
    for off in offsets.iter().take(total_objs) {
        out.extend(format!("{off:010} 00000 n \n").into_bytes());
    }
    out.extend(
        format!(
            "trailer\n<< /Size {} /Root 1 0 R >>\nstartxref\n{xref_start}\n%%EOF\n",
            total_objs + 1
        )
        .into_bytes(),
    );

    Ok(out)
}

/// 从 PDF 提取文本（最小版：解析内容流里的 Tj/TJ 操作）。
pub fn pdf_extract_text(path: &Path) -> Result<String, String> {
    let data = std::fs::read(path).map_err(|e| e.to_string())?;
    let mut out = String::new();

    // 找所有 stream...endstream
    let mut pos = 0;
    while let Some(si) = find_bytes(&data, b"stream", pos) {
        let mut start = si + 6;
        // 跳过换行
        if start < data.len() && data[start] == b'\r' { start += 1; }
        if start < data.len() && data[start] == b'\n' { start += 1; }
        let ei = find_bytes(&data, b"endstream", start).unwrap_or(data.len());
        let stream = &data[start..ei];
        extract_from_stream(stream, &mut out);
        out.push('\n');
        pos = ei + 9;
    }
    if out.is_empty() {
        Err("PDF 内容流里没找到文本操作".to_string())
    } else {
        Ok(out)
    }
}

fn find_bytes(data: &[u8], pat: &[u8], from: usize) -> Option<usize> {
    data[from..].windows(pat.len()).position(|w| w == pat).map(|i| from + i)
}

fn extract_from_stream(stream: &[u8], out: &mut String) {
    let s = String::from_utf8_lossy(stream);
    // 简单状态机：找 (...) Tj 和 <hex> Tj
    let mut i = 0;
    let bytes = s.as_bytes();
    while i < bytes.len() {
        match bytes[i] {
            b'(' => {
                // 括号字符串，找匹配的 )
                let mut depth = 1;
                let mut j = i + 1;
                let mut text = Vec::new();
                while j < bytes.len() && depth > 0 {
                    match bytes[j] {
                        b'\\' => { j += 2; continue; }
                        b'(' => depth += 1,
                        b')' => depth -= 1,
                        _ => {}
                    }
                    if depth > 0 { text.push(bytes[j]); }
                    j += 1;
                }
                if let Ok(t) = String::from_utf8(text) {
                    out.push_str(&t);
                }
                i = j;
            }
            b'<' => {
                // hex 字符串，找 >
                if let Some(end) = s[i+1..].find('>') {
                    let hex: String = s[i+1..i+1+end].chars().filter(|c| !c.is_whitespace()).collect();
                    // 尝试 UTF-16BE
                    if hex.len() % 4 == 0 {
                        let mut chars = Vec::new();
                        for chunk in hex.as_bytes().chunks(4) {
                            if let Ok(n) = u16::from_str_radix(std::str::from_utf8(chunk).unwrap_or(""), 16) {
                                chars.push(n);
                            }
                        }
                        let decoded = String::from_utf16(&chars).unwrap_or_default();
                        if !decoded.is_empty() { out.push_str(&decoded); }
                    }
                    i = i + 1 + end + 1;
                } else { i += 1; }
            }
            _ => i += 1,
        }
    }
}