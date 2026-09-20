//! PPTX 解析 + HTML/PDF 渲染。
//!
//! PPTX 也是 zip：ppt/presentation.xml 列幻灯片，ppt/slides/slideN.xml 是每页内容。
//! 标签和 DOCX 不同：文本在 <p:sp><p:txBody><a:p><a:r><a:t>，图片在 <p:pic><a:blip r:embed>。

use std::collections::HashMap;
use std::io::{Cursor, Read};
use std::path::Path;

use quick_xml::events::Event;
use quick_xml::Reader;

/// 一页幻灯片。
pub struct Slide {
    pub blocks: Vec<SlideBlock>,
}

pub enum SlideBlock {
    Text(String),
    Image { data_uri: String, alt: String },
}

fn norm(s: &str) -> String {
    s.replace('\\', "/")
}

fn base64_encode(data: &[u8]) -> String {
    const T: &[u8; 64] =
        b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    let mut s = String::with_capacity((data.len() + 2) / 3 * 4);
    for chunk in data.chunks(3) {
        let n = ((chunk[0] as u32) << 16)
            | ((if chunk.len() > 1 { chunk[1] as u32 } else { 0 }) << 8)
            | (if chunk.len() > 2 { chunk[2] as u32 } else { 0 });
        s.push(T[((n >> 18) & 63) as usize] as char);
        s.push(T[((n >> 12) & 63) as usize] as char);
        s.push(if chunk.len() > 1 { T[((n >> 6) & 63) as usize] as char } else { '=' });
        s.push(if chunk.len() > 2 { T[(n & 63) as usize] as char } else { '=' });
    }
    s
}

fn mime_of(path: &str) -> &'static str {
    match path.rsplit('.').next().unwrap_or("").to_ascii_lowercase().as_str() {
        "png" => "image/png",
        "jpg" | "jpeg" => "image/jpeg",
        "gif" => "image/gif",
        "bmp" => "image/bmp",
        _ => "application/octet-stream",
    }
}

fn escape_html(s: &str) -> String {
    s.replace('&', "&amp;").replace('<', "&lt;").replace('>', "&gt;")
}

fn parse_rels(xml: &[u8]) -> HashMap<String, String> {
    let mut reader = Reader::from_reader(xml);
    let mut buf = Vec::new();
    let mut map = HashMap::new();
    loop {
        match reader.read_event_into(&mut buf) {
            Ok(Event::Empty(e)) | Ok(Event::Start(e)) => {
                if e.name().as_ref() == b"Relationship" {
                    let mut id = None;
                    let mut target = None;
                    for a in e.attributes().flatten() {
                        match a.key.as_ref() {
                            b"Id" => id = Some(String::from_utf8_lossy(&a.value).into_owned()),
                            b"Target" => target = Some(String::from_utf8_lossy(&a.value).into_owned()),
                            _ => {}
                        }
                    }
                    if let (Some(id), Some(target)) = (id, target) {
                        map.insert(id, target);
                    }
                }
            }
            Ok(Event::Eof) => break,
            _ => {}
        }
        buf.clear();
    }
    map
}

pub struct Pptx {
    pub slides: Vec<Slide>,
}

pub fn parse_pptx(path: &Path) -> Result<Pptx, String> {
    let bytes = std::fs::read(path).map_err(|e| format!("读取 pptx 失败：{e}"))?;
    let cursor = Cursor::new(bytes);
    let mut archive = zip::ZipArchive::new(cursor).map_err(|e| format!("不是有效 zip/pptx：{e}"))?;

    let mut files: HashMap<String, Vec<u8>> = HashMap::new();
    for i in 0..archive.len() {
        let mut e = archive.by_index(i).map_err(|e| e.to_string())?;
        let n = norm(e.name());
        let mut v = Vec::new();
        e.read_to_end(&mut v).map_err(|e| e.to_string())?;
        files.insert(n, v);
    }

    let pres_rels = files
        .get("ppt/_rels/presentation.xml.rels")
        .map(|r| parse_rels(r))
        .unwrap_or_default();

    let pres_xml = files
        .get("ppt/presentation.xml")
        .ok_or_else(|| "pptx 内缺 ppt/presentation.xml".to_string())?;
    let slide_order = read_slide_order(pres_xml, &pres_rels)?;

    let mut slides = Vec::new();
    for slide_path in slide_order {
        let xml = match files.get(&slide_path) {
            Some(x) => x,
            None => continue,
        };
        let rels_path = slide_path.replace("ppt/slides/", "ppt/slides/_rels/") + ".rels";
        let rels = files.get(&rels_path).map(|r| parse_rels(r)).unwrap_or_default();
        let slide = parse_slide(xml, &rels, &files);
        slides.push(slide);
    }

    if slides.is_empty() {
        return Err("pptx 内没有幻灯片".to_string());
    }
    Ok(Pptx { slides })
}

