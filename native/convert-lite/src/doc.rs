//! 文档域 IR + DOCX 解析 + HTML 渲染。
//!
//! 路线：DOCX 本质是 zip 包，正文在 word/document.xml（OOXML），图片在 word/media/，
//! 引用关系在 word/_rels/document.xml.rels。解析成与格式无关的 Block 流，再渲染成 HTML。
//! 图片以 base64 data URI 内嵌，产物单文件自包含。
//! 诚实边界：只提取文字结构与图片；不做页眉页脚、列表编号、复杂样式、DOCX→PDF。

use std::collections::HashMap;
use std::io::{Cursor, Read};
use std::path::Path;

use quick_xml::events::Event;
use quick_xml::Reader;

/// 一个内联 run。
#[derive(Debug, Clone)]
pub struct Run {
    pub text: String,
    pub bold: bool,
    pub italic: bool,
    /// 颜色 "RRGGBB"（None = 默认黑）。
    pub color: Option<String>,
    /// 字号（磅，None = 继承正文 12pt）。
    pub size_pt: Option<f32>,
}

/// 表格单元格。
#[derive(Debug, Clone)]
pub struct Cell {
    pub text: String,
    /// 对齐：0=左 1=中 2=右。
    pub align: u8,
    /// 横向合并列数（gridSpan）。
    pub colspan: u32,
}

/// 块级节点。
#[derive(Debug, Clone)]
pub enum Block {
    Heading { level: u8, runs: Vec<Run> },
    Paragraph { runs: Vec<Run> },
    ListItem { level: u32, ordered: bool, runs: Vec<Run> },
    Table { rows: Vec<Vec<Cell>> },
    /// 图片：data URI（base64 内嵌）。display_*_emu 来自 wp:extent（914400 EMU=1in）。
    /// pos_pt = Some((x,y)) 时为浮动定位（相对页面，PDF 坐标，原点左下）。
    Image { data_uri: String, alt: String, display_w_emu: Option<u32>, display_h_emu: Option<u32>, pos_pt: Option<(f32, f32)> },
}

/// 文档 IR：块级节点流。
#[derive(Debug, Clone)]
pub struct Document {
    pub blocks: Vec<Block>,
    /// 脚注：(id, 纯文本)。
    pub footnotes: Vec<(u32, String)>,
    /// 尾注：(id, 纯文本)。
    pub endnotes: Vec<(u32, String)>,
    /// 页眉/页脚纯文本（None = 无）。
    pub header: Option<String>,
    pub footer: Option<String>,
    /// 分栏数（1=单栏）。
    pub columns: u32,
}

impl Document {
    pub fn new() -> Self {
        Self { blocks: Vec::new(), footnotes: Vec::new(), endnotes: Vec::new(), header: None, footer: None, columns: 1 }
    }
}

fn escape_html(s: &str) -> String {
    s.replace('&', "&amp;")
        .replace('<', "&lt;")
        .replace('>', "&gt;")
}

fn runs_to_html(runs: &[Run]) -> String {
    let mut out = String::new();
    for r in runs {
        let t = escape_html(&r.text);
        let mut style = String::new();
        if let Some(col) = &r.color {
            style.push_str(&format!("color:#{col};"));
        }
        if let Some(sz) = r.size_pt {
            style.push_str(&format!("font-size:{sz:.1}pt;"));
        }
        let style_attr = if style.is_empty() { String::new() } else { format!(" style=\"{style}\"") };
        let inner = if r.italic { format!("<i>{t}</i>") } else { t };
        if r.bold {
            out.push_str(&format!("<strong{style_attr}>{inner}</strong>"));
        } else if !style_attr.is_empty() {
            out.push_str(&format!("<span{style_attr}>{inner}</span>"));
        } else {
            out.push_str(&inner);
        }
    }
    out
}

