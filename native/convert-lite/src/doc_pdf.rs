//! Document IR -> PDF（流式排版：文本折行 + JPEG 直嵌 + 嵌入 TrueType 字体）。
//!
//! 设计取舍：
//! - 嵌入 TrueType 字体（CIDFontType2 + Identity-H + FontFile2），文本用 GID 编码，中文/英文均可显示。
//! - 字体来源：优先 exe 同目录 font.ttf，其次 C:\\Windows\\Fonts\\simhei.ttf；都没有则回退内置 STSong（Adobe 专用）。
//! - 文本按近似字符宽折行（ASCII≈0.5em，CJK≈1.0em），不做真实字体度量。
//! - 图片：JPEG 直接 DCTDecode 嵌入；PNG 等先用 image crate 转成 JPEG 再嵌。
//! - 表格逐 cell 竖线分隔不画线；超出页高自动换页。

use std::collections::BTreeSet;

use crate::doc::{Block, Document, Run};

const PAGE_W: f32 = 595.0;
const PAGE_H: f32 = 842.0;
const MARGIN: f32 = 50.0;
const TEXT_W: f32 = PAGE_W - 2.0 * MARGIN;
const BODY_SIZE: f32 = 12.0;
const BODY_LEADING: f32 = 16.0;

/// 嵌入的 TTF 字体（已子集化）：持有子集字节 + ttf-parser Face。
pub struct EmbeddedFont {
    bytes: Vec<u8>,
    face: ttf_parser::Face<'static>,
}
unsafe impl Send for EmbeddedFont {}

impl EmbeddedFont {
    /// 按字符集合子集化字体（DOCX 和 PPTX 共用）。
    pub fn load_for_chars(chars: &BTreeSet<char>) -> Option<Self> {
        let candidates = [
            std::env::current_exe().ok()?.parent()?.join("font.ttf"),
            std::path::PathBuf::from(r"C:\Windows\Fonts\simhei.ttf"),
        ];
        let original = candidates.iter().find_map(|p| std::fs::read(p).ok())?;

        if chars.is_empty() {
            return None;
        }

        let font = font_subset::Font::opentype(&original).ok()?;
        let subset = font.subset(chars).ok()?;
        let sub_bytes = subset.to_opentype();

        let len = sub_bytes.len();
        let ptr = sub_bytes.as_ptr();
        let leaked: &'static [u8] = unsafe { std::slice::from_raw_parts(ptr, len) };
        let face = ttf_parser::Face::parse(leaked, 0).ok()?;
        Some(Self { bytes: sub_bytes, face })
    }

    pub fn bytes(&self) -> &[u8] { &self.bytes }
    pub fn bytes_len(&self) -> usize { self.bytes.len() }
    pub fn units_per_em(&self) -> u16 { self.face.units_per_em() }
    pub fn gid(&self, c: char) -> u16 { self.face.glyph_index(c).map(|g| g.0).unwrap_or(0) }
    /// 字形 advance width（字体单位）。
    pub fn advance(&self, c: char) -> u16 {
        let gid = self.gid(c);
        self.face.glyph_hor_advance(ttf_parser::GlyphId(gid)).unwrap_or(500)
    }
}

/// 遍历文档收集所有字符。
fn collect_chars(doc: &Document, out: &mut BTreeSet<char>) {
    fn collect_runs(runs: &[Run], out: &mut BTreeSet<char>) {
        for r in runs {
            for c in r.text.chars() {
                out.insert(c);
            }
        }
    }
    for b in &doc.blocks {
        match b {
            Block::Heading { runs, .. } | Block::Paragraph { runs, .. } | Block::ListItem { runs, .. } => collect_runs(runs, out),
            Block::Table { rows } => {
                for row in rows {
                    for cell in row {
                        for c in cell.text.chars() {
                            out.insert(c);
                        }
                    }
                }
            }
            Block::Image { .. } => {}
        }
    }
}

/// "RRGGBB" -> (r,g,b) 0..1
fn color_rgb(hex: &str) -> (f32, f32, f32) {
    let v = |s: &str| u8::from_str_radix(s, 16).unwrap_or(0) as f32 / 255.0;
    (v(&hex[0..2]), v(&hex[2..4]), v(&hex[4..6]))
}