fn read_slide_order(
    pres_xml: &[u8],
    pres_rels: &HashMap<String, String>,
) -> Result<Vec<String>, String> {
    let mut reader = Reader::from_reader(pres_xml);
    let mut buf = Vec::new();
    let mut order = Vec::new();
    loop {
        match reader.read_event_into(&mut buf) {
            Ok(Event::Empty(e)) | Ok(Event::Start(e)) => {
                if e.name().as_ref() == b"p:sldId" {
                    if let Some(rid) = e.attributes().find_map(|a| {
                        let a = a.ok()?;
                        (a.key.as_ref() == b"r:id").then_some(a.value)
                    }) {
                        let rid = String::from_utf8_lossy(&rid);
                        if let Some(target) = pres_rels.get(rid.as_ref()) {
                            order.push(norm(&format!("ppt/{target}")));
                        }
                    }
                }
            }
            Ok(Event::Eof) => break,
            _ => {}
        }
        buf.clear();
    }
    Ok(order)
}

fn parse_slide(xml: &[u8], rels: &HashMap<String, String>, files: &HashMap<String, Vec<u8>>) -> Slide {
    let mut reader = Reader::from_reader(xml);
    reader.config_mut().trim_text(true);
    let mut buf = Vec::new();
    let mut blocks = Vec::new();

    let mut cur_text = String::new();
    let mut in_t = false;

    loop {
        match reader.read_event_into(&mut buf) {
            Ok(Event::Start(e)) => match e.name().as_ref() {
                b"a:t" => in_t = true,
                _ => {}
            },
            Ok(Event::Empty(e)) => match e.name().as_ref() {
                b"a:blip" => {
                    if let Some(rid) = e.attributes().find_map(|a| {
                        let a = a.ok()?;
                        (a.key.as_ref() == b"r:embed").then_some(a.value)
                    }) {
                        let rid = String::from_utf8_lossy(&rid);
                        if let Some(target) = rels.get(rid.as_ref()) {
                            let p = norm(&format!("ppt/slides/{target}"));
                            let p = p.replace("slides/../", "");
                            if let Some(bytes) = files.get(&p) {
                                let mime = mime_of(target);
                                let b64 = base64_encode(bytes);
                                blocks.push(SlideBlock::Image {
                                    data_uri: format!("data:{mime};base64,{b64}"),
                                    alt: target.clone(),
                                });
                            }
                        }
                    }
                }
                _ => {}
            },
            Ok(Event::End(e)) => match e.name().as_ref() {
                b"a:t" => {
                    in_t = false;
                    if !cur_text.trim().is_empty() {
                        blocks.push(SlideBlock::Text(std::mem::take(&mut cur_text)));
                    } else {
                        cur_text.clear();
                    }
                }
                _ => {}
            },
            Ok(Event::Text(t)) if in_t => {
                if let Ok(s) = t.unescape() {
                    cur_text.push_str(&s);
                }
            }
            Ok(Event::Eof) => break,
            _ => {}
        }
        buf.clear();
    }
    Slide { blocks }
}

impl Pptx {
    pub fn to_html(&self) -> String {
        let mut out = String::from(
            "<!doctype html><html><head><meta charset=\"utf-8\"><title>slides</title><style>body{font-family:sans-serif;max-width:900px;margin:auto}section{border:1px solid #ccc;padding:24px;margin:16px 0;page-break-after:always}img{max-width:100%}</style></head><body>",
        );
        for (i, slide) in self.slides.iter().enumerate() {
            out.push_str(&format!("<section><h3>幻灯片 {}</h3>", i + 1));
            for b in &slide.blocks {
                match b {
                    SlideBlock::Text(t) => {
                        out.push_str(&format!("<p>{}</p>", escape_html(t)));
                    }
                    SlideBlock::Image { data_uri, alt } => {
                        out.push_str(&format!("<p><img src=\"{data_uri}\" alt=\"{alt}\"></p>"));
                    }
                }
            }
            out.push_str("</section>");
        }
        out.push_str("</body></html>");
        out
    }
}