/// 文档 IR -> HTML（带 UTF-8 骨架）。
pub fn render_html(doc: &Document) -> String {
    let mut out = String::from(
        "<!doctype html><html><head><meta charset=\"utf-8\"><title>document</title></head><body>",
    );
    for b in &doc.blocks {
        match b {
            Block::Heading { level, runs } => {
                let l = (*level).clamp(1, 6);
                out.push_str(&format!("<h{l}>{}</h{l}>", runs_to_html(runs)));
            }
            Block::Paragraph { runs } => {
                out.push_str(&format!("<p>{}</p>", runs_to_html(runs)));
            }
            Block::ListItem { level, ordered, runs } => {
                let tag = if *ordered { "ol" } else { "ul" };
                out.push_str(&format!("<{tag} style=\"margin-left:{}em\">", (*level + 1) as f32 * 1.5));
                out.push_str(&format!("<li>{}</li>", runs_to_html(runs)));
                out.push_str(&format!("</{tag}>"));
            }
            Block::Table { rows } => {
                out.push_str("<table border=\"1\" cellpadding=\"4\">");
                for row in rows {
                    out.push_str("<tr>");
                    for cell in row {
                        let align = match cell.align { 1 => "center", 2 => "right", _ => "left" };
                        let cs = if cell.colspan > 1 { format!(" colspan=\"{}\"", cell.colspan) } else { String::new() };
                        out.push_str(&format!("<td{cs} style=\"text-align:{align}\">{}</td>", escape_html(&cell.text)));
                    }
                    out.push_str("</tr>");
                }
                out.push_str("</table>");
            }
            Block::Image { data_uri, alt, display_w_emu, display_h_emu, pos_pt: _ } => {
                let mut style = String::from("max-width:100%;");
                if let (Some(w), Some(h)) = (display_w_emu, display_h_emu) {
                    // 914400 EMU = 96px = 1in
                    let css_w = (*w as f32 / 914400.0) * 96.0;
                    let _css_h = (*h as f32 / 914400.0) * 96.0;
                    style.push_str(&format!("width:{css_w:.1}px;"));
                }
                out.push_str(&format!("<p><img src=\"{data_uri}\" alt=\"{alt}\" style=\"{style}\"/></p>"));
            }
        }
    }
    out.push_str("</body></html>");
    out
}

/// 手写 base64（不引依赖）。
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

/// 解析 word/_rels/document.xml.rels，建 rId -> 目标路径（相对 word/）映射。
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

/// 提取单个 XML 文件里所有 w:t 文本（用于 header/footer 纯文本）。
fn extract_all_text(xml: &[u8]) -> String {
    let mut reader = Reader::from_reader(xml);
    let mut buf = Vec::new();
    let mut out = String::new();
    loop {
        match reader.read_event_into(&mut buf) {
            Ok(Event::Text(t)) => {
                if let Ok(s) = t.unescape() {
                    out.push_str(&s);
                }
            }
            Ok(Event::Eof) => break,
            _ => {}
        }
        buf.clear();
    }
    out
}

/// 解析 word/footnotes.xml：footnote id -> 文本。
/// 跳过 id < 0 的分隔符/continuation 注脚。
fn parse_footnotes(xml: &[u8]) -> Vec<(u32, String)> {
    let mut reader = Reader::from_reader(xml);
    let mut buf = Vec::new();
    let mut out = Vec::new();
    let mut cur_id: Option<u32> = None;
    let mut cur_text = String::new();
    loop {
        match reader.read_event_into(&mut buf) {
            Ok(Event::Start(e)) => match e.name().as_ref() {
                b"w:footnote" => {
                    cur_id = e.attributes().find_map(|a| {
                        let a = a.ok()?;
                        (a.key.as_ref() == b"w:id").then_some(a.value)
                    }).and_then(|v| String::from_utf8_lossy(&v).parse::<i64>().ok()).and_then(|v| if v >= 0 { Some(v as u32) } else { None });
                    cur_text.clear();
                }
                _ => {}
            },
            Ok(Event::Text(t)) => {
                if cur_id.is_some() {
                    if let Ok(s) = t.unescape() {
                        cur_text.push_str(&s);
                    }
                }
            }
            Ok(Event::End(e)) => match e.name().as_ref() {
                b"w:footnote" => {
                    if let Some(id) = cur_id.take() {
                        out.push((id, std::mem::take(&mut cur_text)));
                    }
                }
                _ => {}
            },
            Ok(Event::Eof) => break,
            _ => {}
        }
        buf.clear();
    }
    out
}

