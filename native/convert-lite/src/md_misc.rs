//! Markdown -> 杂项轻量文本格式（Pandoc 收尾：opendocument / plain / man / texi / context）。
//!
//! 全部复用 `md_docx::parse_md_to_doc` 解析出的文档 IR，按各格式语义输出：
//! - opendocument：OpenDocument 文本单 XML（office:document + text:h/text:p）
//! - plain：纯文本（无标记；复用 txt_md::md_to_txt 的轻量剥离）
//! - man：roff（.TH/.SH/.PP/.IP）
//! - texi：Texinfo（@node/@section/@itemize）
//! - context：ConTeXt（\starttext/\section/\startitemize）
//!
//! 诚实边界：粗体/斜体在小格式中多数降级为纯文本（man 保留 .B）；不支持表格/图片。

use std::path::Path;

use crate::doc::{Block, Document};
use crate::md_docx::parse_md_to_doc;

fn read_md(input: &Path) -> Result<Document, String> {
    let md = std::fs::read_to_string(input).map_err(|e| e.to_string())?;
    Ok(parse_md_to_doc(&md))
}

fn run_text(runs: &[crate::doc::Run]) -> String {
    runs.iter().map(|r| r.text.clone()).collect()
}

fn doc_title(doc: &Document) -> String {
    for b in &doc.blocks {
        if let Block::Heading { level: 1, runs } = b {
            return run_text(runs);
        }
    }
    "Document".to_string()
}

// ---------- OpenDocument Text ----------

fn block_to_odt(block: &Block) -> String {
    match block {
        Block::Heading { level, runs } => format!(
            "<text:h text:outline-level=\"{level}\">{}</text:h>",
            xml_esc(&run_text(runs))
        ),
        Block::Paragraph { runs } => format!("<text:p>{}</text:p>", xml_esc(&run_text(runs))),
        Block::Table { rows } => {
            let mut s = String::from("<table:table>");
            for row in rows {
                s.push_str("<table:table-row>");
                for cell in row {
                    s.push_str(&format!(
                        "<table:table-cell><text:p>{}</text:p></table:table-cell>",
                        xml_esc(&cell.text)
                    ));
                }
                s.push_str("</table:table-row>");
            }
            s.push_str("</table:table>");
            s
        }
        Block::Image { alt, .. } => format!("<text:p>[图片：{}]</text:p>", xml_esc(alt)),
        Block::ListItem { runs, .. } => runs.iter().map(|r| r.text.as_str()).collect::<String>() + "\n",
    }
}

fn doc_to_odt(doc: &Document) -> String {
    let mut body = String::new();
    for b in &doc.blocks {
        body.push_str(&block_to_odt(b));
    }
    format!(
        r#"<?xml version="1.0" encoding="UTF-8"?>
<office:document xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0"
 xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0"
 xmlns:table="urn:oasis:names:tc:opendocument:xmlns:table:1.0"
 office:version="1.2">
<office:body><office:text>{body}</office:text></office:body>
</office:document>"#
    )
}

fn xml_esc(s: &str) -> String {
    s.replace('&', "&amp;")
        .replace('<', "&lt;")
        .replace('>', "&gt;")
        .replace('"', "&quot;")
}

/// Markdown -> OpenDocument 文本（单 XML）。
pub fn md_to_opendocument(input: &Path, output: &Path) -> Result<(), String> {
    let doc = read_md(input)?;
    std::fs::write(output, doc_to_odt(&doc).as_bytes()).map_err(|e| e.to_string())
}

// ---------- plain（复用 txt_md::md_to_txt 的轻量剥离） ----------

/// Markdown -> plain（纯文本，无标记；复用 txt_md 的轻量剥离）。
pub fn md_to_plain(input: &Path, output: &Path) -> Result<(), String> {
    let s = crate::txt_md::md_to_txt(input)?;
    std::fs::write(output, s.as_bytes()).map_err(|e| e.to_string())
}

// ---------- man（roff） ----------