pub fn pptx_to_html_bytes(path: &Path) -> Result<Vec<u8>, String> {
    let p = parse_pptx(path)?;
    Ok(p.to_html().into_bytes())
}

// ==== PPTX -> PDF（每页一张幻灯片，16:9） ====

const SLIDE_W: f32 = 960.0;
const SLIDE_H: f32 = 540.0;
const SLIDE_MARGIN: f32 = 40.0;
const SLIDE_TEXT_W: f32 = SLIDE_W - 2.0 * SLIDE_MARGIN;
const SLIDE_FONT: f32 = 24.0;
const SLIDE_LEADING: f32 = 34.0;

fn b64_decode(s: &str) -> Result<Vec<u8>, String> {
    const T: &[u8] =
        b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    let mut lookup = [255u8; 256];
    for (i, &c) in T.iter().enumerate() {
        lookup[c as usize] = i as u8;
    }
    let clean: Vec<u8> = s.bytes().filter(|b| !b.is_ascii_whitespace()).collect();
    let mut out = Vec::with_capacity(clean.len() / 4 * 3);
    for chunk in clean.chunks(4) {
        let g = |i: usize| -> Result<u32, String> {
            if i >= chunk.len() { return Ok(0); }
            let v = lookup[chunk[i] as usize];
            if v == 255 { return Err(format!("bad b64 {}", chunk[i] as char)); }
            Ok(v as u32)
        };
        let a = g(0)?; let b = g(1)?;
        let p2 = chunk.get(2) == Some(&b'=');
        let p3 = chunk.get(3) == Some(&b'=');
        let c = if p2 { 0 } else { g(2)? };
        let d = if p3 { 0 } else { g(3)? };
        let n = (a << 18) | (b << 12) | (c << 6) | d;
        out.push((n >> 16) as u8);
        if !p2 { out.push((n >> 8) as u8); }
        if !p3 { out.push(n as u8); }
    }
    Ok(out)
}

fn uri_to_jpeg(uri: &str) -> Result<(Vec<u8>, u32, u32), String> {
    let comma = uri.find(',').ok_or("bad data uri")?;
    let meta = &uri[..comma];
    let raw = b64_decode(&uri[comma + 1..])?;
    if meta.contains("jpeg") || meta.contains("jpg") {
        let (w, h) = crate::ir::jpeg_size(&raw).map_err(|e| e.to_string())?;
        return Ok((raw, w, h));
    }
    let img = image::ImageReader::new(std::io::Cursor::new(&raw))
        .with_guessed_format().map_err(|e| e.to_string())?
        .decode().map_err(|e| e.to_string())?;
    let (w, h) = (img.width(), img.height());
    let mut buf = Vec::new();
    image::codecs::jpeg::JpegEncoder::new_with_quality(&mut buf, 85)
        .encode_image(&img).map_err(|e| e.to_string())?;
    Ok((buf, w, h))
}

impl Pptx {
    pub fn to_pdf(&self) -> Result<Vec<u8>, String> {
        let mut chars = std::collections::BTreeSet::new();
        for s in &self.slides {
            for b in &s.blocks {
                if let SlideBlock::Text(t) = b {
                    for c in t.chars() { chars.insert(c); }
                }
            }
        }
        let font = crate::doc_pdf::EmbeddedFont::load_for_chars(&chars);
        let has_font = font.is_some();
        let mut pages: Vec<(String, Vec<(Vec<u8>, u32, u32)>)> = Vec::new();
        for s in &self.slides {
            pages.push(layout_slide(s, font.as_ref()));
        }
        write_pdf(&pages, font.as_ref(), has_font)
    }
}