/// 归一化 zip 内路径（Windows 反斜杠兼容）。
fn norm(s: &str) -> String {
    s.replace('\\', "/")
}

/// 解析 DOCX 为 Document。
pub fn parse_docx(path: &Path) -> Result<Document, String> {
    let bytes = std::fs::read(path).map_err(|e| format!("读取 docx 失败：{e}"))?;
    let cursor = Cursor::new(bytes);
    let mut archive = zip::ZipArchive::new(cursor).map_err(|e| format!("不是有效 zip/docx：{e}"))?;

    // 把整个 zip 读成 normalized_path -> bytes（docx 体积通常不大；超大文档留作流式优化）。
    let mut files: HashMap<String, Vec<u8>> = HashMap::new();
    for i in 0..archive.len() {
        let mut e = archive.by_index(i).map_err(|e| e.to_string())?;
        let n = norm(e.name());
        let mut v = Vec::new();
        e.read_to_end(&mut v).map_err(|e| e.to_string())?;
        files.insert(n, v);
    }

    let xml = files
        .get("word/document.xml")
        .ok_or_else(|| "docx 内缺 word/document.xml（不是标准 docx）".to_string())?;

    // 关系表
    let rels = files
        .get("word/_rels/document.xml.rels")
        .map(|r| parse_rels(r))
        .unwrap_or_default();

    // numbering.xml：numId -> ordered
    let numbering = files
        .get("word/numbering.xml")
        .map(|x| parse_numbering(x))
        .unwrap_or_default();

    let mut doc = parse_document_xml(xml, &rels, &files, &numbering)?;

    // 脚注
    if let Some(fn_xml) = files.get("word/footnotes.xml") {
        doc.footnotes = parse_footnotes(fn_xml);
    }
    // 尾注（与脚注同结构）
    if let Some(en_xml) = files.get("word/endnotes.xml") {
        doc.endnotes = parse_footnotes(en_xml);
    }
    // 页眉页脚：找 header1/header2/footer1 等（取第一个非空）
    doc.header = files.iter()
        .filter(|(k, _)| k.starts_with("word/header") && k.ends_with(".xml"))
        .map(|(_, v)| extract_all_text(v))
        .find(|s| !s.trim().is_empty());
    doc.footer = files.iter()
        .filter(|(k, _)| k.starts_with("word/footer") && k.ends_with(".xml"))
        .map(|(_, v)| extract_all_text(v))
        .find(|s| !s.trim().is_empty());

    Ok(doc)
}

