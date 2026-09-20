//! JATS XML -> Markdown（替代 Pandoc 的 jats 输入，Journal Article Tag Suite 1.2/1.3 通用子集）。
//!
//! 解析：front 的 article-title 文档标题、abstract 摘要、body 下的 sec（嵌套标题）、
//!   p 段落、list(list-type=bullet/order) 列表、table-wrap 表格、
//!   preformat 代码、disp-quote 引用、fig 图片。
//! 行内：bold / italic / monospace / ext-link(链接) / sub / sup / inline-formula。
//!
//! 诚实边界：不解析 ref 文献列表/aff 作者机构/table 复杂合并（colspan/rowspan 忽略）；
//!   公式保留原文；命名空间前缀统一按 local_name 匹配。

use std::path::Path;

use quick_xml::events::{BytesStart, Event};
use quick_xml::Reader;

fn attr(e: &BytesStart, want: &[u8]) -> Option<String> {
    for a in e.attributes().flatten() {
        if a.key.local_name().as_ref() == want {
            return Some(String::from_utf8_lossy(&a.value).into_owned());
        }
    }
    None
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

/// 收集行内内容直到与 end_name 同名 End。
fn collect_inline(
    reader: &mut Reader<&[u8]>,
    buf: &mut Vec<u8>,
    out: &mut String,
    end_name: &[u8],
) {
    loop {
        match reader.read_event_into(buf) {
            Ok(Event::Start(e)) => {
                let n = e.local_name().as_ref().to_vec();
                match n.as_slice() {
                    b"bold" => {
                        let mut inner = String::new();
                        collect_inline(reader, buf, &mut inner, b"bold");
                        out.push_str(&format!("**{}**", inner.trim()));
                    }
                    b"italic" => {
                        let mut inner = String::new();
                        collect_inline(reader, buf, &mut inner, b"italic");
                        out.push_str(&format!("*{}*", inner.trim()));
                    }
                    b"monospace" => {
                        let mut inner = String::new();
                        collect_inline(reader, buf, &mut inner, b"monospace");
                        out.push_str(&format!("`{}`", inner.trim()));
                    }
                    b"ext-link" => {
                        let href = attr(&e, b"href").unwrap_or_default();
                        let mut inner = String::new();
                        collect_inline(reader, buf, &mut inner, b"ext-link");
                        out.push_str(&format!("[{}]({href})", inner.trim()));
                    }
                    b"sub" => {
                        let mut inner = String::new();
                        collect_inline(reader, buf, &mut inner, b"sub");
                        out.push_str(&format!("<sub>{}</sub>", inner.trim()));
                    }
                    b"sup" => {
                        let mut inner = String::new();
                        collect_inline(reader, buf, &mut inner, b"sup");
                        out.push_str(&format!("<sup>{}</sup>", inner.trim()));
                    }
                    b"inline-formula" => {
                        let mut inner = String::new();
                        collect_inline(reader, buf, &mut inner, b"inline-formula");
                        out.push_str(&format!("`{}`", inner.trim()));
                    }
                    // 未知元素：递归取其文本
                    _ => {
                        collect_inline(reader, buf, out, &n);
                    }
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
}

fn collect_para(reader: &mut Reader<&[u8]>, buf: &mut Vec<u8>, end_name: &[u8]) -> String {
    let mut out = String::new();
    collect_inline(reader, buf, &mut out, end_name);
    out
}

fn parse_table(reader: &mut Reader<&[u8]>, buf: &mut Vec<u8>, rows: &mut Vec<Vec<String>>) {
    loop {
        match reader.read_event_into(buf) {
            Ok(Event::Start(e)) => match e.local_name().as_ref() {
                b"table" | b"thead" | b"tbody" | b"colgroup" | b"tgroup" => {}
                b"tr" => {
                    let mut cells: Vec<String> = Vec::new();
                    parse_table_row(reader, buf, &mut cells);
                    if !cells.is_empty() {
                        rows.push(cells);
                    }
                }
                _ => skip_element(reader, buf),
            },
            Ok(Event::End(e)) => {
                let en = e.local_name().as_ref().to_vec();
                if en == b"table" || en == b"table-wrap" {
                    break;
                }
            }
            Ok(Event::Eof) => break,
            _ => {}
        }
        buf.clear();
    }
}

fn parse_table_row(reader: &mut Reader<&[u8]>, buf: &mut Vec<u8>, cells: &mut Vec<String>) {
    loop {
        match reader.read_event_into(buf) {
            Ok(Event::Start(e)) => {
                let en = e.local_name().as_ref().to_vec();
                match en.as_slice() {
                    b"th" | b"td" => {
                        let t = collect_para(reader, buf, &en);
                        cells.push(t.trim().to_string());
                    }
                    _ => skip_element(reader, buf),
                }
            }
            Ok(Event::End(e)) => {
                if e.local_name().as_ref() == b"tr" {
                    break;
                }
            }
            Ok(Event::Eof) => break,
            _ => {}
        }
        buf.clear();
    }
}

fn emit_table(out: &mut String, rows: &[Vec<String>]) {
    if rows.is_empty() {
        return;
    }
    let max_cols = rows.iter().map(|r| r.len()).max().unwrap_or(0);
    if max_cols == 0 {
        return;
    }
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

/// 块级处理。level 为当前容器标题层级（body 第一层 sec = H2，即 level=2）。
fn blocks(
    reader: &mut Reader<&[u8]>,
    buf: &mut Vec<u8>,
    out: &mut String,
    level: usize,
    ordered: bool,
) {
    loop {
        match reader.read_event_into(buf) {
            Ok(Event::Start(e)) => {
                match e.local_name().as_ref() {
                    b"body" => blocks(reader, buf, out, 1, false),
                    b"sec" => blocks(reader, buf, out, level + 1, false),
                    b"title" => {
                        let t = collect_para(reader, buf, b"title");
                        let t = t.trim();
                        if !t.is_empty() {
                            let lvl = level.clamp(1, 6);
                            out.push_str(&format!("{} {}\n\n", "#".repeat(lvl), t));
                        }
                    }
                    b"p" => {
                        let t = collect_para(reader, buf, b"p");
                        let t = t.trim();
                        if !t.is_empty() {
                            out.push_str(&format!("{t}\n\n"));
                        }
                    }
                    b"list" => {
                        let lt = attr(&e, b"list-type").unwrap_or_default();
                        let ord = lt == "order";
                        blocks(reader, buf, out, level, ord);
                    }
                    b"list-item" => {
                        let t = collect_para(reader, buf, b"list-item");
                        let t = t.trim();
                        if !t.is_empty() {
                            let bullet = if ordered { "1." } else { "-" };
                            out.push_str(&format!("{bullet} {t}\n"));
                        }
                    }
                    b"preformat" => {
                        let mut code = String::new();
                        loop {
                            match reader.read_event_into(buf) {
                                Ok(Event::Text(t)) => {
                                    if let Ok(s) = t.unescape() {
                                        code.push_str(&s);
                                    }
                                }
                                Ok(Event::End(e)) => {
                                    if e.local_name().as_ref() == b"preformat" {
                                        break;
                                    }
                                }
                                Ok(Event::Eof) => break,
                                _ => {}
                            }
                            buf.clear();
                        }
                        let body = code.trim_end();
                        out.push_str("```\n");
                        out.push_str(body);
                        out.push_str("\n```\n\n");
                    }
                    b"disp-quote" | b"abstract" => {
                        let mut q = String::new();
                        blocks(reader, buf, &mut q, level, false);
                        for line in q.lines() {
                            let l = line.trim_end();
                            if !l.is_empty() {
                                out.push_str(&format!("> {l}\n"));
                            }
                        }
                        out.push('\n');
                    }
                    b"table-wrap" | b"table" => {
                        let mut rows: Vec<Vec<String>> = Vec::new();
                        parse_table(reader, buf, &mut rows);
                        emit_table(out, &rows);
                    }
                    b"fig" => {
                        // 取 fig 内 graphic 的 xlink:href
                        let mut img = String::new();
                        loop {
                            match reader.read_event_into(buf) {
                                Ok(Event::Start(e)) => {
                                    if e.local_name().as_ref() == b"graphic" {
                                        if let Some(u) = attr(&e, b"href") {
                                            img = u;
                                        }
                                        skip_element(reader, buf);
                                    } else {
                                        skip_element(reader, buf);
                                    }
                                }
                                Ok(Event::End(e)) => {
                                    if e.local_name().as_ref() == b"fig" {
                                        break;
                                    }
                                }
                                Ok(Event::Eof) => break,
                                _ => {}
                            }
                            buf.clear();
                        }
                        if !img.is_empty() {
                            out.push_str(&format!("![]({img})\n\n"));
                        }
                    }
                    _ => skip_element(reader, buf),
                }
            }
            Ok(Event::End(e)) => {
                let n = e.local_name().as_ref().to_vec();
                if n == b"body" || n == b"sec" || n == b"list" || n == b"list-item"
                    || n == b"disp-quote" || n == b"abstract"
                {
                    break;
                }
            }
            Ok(Event::Eof) => break,
            _ => {}
        }
        buf.clear();
    }
}

/// JATS XML 文件 -> Markdown。
pub fn jats_to_md(input: &Path) -> Result<String, String> {
    let xml = std::fs::read_to_string(input)
        .map_err(|e| format!("读取 {} 失败：{e}", input.display()))?;
    let mut reader = Reader::from_str(&xml);
    reader.config_mut().trim_text(true);
    let mut buf = Vec::new();
    let mut out = String::new();

    // 流式扫描：front 的 article-title -> 文档标题；body -> 正文
    loop {
        match reader.read_event_into(&mut buf) {
            Ok(Event::Start(e)) => {
                match e.local_name().as_ref() {
                    b"article-title" => {
                        let t = collect_para(&mut reader, &mut buf, b"article-title");
                        let t = t.trim();
                        if !t.is_empty() {
                            out.push_str(&format!("# {t}\n\n"));
                        }
                    }
                    b"abstract" => {
                        let mut q = String::new();
                        blocks(&mut reader, &mut buf, &mut q, 1, false);
                        for line in q.lines() {
                            let l = line.trim_end();
                            if !l.is_empty() {
                                out.push_str(&format!("> {l}\n"));
                            }
                        }
                        out.push('\n');
                    }
                    b"body" => {
                        blocks(&mut reader, &mut buf, &mut out, 1, false);
                    }
                    _ => {}
                }
            }
            Ok(Event::Eof) => break,
            _ => {}
        }
        buf.clear();
    }

    if out.trim().is_empty() {
        Err("JATS 没有可提取的文本内容".to_string())
    } else {
        Ok(out.trim().to_string() + "\n")
    }
}
