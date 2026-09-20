//! Markdown -> RTF（替代 Pandoc 的 rtf writer 实用子集）。
//!
//! pulldown-cmark 事件流 → RTF 控制字：标题(粗体+字号)/粗斜体删除线/行内代码
//! (等宽字体切换)/代码块(等宽+par)/列表(符号前缀)/表格(单元格 \tab 分隔)/
//! 链接(HYPERLINK 域)/图片(仅文件名占位)/引用(缩进段落)。
//!
//! 诚实边界：不生成分页/页眉页脚/脚注域；表格不用 \trowd 网格（用 \tab 分隔，
//!   简单编辑器可读）；图片无法内嵌（输出 [图片:url] 占位）；非 BMP 字符按
//!   UTF-16 代理对转 \uN?。

use std::path::Path;

use pulldown_cmark::{Event, HeadingLevel, Options, Parser, Tag, TagEnd};

/// RTF 转义：\ { } 与 CR/LF；非 ASCII 转 \uN?（UTF-16 code unit）。
fn rtf_esc(s: &str) -> String {
    let mut out = String::new();
    for ch in s.chars() {
        let cp = ch as u32;
        if cp == '\\' as u32 {
            out.push_str("\\\\");
        } else if cp == '{' as u32 {
            out.push_str("\\{");
        } else if cp == '}' as u32 {
            out.push_str("\\}");
        } else if cp < 0x80 {
            if ch == '\n' {
                out.push_str("\\par ");
            } else {
                out.push(ch);
            }
        } else if cp <= 0xFFFF {
            out.push_str(&format!("\\u{}?", cp as i32));
        } else {
            // 代理对
            let v = cp - 0x10000;
            let hi = 0xD800 + (v >> 10);
            let lo = 0xDC00 + (v & 0x3FF);
            out.push_str(&format!("\\u{}?\\u{}?", hi as i32, lo as i32));
        }
    }
    out
}