/// 解析 numbering.xml：返回 numId -> ordered（bullet=无序，其余=有序）。
fn parse_numbering(xml: &[u8]) -> HashMap<String, bool> {
    let mut reader = Reader::from_reader(xml);
    let mut buf = Vec::new();
    let mut num_to_abs: HashMap<String, String> = HashMap::new();
    let mut abs_ordered: HashMap<String, bool> = HashMap::new();

    let mut cur_num_id: Option<String> = None;
    let mut cur_abs_id: Option<String> = None;
    let mut abs_is_ordered: bool = true;
    let mut in_lvl = false;

    loop {
        match reader.read_event_into(&mut buf) {
            Ok(Event::Start(e)) => match e.name().as_ref() {
                b"w:num" => {
                    cur_num_id = e.attributes().find_map(|a| {
                        let a = a.ok()?;
                        (a.key.as_ref() == b"w:numId").then_some(String::from_utf8_lossy(&a.value).into_owned())
                    });
                }
                b"w:abstractNum" => {
                    if let Some(v) = e.attributes().find_map(|a| {
                        let a = a.ok()?;
                        (a.key.as_ref() == b"w:abstractNumId").then_some(a.value)
                    }) {
                        cur_abs_id = Some(String::from_utf8_lossy(&v).into_owned());
                        abs_is_ordered = true;
                    }
                }
                b"w:lvl" => in_lvl = true,
                _ => {}
            },
            Ok(Event::Empty(e)) => match e.name().as_ref() {
                b"w:numFmt" if in_lvl => {
                    if let Some(v) = e.attributes().find_map(|a| {
                        let a = a.ok()?;
                        (a.key.as_ref() == b"w:val").then_some(a.value)
                    }) {
                        if &String::from_utf8_lossy(&v) == "bullet" {
                            abs_is_ordered = false;
                        }
                    }
                }
                b"w:abstractNumId" => {
                    if let (Some(nid), Some(v)) = (&cur_num_id, e.attributes().find_map(|a| {
                        let a = a.ok()?;
                        (a.key.as_ref() == b"w:val").then_some(a.value)
                    })) {
                        num_to_abs.insert(nid.clone(), String::from_utf8_lossy(&v).into_owned());
                    }
                }
                _ => {}
            },
            Ok(Event::End(e)) => match e.name().as_ref() {
                b"w:lvl" => in_lvl = false,
                b"w:num" => cur_num_id = None,
                b"w:abstractNum" => {
                    if let Some(id) = cur_abs_id.take() {
                        abs_ordered.insert(id, abs_is_ordered);
                    }
                }
                _ => {}
            },
            Ok(Event::Eof) => break,
            _ => {}
        }
        buf.clear();
    }

    let mut out = HashMap::new();
    for (num_id, abs_id) in num_to_abs {
        if let Some(&ordered) = abs_ordered.get(&abs_id) {
            out.insert(num_id, ordered);
        }
    }
    out
}