fn layout_slide(
    slide: &Slide,
    font: Option<&crate::doc_pdf::EmbeddedFont>,
) -> (String, Vec<(Vec<u8>, u32, u32)>) {
    let mut content = String::new();
    let mut images = Vec::new();
    let mut y = SLIDE_H - SLIDE_MARGIN;

    for b in &slide.blocks {
        match b {
            SlideBlock::Text(t) => {
                let lines = wrap_slide(t);
                for line in lines {
                    if y - SLIDE_LEADING < SLIDE_MARGIN { break; }
                    let tstr = crate::doc_pdf::pdf_text(&line, font);
                    content.push_str(&format!(
                        "BT /F1 {SLIDE_FONT:.1} Tf 1 0 0 1 {SLIDE_MARGIN:.1} {y:.1} Tm {tstr} Tj ET\n"
                    ));
                    y -= SLIDE_LEADING;
                }
                y -= 10.0;
            }
            SlideBlock::Image { data_uri, alt: _ } => {
                let (jpeg, w, h) = match uri_to_jpeg(data_uri) {
                    Ok(v) => v,
                    Err(_) => continue,
                };
                let disp_w = SLIDE_TEXT_W.min(w as f32 * 72.0 / 96.0);
                let disp_h = disp_w * (h as f32 / w as f32);
                if y - disp_h < SLIDE_MARGIN { break; }
                let bottom = y - disp_h;
                let idx = images.len();
                images.push((jpeg, w, h));
                content.push_str(&format!(
                    "q {disp_w:.2} 0 0 {disp_h:.2} {SLIDE_MARGIN:.2} {bottom:.2} cm /Im{idx} Do Q\n"
                ));
                y = bottom - 10.0;
            }
        }
    }
    (content, images)
}

fn wrap_slide(text: &str) -> Vec<String> {
    let mut lines = Vec::new();
    let mut cur = String::new();
    let mut w = 0.0;
    for c in text.chars() {
        let cw = if c.is_ascii() { SLIDE_FONT * 0.5 } else { SLIDE_FONT * 1.0 };
        if w + cw > SLIDE_TEXT_W && !cur.is_empty() {
            lines.push(std::mem::take(&mut cur));
            w = 0.0;
        }
        cur.push(c);
        w += cw;
    }
    if !cur.is_empty() { lines.push(cur); }
    if lines.is_empty() { lines.push(String::new()); }
    lines
}