/// 绘制一行文本（含颜色/字号）。
fn emit_line(
    content: &mut String,
    font: Option<&EmbeddedFont>,
    text: &str,
    x: f32,
    y: f32,
    size: f32,
    color: &Option<String>,
) {
    let (r, g, b) = color.as_deref().map(color_rgb).unwrap_or((0.0, 0.0, 0.0));
    let t = pdf_text(text, font);
    content.push_str(&format!(
        "BT {r:.3} {g:.3} {b:.3} rg /F1 {size:.1} Tf 1 0 0 1 {x:.1} {y:.1} Tm {t} Tj ET\n"
    ));
}

/// 一张排版后的页：内容流 + 该页用到的图片（JPEG 字节 + 像素宽高）。
struct LaidPage {
    content: String,
    images: Vec<(Vec<u8>, u32, u32)>,
}

/// 把文本编成 PDF 十六进制字符串：有嵌入字体用 GID（2 字节），否则 UTF-16BE（STSong）。
pub fn pdf_text(s: &str, font: Option<&EmbeddedFont>) -> String {
    match font {
        Some(f) => {
            let mut out = String::from("<");
            for c in s.chars() {
                out.push_str(&format!("{:04X}", f.gid(c)));
            }
            out.push('>');
            out
        }
        None => {
            let mut out = String::from("<");
            for c in s.encode_utf16() {
                out.push_str(&format!("{c:04X}"));
            }
            out.push('>');
            out
        }
    }
}

/// 字符宽度估算（pt）。
fn char_width(c: char, size: f32) -> f32 {
    if c.is_ascii() {
        size * 0.5
    } else {
        size * 1.0
    }
}

fn wrap(text: &str, size: f32) -> Vec<String> {
    wrap_w(text, size, TEXT_W)
}

fn wrap_w(text: &str, size: f32, width: f32) -> Vec<String> {
    let mut lines = Vec::new();
    let mut cur = String::new();
    let mut w = 0.0;
    for c in text.chars() {
        let cw = char_width(c, size);
        if w + cw > width && !cur.is_empty() {
            lines.push(std::mem::take(&mut cur));
            w = 0.0;
        }
        cur.push(c);
        w += cw;
    }
    if !cur.is_empty() {
        lines.push(cur);
    }
    if lines.is_empty() {
        lines.push(String::new());
    }
    lines
}

fn base64_decode(s: &str) -> Result<Vec<u8>, String> {
    const T: &[u8] =
        b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    let mut lookup = [255u8; 256];
    for (i, &c) in T.iter().enumerate() {
        lookup[c as usize] = i as u8;
    }
    let clean: Vec<u8> = s.bytes().filter(|b| !b.is_ascii_whitespace()).collect();
    let mut out = Vec::with_capacity(clean.len() / 4 * 3);
    for chunk in clean.chunks(4) {
        let get = |i: usize| -> Result<u32, String> {
            if i >= chunk.len() {
                return Ok(0);
            }
            let v = lookup[chunk[i] as usize];
            if v == 255 {
                return Err(format!("非法 base64 字符 {}", chunk[i] as char));
            }
            Ok(v as u32)
        };
        let a = get(0)?;
        let b = get(1)?;
        let pad2 = chunk.get(2) == Some(&b'=');
        let pad3 = chunk.get(3) == Some(&b'=');
        let c = if pad2 { 0 } else { get(2)? };
        let d = if pad3 { 0 } else { get(3)? };
        let n = (a << 18) | (b << 12) | (c << 6) | d;
        out.push((n >> 16) as u8);
        if !pad2 {
            out.push((n >> 8) as u8);
        }
        if !pad3 {
            out.push(n as u8);
        }
    }
    Ok(out)
}

fn data_uri_to_jpeg(data_uri: &str) -> Result<(Vec<u8>, u32, u32), String> {
    let comma = data_uri.find(',').ok_or("data URI 缺逗号")?;
    let meta = &data_uri[..comma];
    let b64 = &data_uri[comma + 1..];
    let raw = base64_decode(b64)?;
    if meta.contains("jpeg") || meta.contains("jpg") {
        let (w, h) = crate::ir::jpeg_size(&raw).map_err(|e| format!("JPEG 帧头解析失败：{e}"))?;
        return Ok((raw, w, h));
    }
    let img = image::ImageReader::new(std::io::Cursor::new(&raw))
        .with_guessed_format()
        .map_err(|e| format!("图片格式识别失败：{e}"))?
        .decode()
        .map_err(|e| format!("图片解码失败：{e}"))?;
    let (w, h) = (img.width(), img.height());
    let mut buf = Vec::new();
    let mut enc = image::codecs::jpeg::JpegEncoder::new_with_quality(&mut buf, 85);
    enc.encode_image(&img).map_err(|e| format!("JPEG 编码失败：{e}"))?;
    Ok((buf, w, h))
}