fn parse_document_xml(
    xml: &[u8],
    rels: &HashMap<String, String>,
    files: &HashMap<String, Vec<u8>>,
    numbering: &HashMap<String, bool>,
) -> Result<Document, String> {
    let mut reader = Reader::from_reader(xml);
    reader.config_mut().trim_text(true);
    let mut buf = Vec::new();

    let mut doc = Document::new();

    let mut cur_runs: Vec<Run> = Vec::new();
    let mut cur_bold = false;
    let mut cur_italic = false;
    let mut cur_color: Option<String> = None;
    let mut cur_size_pt: Option<f32> = None;
    let mut heading_level: Option<u8> = None;
    let mut in_p = false;

    // 列表
    let mut in_list = false;
    let mut list_level: u32 = 0;
    let mut list_ordered: bool = false;

    // 对齐（当前段落/单元格）
    let mut cur_align: u8 = 0;

    // 最近 wp:extent（EMU），供下一张图片使用
    let mut last_extent: Option<(u32, u32)> = None;
    // 浮动图片定位
    let mut in_anchor = false;
    let mut in_pos_h = false;
    let mut anchor_x_emu: Option<i64> = None;
    let mut anchor_y_emu: Option<i64> = None;

    let mut in_table = false;
    let mut cur_row: Vec<Cell> = Vec::new();
    let mut cur_cell: String = String::new();
    let mut cur_cell_align: u8 = 0;
    let mut cur_colspan: u32 = 1;
    let mut rows: Vec<Vec<Cell>> = Vec::new();

    macro_rules! flush_para {
        () => {
            if !cur_runs.is_empty() {
                let runs = std::mem::take(&mut cur_runs);
                if in_list {
                    doc.blocks.push(Block::ListItem { level: list_level, ordered: list_ordered, runs });
                } else {
                    match heading_level {
                        Some(l) => doc.blocks.push(Block::Heading { level: l, runs }),
                        None => doc.blocks.push(Block::Paragraph { runs }),
                    }
                }
            }
            heading_level = None;
            in_list = false;
        };
    }

    // 把 r:embed 指向的图片读成 data URI，push 为 Image block。
    let mut embed_image = |rid: &[u8], last_extent: &Option<(u32, u32)>, pos: Option<(f32, f32)>, doc: &mut Document| {
        let rid = String::from_utf8_lossy(rid);
        if let Some(target) = rels.get(rid.as_ref()) {
            let p = norm(&format!("word/{target}"));
            if let Some(bytes) = files.get(&p) {
                let mime = mime_of(target);
                let b64 = base64_encode(bytes);
                let (w, h) = last_extent.unwrap_or((0, 0));
                doc.blocks.push(Block::Image {
                    data_uri: format!("data:{mime};base64,{b64}"),
                    alt: target.clone(),
                    display_w_emu: if w > 0 { Some(w) } else { None },
                    display_h_emu: if h > 0 { Some(h) } else { None },
                    pos_pt: pos,
                });
            }
        }
    };

    loop {
        match reader.read_event_into(&mut buf) {
            Ok(Event::Start(e)) => {
                match e.name().as_ref() {
                    b"w:p" => {
                        in_p = true;
                        cur_runs.clear();
                        cur_bold = false;
                        cur_italic = false;
                        cur_color = None;
                        cur_size_pt = None;
                        cur_align = 0;
                        heading_level = None;
                    }
                    b"w:r" => {
                        cur_bold = false;
                        cur_italic = false;
                        cur_color = None;
                        cur_size_pt = None;
                    }
                    b"w:b" => cur_bold = true,
                    b"w:i" => cur_italic = true,
                    b"w:numPr" => in_list = true,
                    b"w:tbl" => {
                        in_table = true;
                        rows.clear();
                    }
                    b"w:tr" => cur_row.clear(),
                    b"w:tc" => {
                        cur_cell.clear();
                        cur_cell_align = 0;
                        cur_colspan = 1;
                    }
                    b"wp:anchor" => {
                        in_anchor = true;
                        in_pos_h = false;
                        anchor_x_emu = None;
                        anchor_y_emu = None;
                    }
                    b"wp:inline" => {
                        in_anchor = false;
                    }
                    b"wp:positionH" => in_pos_h = true,
                    b"wp:positionV" => in_pos_h = false,
                    _ => {}
                }
            }
            Ok(Event::Empty(e)) => match e.name().as_ref() {
                b"w:b" => cur_bold = true,
                b"w:i" => cur_italic = true,
                b"w:color" => {
                    if let Some(v) = e.attributes().find_map(|a| {
                        let a = a.ok()?;
                        (a.key.as_ref() == b"w:val").then_some(a.value)
                    }) {
                        let s = String::from_utf8_lossy(&v).to_ascii_uppercase();
                        if s != "AUTO" && s.len() == 6 {
                            cur_color = Some(s);
                        }
                    }
                }
                b"w:sz" => {
                    if let Some(v) = e.attributes().find_map(|a| {
                        let a = a.ok()?;
                        (a.key.as_ref() == b"w:val").then_some(a.value)
                    }) {
                        if let Ok(half) = String::from_utf8_lossy(&v).parse::<f32>() {
                            cur_size_pt = Some(half / 2.0);
                        }
                    }
                }
                b"w:ilvl" => {
                    if let Some(v) = e.attributes().find_map(|a| {
                        let a = a.ok()?;
                        (a.key.as_ref() == b"w:val").then_some(a.value)
                    }) {
                        list_level = String::from_utf8_lossy(&v).parse().unwrap_or(0);
                    }
                }
                b"w:numId" => {
                    if let Some(v) = e.attributes().find_map(|a| {
                        let a = a.ok()?;
                        (a.key.as_ref() == b"w:val").then_some(a.value)
                    }) {
                        let nid = String::from_utf8_lossy(&v);
                        list_ordered = *numbering.get(nid.as_ref()).unwrap_or(&false);
                    }
                }
                b"w:gridSpan" => {
                    if let Some(v) = e.attributes().find_map(|a| {
                        let a = a.ok()?;
                        (a.key.as_ref() == b"w:val").then_some(a.value)
                    }) {
                        cur_colspan = String::from_utf8_lossy(&v).parse().unwrap_or(1).max(1);
                    }
                }
                b"w:jc" => {
                    if let Some(v) = e.attributes().find_map(|a| {
                        let a = a.ok()?;
                        (a.key.as_ref() == b"w:val").then_some(a.value)
                    }) {
                        let s = String::from_utf8_lossy(&v);
                        cur_align = match s.as_ref() { "center" => 1, "right" => 2, _ => 0 };
                        if in_table {
                            cur_cell_align = cur_align;
                        }
                    }
                }
                b"w:cols" => {
                    if let Some(v) = e.attributes().find_map(|a| {
                        let a = a.ok()?;
                        (a.key.as_ref() == b"w:num").then_some(a.value)
                    }) {
                        let n: u32 = String::from_utf8_lossy(&v).parse().unwrap_or(1);
                        doc.columns = n.clamp(1, 4);
                    }
                }
                b"wp:posOffset" => {
                    if let Some(v) = e.attributes().find_map(|a| {
                        let a = a.ok()?;
                        (a.key.as_ref() == b"w:val").then_some(a.value)
                    }) {
                        let val = String::from_utf8_lossy(&v).parse::<i64>().unwrap_or(0);
                        if in_pos_h { anchor_x_emu = Some(val); } else { anchor_y_emu = Some(val); }
                    }
                }
                b"wp:extent" => {
                    let mut cx: Option<u32> = None;
                    let mut cy: Option<u32> = None;
                    for a in e.attributes().flatten() {
                        let k = a.key.as_ref();
                        let v = String::from_utf8_lossy(&a.value);
                        if k == b"cx" { cx = v.parse().ok(); }
                        else if k == b"cy" { cy = v.parse().ok(); }
                    }
                    if let (Some(cx), Some(cy)) = (cx, cy) {
                        last_extent = Some((cx, cy));
                    }
                }
                b"w:pStyle" => {
                    if let Some(val) = e.attributes().find_map(|a| {
                        let a = a.ok()?;
                        (a.key.as_ref() == b"w:val").then_some(a.value)
                    }) {
                        let s = String::from_utf8_lossy(&val);
                        if s.starts_with("Heading") {
                            heading_level = s.chars().filter(|c| c.is_ascii_digit()).collect::<String>().parse().ok();
                        }
                    }
                }
                b"w:footnoteReference" => {
                    if in_p {
                        if let Some(v) = e.attributes().find_map(|a| {
                            let a = a.ok()?;
                            (a.key.as_ref() == b"w:id").then_some(a.value)
                        }) {
                            let id = String::from_utf8_lossy(&v);
                            cur_runs.push(Run {
                                text: format!("[{id}]"),
                                bold: false, italic: false, color: None, size_pt: None,
                            });
                        }
                    }
                }
                b"w:endnoteReference" => {
                    if in_p {
                        if let Some(v) = e.attributes().find_map(|a| {
                            let a = a.ok()?;
                            (a.key.as_ref() == b"w:id").then_some(a.value)
                        }) {
                            let id = String::from_utf8_lossy(&v);
                            cur_runs.push(Run {
                                text: format!("[{id}]"),
                                bold: false, italic: false, color: None, size_pt: None,
                            });
                        }
                    }
                }
                // <a:blip r:embed="rIdX"/>
                b"a:blip" => {
                    if let Some(rid) = e.attributes().find_map(|a| {
                        let a = a.ok()?;
                        (a.key.as_ref() == b"r:embed").then_some(a.value)
                    }) {
                        // 浮动定位：EMU -> pt（914400 EMU = 72pt），PDF 原点左下需翻转 y
                        let pos = if in_anchor {
                            match (anchor_x_emu, anchor_y_emu) {
                                (Some(x), Some(y)) => {
                                    let x_pt = x as f32 / 914400.0 * 72.0;
                                    let y_pt = 842.0 - (y as f32 / 914400.0 * 72.0);
                                    Some((x_pt, y_pt))
                                }
                                _ => None,
                            }
                        } else { None };
                        embed_image(&rid, &last_extent, pos, &mut doc);
                        last_extent = None;
                    }
                }
                _ => {}
            },
            Ok(Event::End(e)) => match e.name().as_ref() {
                b"w:p" => {
                    if !in_table {
                        flush_para!();
                    }
                    in_p = false;
                }
                b"w:b" => cur_bold = false,
                b"w:i" => cur_italic = false,
                b"w:tc" => {
                    cur_row.push(Cell { text: std::mem::take(&mut cur_cell), align: cur_cell_align, colspan: cur_colspan });
                }
                b"w:tr" => {
                    if !cur_row.is_empty() {
                        rows.push(std::mem::take(&mut cur_row));
                    }
                }
                b"w:tbl" => {
                    if !rows.is_empty() {
                        doc.blocks.push(Block::Table { rows: std::mem::take(&mut rows) });
                    }
                    in_table = false;
                }
                _ => {}
            },
            Ok(Event::Text(t)) => {
                let text = t.unescape().map_err(|e| format!("XML 文本解码失败：{e}"))?;
                if in_table {
                    cur_cell.push_str(&text);
                } else if in_p {
                    cur_runs.push(Run {
                        text: text.to_string(),
                        bold: cur_bold,
                        italic: cur_italic,
                        color: cur_color.clone(),
                        size_pt: cur_size_pt,
                    });
                }
            }
            Ok(Event::Eof) => break,
            Err(e) => return Err(format!("document.xml 解析失败：{e}")),
            _ => {}
        }
        buf.clear();
    }
    flush_para!();
    Ok(doc)
}