fn block_to_man(block: &Block) -> String {
    match block {
        Block::Heading { level, runs } => {
            let t = run_text(runs);
            if *level == 1 {
                format!(".SH {}\n", t.to_uppercase())
            } else {
                format!(".SS {}\n", t)
            }
        }
        Block::Paragraph { runs } => format!(".PP\n{}\n", run_text(runs)),
        Block::Table { rows } => {
            let mut s = String::from(".PP\n");
            for row in rows {
                s.push_str(&format!(".IP \\[bu] 2\n{}\n", row.iter().map(|x| x.text.as_str()).collect::<Vec<_>>().join(" | ")));
            }
            s
        }
        Block::Image { alt, .. } => format!(".PP\n[图片：{alt}]\n"),
        Block::ListItem { runs, .. } => runs.iter().map(|r| r.text.as_str()).collect::<String>() + "\n",
    }
}

/// Markdown -> man（roff 手册页；标题 1 -> .SH，标题 2+ -> .SS）。
pub fn md_to_man(input: &Path, output: &Path) -> Result<(), String> {
    let doc = read_md(input)?;
    let title = doc_title(&doc);
    let mut body = String::new();
    for b in &doc.blocks {
        body.push_str(&block_to_man(b));
    }
    let s = format!(
        ".TH \"{}\" \"1\" \"\" \"\" \"\"\n.SH NAME\n{}\n{}\n",
        title.to_uppercase(),
        title,
        body
    );
    std::fs::write(output, s.as_bytes()).map_err(|e| e.to_string())
}

// ---------- texi（Texinfo） ----------

fn block_to_texi(block: &Block) -> String {
    match block {
        Block::Heading { level, runs } => {
            let t = run_text(runs);
            match level {
                1 => format!("@node {t}\n@section {t}\n"),
                2 => format!("@subsection {t}\n"),
                _ => format!("@subsubsection {t}\n"),
            }
        }
        Block::Paragraph { runs } => format!("{}\n\n", run_text(runs)),
        Block::Table { rows } => {
            let mut s = String::from("@itemize @bullet\n");
            for row in rows {
                s.push_str(&format!("@item {}\n", row.iter().map(|x| x.text.as_str()).collect::<Vec<_>>().join(" | ")));
            }
            s.push_str("@end itemize\n");
            s
        }
        Block::Image { alt, .. } => format!("[图片：{alt}]\n"),
        Block::ListItem { runs, .. } => runs.iter().map(|r| r.text.as_str()).collect::<String>() + "\n",
    }
}

/// Markdown -> Texinfo（@node/@section/@itemize）。
pub fn md_to_texi(input: &Path, output: &Path) -> Result<(), String> {
    let doc = read_md(input)?;
    let mut body = String::new();
    for b in &doc.blocks {
        body.push_str(&block_to_texi(b));
    }
    let s = format!(
        "\\input texinfo\n@settitle {}\n@node Top\n@top {}\n\n{}\n@bye\n",
        doc_title(&doc),
        doc_title(&doc),
        body
    );
    std::fs::write(output, s.as_bytes()).map_err(|e| e.to_string())
}

// ---------- context（ConTeXt） ----------

fn block_to_context(block: &Block) -> String {
    match block {
        Block::Heading { level, runs } => {
            let t = run_text(runs);
            match level {
                1 => format!("\\section{{{t}}}\n"),
                2 => format!("\\subsection{{{t}}}\n"),
                _ => format!("\\subsubsection{{{t}}}\n"),
            }
        }
        Block::Paragraph { runs } => format!("{}\n\n", run_text(runs)),
        Block::Table { rows } => {
            let mut s = String::from("\\startitemize[packed]\n");
            for row in rows {
                s.push_str(&format!("\\item {}\n", row.iter().map(|x| x.text.as_str()).collect::<Vec<_>>().join(" | ")));
            }
            s.push_str("\\stopitemize\n");
            s
        }
        Block::Image { alt, .. } => format!("[图片：{alt}]\n"),
        Block::ListItem { runs, .. } => runs.iter().map(|r| r.text.as_str()).collect::<String>() + "\n",
    }
}

/// Markdown -> ConTeXt（\starttext/\section/\startitemize）。
pub fn md_to_context(input: &Path, output: &Path) -> Result<(), String> {
    let doc = read_md(input)?;
    let mut body = String::new();
    for b in &doc.blocks {
        body.push_str(&block_to_context(b));
    }
    let s = format!("\\starttext\n{}\n\\stoptext\n", body);
    std::fs::write(output, s.as_bytes()).map_err(|e| e.to_string())
}
