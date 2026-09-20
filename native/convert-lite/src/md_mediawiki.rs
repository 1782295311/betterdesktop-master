//! Markdown -> MediaWiki（替代 Pandoc 的 mediawiki writer 实用子集）。
//!
//! pulldown-cmark 事件流 → wiki 语法：标题(= 族)/粗斜体/行内代码(<code>)/代码块
//! (<syntaxhighlight>)/列表(* # 嵌套)/表格({| ! |})/链接([url text] 与 [[text]])/
//! 图片([[File:url]])/引用(<blockquote>)/水平线(----)。
//!
//! 诚实边界：不生成模板/分类/脚注；内部链接无法区分目标（统一 [[text]]）；
//!   表格单元格内不做复杂行内嵌套处理。

use std::path::Path;

use pulldown_cmark::{CodeBlockKind, Event, HeadingLevel, Options, Parser, Tag, TagEnd};

/// Markdown 文件 -> MediaWiki。
pub fn md_to_mediawiki(input: &Path) -> Result<String, String> {
    let md = std::fs::read_to_string(input)
        .map_err(|e| format!("读取 {} 失败：{e}", input.display()))?;
    let parser = Parser::new_ext(&md, Options::ENABLE_TABLES | Options::ENABLE_STRIKETHROUGH);
    let mut out = String::new();
    let mut list_stack: Vec<&str> = Vec::new(); // "*" 或 "#"
    let mut heading_stack: Vec<usize> = Vec::new();
    let mut link_stack: Vec<String> = Vec::new();
    let mut link_text = String::new();
    let mut in_link = false;
    let mut in_item = false;
    let mut in_image = false;
    let mut item_buf = String::new();
    let mut table_buf: Vec<Vec<String>> = Vec::new();
    let mut cur_row: Vec<String> = Vec::new();
    let mut cur_cell = String::new();
    let mut in_table = false;

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
                heading_stack.push(n);
                out.push_str(&"=".repeat(n));
                out.push(' ');
            }
            Event::End(TagEnd::Heading(_)) => {
                let n = heading_stack.pop().unwrap_or(1);
                out.push(' ');
                out.push_str(&"=".repeat(n));
                out.push_str("\n\n");
            }
            Event::Start(Tag::Paragraph) => {}
            Event::End(TagEnd::Paragraph) => out.push('\n'),
            Event::Text(t) => {
                if in_table {
                    cur_cell.push_str(&t);
                } else if in_image {
                    // 图片 alt 文本丢弃
                } else if in_link {
                    link_text.push_str(&t);
                } else if in_item {
                    item_buf.push_str(&t);
                } else {
                    out.push_str(&t);
                }
            }
            Event::SoftBreak => {
                if in_link {
                    link_text.push(' ');
                } else if in_item {
                    item_buf.push(' ');
                } else {
                    out.push(' ');
                }
            }
            Event::HardBreak => out.push_str("<br>\n"),
            Event::Start(Tag::Strong) => {
                if in_link {
                    link_text.push_str("'''");
                } else if in_item {
                    item_buf.push_str("'''");
                } else {
                    out.push_str("'''");
                }
            }
            Event::End(TagEnd::Strong) => {
                if in_link {
                    link_text.push_str("'''");
                } else if in_item {
                    item_buf.push_str("'''");
                } else {
                    out.push_str("'''");
                }
            }
            Event::Start(Tag::Emphasis) => {
                if in_link {
                    link_text.push_str("''");
                } else if in_item {
                    item_buf.push_str("''");
                } else {
                    out.push_str("''");
                }
            }
            Event::End(TagEnd::Emphasis) => {
                if in_link {
                    link_text.push_str("''");
                } else if in_item {
                    item_buf.push_str("''");
                } else {
                    out.push_str("''");
                }
            }
            Event::Start(Tag::Strikethrough) => {
                if in_item {
                    item_buf.push_str("<s>");
                } else {
                    out.push_str("<s>");
                }
            }
            Event::End(TagEnd::Strikethrough) => {
                if in_item {
                    item_buf.push_str("</s>");
                } else {
                    out.push_str("</s>");
                }
            }
            Event::Code(t) => {
                let s = format!("<code>{t}</code>");
                if in_item {
                    item_buf.push_str(&s);
                } else {
                    out.push_str(&s);
                }
            }
            Event::Start(Tag::CodeBlock(kind)) => {
                let lang = match kind {
                    CodeBlockKind::Fenced(l) => l.to_string(),
                    CodeBlockKind::Indented => String::new(),
                };
                if lang.is_empty() {
                    out.push_str("<syntaxhighlight>\n");
                } else {
                    out.push_str(&format!("<syntaxhighlight lang=\"{lang}\">\n"));
                }
            }
            Event::End(TagEnd::CodeBlock) => out.push_str("</syntaxhighlight>\n\n"),
            Event::Start(Tag::Link { dest_url, .. }) => {
                link_stack.push(dest_url.to_string());
                link_text.clear();
                in_link = true;
            }
            Event::End(TagEnd::Link) => {
                in_link = false;
                let url = link_stack.pop().unwrap_or_default();
                let text = link_text.trim().to_string();
                let s = if url.starts_with("http://") || url.starts_with("https://") {
                    if text.is_empty() {
                        format!("[{url}]")
                    } else {
                        format!("[{url} {text}]")
                    }
                } else if text.is_empty() || text == url {
                    format!("[[{text}]]")
                } else {
                    format!("[[{url}|{text}]]")
                };
                if in_item {
                    item_buf.push_str(&s);
                } else {
                    out.push_str(&s);
                }
            }
            Event::Start(Tag::Image { dest_url, .. }) => {
                in_image = true;
                let s = format!("[[File:{dest_url}]]");
                if in_item {
                    item_buf.push_str(&s);
                } else {
                    out.push_str(&s);
                }
            }
            Event::End(TagEnd::Image) => in_image = false,
            Event::Start(Tag::BlockQuote(_)) => out.push_str("<blockquote>\n"),
            Event::End(TagEnd::BlockQuote(_)) => out.push_str("\n</blockquote>\n\n"),
            Event::Rule => out.push_str("----\n\n"),
            Event::Start(Tag::List(start)) => {
                flush_item(&mut out, &mut item_buf, list_stack.last().copied().unwrap_or("*"), list_stack.len());
                let bullet = if start.is_some() { "#" } else { "*" };
                list_stack.push(bullet);
            }
            Event::End(TagEnd::List(_)) => {
                list_stack.pop();
                out.push('\n');
            }
            Event::Start(Tag::Item) => {
                in_item = true;
                item_buf.clear();
            }
            Event::End(TagEnd::Item) => {
                flush_item(&mut out, &mut item_buf, list_stack.last().copied().unwrap_or("*"), list_stack.len());
                in_item = false;
            }
            Event::Start(Tag::Table(_)) => {
                table_buf.clear();
                cur_row.clear();
                in_table = true;
            }
            Event::End(TagEnd::Table) => {
                in_table = false;
                emit_wiki_table(&mut out, &table_buf);
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
    if out.trim().is_empty() {
        Err("没有可转换的 Markdown 内容".to_string())
    } else {
        Ok(out.trim().to_string() + "\n")
    }
}

fn flush_item(out: &mut String, item_buf: &mut String, bullet: &str, depth: usize) {
    if item_buf.is_empty() {
        return;
    }
    for _ in 0..depth {
        out.push_str(bullet);
    }
    out.push(' ');
    out.push_str(item_buf);
    out.push('\n');
    item_buf.clear();
}

fn emit_wiki_table(out: &mut String, rows: &[Vec<String>]) {
    if rows.is_empty() {
        return;
    }
    let cols = rows.iter().map(|r| r.len()).max().unwrap_or(0);
    if cols == 0 {
        return;
    }
    out.push_str("{|\n");
    for (ri, row) in rows.iter().enumerate() {
        let cells: Vec<String> = (0..cols)
            .map(|c| row.get(c).cloned().unwrap_or_default())
            .collect();
        if ri == 0 {
            out.push_str(&format!("! {}\n", cells.join(" !! ")));
        } else {
            out.push_str(&format!("|-\n| {}\n", cells.join(" || ")));
        }
    }
    out.push_str("|}\n\n");
}
