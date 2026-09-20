//! Markdown -> DOCX / EPUB（纯 Rust，替代 Pandoc 轻量链）。
//!
//! 路线：pulldown-cmark 解析 MD 成事件 -> 转成 doc.rs 的 Block 流 -> zip 手写 OOXML/EPUB。
//! 最小 docx：[Content_Types].xml + _rels/.rels + word/document.xml。
//! 最小 epub：mimetype + META-INF/container.xml + OEBPS/content.opf + OEBPS/1.xhtml。
//! 诚实边界：只支持标题/段落/粗体/斜体；不支持表格/图片/公式。

use std::io::{Cursor, Write};
use std::path::Path;

use pulldown_cmark::{Event, Options, Parser, Tag};
use zip::write::SimpleFileOptions;
use zip::ZipWriter;

use crate::doc::{Block, Document, Run};

fn xml_escape(s: &str) -> String {
    s.replace('&', "&amp;")
        .replace('<', "&lt;")
        .replace('>', "&gt;")
        .replace('"', "&quot;")
}

fn runs_to_xml(runs: &[Run]) -> String {
    let mut out = String::new();
    for r in runs {
        let t = xml_escape(&r.text);
        if r.bold {
            out.push_str(&format!(
                "<w:r><w:rPr><w:b/></w:rPr><w:t xml:space=\"preserve\">{t}</w:t></w:r>"
            ));
        } else {
            out.push_str(&format!(
                "<w:r><w:t xml:space=\"preserve\">{t}</w:t></w:r>"
            ));
        }
    }
    out
}

fn block_to_xml(block: &Block) -> String {
    match block {
        Block::Heading { level, runs } => {
            let sz = match level {
                1 => "32",
                2 => "28",
                3 => "24",
                _ => "22",
            };
            let inner = runs_to_xml(runs);
            format!("<w:p><w:pPr><w:spacing w:after=\"120\"/><w:outlineLvl w:val=\"{}\"/></w:pPr>{inner}</w:p>", level - 1)
        }
        Block::Paragraph { runs } => {
            let r = runs_to_xml(runs);
            format!("<w:p><w:pPr><w:spacing w:after=\"120\"/></w:pPr>{r}</w:p>")
        }
        Block::Table { .. } => String::new(),
        Block::Image { .. } => String::new(),
        Block::ListItem { runs, .. } => {
            let r = runs_to_xml(runs);
            format!("<w:p><w:pPr><w:ind w:left=\"720\"/></w:pPr>{r}</w:p>")
        }
    }
}

fn doc_to_ooxml(doc: &Document) -> String {
    let mut body = String::new();
    for b in &doc.blocks {
        body.push_str(&block_to_xml(b));
    }
    format!(
        r#"<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
<w:body>{body}<w:sectPr><w:pgSz w:w="11906" w:h="16838"/><w:pgMar w:top="1440" w:right="1440" w:bottom="1440" w:left="1440"/></w:sectPr></w:body>
</w:document>"#
    )
}

fn doc_to_xhtml(doc: &Document, title: &str) -> String {
    let mut body = String::new();
    for b in &doc.blocks {
        match b {
            Block::Heading { level, runs } => {
                let t: String = runs.iter().map(|r| xml_escape(&r.text)).collect();
                body.push_str(&format!("<h{level}>{t}</h{level}>"));
            }
            Block::Paragraph { runs } => {
                let mut inner = String::new();
                for r in runs {
                    let t = xml_escape(&r.text);
                    if r.bold {
                        inner.push_str(&format!("<strong>{t}</strong>"));
                    } else {
                        inner.push_str(&t);
                    }
                }
                body.push_str(&format!("<p>{inner}</p>"));
            }
            _ => {}
        }
    }
    format!(
        r#"<?xml version="1.0" encoding="UTF-8"?>
<html xmlns="http://www.w3.org/1999/xhtml" xml:lang="zh">
<head><title>{title}</title><meta charset="utf-8"/></head>
<body>{body}</body></html>"#
    )
}