fn write_pdf(
    pages: &[(String, Vec<(Vec<u8>, u32, u32)>)],
    font: Option<&crate::doc_pdf::EmbeddedFont>,
    has_font: bool,
) -> Result<Vec<u8>, String> {
    let n = pages.len();
    let total_imgs: usize = pages.iter().map(|p| p.1.len()).sum();
    let (img_start, page_start, total_objs) = if has_font {
        (7, 7 + total_imgs, 6 + total_imgs + 2 * n)
    } else {
        (6, 6 + total_imgs, 5 + total_imgs + 2 * n)
    };

    let mut out: Vec<u8> = Vec::new();
    let mut offsets = Vec::new();
    out.extend(b"%PDF-1.3\n");

    offsets.push(out.len());
    out.extend(b"1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

    let mut kids = String::from("[");
    for k in 0..n {
        if k > 0 { kids.push(' '); }
        kids.push_str(&format!("{} 0 R", page_start + 2 * k));
    }
    kids.push(']');
    offsets.push(out.len());
    out.extend(format!("2 0 obj\n<< /Type /Pages /Count {n} /Kids {kids} >>\nendobj\n").into_bytes());

    if let Some(f) = font {
        let ttf_len = f.bytes_len();
        let upm = f.units_per_em();
        offsets.push(out.len());
        out.extend(b"3 0 obj\n<< /Type /Font /Subtype /Type0 /BaseFont /SimHei /Encoding /Identity-H /DescendantFonts [4 0 R] >>\nendobj\n");
        offsets.push(out.len());
        out.extend(b"4 0 obj\n<< /Type /Font /Subtype /CIDFontType2 /BaseFont /SimHei /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor 5 0 R /DW 1000 >>\nendobj\n");
        offsets.push(out.len());
        out.extend(format!("5 0 obj\n<< /Type /FontDescriptor /FontName /SimHei /Flags 6 /FontBBox [0 0 {upm} {upm}] /ItalicAngle 0 /Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 /FontFile2 6 0 R >>\nendobj\n").into_bytes());
        offsets.push(out.len());
        out.extend(format!("6 0 obj\n<< /Length {ttf_len} /Length1 {ttf_len} >>\nstream\n").into_bytes());
        out.extend_from_slice(&f.bytes());
        out.extend(b"\nendstream\nendobj\n");
    } else {
        offsets.push(out.len());
        out.extend(b"3 0 obj\n<< /Type /Font /Subtype /Type0 /BaseFont /STSong-Light /Encoding /UniGB-UCS2-H /DescendantFonts [4 0 R] >>\nendobj\n");
        offsets.push(out.len());
        out.extend(b"4 0 obj\n<< /Type /Font /Subtype /CIDFontType0 /BaseFont /STSong-Light /CIDSystemInfo << /Registry (Adobe) /Ordering (GB1) /Supplement 4 >> /FontDescriptor 5 0 R >>\nendobj\n");
        offsets.push(out.len());
        out.extend(b"5 0 obj\n<< /Type /FontDescriptor /FontName /STSong-Light /Flags 6 /FontBBox [0 0 1000 1000] /ItalicAngle 0 /Ascent 880 /Descent -120 /CapHeight 800 /StemV 80 >>\nendobj\n");
    }

    let mut oid = img_start;
    for (_, images) in pages {
        for (jpeg, w, h) in images {
            offsets.push(out.len());
            out.extend(format!("{oid} 0 obj\n<< /Type /XObject /Subtype /Image /Width {w} /Height {h} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {} >>\nstream\n", jpeg.len()).into_bytes());
            out.extend_from_slice(jpeg);
            out.extend(b"\nendstream\nendobj\n");
            oid += 1;
        }
    }

    let mut img_cur = img_start;
    for (k, (content, images)) in pages.iter().enumerate() {
        let page_obj = page_start + 2 * k;
        let cont_obj = page_obj + 1;
        let mut xdict = String::from("<< ");
        for i in 0..images.len() {
            xdict.push_str(&format!("/Im{i} {} 0 R ", img_cur + i));
        }
        xdict.push_str(">>");
        img_cur += images.len();

        offsets.push(out.len());
        out.extend(format!("{page_obj} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {SLIDE_W} {SLIDE_H}] /Resources << /Font << /F1 3 0 R >> /XObject {xdict} >> /Contents {cont_obj} 0 R >>\nendobj\n").into_bytes());

        let clen = content.len();
        offsets.push(out.len());
        out.extend(format!("{cont_obj} 0 obj\n<< /Length {clen} >>\nstream\n{}endstream\nendobj\n", content).into_bytes());
    }

    let xref_start = out.len();
    out.extend(format!("xref\n0 {}\n", total_objs + 1).into_bytes());
    out.extend(b"0000000000 65535 f \n");
    for off in offsets.iter().take(total_objs) {
        out.extend(format!("{off:010} 00000 n \n").into_bytes());
    }
    out.extend(format!("trailer\n<< /Size {} /Root 1 0 R >>\nstartxref\n{xref_start}\n%%EOF\n", total_objs + 1).into_bytes());
    Ok(out)
}

pub fn pptx_to_pdf_bytes(path: &Path) -> Result<Vec<u8>, String> {
    let p = parse_pptx(path)?;
    p.to_pdf()
}

/// PPTX -> Markdown：文本 block 按段落输出，图片 block 输出占位标注（不内嵌 base64）。
/// 语义对齐 Pandoc 3.x pptx reader：每页一个标题、正文段落、图片占位。
pub fn pptx_to_md(path: &Path) -> Result<String, String> {
    let p = parse_pptx(path)?;
    let mut out = String::new();
    for (i, slide) in p.slides.iter().enumerate() {
        out.push_str(&format!("## 幻灯片 {}\n\n", i + 1));
        for b in &slide.blocks {
            match b {
                SlideBlock::Text(t) => {
                    for line in t.lines().map(str::trim).filter(|l| !l.is_empty()) {
                        out.push_str(line);
                        out.push_str("\n\n");
                    }
                }
                SlideBlock::Image { alt, .. } => {
                    out.push_str(&format!("![{}](slide-{}-{})\n\n", alt, i + 1, alt));
                }
            }
        }
    }
    Ok(out.trim().to_string() + "\n")
}
