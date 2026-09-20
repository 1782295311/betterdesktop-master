//! Markdown -> Org-mode（替代 Pandoc 的 org writer 实用子集）。
//!
//! pulldown-cmark 事件流 → org 语法：标题(* 族)/粗体(*text*)/斜体(/text/)/
//! 行内代码(=text=)/删除线(+text+)/代码块(#+BEGIN_SRC)/列表(- 与 1.)/表格(org 管道表)/
//! 链接([[url][text]])/图片([[url]])/引用(#+BEGIN_QUOTE)/水平线(-----)。
//!
//! 诚实边界：不生成 #+TITLE 元数据；表格对齐列（|<r>| 等）不输出；
//!   图片只输出 URL 链接形式。

use std::path::Path;

use pulldown_cmark::{CodeBlockKind, Event, HeadingLevel, Options, Parser, Tag, TagEnd};

/// Markdown 文件 -> Org-mode。
pub fn md_to_org(input: &Path) -> Result<String, String> {
    let md = std::fs::read_to_string(input)
        .map_err(|e| format!("读取 {} 失败：{e}", input.display()))?;
    let parser = Parser::new_ext(&md, Options::ENABLE_TABLES | Options::ENABLE_STRIKETHROUGH);
    let mut out = String::new();
    let mut list_depth = 0usize;
    let mut ordered_stack: Vec<bool> = Vec::new();
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
                out.push_str(&"*".repeat(n));
                out.push(' ');
            }
            Event::End(TagEnd::Heading(_)) => out.push_str("\n\n"),
            Event::Start(Tag::Paragraph) => {}
            Event::End(TagEnd::Paragraph) => out.push('\n'),
            Event::Text(t) => {
                if in_table {
                    cur_cell.push_str(&t);
                } else if in_image {
                    // alt 文本丢弃
                } else if in_link {
                    link_text.push_str(&t);
                } else if in_item {
                    item_buf.push_str(&t);
                } else {
                    out.push_str(&t);
                }
            }
            Event::SoftBreak => {
                if in_item {
                    item_buf.push(' ');
                } else {
                    out.push(' ');
                }
            }
            Event::HardBreak => out.push_str("\\\\\n"),
            Event::Start(Tag::Strong) => {
                if in_link {
                    link_text.push('*');
                } else if in_item {
                    item_buf.push('*');
                } else {
                    out.push('*');
                }
            }
            Event::End(TagEnd::Strong) => {
                if in_link {
                    link_text.push('*');
                } else if in_item {
                    item_buf.push('*');
                } else {
                    out.push('*');
                }
            }
            Event::Start(Tag::Emphasis) => {
                if in_link {
                    link_text.push('/');
                } else if in_item {
                    item_buf.push('/');
                } else {
                    out.push('/');
                }
            }
            Event::End(TagEnd::Emphasis) => {
                if in_link {
                    link_text.push('/');
                } else if in_item {
                    item_buf.push('/');
                } else {
                    out.push('/');
                }
            }
            Event::Start(Tag::Strikethrough) => {
                if in_link {
                    link_text.push('+');
                } else if in_item {
                    item_buf.push('+');
                } else {
                    out.push('+');
                }
            }
            Event::End(TagEnd::Strikethrough) => {
                if in_link {
                    link_text.push('+');
                } else if in_item {
                    item_buf.push('+');
                } else {
                    out.push('+');
                }
            }
            Event::Code(t) => {
                let s = format!("={t}=");
                if in_link {
                    link_text.push_str(&s);
                } else if in_item {
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
                    out.push_str("#+BEGIN_SRC\n");
                } else {
                    out.push_str(&format!("#+BEGIN_SRC {lang}\n"));
                }
            }
            Event::End(TagEnd::CodeBlock) => out.push_str("#+END_SRC\n\n"),
            Event::Start(Tag::Link { dest_url, .. }) => {
                link_stack.push(dest_url.to_string());
                link_text.clear();
                in_link = true;
            }
            Event::End(TagEnd::Link) => {
                in_link = false;
                let url = link_stack.pop().unwrap_or_default();
                let text = link_text.trim().to_string();
                let s = if text.is_empty() || text == url {
                    format!("[[{url}]]")
                } else {
                    format!("[[{url}][{text}]]")
                };
                if in_item {
                    item_buf.push_str(&s);
                } else {
                    out.push_str(&s);
                }
            }
            Event::Start(Tag::Image { dest_url, .. }) => {
                in_image = true;
                let s = format!("[[{dest_url}]]");
                if in_item {
                    item_buf.push_str(&s);
                } else {
                    out.push_str(&s);
                }
            }
            Event::End(TagEnd::Image) => in_image = false,
            Event::Start(Tag::BlockQuote(_)) => out.push_str("#+BEGIN_QUOTE\n"),
            Event::End(TagEnd::BlockQuote(_)) => out.push_str("#+END_QUOTE\n\n"),
            Event::Rule => out.push_str("-----\n\n"),
            Event::Start(Tag::List(start)) => {
                flush_item(
                    &mut out,
                    &mut item_buf,
                    list_depth,
                    ordered_stack.last().copied().unwrap_or(false),
                );
                ordered_stack.push(start.is_some());
                list_depth += 1;
            }
            Event::End(TagEnd::List(_)) => {
                list_depth = list_depth.saturating_sub(1);
                ordered_stack.pop();
                out.push('\n');
            }
            Event::Start(Tag::Item) => {
                in_item = true;
                item_buf.clear();
            }
            Event::End(TagEnd::Item) => {
                flush_item(
                    &mut out,
                    &mut item_buf,
                    list_depth,
                    ordered_stack.last().copied().unwrap_or(false),
                );
                in_item = false;
            }
            Event::Start(Tag::Table(_)) => {
                table_buf.clear();
                cur_row.clear();
                in_table = true;
            }
            Event::End(TagEnd::Table) => {
                in_table = false;
                emit_org_table(&mut out, &table_buf);
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

fn flush_item(out: &mut String, item_buf: &mut String, depth: usize, ordered: bool) {
    if item_buf.is_empty() {
        return;
    }
    out.push_str(&"  ".repeat(depth.saturating_sub(1)));
    if ordered {
        out.push_str("1. ");
    } else {
        out.push_str("- ");
    }
    out.push_str(item_buf);
    out.push('\n');
    item_buf.clear();
}

fn emit_org_table(out: &mut String, rows: &[Vec<String>]) {
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
        out.push_str(&format!("| {} |\n", cells.join(" | ")));
        if ri == 0 {
            out.push_str(&format!("|{}|\n", "---+".repeat(cols).trim_end_matches('+')));
        }
    }
    out.push('\n');
}
