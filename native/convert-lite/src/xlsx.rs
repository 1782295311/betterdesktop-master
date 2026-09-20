//! XLSX 解析 + CSV/HTML 渲染。
//!
//! xlsx 也是 zip：`xl/sharedStrings.xml` 存共享字符串，`xl/worksheets/sheet1.xml` 存单元格。
//! 本 spike 只读第一个 sheet、字符串与数字；不展开公式、不处理样式/合并单元格。

use std::collections::HashMap;
use std::io::{Cursor, Read, Write};
use std::path::Path;

use quick_xml::events::Event;
use quick_xml::Reader;

/// 二维表：行 × 单元格。
pub struct Sheet {
    pub rows: Vec<Vec<String>>,
}

fn norm(s: &str) -> String {
    s.replace('\\', "/")
}

/// A1/B2/AA1 -> 0-based 列号。
fn col_to_idx(addr: &str) -> usize {
    let mut n = 0;
    for c in addr.chars().take_while(|c| c.is_ascii_alphabetic()) {
        n = n * 26 + (c.to_ascii_uppercase() as u8 - b'A' + 1) as usize;
    }
    n.saturating_sub(1)
}

/// 读 sharedStrings：<si><t>...</t></si>（一个 si 内可能多个 t，拼接）。
fn parse_shared(xml: &[u8]) -> Vec<String> {
    let mut reader = Reader::from_reader(xml);
    let mut buf = Vec::new();
    let mut out = Vec::new();
    let mut cur = String::new();
    let mut in_si = false;
    loop {
        match reader.read_event_into(&mut buf) {
            Ok(Event::Start(e)) => match e.name().as_ref() {
                b"si" => {
                    in_si = true;
                    cur.clear();
                }
                _ => {}
            },
            Ok(Event::End(e)) => match e.name().as_ref() {
                b"si" => {
                    in_si = false;
                    out.push(std::mem::take(&mut cur));
                }
                _ => {}
            },
            Ok(Event::Text(t)) if in_si => {
                if let Ok(s) = t.unescape() {
                    cur.push_str(&s);
                }
            }
            Ok(Event::Eof) => break,
            _ => {}
        }
        buf.clear();
    }
    out
}

/// 读 worksheet：<row><c r="A1" t="s"><v>0</v></c></row>
fn parse_sheet(xml: &[u8], shared: &[String]) -> Sheet {
    let mut reader = Reader::from_reader(xml);
    let mut buf = Vec::new();
    let mut rows: Vec<Vec<String>> = Vec::new();

    let mut cur_row: Vec<String> = Vec::new();
    let mut last_col: usize = 0;
    let mut cell_ref: String = String::new();
    let mut cell_type: String = String::new();
    let mut cell_val: String = String::new();
    let mut in_v = false;

    fn push_cell(row: &mut Vec<String>, col: usize, val: String, last_col: &mut usize) {
        while row.len() <= col {
            row.push(String::new());
        }
        row[col] = val;
        *last_col = (*last_col).max(col);
    }

    loop {
        match reader.read_event_into(&mut buf) {
            Ok(Event::Start(e)) => match e.name().as_ref() {
                b"row" => {
                    cur_row.clear();
                    last_col = 0;
                }
                b"c" => {
                    cell_ref.clear();
                    cell_type.clear();
                    cell_val.clear();
                    for a in e.attributes().flatten() {
                        match a.key.as_ref() {
                            b"r" => cell_ref = String::from_utf8_lossy(&a.value).into_owned(),
                            b"t" => cell_type = String::from_utf8_lossy(&a.value).into_owned(),
                            _ => {}
                        }
                    }
                }
                b"v" => in_v = true,
                _ => {}
            },
            Ok(Event::End(e)) => match e.name().as_ref() {
                b"c" => {
                    let col = col_to_idx(&cell_ref);
                    let val = match cell_type.as_str() {
                        "s" => shared
                            .get(cell_val.parse::<usize>().unwrap_or(usize::MAX))
                            .cloned()
                            .unwrap_or_default(),
                        _ => cell_val.clone(),
                    };
                    push_cell(&mut cur_row, col, val, &mut last_col);
                }
                b"row" => rows.push(std::mem::take(&mut cur_row)),
                b"v" => in_v = false,
                _ => {}
            },
            Ok(Event::Text(t)) if in_v => {
                if let Ok(s) = t.unescape() {
                    cell_val.push_str(&s);
                }
            }
            Ok(Event::Eof) => break,
            _ => {}
        }
        buf.clear();
    }
    Sheet { rows }
}

