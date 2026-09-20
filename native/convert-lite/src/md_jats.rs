//! Markdown -> JATS（替代 Pandoc 的 jats writer 实用子集，JATS 1.3）。
//!
//! pulldown-cmark 事件流 → JATS XML：<article> 根 + <front><article-meta> 元数据、
//!   <body> 下 <sec> 嵌套标题（按 H 级别自动开合）、<p>、<bold>/<italic>/
//!   <monospace>、<preformat>、<list list-type>、<table-wrap> 表格、
//!   <ext-link>、<fig> 图片、<disp-quote>。
//!
//! 诚实边界：article-title 固定 "Untitled"（md 无元数据标题）；删除线降级为
//!   <italic>（JATS 无删除线标记）；图片仅 href 引用不打包；水平线忽略。

use std::path::Path;

use pulldown_cmark::{Event, HeadingLevel, Options, Parser, Tag, TagEnd};

fn xml_esc(s: &str) -> String {
    s.replace('&', "&amp;")
        .replace('<', "&lt;")
        .replace('>', "&gt;")
        .replace('"', "&quot;")
}

/// Markdown 文件 -> JATS XML。
pub fn md_to_jats(input: &Path) -> Result<String, String> {
    let md = std::fs::read_to_string(input)
        .map_err(|e| format!("读取 {} 失败：{e}", input.display()))?;
    let parser = Parser::new_ext(&md, Options::ENABLE_TABLES | Options::ENABLE_STRIKETHROUGH);
    let mut body = String::new();
    let mut sec_stack: Vec<usize> = Vec::new(); // 已打开 sec 的标题级别
    let mut list_stack: Vec<bool> = Vec::new(); // ordered
    let mut link_stack: Vec<String> = Vec::new();
    let mut link_text = String::new();
    let mut in_link = false;
    let mut in_item = false;
    let mut in_image = false;
    let mut item_buf = String::new();
    let mut in_code = false;
    let mut code_buf = String::new();
    let mut table_buf: Vec<Vec<String>> = Vec::new();
    let mut cur_row: Vec<String> = Vec::new();
    let mut cur_cell = String::new();
    let mut in_table = false;

    macro_rules! close_secs {
        ($n:expr) => {
            while let Some(&lv) = sec_stack.last() {
                if lv >= $n {
                    body.push_str("</sec>\n");
                    sec_stack.pop();
                } else {
                    break;
                }
            }
        };
    }
    macro_rules! open_secs {
        ($n:expr) => {
            while sec_stack.len() < $n {
                body.push_str("<sec>\n");
                sec_stack.push(sec_stack.len() + 1);
            }
        };
    }

    for ev in parser {
        match ev {
            Event::Start(Tag::Heading { level, .. }) => {
                let n = match level {
                    HeadingLevel::H1 => 1usize,
                    HeadingLevel::H2 => 2,
                    HeadingLevel::H3 => 3,
                    HeadingLevel::H4 => 4,
                    HeadingLevel::H5 => 5,
                    HeadingLevel::H6 => 6,
                };
                close_secs!(n);
                open_secs!(n);
                body.push_str("<title>");
            }
            Event::End(TagEnd::Heading(_)) => body.push_str("</title>\n"),
            Event::Start(Tag::Paragraph) => body.push_str("<p>"),
            Event::End(TagEnd::Paragraph) => body.push_str("</p>\n"),
            Event::Text(t) => {
                if in_table {
                    cur_cell.push_str(&xml_esc(&t));
                } else if in_image {
                    // alt 文本丢弃
                } else if in_link {
                    link_text.push_str(&t);
                } else if in_code {
                    code_buf.push_str(&t);
                } else if in_item {
                    item_buf.push_str(&xml_esc(&t));
                } else {
                    body.push_str(&xml_esc(&t));
                }
            }
            Event::SoftBreak => {
                if in_item {
                    item_buf.push(' ');
                } else {
                    body.push(' ');
                }
            }
            Event::HardBreak => body.push('\n'),
            Event::Start(Tag::Strong) => {
                let s = "<bold>";
                if in_item { item_buf.push_str(s) } else { body.push_str(s) }
            }
            Event::End(TagEnd::Strong) => {
                let s = "</bold>";
                if in_item { item_buf.push_str(s) } else { body.push_str(s) }
            }
            Event::Start(Tag::Emphasis) => {
                let s = "<italic>";
                if in_item { item_buf.push_str(s) } else { body.push_str(s) }
            }
            Event::End(TagEnd::Emphasis) => {
                let s = "</italic>";
                if in_item { item_buf.push_str(s) } else { body.push_str(s) }
            }
            Event::Start(Tag::Strikethrough) => {
                let s = "<italic>";
                if in_item { item_buf.push_str(s) } else { body.push_str(s) }
            }
            Event::End(TagEnd::Strikethrough) => {
                let s = "</italic>";
                if in_item { item_buf.push_str(s) } else { body.push_str(s) }
            }
            Event::Code(t) => {
                let s = format!("<monospace>{}</monospace>", xml_esc(&t));
                if in_item { item_buf.push_str(&s) } else { body.push_str(&s) }
            }
            Event::Start(Tag::CodeBlock(_)) => {
                in_code = true;
                code_buf.clear();
            }
            Event::End(TagEnd::CodeBlock) => {
                in_code = false;
                body.push_str(&format!(
                    "<preformat>{}</preformat>\n",
                    xml_esc(code_buf.trim_end_matches('\n'))
                ));
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
                let s = format!(
                    "<ext-link ext-link-type=\"uri\" xlink:href=\"{}\">{}</ext-link>",
                    xml_esc(&url),
                    xml_esc(&text)
                );
                if in_item { item_buf.push_str(&s) } else { body.push_str(&s) }
            }
            Event::Start(Tag::Image { dest_url, .. }) => {
                in_image = true;
                let s = format!(
                    "<fig><graphic xlink:href=\"{}\"/></fig>",
                    xml_esc(&dest_url)
                );
                if in_item { item_buf.push_str(&s) } else { body.push_str(&s) }
            }
            Event::End(TagEnd::Image) => in_image = false,
            Event::Start(Tag::BlockQuote(_)) => body.push_str("<disp-quote>\n"),
            Event::End(TagEnd::BlockQuote(_)) => body.push_str("</disp-quote>\n"),
            Event::Rule => {}
            Event::Start(Tag::List(start)) => {
                flush_item(&mut body, &mut item_buf);
                let ord = start.is_some();
                list_stack.push(ord);
                if ord {
                    body.push_str("<list list-type=\"order\">\n");
                } else {
                    body.push_str("<list list-type=\"bullet\">\n");
                }
            }
            Event::End(TagEnd::List(_)) => {
                let ord = list_stack.pop().unwrap_or(false);
                if ord {
                    body.push_str("</list>\n");
                } else {
                    body.push_str("</list>\n");
                }
            }
            Event::Start(Tag::Item) => {
                body.push_str("<list-item>\n");
                in_item = true;
                item_buf.clear();
            }
            Event::End(TagEnd::Item) => {
                flush_item(&mut body, &mut item_buf);
                body.push_str("</list-item>\n");
                in_item = false;
            }
            Event::Start(Tag::Table(_)) => {
                table_buf.clear();
                cur_row.clear();
                in_table = true;
            }
            Event::End(TagEnd::Table) => {
                in_table = false;
                emit_jats_table(&mut body, &table_buf);
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
    while !sec_stack.is_empty() {
        body.push_str("</sec>\n");
        sec_stack.pop();
    }
    if body.trim().is_empty() {
        return Err("没有可转换的 Markdown 内容".to_string());
    }
    let xml = format!(
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n\
         <article xmlns:xlink=\"http://www.w3.org/1999/xlink\" dtd-version=\"1.3\"\n\
         article-type=\"research-article\">\n\
         <front><article-meta><title-group><article-title>Untitled</article-title>\
         </title-group></article-meta></front>\n\
         <body>\n{body}</body>\n</article>\n"
    );
    Ok(xml)
}

fn flush_item(out: &mut String, item_buf: &mut String) {
    if item_buf.is_empty() {
        return;
    }
    out.push_str("<p>");
    out.push_str(item_buf);
    out.push_str("</p>\n");
    item_buf.clear();
}

fn emit_jats_table(out: &mut String, rows: &[Vec<String>]) {
    if rows.is_empty() {
        return;
    }
    let cols = rows.iter().map(|r| r.len()).max().unwrap_or(0);
    if cols == 0 {
        return;
    }
    out.push_str("<table-wrap><table>\n");
    if let Some(head) = rows.first() {
        out.push_str("<thead><tr>\n");
        for c in 0..cols {
            out.push_str(&format!("<th>{}</th>", head.get(c).cloned().unwrap_or_default()));
        }
        out.push_str("\n</tr></thead>\n");
    }
    out.push_str("<tbody>\n");
    for row in rows.iter().skip(1) {
        out.push_str("<tr>");
        for c in 0..cols {
            out.push_str(&format!("<td>{}</td>", row.get(c).cloned().unwrap_or_default()));
        }
        out.push_str("</tr>\n");
    }
    out.push_str("</tbody>\n</table></table-wrap>\n");
}