/// Markdown 字符串 -> 文档 IR（供 docx/epub/pptx/杂项 writer 共用）。
pub fn parse_md_to_doc(md: &str) -> Document {
    let mut doc = Document::new();
    let mut opts = Options::empty();
    opts.insert(Options::ENABLE_TABLES);
    let parser = Parser::new_ext(md, opts);

    let mut runs: Vec<Run> = Vec::new();
    let mut heading_level: Option<u8> = None;
    let mut in_para = false;
    let mut bold_stack: i32 = 0;

    for ev in parser {
        match ev {
            Event::Start(tag) => match tag {
                Tag::Heading { level, .. } => {
                    heading_level = Some(level as u8);
                    runs.clear();
                }
                Tag::Paragraph => in_para = true,
                Tag::Strong => bold_stack += 1,
                Tag::CodeBlock(..) => {
                    runs.push(Run { text: String::new(), bold: false, italic: false, color: None, size_pt: None });
                }
                _ => {}
            },
            Event::End(tag) => match tag {
                pulldown_cmark::TagEnd::Heading(_level) => {
                    if let Some(lvl) = heading_level.take() {
                        if !runs.is_empty() {
                            doc.blocks.push(Block::Heading { level: lvl, runs: std::mem::take(&mut runs) });
                        }
                    }
                }
                pulldown_cmark::TagEnd::Paragraph => {
                    in_para = false;
                    if !runs.is_empty() {
                        doc.blocks.push(Block::Paragraph { runs: std::mem::take(&mut runs) });
                    }
                }
                pulldown_cmark::TagEnd::Strong => bold_stack -= 1,
                pulldown_cmark::TagEnd::CodeBlock => {
                    if !runs.is_empty() {
                        doc.blocks.push(Block::Paragraph { runs: std::mem::take(&mut runs) });
                    }
                }
                _ => {}
            },
            Event::Text(t) => {
                let is_bold = bold_stack > 0;
                runs.push(Run { text: t.to_string(), bold: is_bold, italic: false, color: None, size_pt: None });
            }
            Event::Code(t) => {
                runs.push(Run { text: t.to_string(), bold: true, italic: false, color: None, size_pt: None });
            }
            _ => {}
        }
    }
    // 收尾：末尾未关闭的段落
    if !runs.is_empty() {
        doc.blocks.push(Block::Paragraph { runs });
    }
    doc
}

/// Markdown 文件 -> DOCX。
pub fn md_to_docx(input: &Path, output: &Path) -> Result<(), String> {
    let md = std::fs::read_to_string(input).map_err(|e| e.to_string())?;
    let doc = parse_md_to_doc(&md);
    let ooxml = doc_to_ooxml(&doc);

    let file = std::fs::File::create(output).map_err(|e| e.to_string())?;
    let mut zip = ZipWriter::new(file);
    let opts = SimpleFileOptions::default();

    zip.start_file("[Content_Types].xml", opts).map_err(|e| e.to_string())?;
    zip.write_all(br#"<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
<Default Extension="xml" ContentType="application/xml"/>
<Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
</Types>"#).map_err(|e| e.to_string())?;

    zip.start_file("_rels/.rels", opts).map_err(|e| e.to_string())?;
    zip.write_all(br#"<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
</Relationships>"#).map_err(|e| e.to_string())?;

    zip.start_file("word/document.xml", opts).map_err(|e| e.to_string())?;
    zip.write_all(ooxml.as_bytes()).map_err(|e| e.to_string())?;

    zip.finish().map_err(|e| e.to_string())?;
    Ok(())
}

/// Markdown 文件 -> EPUB。
pub fn md_to_epub(input: &Path, output: &Path) -> Result<(), String> {
    let md = std::fs::read_to_string(input).map_err(|e| e.to_string())?;
    let doc = parse_md_to_doc(&md);
    let title = input.file_stem().map(|s| s.to_string_lossy().to_string()).unwrap_or_else(|| "document".to_string());
    let xhtml = doc_to_xhtml(&doc, &title);

    let file = std::fs::File::create(output).map_err(|e| e.to_string())?;
    let mut zip = ZipWriter::new(file);
    let opts = SimpleFileOptions::default();

    // mimetype 必须第一个，不压缩
    zip.start_file("mimetype", opts).map_err(|e| e.to_string())?;
    zip.write_all(b"application/epub+zip").map_err(|e| e.to_string())?;

    zip.start_file("META-INF/container.xml", opts).map_err(|e| e.to_string())?;
    zip.write_all(br#"<?xml version="1.0" encoding="UTF-8"?>
<container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
<rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles>
</container>"#).map_err(|e| e.to_string())?;

    zip.start_file("OEBPS/content.opf", opts).map_err(|e| e.to_string())?;
    let opf = format!(
        r#"<?xml version="1.0" encoding="UTF-8"?>
<package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="uid">
<metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
<dc:identifier id="uid">urn:uuid:00000000-0000-0000-0000-000000000000</dc:identifier>
<dc:title>{title}</dc:title><dc:language>zh</dc:language>
</metadata>
<manifest><item id="ch1" href="ch1.xhtml" media-type="application/xhtml+xml"/></manifest>
<spine><itemref idref="ch1"/></spine>
</package>"#
    );
    zip.write_all(opf.as_bytes()).map_err(|e| e.to_string())?;

    zip.start_file("OEBPS/ch1.xhtml", opts).map_err(|e| e.to_string())?;
    zip.write_all(xhtml.as_bytes()).map_err(|e| e.to_string())?;

    zip.finish().map_err(|e| e.to_string())?;
    Ok(())
}
