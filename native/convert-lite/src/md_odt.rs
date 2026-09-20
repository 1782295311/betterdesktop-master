//! Markdown -> ODT（替代 Pandoc 的 odt writer 实用子集，零外部二进制）。
//!
//! pulldown-cmark 事件流 → content.xml（office:text 块级结构）+ styles.xml
//! （T1 粗体/T2 斜体/T3 粗斜体/T4 等宽行内/T5 等宽段落）+ META-INF/manifest.xml，
//! zip::ZipWriter 打包（mimetype 用 STORED 且第一项，符合 ODF 规范）。
//!
//! 诚实边界：不打包图片资源（图片输出 [图片:url] 占位段落）；不生成页眉页脚/
//! 脚注/分栏；链接用 text:a；表格用 table:table 最小骨架（无合并列）。

use std::fs::File;
use std::io::Write;
use std::path::Path;

use pulldown_cmark::{Event, HeadingLevel, Options, Parser, Tag, TagEnd};
use zip::write::SimpleFileOptions;
use zip::ZipWriter;

fn xml_esc(s: &str) -> String {
    s.replace('&', "&amp;")
        .replace('<', "&lt;")
        .replace('>', "&gt;")
        .replace('"', "&quot;")
}

/// Markdown 文件 -> ODT（zip 字节写盘）。
pub fn md_to_odt(input: &Path, out: &Path) -> Result<(), String> {
    let md = std::fs::read_to_string(input)
        .map_err(|e| format!("读取 {} 失败：{e}", input.display()))?;
    let parser = Parser::new_ext(&md, Options::ENABLE_TABLES | Options::ENABLE_STRIKETHROUGH);
    let mut body = String::new();
    let mut list_stack: Vec<&str> = Vec::new(); // itemize / enumerate
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
    let mut in_quote = false;

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
                body.push_str(&format!("<text:h text:outline-level=\"{n}\">"));
            }
            Event::End(TagEnd::Heading(_)) => body.push_str("</text:h>\n"),
            Event::Start(Tag::Paragraph) => {
                if !in_quote {
                    body.push_str("<text:p>");
                }
            }
            Event::End(TagEnd::Paragraph) => {
                if !in_quote {
                    body.push_str("</text:p>\n");
                }
            }
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
            Event::SoftBreak | Event::HardBreak => {
                if in_code {
                    code_buf.push('\n');
                } else if in_link {
                    // 链接内忽略
                } else if in_item {
                    item_buf.push(' ');
                } else {
                    body.push(' ');
                }
            }
            Event::Start(Tag::Strong) => {
                let s = "<text:span text:style-name=\"T1\">";
                if in_item { item_buf.push_str(s) } else { body.push_str(s) }
            }
            Event::End(TagEnd::Strong) => {
                let s = "</text:span>";
                if in_item { item_buf.push_str(s) } else { body.push_str(s) }
            }
            Event::Start(Tag::Emphasis) => {
                let s = "<text:span text:style-name=\"T2\">";
                if in_item { item_buf.push_str(s) } else { body.push_str(s) }
            }
            Event::End(TagEnd::Emphasis) => {
                let s = "</text:span>";
                if in_item { item_buf.push_str(s) } else { body.push_str(s) }
            }
            Event::Start(Tag::Strikethrough) => {
                let s = "<text:span text:style-name=\"T3\">";
                if in_item { item_buf.push_str(s) } else { body.push_str(s) }
            }
            Event::End(TagEnd::Strikethrough) => {
                let s = "</text:span>";
                if in_item { item_buf.push_str(s) } else { body.push_str(s) }
            }
            Event::Code(t) => {
                let s = format!(
                    "<text:span text:style-name=\"T4\">{}</text:span>",
                    xml_esc(&t)
                );
                if in_item { item_buf.push_str(&s) } else { body.push_str(&s) }
            }
            Event::Start(Tag::CodeBlock(_)) => {
                in_code = true;
                code_buf.clear();
            }
            Event::End(TagEnd::CodeBlock) => {
                in_code = false;
                for line in code_buf.trim_end_matches('\n').split('\n') {
                    body.push_str(&format!(
                        "<text:p text:style-name=\"T5\">{}</text:p>\n",
                        xml_esc(line)
                    ));
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
                let s = format!(
                    "<text:a xlink:type=\"simple\" xlink:href=\"{}\">{}</text:a>",
                    xml_esc(&url),
                    xml_esc(&text)
                );
                if in_item { item_buf.push_str(&s) } else { body.push_str(&s) }
            }
            Event::Start(Tag::Image { dest_url, .. }) => {
                in_image = true;
                let s = format!("[图片:{}]", xml_esc(&dest_url));
                if in_item { item_buf.push_str(&s) } else { body.push_str(&s) }
            }
            Event::End(TagEnd::Image) => in_image = false,
            Event::Start(Tag::BlockQuote(_)) => {
                in_quote = true;
                body.push_str("<text:p text:style-name=\"T6\">");
            }
            Event::End(TagEnd::BlockQuote(_)) => {
                in_quote = false;
                body.push_str("</text:p>\n");
            }
            Event::Rule => body.push_str("<text:p text:style-name=\"T6\">---</text:p>\n"),
            Event::Start(Tag::List(start)) => {
                flush_item(&mut body, &mut item_buf);
                if start.is_some() {
                    body.push_str("<text:list text:style-name=\"L1\">\n");
                    list_stack.push("ordered");
                } else {
                    body.push_str("<text:list>\n");
                    list_stack.push("itemize");
                }
            }
            Event::End(TagEnd::List(_)) => {
                list_stack.pop();
                body.push_str("</text:list>\n");
            }
            Event::Start(Tag::Item) => {
                body.push_str("<text:list-item>\n");
                in_item = true;
                item_buf.clear();
            }
            Event::End(TagEnd::Item) => {
                flush_item(&mut body, &mut item_buf);
                body.push_str("</text:list-item>\n");
                in_item = false;
            }
            Event::Start(Tag::Table(_)) => {
                table_buf.clear();
                cur_row.clear();
                in_table = true;
            }
            Event::End(TagEnd::Table) => {
                in_table = false;
                emit_odt_table(&mut body, &table_buf);
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

    let content_xml = format!(
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n\
         <office:document-content xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\"\n\
         xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\"\n\
         xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\"\n\
         xmlns:xlink=\"http://www.w3.org/1999/xlink\"\n\
         xmlns:style=\"urn:oasis:names:tc:opendocument:xmlns:style:1.0\"\n\
         office:version=\"1.2\">\n\
         <office:body><office:text>\n{body}</office:text></office:body>\n\
         </office:document-content>\n"
    );

    let styles_xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n\
        <office:document-styles xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\"\n\
        xmlns:style=\"urn:oasis:names:tc:opendocument:xmlns:style:1.0\"\n\
        xmlns:fo=\"urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0\"\n\
        office:version=\"1.2\">\n\
        <office:styles>\n\
        <style:style style:name=\"T1\" style:family=\"text\"><style:text-properties fo:font-weight=\"bold\"/></style:style>\n\
        <style:style style:name=\"T2\" style:family=\"text\"><style:text-properties fo:font-style=\"italic\"/></style:style>\n\
        <style:style style:name=\"T3\" style:family=\"text\"><style:text-properties fo:font-weight=\"bold\" fo:font-style=\"italic\"/></style:style>\n\
        <style:style style:name=\"T4\" style:family=\"text\"><style:text-properties style:font-name=\"Courier New\"/></style:style>\n\
        <style:style style:name=\"T5\" style:family=\"paragraph\"><style:text-properties style:font-name=\"Courier New\"/></style:style>\n\
        <style:style style:name=\"T6\" style:family=\"paragraph\"><style:paragraph-properties fo:margin-left=\"1cm\"/></style:style>\n\
        <text:list-style style:name=\"L1\">\n\
          <text:list-level-style-number text:level=\"1\" style:num-format=\"1\"/>\n\
          <text:list-level-style-number text:level=\"2\" style:num-format=\"1\"/>\n\
          <text:list-level-style-number text:level=\"3\" style:num-format=\"1\"/>\n\
        </text:list-style>\n\
        </office:styles>\n</office:document-styles>\n";

    let manifest_xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n\
        <manifest:manifest xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\" manifest:version=\"1.2\">\n\
        <manifest:file-entry manifest:full-path=\"/\" manifest:media-type=\"application/vnd.oasis.opendocument.text\"/>\n\
        <manifest:file-entry manifest:full-path=\"content.xml\" manifest:media-type=\"text/xml\"/>\n\
        <manifest:file-entry manifest:full-path=\"styles.xml\" manifest:media-type=\"text/xml\"/>\n\
        </manifest:manifest>\n";

    let file = File::create(out).map_err(|e| format!("创建 {} 失败：{e}", out.display()))?;
    let mut zw = ZipWriter::new(file);
    let stored = SimpleFileOptions::default().compression_method(zip::CompressionMethod::Stored);
    let deflated = SimpleFileOptions::default().compression_method(zip::CompressionMethod::Deflated);
    zw.start_file("mimetype", stored)
        .map_err(|e| e.to_string())?;
    zw.write_all(b"application/vnd.oasis.opendocument.text")
        .map_err(|e| e.to_string())?;
    zw.start_file("content.xml", deflated).map_err(|e| e.to_string())?;
    zw.write_all(content_xml.as_bytes()).map_err(|e| e.to_string())?;
    zw.start_file("styles.xml", deflated).map_err(|e| e.to_string())?;
    zw.write_all(styles_xml.as_bytes()).map_err(|e| e.to_string())?;
    zw.start_file("META-INF/manifest.xml", deflated)
        .map_err(|e| e.to_string())?;
    zw.write_all(manifest_xml.as_bytes()).map_err(|e| e.to_string())?;
    zw.finish().map_err(|e| e.to_string())?;
    Ok(())
}

fn flush_item(out: &mut String, item_buf: &mut String) {
    if item_buf.is_empty() {
        return;
    }
    out.push_str("<text:p>");
    out.push_str(item_buf);
    out.push_str("</text:p>\n");
    item_buf.clear();
}

fn emit_odt_table(out: &mut String, rows: &[Vec<String>]) {
    if rows.is_empty() {
        return;
    }
    let cols = rows.iter().map(|r| r.len()).max().unwrap_or(0);
    if cols == 0 {
        return;
    }
    out.push_str(&format!(
        "<table:table table:name=\"Table1\"><table:table-column table:number-columns-repeated=\"{cols}\"/>\n"
    ));
    for row in rows.iter() {
        out.push_str("<table:table-row>\n");
        for c in 0..cols {
            let cell = row.get(c).cloned().unwrap_or_default();
            out.push_str(&format!("<table:table-cell><text:p>{}</text:p></table:table-cell>\n", cell));
        }
        out.push_str("</table:table-row>\n");
    }
    out.push_str("</table:table>\n");
}
