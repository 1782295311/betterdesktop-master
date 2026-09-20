//! HTML -> Markdown（ReverseMarkdown，替代 Pandoc 的 html 输入）。

use std::path::Path;
use scraper::{ElementRef, Html, Node};

fn text_of(el: &ElementRef) -> String { el.text().collect::<Vec<_>>().join("") }

fn decode_text(s: &str) -> String {
    s.replace("&amp;", "&").replace("&lt;", "<").replace("&gt;", ">").replace("&quot;", "\"").replace("&#39;", "'").replace("&nbsp;", " ")
}

fn node_to_md(el: ElementRef, list_idx: &mut usize) -> String {
    let name: String = el.value().name().to_string();
    match name.as_str() {
        "h1"|"h2"|"h3"|"h4"|"h5"|"h6" => {
            let level: usize = name[1..].parse().unwrap_or(1);
            let text = decode_text(&text_of(&el));
            format!("{} {}\n\n", "#".repeat(level), text.trim())
        }
        "p" => { let t = inline_to_md(el); if t.trim().is_empty() {String::new()} else {format!("{}\n\n", t.trim())} }
        "strong"|"b" => { let t = inline_to_md(el); format!("**{}**", t.trim()) }
        "em"|"i" => { let t = inline_to_md(el); format!("*{}*", t.trim()) }
        "code" => { let t = text_of(&el); format!("`{}`", t.trim()) }
        "pre" => { let c = text_of(&el); format!("```\n{}\n```\n\n", c.trim()) }
        "br" => "\n".to_string(),
        "a" => {
            let href = el.value().attr("href").unwrap_or("");
            let text = decode_text(&text_of(&el));
            if href.is_empty() { text } else { format!("[{}]({})", text.trim(), href) }
        }
        "img" => format!("![{}]({})", el.value().attr("alt").unwrap_or(""), el.value().attr("src").unwrap_or("")),
        "ul" => {
            let mut out = String::new();
            for c in el.children() {
                if let Some(e) = ElementRef::wrap(c) { if e.value().name()=="li" { out.push_str(&format!("- {}\n", inline_to_md(e).trim())); } }
            }
            out.push('\n'); out
        }
        "ol" => {
            *list_idx = 1;
            let mut out = String::new();
            for c in el.children() {
                if let Some(e) = ElementRef::wrap(c) { if e.value().name()=="li" { out.push_str(&format!("{}. {}\n", *list_idx, inline_to_md(e).trim())); *list_idx+=1; } }
            }
            out.push('\n'); out
        }
        "li" => String::new(),
        "table" => table_to_md(el),
        "thead"|"tbody" => {
            let mut out = String::new();
            for c in el.children() { if let Some(e)=ElementRef::wrap(c) { out.push_str(&node_to_md(e, list_idx)); } }
            out
        }
        "tr" => {
            let mut cells = Vec::new();
            for c in el.children() {
                if let Some(e)=ElementRef::wrap(c) {
                    let n: String = e.value().name().to_string();
                    if n=="td"||n=="th" { cells.push(decode_text(&text_of(&e)).trim().to_string()); }
                }
            }
            format!("| {} |\n", cells.join(" | "))
        }
        "div"|"section"|"article"|"main"|"span"|"body"|"html"|"header"|"footer"|"nav"|"aside"|"figure"|"blockquote" => walk_children(el, list_idx),
        "script"|"style"|"noscript"|"meta"|"link"|"title" => String::new(),
        _ => walk_children(el, list_idx),
    }
}

fn walk_children(el: ElementRef, list_idx: &mut usize) -> String {
    let mut out = String::new();
    for c in el.children() {
        out.push_str(&match c.value() {
            Node::Text(t) => decode_text(&t),
            Node::Element(_) => if let Some(e)=ElementRef::wrap(c) { node_to_md(e, list_idx) } else { String::new() },
            _ => String::new(),
        });
    }
    out
}

fn inline_to_md(el: ElementRef) -> String {
    let mut out = String::new();
    let mut dummy = 1usize;
    for c in el.children() {
        out.push_str(&match c.value() {
            Node::Text(t) => decode_text(&t),
            Node::Element(_) => if let Some(e)=ElementRef::wrap(c) { node_to_md(e, &mut dummy) } else { String::new() },
            _ => String::new(),
        });
    }
    out
}

fn table_to_md(el: ElementRef) -> String {
    let mut rows: Vec<Vec<String>> = Vec::new();
    for tr in el.select(&scraper::Selector::parse("tr").unwrap()) {
        let mut cells = Vec::new();
        for c in tr.children() {
            if let Some(e)=ElementRef::wrap(c) {
                let n: String = e.value().name().to_string();
                if n=="td"||n=="th" { cells.push(decode_text(&text_of(&e)).trim().to_string()); }
            }
        }
        if !cells.is_empty() { rows.push(cells); }
    }
    let mut out = String::new();
    for (i,row) in rows.iter().enumerate() {
        out.push_str(&format!("| {} |\n", row.join(" | ")));
        if i==0 { out.push_str("|"); for _ in row { out.push_str(" --- |"); } out.push_str("\n"); }
    }
    out.push('\n'); out
}

pub fn html_to_md(input: &Path) -> Result<String, String> {
    let html = std::fs::read_to_string(input).map_err(|e| e.to_string())?;
    let doc = Html::parse_document(&html);
    let body = doc.select(&scraper::Selector::parse("body").unwrap()).next();
    let root = body.unwrap_or_else(|| doc.root_element());
    let mut out = String::new();
    let mut list_idx = 1usize;
    for c in root.children() {
        out.push_str(&match c.value() {
            Node::Text(t) => decode_text(&t),
            Node::Element(_) => if let Some(e)=ElementRef::wrap(c) { node_to_md(e, &mut list_idx) } else { String::new() },
            _ => String::new(),
        });
    }
    let out = out.replace("\n\n\n", "\n\n");
    Ok(out.trim().to_string() + "\n")
}

/// HTML 字符串 -> Markdown（内部复用）。
pub fn html_str_to_md(html: &str) -> String {
    let doc = Html::parse_document(html);
    let body = doc.select(&scraper::Selector::parse("body").unwrap()).next();
    let root = body.unwrap_or_else(|| doc.root_element());
    let mut out = String::new();
    let mut list_idx = 1usize;
    for c in root.children() {
        out.push_str(&match c.value() {
            Node::Text(t) => decode_text(&t),
            Node::Element(_) => if let Some(e)=ElementRef::wrap(c) { node_to_md(e, &mut list_idx) } else { String::new() },
            _ => String::new(),
        });
    }
    let out = out.replace("\n\n\n", "\n\n");
    out.trim().to_string() + "\n"
}