pub fn parse_xlsx(path: &Path) -> Result<Sheet, String> {
    let bytes = std::fs::read(path).map_err(|e| format!("读取 xlsx 失败：{e}"))?;
    let cursor = Cursor::new(bytes);
    let mut archive = zip::ZipArchive::new(cursor).map_err(|e| format!("不是有效 zip/xlsx：{e}"))?;

    let mut files: HashMap<String, Vec<u8>> = HashMap::new();
    for i in 0..archive.len() {
        let mut e = archive.by_index(i).map_err(|e| e.to_string())?;
        let n = norm(e.name());
        let mut v = Vec::new();
        e.read_to_end(&mut v).map_err(|e| e.to_string())?;
        files.insert(n, v);
    }

    let shared = files
        .get("xl/sharedStrings.xml")
        .map(|s| parse_shared(s))
        .unwrap_or_default();
    let sheet = files
        .get("xl/worksheets/sheet1.xml")
        .ok_or_else(|| "xlsx 内缺 xl/worksheets/sheet1.xml".to_string())?;
    let rows = parse_sheet(sheet, &shared);
    Ok(rows)
}

fn csv_escape(field: &str) -> String {
    if field.contains(',') || field.contains('"') || field.contains('\n') {
        format!("\"{}\"", field.replace('"', "\"\""))
    } else {
        field.to_string()
    }
}

impl Sheet {
    pub fn to_csv(&self) -> String {
        let mut out = String::new();
        for row in &self.rows {
            let line: Vec<String> = row.iter().map(|c| csv_escape(c)).collect();
            out.push_str(&line.join(","));
            out.push('\n');
        }
        out
    }

    pub fn to_html(&self) -> String {
        let mut out = String::from(
            "<!doctype html><html><head><meta charset=\"utf-8\"><title>sheet</title></head><body><table border=\"1\" cellpadding=\"4\">",
        );
        for row in &self.rows {
            out.push_str("<tr>");
            for cell in row {
                out.push_str(&format!("<td>{}</td>", cell.replace('&', "&amp;").replace('<', "&lt;")));
            }
            out.push_str("</tr>");
        }
        out.push_str("</table></body></html>");
        out
    }
}

pub fn xlsx_to_csv_bytes(path: &Path) -> Result<Vec<u8>, String> {
    let s = parse_xlsx(path)?;
    Ok(s.to_csv().into_bytes())
}

pub fn xlsx_to_html_bytes(path: &Path) -> Result<Vec<u8>, String> {
    let s = parse_xlsx(path)?;
    Ok(s.to_html().into_bytes())
}

// ==== CSV -> XLSX（soffice 替代链：表格回填 xlsx） ====

fn xml_escape(s: &str) -> String {
    s.replace('&', "&amp;")
        .replace('<', "&lt;")
        .replace('>', "&gt;")
        .replace('"', "&quot;")
        .replace('\'', "&apos;")
}

/// 0-based 列号 -> Excel 列名（0=A, 25=Z, 26=AA）。
fn col_name(col: usize) -> String {
    let mut n = col + 1;
    let mut out = String::new();
    while n > 0 {
        let rem = (n - 1) % 26;
        out.insert(0, (b'A' + rem as u8) as char);
        n = (n - 1) / 26;
    }
    out
}

/// 读 CSV 文件：自动检测编码（BOM/UTF-8/GBK）和分隔符（逗号/分号/Tab）。
fn read_csv_auto(path: &Path) -> Result<Vec<Vec<String>>, String> {
    let raw = std::fs::read(path).map_err(|e| format!("读取失败：{e}"))?;
    // 1. 编码检测
    let text = if raw.starts_with(&[0xEF, 0xBB, 0xBF]) {
        // UTF-8 BOM
        String::from_utf8_lossy(&raw[3..]).to_string()
    } else if let Ok(s) = std::str::from_utf8(&raw) {
        // 纯 UTF-8
        s.to_string()
    } else {
        // 试 GBK（用 encoding_rs）
        match encoding_rs::GBK.decode(&raw) {
            (s, _, _) => s.to_string(),
        }
    };
    // 2. 分隔符检测：统计前 5 行的逗号/分号/Tab 数量
    let sample: Vec<&str> = text.lines().take(5).collect();
    let mut comma = 0;
    let mut semicolon = 0;
    let mut tab = 0;
    for line in &sample {
        comma += line.matches(',').count();
        semicolon += line.matches(';').count();
        tab += line.matches('\t').count();
    }
    let delimiter = if tab > comma && tab > semicolon {
        b'\t'
    } else if semicolon > comma {
        b';'
    } else {
        b','
    };
    // 3. 解析
    let mut rdr = csv::ReaderBuilder::new()
        .has_headers(false)
        .delimiter(delimiter)
        .from_reader(text.as_bytes());
    let mut rows: Vec<Vec<String>> = Vec::new();
    for rec in rdr.records() {
        let rec = rec.map_err(|e| format!("CSV 解析失败：{e}"))?;
        rows.push(rec.iter().map(|s| s.to_string()).collect());
    }
    Ok(rows)
}