fn runs_text(runs: &[Run]) -> String {
    runs.iter().map(|r| r.text.as_str()).collect()
}

fn layout(doc: &Document, font: Option<&EmbeddedFont>) -> Result<Vec<LaidPage>, String> {
    let mut pages: Vec<LaidPage> = Vec::new();
    let mut cur = LaidPage { content: String::new(), images: Vec::new() };
    let mut y = PAGE_H - MARGIN;

    // 分栏
    let col_count = doc.columns.max(1);
    let col_w = TEXT_W / col_count as f32;
    let mut cur_col: u32 = 0;

    fn need(cur_y: &mut f32, cur_col: &mut u32, col_count: u32, cur: &mut LaidPage, pages: &mut Vec<LaidPage>, h: f32) {
        if *cur_y - h < MARGIN {
            if *cur_col + 1 < col_count {
                *cur_col += 1;
                *cur_y = PAGE_H - MARGIN;
            } else {
                pages.push(std::mem::replace(
                    cur,
                    LaidPage { content: String::new(), images: Vec::new() },
                ));
                *cur_y = PAGE_H - MARGIN;
                *cur_col = 0;
            }
        }
    }

    for b in &doc.blocks {
        let col_left = MARGIN + cur_col as f32 * col_w;
        match b {
            Block::Heading { level, runs } => {
                let size = match *level { 1 => 20.0, 2 => 16.0, _ => 14.0 };
                let leading = size + 4.0;
                // heading 颜色：取第一个非 None 的 run color
                let color = runs.iter().find_map(|r| r.color.clone());
                let text: String = runs.iter().map(|r| r.text.as_str()).collect();
                for line in wrap_w(&text, size, col_w) {
                    need(&mut y, &mut cur_col, col_count, &mut cur, &mut pages, leading);
                    emit_line(&mut cur.content, font, &line, col_left, y, size, &color);
                    y -= leading;
                }
                y -= 4.0;
            }
            Block::Paragraph { runs } => {
                let text: String = runs.iter().map(|r| r.text.as_str()).collect();
                // paragraph 颜色：取第一个 run 的 color
                let color = runs.first().and_then(|r| r.color.clone());
                for line in wrap_w(&text, BODY_SIZE, col_w) {
                    need(&mut y, &mut cur_col, col_count, &mut cur, &mut pages, BODY_LEADING);
                    emit_line(&mut cur.content, font, &line, col_left, y, BODY_SIZE, &color);
                    y -= BODY_LEADING;
                }
            }
            Block::ListItem { level, ordered, runs } => {
                let indent = MARGIN + (*level as f32) * 18.0;
                let bullet = if *ordered { "1. " } else { "• " };
                let text: String = runs.iter().map(|r| r.text.as_str()).collect();
                let lines = wrap_w(&text, BODY_SIZE - 1.0, col_w);
                for (i, line) in lines.iter().enumerate() {
                    need(&mut y, &mut cur_col, col_count, &mut cur, &mut pages, BODY_LEADING);
                    let prefix = if i == 0 { bullet } else { "  " };
                    let full = format!("{prefix}{line}");
                    emit_line(&mut cur.content, font, &full, col_left + (indent - MARGIN), y, BODY_SIZE, &None);
                    y -= BODY_LEADING;
                }
            }
            Block::Table { rows } => {
                // 总列数：各行列数（含 colspan 展开）的最大值
                let total_cols = rows.iter()
                    .map(|r| r.iter().map(|c| c.colspan as usize).sum())
                    .max()
                    .unwrap_or(0);
                if total_cols == 0 { continue; }
                let col_w = TEXT_W / total_cols as f32;
                let mut row_bounds: Vec<(f32, f32)> = Vec::new();
                for row in rows {
                    let max_lines = row.iter().map(|cell| wrap(&cell.text, BODY_SIZE).len()).max().unwrap_or(1);
                    let row_h = max_lines as f32 * BODY_LEADING + 6.0;
                    need(&mut y, &mut cur_col, col_count, &mut cur, &mut pages, row_h);
                    let top = y;
                    let bottom = y - row_h;
                    let mut col_idx: usize = 0;
                    for cell in row {
                        let span = cell.colspan.max(1) as usize;
                        let cx0 = MARGIN + col_idx as f32 * col_w;
                        let cell_w = col_w * span as f32;
                        let lines = wrap(&cell.text, BODY_SIZE);
                        for (li, line) in lines.iter().enumerate() {
                            let ly = top - 6.0 - (li as f32 * BODY_LEADING);
                            let tw: f32 = line.chars().map(|ch| char_width(ch, BODY_SIZE)).sum();
                            let tx = match cell.align {
                                1 => cx0 + (cell_w - tw) / 2.0,
                                2 => cx0 + cell_w - tw - 3.0,
                                _ => cx0 + 3.0,
                            };
                            emit_line(&mut cur.content, font, line, tx, ly, BODY_SIZE, &None);
                        }
                        col_idx += span;
                    }
                    row_bounds.push((top, bottom));
                    y = bottom;
                }
                // 边框线
                for (top, bottom) in &row_bounds {
                    cur.content.push_str(&format!("0.5 w {MARGIN:.1} {top:.1} m {:.1} l S\n", MARGIN + TEXT_W));
                    cur.content.push_str(&format!("0.5 w {MARGIN:.1} {bottom:.1} m {:.1} l S\n", MARGIN + TEXT_W));
                }
                for ci in 0..=total_cols {
                    let x = MARGIN + ci as f32 * col_w;
                    for (top, bottom) in &row_bounds {
                        cur.content.push_str(&format!("0.5 w {x:.1} {top:.1} m {x:.1} {bottom:.1} l S\n"));
                    }
                }
                y -= 4.0;
            }
            Block::Image { data_uri, alt: _, display_w_emu, display_h_emu, pos_pt } => {
                let (jpeg, w_px, h_px) = match data_uri_to_jpeg(data_uri) {
                    Ok(v) => v,
                    Err(e) => {
                        need(&mut y, &mut cur_col, col_count, &mut cur, &mut pages, BODY_LEADING);
                        emit_line(&mut cur.content, font, &format!("[image decode failed: {e}]"), MARGIN, y, BODY_SIZE, &None);
                        y -= BODY_LEADING;
                        continue;
                    }
                };
                let disp_w = match (display_w_emu, display_h_emu) {
                    (Some(w), _) if *w > 0 => (*w as f32 / 914400.0) * 72.0,
                    _ => TEXT_W.min(w_px as f32 * 72.0 / 96.0),
                };
                let disp_h = match (display_w_emu, display_h_emu) {
                    (Some(_), Some(h)) if *h > 0 => (*h as f32 / 914400.0) * 72.0,
                    _ => disp_w * (h_px as f32 / w_px as f32),
                };
                let img_idx = cur.images.len();
                let xobj_name = format!("Im{img_idx}");
                cur.images.push((jpeg, w_px, h_px));
                if let Some((px, py)) = pos_pt {
                    // 浮动绝对定位：图片底部 = py - disp_h（py 是左上角 y）
                    let bottom = py - disp_h;
                    cur.content.push_str(&format!(
                        "q {disp_w:.2} 0 0 {disp_h:.2} {px:.2} {bottom:.2} cm /{xobj_name} Do Q\n"
                    ));
                } else {
                    need(&mut y, &mut cur_col, col_count, &mut cur, &mut pages, disp_h + 10.0);
                    let bottom = y - disp_h;
                    cur.content.push_str(&format!(
                        "q {disp_w:.2} 0 0 {disp_h:.2} {MARGIN:.2} {bottom:.2} cm /{xobj_name} Do Q\n"
                    ));
                    y = bottom - 10.0;
                }
            }
        }
    }
    // 脚注区（文末）
    if !doc.footnotes.is_empty() {
        need(&mut y, &mut cur_col, col_count, &mut cur, &mut pages, 20.0);
        // 分隔线
        cur.content.push_str(&format!("0.5 w {MARGIN:.1} {y:.1} m {:.1} l S\n", MARGIN + TEXT_W));
        y -= 14.0;
        for (id, text) in &doc.footnotes {
            let line = format!("[{id}] {text}");
            for ln in wrap(&line, BODY_SIZE - 1.0) {
                need(&mut y, &mut cur_col, col_count, &mut cur, &mut pages, BODY_LEADING - 2.0);
                emit_line(&mut cur.content, font, &ln, MARGIN, y, BODY_SIZE - 1.0, &None);
                y -= BODY_LEADING - 2.0;
            }
        }
    }
    // 尾注区（脚注之后）
    if !doc.endnotes.is_empty() {
        need(&mut y, &mut cur_col, col_count, &mut cur, &mut pages, 20.0);
        cur.content.push_str(&format!("0.5 w {MARGIN:.1} {y:.1} m {:.1} l S\n", MARGIN + TEXT_W));
        y -= 14.0;
        for (id, text) in &doc.endnotes {
            let line = format!("[{id}] {text}");
            for ln in wrap(&line, BODY_SIZE - 1.0) {
                need(&mut y, &mut cur_col, col_count, &mut cur, &mut pages, BODY_LEADING - 2.0);
                emit_line(&mut cur.content, font, &ln, MARGIN, y, BODY_SIZE - 1.0, &None);
                y -= BODY_LEADING - 2.0;
            }
        }
    }
    if !cur.content.is_empty() || !cur.images.is_empty() {
        pages.push(cur);
    }
    if pages.is_empty() {
        return Err("空文档，无法生成 PDF".to_string());
    }
    Ok(pages)
}