/// Markdown 文件 -> RTF。
pub fn md_to_rtf(input: &Path) -> Result<String, String> {
    let md = std::fs::read_to_string(input)
        .map_err(|e| format!("读取 {} 失败：{e}", input.display()))?;
    let parser = Parser::new_ext(&md, Options::ENABLE_TABLES | Options::ENABLE_STRIKETHROUGH);
    let mut body = String::new();
    let mut list_stack: Vec<&str> = Vec::new(); // "- " 或 "1. "
    let mut link_stack: Vec<String> = Vec::new();
    let mut link_text = String::new();
    let mut in_link = false;
    let mut in_item = false;
    let mut in_image = false;
    let mut item_buf = String::new();
    let mut in_code_block = false;
    let mut code_buf = String::new();
    let mut table_buf: Vec<Vec<String>> = Vec::new();
    let mut cur_row: Vec<String> = Vec::new();
    let mut cur_cell = String::new();
    let mut in_table = false;

    for ev in parser {
        match ev {
            Event::Start(Tag::Heading { level, .. }) => {
                let fs = match level {
                    HeadingLevel::H1 => 48,
                    HeadingLevel::H2 => 40,
                    HeadingLevel::H3 => 32,
                    HeadingLevel::H4 => 28,
                    HeadingLevel::H5 => 24,
                    HeadingLevel::H6 => 24,
                };
                body.push_str(&format!("\\fs{fs}\\b "));
            }
            Event::End(TagEnd::Heading(_)) => {
                body.push_str("\\b0\\fs24\\par\n");
            }
            Event::Start(Tag::Paragraph) => {}
            Event::End(TagEnd::Paragraph) => body.push_str("\\par\n"),
            Event::Text(t) => {
                if in_code_block {
                    code_buf.push_str(&t);
                } else if in_table {
                    cur_cell.push_str(&rtf_esc(&t));
                } else if in_image {
                    // alt 文本丢弃
                } else if in_link {
                    link_text.push_str(&t);
                } else if in_item {
                    item_buf.push_str(&rtf_esc(&t));
                } else {
                    body.push_str(&rtf_esc(&t));
                }
            }
            Event::SoftBreak => {
                if in_link {
                    link_text.push(' ');
                } else if in_item {
                    item_buf.push(' ');
                } else {
                    body.push(' ');
                }
            }
            Event::HardBreak => body.push_str("\\line\n"),
            Event::Start(Tag::Strong) => {
                let s = "\\b ";
                if in_item { item_buf.push_str(s) } else { body.push_str(s) }
            }
            Event::End(TagEnd::Strong) => {
                let s = "\\b0 ";
                if in_item { item_buf.push_str(s) } else { body.push_str(s) }
            }
            Event::Start(Tag::Emphasis) => {
                let s = "\\i ";
                if in_item { item_buf.push_str(s) } else { body.push_str(s) }
            }
            Event::End(TagEnd::Emphasis) => {
                let s = "\\i0 ";
                if in_item { item_buf.push_str(s) } else { body.push_str(s) }
            }
            Event::Start(Tag::Strikethrough) => {
                let s = "\\strike ";
                if in_item { item_buf.push_str(s) } else { body.push_str(s) }
            }
            Event::End(TagEnd::Strikethrough) => {
                let s = "\\strike0 ";
                if in_item { item_buf.push_str(s) } else { body.push_str(s) }
            }
            Event::Code(t) => {
                let s = format!("{{\\f1 {}\\f0 }}", rtf_esc(&t));
                if in_item { item_buf.push_str(&s) } else { body.push_str(&s) }
            }
            Event::Start(Tag::CodeBlock(_)) => {
                in_code_block = true;
                code_buf.clear();
            }
            Event::End(TagEnd::CodeBlock) => {
                in_code_block = false;
                let text = code_buf.trim_end_matches('\n');
                for line in text.split('\n') {
                    body.push_str(&format!("{{\\f1 {}\\f0\\par}}\n", rtf_esc(line)));
                }
            }
            Event::Start(Tag::Link { dest_url, .. }) => {
                link_stack.push(dest_url.to_string());
                link_text.clear();
                in_link = true;
            }
            Event::End(TagEnd::Link) => {
                in_link = false;
                let url = link_stack.pop().unwrap_or_default();
                let text = link_text.trim().to_string();
                let text = if text.is_empty() { url.clone() } else { text };
                let esc_url = url.replace('\\', "\\\\").replace('"', "\\\"");
                let s = format!(
                    "{{\\field{{\\*\\fldinst HYPERLINK \"{esc_url}\"}}{{\\fldrslt {}}}}}",
                    rtf_esc(&text)
                );
                if in_item { item_buf.push_str(&s) } else { body.push_str(&s) }
            }
            Event::Start(Tag::Image { dest_url, .. }) => {
                in_image = true;
                let s = format!("[图片:{}]", rtf_esc(&dest_url));
                if in_item { item_buf.push_str(&s) } else { body.push_str(&s) }
            }
            Event::End(TagEnd::Image) => in_image = false,
            Event::Start(Tag::BlockQuote(_)) => body.push_str("\\ql\\li720 "),
            Event::End(TagEnd::BlockQuote(_)) => body.push_str("\\par\n"),
            Event::Rule => body.push_str("\\par\\qc{\\ul ------}\\par\n"),
            Event::Start(Tag::List(start)) => {
                flush_item(&mut body, &mut item_buf, list_stack.last().copied());
                let prefix = if start.is_some() { "1. " } else { "- " };
                list_stack.push(prefix);
            }
            Event::End(TagEnd::List(_)) => {
                list_stack.pop();
                body.push_str("\\par\n");
            }
            Event::Start(Tag::Item) => {
                in_item = true;
                item_buf.clear();
            }
            Event::End(TagEnd::Item) => {
                flush_item(&mut body, &mut item_buf, list_stack.last().copied());
                in_item = false;
            }
            Event::Start(Tag::Table(_)) => {
                table_buf.clear();
                cur_row.clear();
                in_table = true;
            }
            Event::End(TagEnd::Table) => {
                in_table = false;
                emit_rtf_table(&mut body, &table_buf);
                table_buf.clear();
            }
            Event::Start(Tag::TableHead) => {}
            Event::End(TagEnd::TableHead) => {
                if !cur_row.is_empty() {
                    table_buf.push(std::mem::take(&mut cur_row));
                }
            }
            Event::Start(Tag::TableRow) => {
                cur_row.clear();
            }
            Event::End(TagEnd::TableRow) => {
                if !cur_row.is_empty() {
                    table_buf.push(std::mem::take(&mut cur_row));
                }
            }
            Event::Start(Tag::TableCell) => cur_cell.clear(),
            Event::End(TagEnd::TableCell) => cur_row.push(std::mem::take(&mut cur_cell)),
            _ => {}
        }
    }
    if body.trim().is_empty() {
        return Err("没有可转换的 Markdown 内容".to_string());
    }
    let mut rtf = String::from(
        "{\\rtf1\\ansi\\ansicpg1252\\deff0{\\fonttbl{\\f0 Times New Roman;}{\\f1 Courier New;}}\\f0\\fs24\n",
    );
    rtf.push_str(&body);
    rtf.push_str("}\n");
    Ok(rtf)
}

fn flush_item(out: &mut String, item_buf: &mut String, prefix: Option<&str>) {
    if item_buf.is_empty() {
        return;
    }
    if let Some(p) = prefix {
        out.push_str(p);
    }
    out.push_str(item_buf);
    out.push_str("\\par\n");
    item_buf.clear();
}

fn emit_rtf_table(out: &mut String, rows: &[Vec<String>]) {
    if rows.is_empty() {
        return;
    }
    let cols = rows.iter().map(|r| r.len()).max().unwrap_or(0);
    if cols == 0 {
        return;
    }
    for (ri, row) in rows.iter().enumerate() {
        let cells: Vec<String> = (0..cols)
            .map(|c| row.get(c).cloned().unwrap_or_default())
            .collect();
        if ri == 0 {
            out.push_str("\\b ");
            out.push_str(&cells.join("\\tab "));
            out.push_str("\\b0\\par\n");
        } else {
            out.push_str(&cells.join("\\tab "));
            out.push_str("\\par\n");
        }
    }
}