/// CSV -> XLSX（zip + 最小 OOXML：sharedStrings 字符串 + 数字直写 <v>）。
pub fn csv_to_xlsx_bytes(path: &Path) -> Result<Vec<u8>, String> {
    let rows = read_csv_auto(path)?;

    let mut shared: Vec<String> = Vec::new();
    let mut idx: HashMap<String, usize> = HashMap::new();
    let mut sheet_rows = String::new();
    for (r, row) in rows.iter().enumerate() {
        sheet_rows.push_str(&format!("<row r=\"{}\"", r + 1));
        sheet_rows.push('>');
        for (c, cell) in row.iter().enumerate() {
            let addr = col_name(c);
            let trimmed = cell.trim();
            let is_num = !trimmed.is_empty() && trimmed.parse::<f64>().is_ok();
            if is_num {
                sheet_rows.push_str(&format!("<c r=\"{addr}\"><v>{trimmed}</v></c>"));
            } else {
                let id = *idx.entry(cell.clone()).or_insert_with(|| {
                    shared.push(cell.clone());
                    shared.len() - 1
                });
                sheet_rows.push_str(&format!("<c r=\"{addr}\" t=\"s\"><v>{id}</v></c>"));
            }
        }
        sheet_rows.push_str("</row>");
    }
    let sheet_xml = format!(
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\
         <worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">\
         <sheetData>{sheet_rows}</sheetData></worksheet>"
    );
    let shared_xml: String = shared
        .iter()
        .map(|s| format!("<si><t>{}</t></si>", xml_escape(s)))
        .collect();
    let shared_xml = format!(
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\
         <sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" \
         count=\"{n}\" uniqueCount=\"{n}\">{shared_xml}</sst>",
        n = shared.len()
    );
    let workbook_xml = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\
        <workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" \
        xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">\
        <sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>";
    let content_types = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\
        <Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">\
        <Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>\
        <Default Extension=\"xml\" ContentType=\"application/xml\"/>\
        <Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>\
        <Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>\
        <Override PartName=\"/xl/sharedStrings.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml\"/>\
        </Types>";
    let rels = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\
        <Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">\
        <Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>\
        </Relationships>";
    let wb_rels = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\
        <Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">\
        <Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>\
        <Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings\" Target=\"sharedStrings.xml\"/>\
        </Relationships>";

    let mut buf = Vec::new();
    {
        let mut zw = zip::ZipWriter::new(std::io::Cursor::new(&mut buf));
        let opts = zip::write::SimpleFileOptions::default();
        let mut put = |name: &str, content: &str| -> Result<(), String> {
            zw.start_file(name, opts).map_err(|e| e.to_string())?;
            zw.write_all(content.as_bytes()).map_err(|e| e.to_string())
        };
        put("[Content_Types].xml", content_types)?;
        put("_rels/.rels", rels)?;
        put("xl/workbook.xml", workbook_xml)?;
        put("xl/_rels/workbook.xml.rels", wb_rels)?;
        put("xl/sharedStrings.xml", &shared_xml)?;
        put("xl/worksheets/sheet1.xml", &sheet_xml)?;
        zw.finish().map_err(|e| e.to_string())?;
    }
    Ok(buf)
}

// ==== XLSX -> PDF（表格渲染：Sheet -> 文档 IR 表格块 -> doc_pdf 渲染） ====

/// 行集 -> PDF：转为文档 IR 表格块，复用 doc_pdf 流式渲染
/// （逐 cell 竖线分隔、JPEG 内嵌、嵌入字体；超出页高自动换页）。
pub fn rows_to_pdf_bytes(rows: &[Vec<String>]) -> Result<Vec<u8>, String> {
    let mut doc = crate::doc::Document::new();
    doc.blocks.push(crate::doc::Block::Table { rows: rows.iter().map(|r| r.iter().map(|s| crate::doc::Cell { text: s.clone(), align: 0, colspan: 1 }).collect()).collect() });
    crate::doc_pdf::render_doc_pdf(&doc)
}

