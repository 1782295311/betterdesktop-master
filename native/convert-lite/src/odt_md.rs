//! ODT (OpenDocument Text) -> Markdown（替代 Pandoc 的 odt 输入 / LibreOffice 轻量路径）。
//!
//! ODT 本质是 zip + content.xml。解析 office:text 下的块级结构：
//!   text:h（标题，outline-level） / text:p（段落） / text:list（列表，可嵌套） /
//!   table:table（表格）/ text:span（行内样式，查 automatic-styles 的 bold/italic）。
//!
//! 诚实边界：不处理图片/页眉页脚/脚注/分栏；样式只识别粗体/斜体两种；
//!   text:s 空格按 c 属性；命名空间前缀按标准 office/text/table/style/fo。

use std::collections::HashMap;
use std::io::Read;
use std::path::Path;

use quick_xml::events::{BytesStart, Event};
use quick_xml::Reader;

use zip::ZipArchive;

type Style = (bool, bool); // (bold, italic)

/// 预扫描 office:automatic-styles，建 style-name -> (bold, italic)。
fn scan_styles(xml: &str) -> HashMap<String, Style> {
    let mut map = HashMap::new();
    let mut pos = 0usize;
    while let Some(i) = xml[pos..].find("<style:style style:name=\"") {
        let seg_start = pos + i;
        let name_start = seg_start + "<style:style style:name=\"".len();
        let after_name = &xml[name_start..];
        let name_end = after_name.find('"').map(|j| name_start + j).unwrap_or(xml.len());
        let name = &xml[name_start..name_end];
        // 该 style 元素段：到 </style:style> 或下一个 <style:style 或文件尾
        let seg_rest = &xml[name_end..];
        let seg_end = seg_rest
            .find("</style:style>")
            .map(|j| name_end + j)
            .unwrap_or(xml.len());
        let seg = &xml[name_end..seg_end];
        let bold = seg.contains("fo:font-weight=\"bold\"") || seg.contains("font-weight=\"bold\"");
        let italic = seg.contains("fo:font-style=\"italic\"") || seg.contains("font-style=\"italic\"");
        if bold || italic {
            map.insert(name.to_string(), (bold, italic));
        }
        pos = seg_end;
    }
    map
}

fn attr(e: &BytesStart, want: &[u8]) -> Option<String> {
    for a in e.attributes().flatten() {
        if a.key.local_name().as_ref() == want {
            return Some(String::from_utf8_lossy(&a.value).into_owned());
        }
    }
    None
}

/// 收集元素内文本（含 span 样式），直到与 end_name 同名的 End。
fn collect_text(
    reader: &mut Reader<&[u8]>,
    buf: &mut Vec<u8>,
    ctx: &HashMap<String, Style>,
    style: Style,
    end_name: &[u8],
) -> String {
    let mut out = String::new();
    loop {
        match reader.read_event_into(buf) {
            Ok(Event::Start(e)) => {
                match e.local_name().as_ref() {
                    b"span" => {
                        let sname = attr(&e, b"style-name");
                        let sb = sname.and_then(|n| ctx.get(&n).copied()).unwrap_or((false, false));
                        let inner = collect_text(reader, buf, ctx, sb, b"span");
                        let (bold, italic) = style;
                        let mut t = inner;
                        if bold {
                            t = format!("**{t}**");
                        }
                        if italic {
                            t = format!("*{t}*");
                        }
                        out.push_str(&t);
                    }
                    b"s" => {
                        let n = attr(&e, b"c").and_then(|c| c.parse::<usize>().ok()).unwrap_or(1);
                        out.push_str(&" ".repeat(n));
                    }
                    b"tab" => out.push('\t'),
                    b"line-break" | b"soft-page-break" => out.push('\n'),
                    _ => skip_element(reader, buf),
                }
            }
            Ok(Event::Text(t)) => {
                if let Ok(s) = t.unescape() {
                    out.push_str(&s);
                }
            }
            Ok(Event::End(e)) => {
                let n = e.local_name().as_ref().to_vec();
                if n == end_name {
                    break;
                }
            }
            Ok(Event::Eof) => break,
            _ => {}
        }
        buf.clear();
    }
    let (bold, italic) = style;
    let mut t = out;
    if bold {
        t = format!("**{t}**");
    }
    if italic {
        t = format!("*{t}*");
    }
    t
}

fn skip_element(reader: &mut Reader<&[u8]>, buf: &mut Vec<u8>) {
    let mut depth = 1usize;
    while depth > 0 {
        match reader.read_event_into(buf) {
            Ok(Event::Start(_)) => depth += 1,
            Ok(Event::End(_)) => depth -= 1,
            Ok(Event::Eof) => break,
            _ => {}
        }
        buf.clear();
    }
}

