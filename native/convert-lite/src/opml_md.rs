//! OPML 2.0 -> Markdown（零外部二进制）。
//!
//! 解析：body 下递归 outline：层级 1-6 -> md 标题（#…######），
//!   带 URL 的 outline -> [text](url)，text 缺失用 title 属性。
//!   头部 head/title 作文档主标题（#）。
//!
//! 诚实边界：_note/_created/_type 等属性忽略；无 URL 的深层 outline
//!   转标题；层级超过 6 降级为 ###### 后接列表项（不编造层级）。

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

fn parse_outline(reader: &mut Reader<&[u8]>, buf: &mut Vec<u8>, out: &mut String, depth: usize) {
    loop {
        match reader.read_event_into(buf) {
            Ok(Event::Start(e)) => {
                let n = e.local_name().as_ref().to_vec();
                if n == b"outline" {
                    emit_outline(&e, out, depth);
                    parse_outline(reader, buf, out, depth + 1);
                } else {
                    // 其他元素：跳过（保持嵌套遍历安全）
                    let mut d = 1usize;
                    loop {
                        match reader.read_event_into(buf) {
                            Ok(Event::Start(_)) => d += 1,
                            Ok(Event::End(_)) => {
                                d -= 1;
                                if d == 0 {
                                    break;
                                }
                            }
                            Ok(Event::Eof) => break,
                            _ => {}
                        }
                        buf.clear();
                    }
                }
            }
            Ok(Event::Empty(e)) => {
                let n = e.local_name().as_ref().to_vec();
                if n == b"outline" {
                    emit_outline(&e, out, depth);
                }
            }
            Ok(Event::End(e)) => {
                if e.local_name().as_ref() == b"outline" {
                    return;
                }
            }
            Ok(Event::Eof) => return,
            _ => {}
        }
        buf.clear();
    }
}

fn emit_outline(e: &BytesStart, out: &mut String, depth: usize) {
    let text = attr(e, b"text")
        .or_else(|| attr(e, b"title"))
        .unwrap_or_default();
    let url = attr(e, b"url").or_else(|| attr(e, b"htmlUrl"));
    let text = text.trim().to_string();
    let mut line = String::new();
    if !text.is_empty() {
        if let Some(u) = url {
            line = format!("[{text}]({u})");
        } else {
            line = text.clone();
        }
    } else if let Some(u) = url {
        line = format!("[{u}]({u})");
    }
    if !line.is_empty() {
        if depth < 6 {
            out.push_str(&format!("{} {}\n\n", "#".repeat(depth + 1), line));
        } else {
            out.push_str(&format!("- {line}\n"));
        }
    }
}

/// OPML -> Markdown。
pub fn opml_to_md(path: &Path) -> Result<String, String> {
    let bytes = std::fs::read(path).map_err(|e| format!("读取失败：{e}"))?;
    let mut reader = Reader::from_reader(&bytes[..]);
    reader.config_mut().trim_text(true);
    let mut buf = Vec::new();
    let mut out = String::new();
    let mut doc_title: Option<String> = None;
    let mut in_body = false;
    loop {
        match reader.read_event_into(&mut buf) {
            Ok(Event::Start(e)) => {
                let n = e.local_name().as_ref().to_vec();
                if n == b"title" {
                    // 只在 head 内取一次
                    if doc_title.is_none() && !in_body {
                        let mut t = String::new();
                        loop {
                            match reader.read_event_into(&mut buf) {
                                Ok(Event::Text(tx)) => {
                                    let s = tx.unescape().unwrap_or_default();
                                    t.push_str(&s);
                                }
                                Ok(Event::End(en)) => {
                                    if en.local_name().as_ref() == b"title" {
                                        break;
                                    }
                                }
                                Ok(Event::Eof) => break,
                                _ => {}
                            }
                            buf.clear();
                        }
                        let t = t.trim().to_string();
                        if !t.is_empty() {
                            doc_title = Some(t);
                        }
                    } else {
                        skip_to_end(&mut reader, &mut buf, b"title");
                    }
                } else if n == b"body" {
                    in_body = true;
                } else if in_body && n == b"outline" {
                    emit_outline(&e, &mut out, 0);
                    parse_outline(&mut reader, &mut buf, &mut out, 1);
                }
            }
            Ok(Event::Empty(e)) => {
                let n = e.local_name().as_ref().to_vec();
                if in_body && n == b"outline" {
                    emit_outline(&e, &mut out, 0);
                }
            }
            Ok(Event::Eof) => break,
            _ => {}
        }
        buf.clear();
    }
    let mut result = String::new();
    if let Some(t) = doc_title {
        result.push_str(&format!("# {t}\n\n"));
    }
    result.push_str(out.trim_end());
    let result = result.trim_end().to_string();
    if result.is_empty() {
        return Err("未提取到大纲（非 OPML 或无 body/outline）".into());
    }
    Ok(result)
}

fn skip_to_end(reader: &mut Reader<&[u8]>, buf: &mut Vec<u8>, end_name: &[u8]) {
    loop {
        match reader.read_event_into(buf) {
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