/// docx -> html 的高层入口。
pub fn docx_to_html_bytes(path: &Path) -> Result<Vec<u8>, String> {
    let doc = parse_docx(path)?;
    Ok(render_html(&doc).into_bytes())
}

/// Document -> Markdown（反向：docx -> md）。
pub fn doc_to_markdown(doc: &Document) -> String {
    let mut out = String::new();
    for b in &doc.blocks {
        match b {
            Block::Heading { level, runs } => {
                let t: String = runs.iter().map(|r| r.text.as_str()).collect();
                out.push_str(&format!("{} {}\n\n", "#".repeat(*level as usize), t.trim()));
            }
            Block::ListItem { level, ordered, runs } => {
                let indent = "  ".repeat(*level as usize);
                let bullet = if *ordered { "1." } else { "-" };
                let t: String = runs.iter().map(|r| r.text.as_str()).collect();
                out.push_str(&format!("{indent}{bullet} {t}\n"));
            }
            Block::Paragraph { runs } => {
                for r in runs {
                    let s = if r.italic { format!("*{}*", r.text) } else { r.text.clone() };
                    if r.bold {
                        out.push_str(&format!("**{s}**"));
                    } else {
                        out.push_str(&s);
                    }
                }
                out.push_str("\n\n");
            }
            Block::Table { rows } => {
                for (i, row) in rows.iter().enumerate() {
                    out.push_str("| ");
                    out.push_str(&row.iter().map(|c| c.text.as_str()).collect::<Vec<_>>().join(" | "));
                    out.push_str(" |\n");
                    if i == 0 {
                        out.push_str("|");
                        for _ in row { out.push_str(" --- |"); }
                        out.push_str("\n");
                    }
                }
                out.push('\n');
            }
            Block::Image { alt, .. } => {
                out.push_str(&format!("![{}](image)\n\n", alt));
            }
        }
    }
    out
}

/// DOCX 文件 -> Markdown 字符串。
pub fn docx_to_markdown(path: &Path) -> Result<String, String> {
    let doc = parse_docx(path)?;
    Ok(doc_to_markdown(&doc))
}