/// 处理块级内容（office:text 下），list_level 为当前列表嵌套深度（0=非列表）。
fn blocks(
    reader: &mut Reader<&[u8]>,
    buf: &mut Vec<u8>,
    ctx: &HashMap<String, Style>,
    out: &mut String,
    list_level: usize,
) {
    loop {
        match reader.read_event_into(buf) {
            Ok(Event::Start(e)) => {
                match e.local_name().as_ref() {
                    b"h" => {
                        let level = attr(&e, b"outline-level")
                            .and_then(|s| s.parse::<usize>().ok())
                            .unwrap_or(1)
                            .clamp(1, 6);
                        let text = collect_text(reader, buf, ctx, (false, false), b"h");
                        out.push_str(&format!("{} {}\n\n", "#".repeat(level), text.trim()));
                    }
                    b"p" => {
                        let text = collect_text(reader, buf, ctx, (false, false), b"p");
                        let t = text.trim();
                        if !t.is_empty() {
                            if list_level > 0 {
                                out.push_str(&format!("{}- {}\n", "    ".repeat(list_level - 1), t));
                            } else {
                                out.push_str(&format!("{t}\n\n"));
                            }
                        }
                    }
                    b"list" => {
                        blocks(reader, buf, ctx, out, list_level + 1);
                    }
                    b"list-item" => {
                        blocks(reader, buf, ctx, out, list_level.max(1));
                    }
                    b"table" => {
                        let mut rows: Vec<Vec<String>> = Vec::new();
                        parse_table(reader, buf, ctx, &mut rows);
                        if !rows.is_empty() {
                            let max_cols = rows.iter().map(|r| r.len()).max().unwrap_or(0);
                            for (ri, row) in rows.iter().enumerate() {
                                let cells: Vec<String> = (0..max_cols)
                                    .map(|c| row.get(c).cloned().unwrap_or_default())
                                    .collect();
                                out.push_str(&format!("| {} |\n", cells.join(" | ")));
                                if ri == 0 {
                                    out.push_str(&format!("|{}\n", " --- |".repeat(max_cols)));
                                }
                            }
                            out.push('\n');
                        }
                    }
                    _ => skip_element(reader, buf),
                }
            }
            Ok(Event::End(e)) => {
                let n = e.local_name().as_ref().to_vec();
                if n == b"text" || n == b"list" || n == b"list-item" || n == b"table" {
                    break;
                }
            }
            Ok(Event::Eof) => break,
            _ => {}
        }
        buf.clear();
    }
}

fn parse_table(
    reader: &mut Reader<&[u8]>,
    buf: &mut Vec<u8>,
    ctx: &HashMap<String, Style>,
    rows: &mut Vec<Vec<String>>,
) {
    loop {
        match reader.read_event_into(buf) {
            Ok(Event::Start(e)) => match e.local_name().as_ref() {
                b"table-row" => {
                    let mut cells: Vec<String> = Vec::new();
                    parse_table_row(reader, buf, ctx, &mut cells);
                    if !cells.is_empty() {
                        rows.push(cells);
                    }
                }
                _ => skip_element(reader, buf),
            },
            Ok(Event::End(e)) => {
                if e.local_name().as_ref() == b"table" {
                    break;
                }
            }
            Ok(Event::Eof) => break,
            _ => {}
        }
        buf.clear();
    }
}

fn parse_table_row(
    reader: &mut Reader<&[u8]>,
    buf: &mut Vec<u8>,
    ctx: &HashMap<String, Style>,
    cells: &mut Vec<String>,
) {
    loop {
        match reader.read_event_into(buf) {
            Ok(Event::Start(e)) => match e.local_name().as_ref() {
                b"table-cell" | b"covered-table-cell" => {
                    let text = cell_text(reader, buf, ctx);
                    cells.push(text);
                }
                _ => skip_element(reader, buf),
            },
            Ok(Event::End(e)) => {
                if e.local_name().as_ref() == b"table-row" {
                    break;
                }
            }
            Ok(Event::Eof) => break,
            _ => {}
        }
        buf.clear();
    }
}

fn cell_text(reader: &mut Reader<&[u8]>, buf: &mut Vec<u8>, ctx: &HashMap<String, Style>) -> String {
    let mut out = String::new();
    loop {
        match reader.read_event_into(buf) {
            Ok(Event::Start(e)) => match e.local_name().as_ref() {
                b"p" => {
                    let t = collect_text(reader, buf, ctx, (false, false), b"p").trim().to_string();
                    if !out.is_empty() && !t.is_empty() {
                        out.push(' ');
                    }
                    out.push_str(&t);
                }
                _ => skip_element(reader, buf),
            },
            Ok(Event::End(e)) => {
                let n = e.local_name().as_ref().to_vec();
                if n == b"table-cell" || n == b"covered-table-cell" {
                    break;
                }
            }
            Ok(Event::Eof) => break,
            _ => {}
        }
        buf.clear();
    }
    out
}

/// ODT 文件 -> Markdown。
pub fn odt_to_md(input: &Path) -> Result<String, String> {
    let file = std::fs::File::open(input).map_err(|e| format!("读取 {} 失败：{e}", input.display()))?;
    let mut zip = ZipArchive::new(file).map_err(|e| format!("不是有效 zip/odt：{e}"))?;
    let mut content = String::new();
    zip.by_name("content.xml")
        .map_err(|_| "ODT 内缺 content.xml".to_string())?
        .read_to_string(&mut content)
        .map_err(|e| e.to_string())?;

    let styles = scan_styles(&content);
    let mut reader = Reader::from_str(&content);
    reader.config_mut().trim_text(true);
    let mut buf = Vec::new();

    // 定位到 office:text 的 Start
    let mut found = false;
    loop {
        match reader.read_event_into(&mut buf) {
            Ok(Event::Start(e)) => {
                if e.local_name().as_ref() == b"text" {
                    found = true;
                    break;
                }
            }
            Ok(Event::Eof) => break,
            _ => {}
        }
        buf.clear();
    }
    if !found {
        return Err("content.xml 里找不到 office:text 正文".to_string());
    }

    let mut out = String::new();
    blocks(&mut reader, &mut buf, &styles, &mut out, 0);

    if out.trim().is_empty() {
        Err("ODT 没有可提取的文本内容".to_string())
    } else {
        Ok(out.trim().to_string() + "\n")
    }
}
