//! DocBook XML -> Markdown（替代 Pandoc 的 docbook 输入，DocBook 4/5 通用子集）。
//!
//! 解析块级：book/article/chapter/section(含 sect1-5)/appendix 的 title 标题、
//!   para 段落、itemizedlist/orderedlist 列表、table/informaltable 表格、
//!   programlisting/screen 代码、blockquote 引用。
//! 行内：emphasis(role=bold→粗体)/literal(代码)/link|ulink(链接)/subscript|superscript。
//!
//! 诚实边界：不解析 xref 引用解析/索引/脚注；公式元素 inlineequation/equation
//!   保留原文；不处理 xinclude/entity 外部解析。

use std::io::Read;
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

/// 收集行内内容直到与 end_name 同名 End。返回 (文本, 链接前缀)。
/// 链接前缀处理：link/ulink 先把 href 存进 link_href，文本输出为 [text](url)。
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
                    b"emphasis" => {
                        let role = attr(&e, b"role");
                        let mut inner = String::new();
                        collect_inline(reader, buf, &mut inner, b"emphasis");
                        if role.as_deref() == Some("bold") {
                            out.push_str(&format!("**{}**", inner));
                        } else {
                            out.push_str(&format!("*{inner}*"));
                        }
                    }
                    b"literal" => {
                        let mut inner = String::new();
                        collect_inline(reader, buf, &mut inner, b"literal");
                        out.push_str(&format!("`{inner}`"));
                    }
                    b"link" | b"ulink" => {
                        let href = attr(&e, b"href")
                            .or_else(|| attr(&e, b"url"))
                            .unwrap_or_default();
                        let mut inner = String::new();
                        collect_inline(reader, buf, &mut inner, &n);
                        out.push_str(&format!("[{}]({href})", inner.trim()));
                    }
                    b"subscript" => {
                        let mut inner = String::new();
                        collect_inline(reader, buf, &mut inner, b"subscript");
                        out.push_str(&format!("<sub>{inner}</sub>"));
                    }
                    b"superscript" => {
                        let mut inner = String::new();
                        collect_inline(reader, buf, &mut inner, b"superscript");
                        out.push_str(&format!("<sup>{inner}</sup>"));
                    }
                    b"inlineequation" | b"equation" | b"informalequation" => {
                        let mut inner = String::new();
                        collect_inline(reader, buf, &mut inner, &n);
                        out.push_str(&format!("`{inner}`"));
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

/// 段落级收集（para/entry/blockquote 等），到 end_name 结束。
fn collect_para(reader: &mut Reader<&[u8]>, buf: &mut Vec<u8>, end_name: &[u8]) -> String {
    let mut out = String::new();
    collect_inline(reader, buf, &mut out, end_name);
    out
}

fn parse_table(
    reader: &mut Reader<&[u8]>,
    buf: &mut Vec<u8>,
    rows: &mut Vec<Vec<String>>,
) {
    loop {
        match reader.read_event_into(buf) {
            Ok(Event::Start(e)) => match e.local_name().as_ref() {
                b"tgroup" | b"thead" | b"tbody" | b"table" => {}
                b"row" | b"tr" => {
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
                if en == b"table" || en == b"informaltable" {
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
                    b"entry" | b"th" | b"td" => {
                        let t = collect_para(reader, buf, &en);
                        cells.push(t.trim().to_string());
                    }
                    _ => skip_element(reader, buf),
                }
            },
            Ok(Event::End(e)) => {
                if e.local_name().as_ref() == b"row" || e.local_name().as_ref() == b"tr" {
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

/// 块级处理。container_level 表示当前容器（chapter/section…）的标题层级。
fn blocks(
    reader: &mut Reader<&[u8]>,
    buf: &mut Vec<u8>,
    out: &mut String,
    container_level: usize,
    ordered: bool,
) {
    loop {
        match reader.read_event_into(buf) {
            Ok(Event::Start(e)) => {
                let n = e.local_name().as_ref().to_vec();
                match n.as_slice() {
                    b"book" | b"article" | b"chapter" | b"appendix" | b"part" | b"preface"
                    | b"reference" | b"glossary" | b"bibliography" | b"colophon" => {
                        blocks(reader, buf, out, container_level.max(1), false);
                    }
                    b"section" | b"sect1" | b"sect2" | b"sect3" | b"sect4" | b"sect5"
                    | b"simplesect" => {
                        blocks(reader, buf, out, container_level + 1, false);
                    }
                    b"title" => {
                        let t = collect_para(reader, buf, b"title");
                        let t = t.trim();
                        if !t.is_empty() {
                            let lvl = container_level.clamp(1, 6);
                            out.push_str(&format!("{} {}\n\n", "#".repeat(lvl), t));
                        }
                    }
                    b"para" | b"simpara" | b"formalpara" => {
                        let t = collect_para(reader, buf, &n);
                        let t = t.trim();
                        if !t.is_empty() {
                            out.push_str(&format!("{t}\n\n"));
                        }
                    }
                    b"itemizedlist" | b"orderedlist" => {
                        let ord = n == b"orderedlist";
                        blocks(reader, buf, out, container_level, ord);
                    }
                    b"listitem" => {
                        let t = collect_para(reader, buf, b"listitem");
                        let t = t.trim();
                        if !t.is_empty() {
                            let bullet = if ordered { "1." } else { "-" };
                            out.push_str(&format!("{bullet} {t}\n"));
                        }
                    }
                    b"programlisting" | b"screen" | b"literallayout" => {
                        let mut code = String::new();
                        loop {
                            match reader.read_event_into(buf) {
                                Ok(Event::Text(t)) => {
                                    if let Ok(s) = t.unescape() {
                                        code.push_str(&s);
                                    }
                                }
                                Ok(Event::End(e)) => {
                                    if e.local_name().as_ref() == b"programlisting"
                                        || e.local_name().as_ref() == b"screen"
                                        || e.local_name().as_ref() == b"literallayout"
                                    {
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
                    b"blockquote" => {
                        let mut q = String::new();
                        blocks(reader, buf, &mut q, container_level, false);
                        for line in q.lines() {
                            let l = line.trim_end();
                            if !l.is_empty() {
                                out.push_str(&format!("> {l}\n"));
                            }
                        }
                        out.push('\n');
                    }
                    b"table" | b"informaltable" => {
                        let mut rows: Vec<Vec<String>> = Vec::new();
                        parse_table(reader, buf, &mut rows);
                        emit_table(out, &rows);
                    }
                    b"mediaobject" | b"figure" | b"inlinemediaobject" => {
                        // 图片：取 fileref 属性
                        let mut img = String::new();
                        loop {
                            match reader.read_event_into(buf) {
                                Ok(Event::Start(e)) => {
                                    match e.local_name().as_ref() {
                                        b"imagedata" => {
                                            if let Some(u) = attr(&e, b"fileref") {
                                                img = u;
                                            }
                                            skip_element(reader, buf);
                                        }
                                        _ => skip_element(reader, buf),
                                    }
                                }
                                Ok(Event::End(e)) => {
                                    let n = e.local_name().as_ref().to_vec();
                                    if n == b"mediaobject" || n == b"figure"
                                        || n == b"inlinemediaobject"
                                    {
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
                if n == b"book" || n == b"article" || n == b"chapter" || n == b"section"
                    || n == b"sect1" || n == b"sect2" || n == b"sect3" || n == b"sect4"
                    || n == b"sect5" || n == b"simplesect" || n == b"appendix" || n == b"part"
                    || n == b"preface" || n == b"reference" || n == b"glossary"
                    || n == b"bibliography" || n == b"colophon" || n == b"itemizedlist"
                    || n == b"orderedlist" || n == b"blockquote"
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

/// DocBook XML 文件 -> Markdown。
pub fn docbook_to_md(input: &Path) -> Result<String, String> {
    let xml = std::fs::read_to_string(input)
        .map_err(|e| format!("读取 {} 失败：{e}", input.display()))?;
    let mut reader = Reader::from_str(&xml);
    reader.config_mut().trim_text(true);
    let mut buf = Vec::new();

    // 定位到根元素（跳过 XML 声明/DOCTYPE）
    let mut found = false;
    loop {
        match reader.read_event_into(&mut buf) {
            Ok(Event::Start(e)) => {
                let n = e.local_name().as_ref().to_vec();
                if n == b"book" || n == b"article" || n == b"chapter" {
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
        return Err("不是 DocBook 文档（缺 book/article 根元素）".to_string());
    }

    let mut out = String::new();
    blocks(&mut reader, &mut buf, &mut out, 1, false);

    if out.trim().is_empty() {
        Err("DocBook 没有可提取的文本内容".to_string())
    } else {
        Ok(out.trim().to_string() + "\n")
    }
}