/// XLSX -> PDF（表格渲染）。
pub fn xlsx_to_pdf_bytes(path: &Path) -> Result<Vec<u8>, String> {
    let s = parse_xlsx(path)?;
    rows_to_pdf_bytes(&s.rows)
}

/// XLS -> PDF（两跳：xls -> csv(内存) -> 表格渲染 PDF）。
pub fn xls_to_pdf_bytes(path: &Path) -> Result<Vec<u8>, String> {
    let csv = crate::xls_legacy::xls_to_csv(path)?;
    let mut rdr = csv::ReaderBuilder::new()
        .has_headers(false)
        .from_reader(csv.as_bytes());
    let mut rows: Vec<Vec<String>> = Vec::new();
    for rec in rdr.records() {
        let rec = rec.map_err(|e| format!("CSV 解析失败：{e}"))?;
        rows.push(rec.iter().map(|s| s.to_string()).collect());
    }
    rows_to_pdf_bytes(&rows)
}

// ==== CSV -> ODS（soffice 替代链：csv -> ods；mimetype 首个条目且不压缩） ====

/// ODS 打包（zip + OpenDocument Spreadsheet 最小包：content.xml + manifest）。
/// 字符串单元格 office:string-value，数字单元格 office:value-type="float"；
/// mimetype 必须首个条目且不压缩（Stored）。
fn rows_to_ods_bytes(rows: &[Vec<String>]) -> Result<Vec<u8>, String> {
    let mut table_rows = String::new();
    for row in rows {
        table_rows.push_str("<table:table-row>");
        for cell in row {
            let trimmed = cell.trim();
            let is_num = !trimmed.is_empty() && trimmed.parse::<f64>().is_ok();
            if is_num {
                table_rows.push_str(&format!(
                    "<table:table-cell office:value-type=\"float\" office:value=\"{trimmed}\"><text:p>{trimmed}</text:p></table:table-cell>"
                ));
            } else {
                table_rows.push_str(&format!(
                    "<table:table-cell office:value-type=\"string\"><text:p>{}</text:p></table:table-cell>",
                    xml_escape(cell)
                ));
            }
        }
        table_rows.push_str("</table:table-row>");
    }
    let content = format!(
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\
         <office:document-content xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" \
         xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\" \
         xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\">\
         <office:body><office:spreadsheet><table:table table:name=\"Sheet1\">{table_rows}</table:table>\
         </office:spreadsheet></office:body></office:document-content>"
    );
    let manifest = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\
        <manifest:manifest xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\" \
        manifest:version=\"1.2\">\
        <manifest:file-entry manifest:full-path=\"/\" manifest:media-type=\"application/vnd.oasis.opendocument.spreadsheet\"/>\
        <manifest:file-entry manifest:full-path=\"content.xml\" manifest:media-type=\"text/xml\"/>\
        </manifest:manifest>";

    let mut buf = Vec::new();
    {
        let mut zw = zip::ZipWriter::new(std::io::Cursor::new(&mut buf));
        // mimetype 必须首个且不压缩
        zw.start_file(
            "mimetype",
            zip::write::SimpleFileOptions::default().compression_method(zip::CompressionMethod::Stored),
        )
        .map_err(|e| e.to_string())?;
        zw.write_all(b"application/vnd.oasis.opendocument.spreadsheet")
            .map_err(|e| e.to_string())?;
        let opts = zip::write::SimpleFileOptions::default();
        let mut put = |name: &str, content: &str| -> Result<(), String> {
            zw.start_file(name, opts).map_err(|e| e.to_string())?;
            zw.write_all(content.as_bytes()).map_err(|e| e.to_string())
        };
        put("META-INF/manifest.xml", manifest)?;
        put("content.xml", &content)?;
        zw.finish().map_err(|e| e.to_string())?;
    }
    Ok(buf)
}

/// CSV -> ODS。
pub fn csv_to_ods_bytes(path: &Path) -> Result<Vec<u8>, String> {
    let rows = read_csv_auto(path)?;
    rows_to_ods_bytes(&rows)
}

/// XLSX -> ODS（表格族互通：解析第一个 sheet 后复用 ODS 打包）。
pub fn xlsx_to_ods_bytes(path: &Path) -> Result<Vec<u8>, String> {
    let s = parse_xlsx(path)?;
    rows_to_ods_bytes(&s.rows)
}