pub fn render_doc_pdf(doc: &Document) -> Result<Vec<u8>, String> {
    let mut chars = BTreeSet::new();
    collect_chars(doc, &mut chars);
    let font = EmbeddedFont::load_for_chars(&chars);
    let has_font = font.is_some();
    let pages = layout(doc, font.as_ref())?;
    let n = pages.len();
    let total_imgs: usize = pages.iter().map(|p| p.images.len()).sum();

    // 对象布局：
    //   无嵌入字体：1=Catalog,2=Pages,3=Type0,4=CID,5=FD,图片从6
    //   有嵌入字体：1=Catalog,2=Pages,3=Type0,4=CIDFontType2,5=FD,6=FontFile2,图片从7
    let (img_obj_start, page_obj_start, total_objs);
    if has_font {
        img_obj_start = 7;
        page_obj_start = img_obj_start + total_imgs;
        total_objs = 6 + total_imgs + 2 * n;
    } else {
        img_obj_start = 6;
        page_obj_start = img_obj_start + total_imgs;
        total_objs = 5 + total_imgs + 2 * n;
    }

    let mut out: Vec<u8> = Vec::new();
    let mut offsets: Vec<usize> = Vec::with_capacity(total_objs + 1);
    out.extend(b"%PDF-1.3\n");

    offsets.push(out.len());
    out.extend(b"1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

    let mut kids = String::from("[");
    for k in 0..n {
        if k > 0 {
            kids.push(' ');
        }
        kids.push_str(&format!("{} 0 R", page_obj_start + 2 * k));
    }
    kids.push(']');
    offsets.push(out.len());
    out.extend(format!("2 0 obj\n<< /Type /Pages /Count {n} /Kids {kids} >>\nendobj\n").into_bytes());

    // 字体对象
    if let Some(f) = &font {
        let ttf_len = f.bytes.len();
        let upm = f.units_per_em();
        // obj 3 Type0
        offsets.push(out.len());
        out.extend(b"3 0 obj\n<< /Type /Font /Subtype /Type0 /BaseFont /SimHei /Encoding /Identity-H /DescendantFonts [4 0 R] >>\nendobj\n");
        // obj 4 CIDFontType2
        offsets.push(out.len());
        // 建 /W 数组：遍历用到的字符，GID + advance*1000/upm
        let mut w_parts = String::from("/W [");
        for ch in &chars {
            let g = f.gid(*ch);
            let adv = f.advance(*ch);
            let w = 1000u16; // debug: all 1000
            w_parts.push_str(&format!("{g} {w} "));
        }
        w_parts.push(']');
        out.extend(b"4 0 obj\n<< /Type /Font /Subtype /CIDFontType2 /BaseFont /SimHei /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor 5 0 R /DW 1000 >>\nendobj\n");
        // obj 5 FontDescriptor
        offsets.push(out.len());
        out.extend(format!("5 0 obj\n<< /Type /FontDescriptor /FontName /SimHei /Flags 6 /FontBBox [0 0 {upm} {upm}] /ItalicAngle 0 /Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 /FontFile2 6 0 R >>\nendobj\n").into_bytes());
        // obj 6 FontFile2（嵌入 TTF 字节）
        offsets.push(out.len());
        out.extend(format!("6 0 obj\n<< /Length {ttf_len} /Length1 {ttf_len} >>\nstream\n").into_bytes());
        out.extend_from_slice(&f.bytes);
        out.extend(b"\nendstream\nendobj\n");
    } else {
        offsets.push(out.len());
        out.extend(b"3 0 obj\n<< /Type /Font /Subtype /Type0 /BaseFont /STSong-Light /Encoding /UniGB-UCS2-H /DescendantFonts [4 0 R] >>\nendobj\n");
        offsets.push(out.len());
        out.extend(b"4 0 obj\n<< /Type /Font /Subtype /CIDFontType0 /BaseFont /STSong-Light /CIDSystemInfo << /Registry (Adobe) /Ordering (GB1) /Supplement 4 >> /FontDescriptor 5 0 R >>\nendobj\n");
        offsets.push(out.len());
        out.extend(b"5 0 obj\n<< /Type /FontDescriptor /FontName /STSong-Light /Flags 6 /FontBBox [0 0 1000 1000] /ItalicAngle 0 /Ascent 880 /Descent -120 /CapHeight 800 /StemV 80 >>\nendobj\n");
    }

    // 图片对象
    let mut img_obj_id = img_obj_start;
    for page in &pages {
        for (jpeg, w, h) in &page.images {
            offsets.push(out.len());
            out.extend(
                format!(
                    "{img_obj_id} 0 obj\n<< /Type /XObject /Subtype /Image /Width {w} /Height {h} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {} >>\nstream\n",
                    jpeg.len()
                )
                .into_bytes(),
            );
            out.extend_from_slice(jpeg);
            out.extend(b"\nendstream\nendobj\n");
            img_obj_id += 1;
        }
    }

    let mut global_img_cursor = img_obj_start;
    for (k, page) in pages.iter().enumerate() {
        let page_obj = page_obj_start + 2 * k;
        let cont_obj = page_obj + 1;

        // 页眉（顶部小灰字）
        let mut header_footer = String::new();
        if let Some(h) = &doc.header {
            emit_line(&mut header_footer, font.as_ref(), h, MARGIN, PAGE_H - 30.0, 9.0, &Some("808080".to_string()));
        }
        // 页脚（底部页码）
        let footer_text = match &doc.footer {
            Some(f) => format!("{f} — 第 {} 页", k + 1),
            None => format!("第 {} 页", k + 1),
        };
        emit_line(&mut header_footer, font.as_ref(), &footer_text, MARGIN, 30.0, 9.0, &Some("808080".to_string()));
        let mut full_content = header_footer;
        full_content.push_str(&page.content);

        let mut xdict = String::new();
        if !page.images.is_empty() {
            xdict.push_str("<< ");
            for i in 0..page.images.len() {
                let oid = global_img_cursor + i;
                xdict.push_str(&format!("/Im{i} {oid} 0 R "));
            }
            xdict.push_str(">>");
        } else {
            xdict = "<< >>".to_string();
        }
        global_img_cursor += page.images.len();

        offsets.push(out.len());
        out.extend(
            format!(
                "{page_obj} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PAGE_W} {PAGE_H}] /Resources << /Font << /F1 3 0 R >> /XObject {xdict} >> /Contents {cont_obj} 0 R >>\nendobj\n"
            )
            .into_bytes(),
        );

        let clen = full_content.len();
        offsets.push(out.len());
        out.extend(
            format!(
                "{cont_obj} 0 obj\n<< /Length {clen} >>\nstream\n{}endstream\nendobj\n",
                full_content
            )
            .into_bytes(),
        );
    }

    let xref_start = out.len();
    out.extend(format!("xref\n0 {}\n", total_objs + 1).into_bytes());
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

pub fn docx_to_pdf_bytes(path: &std::path::Path) -> Result<Vec<u8>, String> {
    let doc = crate::doc::parse_docx(path)?;
    render_doc_pdf(&doc)
}
