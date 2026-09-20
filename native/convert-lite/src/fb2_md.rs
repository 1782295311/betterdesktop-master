//! FB2（FictionBook 2.x XML）-> Markdown（零外部二进制）。
//!
//! 解析：body 下递归 section：title -> 标题（# 按深度）、p -> 段落、
//!   行内 strong/emphasis（粗斜体）、v（诗歌行）逐行输出、
//!   subtitle 作小节标题。description 的书名/作者忽略（诚实边界：
//!   正文优先，元数据不进入 md 正文）。
//!
//! 诚实边界：不解析 poem/stanza 结构（v 行原样段落化）、table/cite/epigraph
//!   简单段落化；命名空间前缀按 local_name 匹配；图片/链接不提取。

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

/// 收集行内文本直到 end_name 同名 End；strong/emphasis 转 md 标记。
fn collect_inline(reader: &mut Reader<&[u8]>, buf: &mut Vec<u8>, out: &mut String, end_name: &[u8]) {
    loop {
        match reader.read_event_into(buf) {
            Ok(Event::Start(e)) => {
                let n = e.local_name().as_ref().to_vec();
                match n.as_slice() {
                    b"strong" => {
                        let mut inner = String::new();
                        collect_inline(reader, buf, &mut inner, b"strong");
                        out.push_str(&format!("**{}**", inner.trim()));
                    }
                    b"emphasis" => {
                        let mut inner = String::new();
                        collect_inline(reader, buf, &mut inner, b"emphasis");
                        out.push_str(&format!("*{}*", inner.trim()));
                    }
                    b"a" => {
                        let href = attr(&e, b"href");
                        let mut inner = String::new();
                        collect_inline(reader, buf, &mut inner, b"a");
                        let t = inner.trim();
                        if let Some(h) = href {
                            if t.is_empty() {
                                out.push_str(&format!("[{h}]"));
                            } else {
                                out.push_str(&format!("[{t}]({h})"));
                            }
                        } else {
                            out.push_str(t);
                        }
                    }
                    _ => {
                        let mut inner = String::new();
                        collect_inline(reader, buf, &mut inner, &n);
                        out.push_str(&inner);
                    }
                }
            }
            Ok(Event::Text(t)) => {
                let s = t.unescape().unwrap_or_default();
                out.push_str(&s);
            }
            Ok(Event::End(e)) => {
                if e.local_name().as_ref() == end_name {
                    return;
                }
            }
            Ok(Event::Eof) => return,
            _ => {}
        }
        buf.clear();
    }
}

/// 递归解析 section（depth 决定标题级别）。
fn parse_section(reader: &mut Reader<&[u8]>, buf: &mut Vec<u8>, out: &mut String, depth: usize) {
    let mut title_done = false;
    loop {
        match reader.read_event_into(buf) {
            Ok(Event::Start(e)) => {
                let n = e.local_name().as_ref().to_vec();
                match n.as_slice() {
                    b"title" => {
                        let mut t = String::new();
                        collect_inline(reader, buf, &mut t, b"title");
                        let t = t.trim();
                        if !t.is_empty() {
                            let level = (depth + 1).min(6);
                            out.push_str(&format!("{} {}\n\n", "#".repeat(level), t));
                        }
                        title_done = true;
                    }
                    b"p" => {
                        let mut t = String::new();
                        collect_inline(reader, buf, &mut t, b"p");
                        let t = t.trim();
                        if !t.is_empty() {
                            out.push_str(t);
                            out.push_str("\n\n");
                        }
                    }
                    b"subtitle" => {
                        let mut t = String::new();
                        collect_inline(reader, buf, &mut t, b"subtitle");
                        let t = t.trim();
                        if !t.is_empty() {
                            let level = (depth + 2).min(6);
                            out.push_str(&format!("{} {}\n\n", "#".repeat(level), t));
                        }
                    }
                    b"section" => parse_section(reader, buf, out, depth + 1),
                    b"v" => {
                        // 诗歌行：逐行输出（保持换行）
                        let mut t = String::new();
                        collect_inline(reader, buf, &mut t, b"v");
                        for l in t.lines() {
                            let l = l.trim();
                            if !l.is_empty() {
                                out.push_str(l);
                                out.push_str("  \n");
                            }
                        }
                        out.push('\n');
                    }
                    _ => skip_element(reader, buf),
                }
            }
            Ok(Event::End(e)) => {
                if e.local_name().as_ref() == b"section" {
                    let _ = title_done;
                    return;
                }
            }
            Ok(Event::Eof) => return,
            _ => {}
        }
        buf.clear();
    }
}

/// FB2 -> Markdown 正文。
pub fn fb2_to_md(path: &Path) -> Result<String, String> {
    let bytes = std::fs::read(path).map_err(|e| format!("读取失败：{e}"))?;
    let mut reader = Reader::from_reader(&bytes[..]);
    reader.config_mut().trim_text(true);
    let mut buf = Vec::new();
    let mut out = String::new();
    let mut in_body = false;
    loop {
        match reader.read_event_into(&mut buf) {
            Ok(Event::Start(e)) => {
                let n = e.local_name().as_ref().to_vec();
                if n == b"body" {
                    in_body = true;
                } else if in_body && n == b"section" {
                    parse_section(&mut reader, &mut buf, &mut out, 0);
                } else if in_body {
                    skip_element(&mut reader, &mut buf);
                }
            }
            Ok(Event::Eof) => break,
            _ => {}
        }
        buf.clear();
    }
    let out = out.trim_end().to_string();
    if out.is_empty() {
        return Err("未提取到正文（非 FB2 或无 body/section）".into());
    }
    Ok(out)
}
