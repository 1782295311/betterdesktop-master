// PdfText 引擎（S9.5 补全：PDF 作为源的文本类转换——用户实测发现唯一缺口）。
// 语义锚定 C# TextPdfEngine.cs：pdftotext 提取（xlsx 加 -layout 保留对齐）→ 托管生成
// txt/md/html/rtf/opendocument（直写）与 odt/epub/docx/xlsx（zip 最小包）。诚实降级：无版式，
// 菜单文本已标注「文本提取」。
// 红线：pdftotext 缺失 = EngineMissing（不是文件错误）；非零退出/无产物 = ConversionFailed。
use std::io::Write;
use std::path::{Path, PathBuf};

use crate::contract::{ConversionTarget, EngineKind};
use crate::error::Error;
use crate::exec;
use crate::run::{Engine, Job};
use crate::{engines::locate_pdftotext, constants::TIMEOUT_MS};

/// PDF 文本提取引擎（pdftotext + 托管 OOXML；与 C# TextPdfEngine 逐格式对齐）。
pub struct PdfTextEngine;

const TARGETS: [&str; 9] = ["txt", "md", "html", "docx", "xlsx", "epub", "odt", "rtf", "opendocument"];

impl Engine for PdfTextEngine {
    fn kind(&self) -> EngineKind {
        EngineKind::PdfText
    }

    fn can_handle(&self, sources: &[PathBuf], target: &ConversionTarget) -> bool {
        sources.len() == 1
            && sources[0]
                .extension()
                .map(|e| e.eq_ignore_ascii_case("pdf"))
                .unwrap_or(false)
            && (target.prefer == EngineKind::PdfText || target.fallback == Some(EngineKind::PdfText))
            && TARGETS.contains(&target.format.as_str())
    }

    fn run(&self, job: &Job) -> Result<Vec<PathBuf>, Error> {
        let pdftotext = locate_pdftotext()
            .ok_or_else(|| Error::engine_missing("内置引擎未就绪（未找到 pdftotext，Poppler 套件缺失）——这不是文件错误"))?;
        let input = job.primary_source();
        let format = job.target.format.as_str();
        let name = input
            .file_stem()
            .map(|s| s.to_string_lossy().to_string())
            .unwrap_or_else(|| "output".to_string());

        // 1) pdftotext 提取（xlsx 用 -layout 保留对齐以便拆列）
        let text_file = job.temp_dir.join(format!("{name}.raw.txt"));
        let mut args = vec!["-enc".to_string(), "UTF-8".to_string()];
        if format == "xlsx" {
            args.insert(0, "-layout".to_string());
        }
        args.push(input.to_string_lossy().to_string());
        args.push(text_file.to_string_lossy().to_string());
        let out = exec::run(&pdftotext, &args, None, TIMEOUT_MS)?;
        if out.code != Some(0) || !text_file.is_file() {
            return Err(Error::conversion_failed(format!(
                "pdftotext 退出码 {} {}",
                out.code.map(|c| c.to_string()).unwrap_or_else(|| "-".to_string()),
                truncate(&out.stderr)
            )));
        }
        let text = std::fs::read_to_string(&text_file)
            .map_err(|e| Error::conversion_failed(format!("提取文本读取失败: {e}")))?;

        // 2) 按目标格式生成（纯托管；无版式，诚实文本提取）
        let ext = extension_of(format);
        let product = job.temp_dir.join(format!("{name}.{ext}"));
        match format {
            "txt" | "md" => write_utf8(&product, &text)?,
            "html" => write_utf8(&product, &build_html(&text))?,
            "rtf" => write_utf8(&product, &build_rtf(&text))?,
            "opendocument" => write_utf8(&product, &build_opendocument_xml(&text))?,
            "odt" => write_odt(&product, &text)?,
            "epub" => write_epub(&product, &name, &text)?,
            "docx" => write_docx(&product, &text)?,
            "xlsx" => write_xlsx(&product, &text)?,
            _ => return Err(Error::input_invalid(format!("TextPdf 引擎不支持的转换目标: {format}"))),
        }
        Ok(vec![product])
    }
}

fn extension_of(format: &str) -> &str {
    match format {
        "md" => "md",
        "html" => "html",
        "epub" => "epub",
        "odt" => "odt",
        "rtf" => "rtf",
        "opendocument" => "xml",
        _ => format,
    }
}

fn write_utf8(path: &Path, content: &str) -> Result<(), Error> {
    std::fs::write(path, content).map_err(|e| Error::output_failed(format!("产物写入失败: {e}")))
}

/// XML 转义（& < > " '，对齐 C# EscapeXml）。
fn escape_xml(text: &str) -> String {
    text.chars()
        .map(|c| match c {
            '&' => "&amp;".to_string(),
            '<' => "&lt;".to_string(),
            '>' => "&gt;".to_string(),
            '"' => "&quot;".to_string(),
            '\'' => "&apos;".to_string(),
            _ => c.to_string(),
        })
        .collect()
}

fn normalize_lines(text: &str) -> Vec<String> {
    text.replace("\r\n", "\n")
        .split('\n')
        .map(|s| s.to_string())
        .collect()
}

// —— html（转义段落，CJK 友好） ——

fn build_html(text: &str) -> String {
    let body: String = normalize_lines(text)
        .iter()
        .map(|line| format!("<p>{}</p>\n", escape_xml(line)))
        .collect();
    format!(
        "<!DOCTYPE html>\n<html lang=\"zh-CN\">\n<head><meta charset=\"utf-8\"/></head>\n<body>\n{body}</body>\n</html>\n"
    )
}

// —— rtf（\rtf1 单段落序列；\n 转 \par） ——

fn build_rtf(text: &str) -> String {
    let escaped = text
        .replace('\\', "\\\\")
        .replace('{', "\\{")
        .replace('}', "\\}")
        .replace("\r\n", "\n")
        .replace('\n', "\\par\n");
    format!("{{\\rtf1\\ansi\\deff0{{\\fonttbl{{\\f0 Courier New;}}}}\\f0\\fs24\n{escaped}\n}}")
}

// —— opendocument（单 XML，pandoc -t opendocument 同形态：office:text 内容流） ——

fn build_opendocument_xml(text: &str) -> String {
    let paragraphs: String = normalize_lines(text)
        .iter()
        .map(|line| format!("<text:p>{}</text:p>\n", escape_xml(line)))
        .collect();
    format!(
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n\
         <office:document xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" \
         xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\">\n\
         <office:body><office:text>\n{paragraphs}</office:text></office:body>\n</office:document>\n"
    )
}

// —— zip 写入辅助（zip 2.x：ZipWriter + SimpleFileOptions；失败统一 OutputFailed） ——

fn zip_write(path: &Path, entries: &[(&str, &str)]) -> Result<(), Error> {
    let file = std::fs::File::create(path)
        .map_err(|e| Error::output_failed(format!("zip 创建失败: {e}")))?;
    let mut zip = zip::ZipWriter::new(file);
    for (name, content) in entries {
        zip.start_file(*name, zip::write::SimpleFileOptions::default())
            .map_err(|e| Error::output_failed(format!("zip 条目 {name} 失败: {e}")))?;
        zip.write_all(content.as_bytes())
            .map_err(|e| Error::output_failed(format!("zip 写入 {name} 失败: {e}")))?;
    }
    zip.finish()
        .map_err(|e| Error::output_failed(format!("zip 收尾失败: {e}")))?;
    Ok(())
}

// —— odt（zip：mimetype + content.xml，office:text 内容流） ——

fn write_odt(path: &Path, text: &str) -> Result<(), Error> {
    let paragraphs: String = normalize_lines(text)
        .iter()
        .map(|line| format!("<text:p>{}</text:p>\n", escape_xml(line)))
        .collect();
    let content = format!(
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n\
         <office:document-content xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" \
         xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\">\n\
         <office:body><office:text>\n{paragraphs}</office:text></office:body>\n</office:document-content>\n"
    );
    zip_write(path, &[("mimetype", "application/vnd.oasis.opendocument.text"), ("content.xml", &content)])
}

// —— epub（最小单章包：mimetype/container.opf/xhtml） ——

fn write_epub(path: &Path, title: &str, text: &str) -> Result<(), Error> {
    let body: String = normalize_lines(text)
        .iter()
        .map(|line| format!("<p>{}</p>\n", escape_xml(line)))
        .collect();
    let container = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n\
         <container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\">\
         <rootfiles><rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/>\
         </rootfiles></container>";
    let opf = format!(
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n\
         <package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"uid\">\
         <metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\">\
         <dc:identifier id=\"uid\">betterdt-pdf-text</dc:identifier>\
         <dc:title>{}</dc:title>\
         <dc:language>zh-CN</dc:language></metadata>\
         <manifest><item id=\"c1\" href=\"chapter.xhtml\" media-type=\"application/xhtml+xml\"/></manifest>\
         <spine><itemref idref=\"c1\"/></spine></package>",
        escape_xml(title)
    );
    let chapter = format!(
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n\
         <html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>{}</title></head><body>\n{body}</body></html>",
        escape_xml(title)
    );
    zip_write(
        path,
        &[
            ("mimetype", "application/epub+zip"),
            ("META-INF/container.xml", &container),
            ("OEBPS/content.opf", &opf),
            ("OEBPS/chapter.xhtml", &chapter),
        ],
    )
}

// —— docx 最小包（段落文本） ——

fn write_docx(path: &Path, text: &str) -> Result<(), Error> {
    let content_types = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n\
         <Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">\
         <Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>\
         <Default Extension=\"xml\" ContentType=\"application/xml\"/>\
         <Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>\
         </Types>";
    let rels = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n\
         <Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">\
         <Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>\
         </Relationships>";
    let paragraphs: String = normalize_lines(text)
        .iter()
        .map(|line| format!("<w:p><w:r><w:t xml:space=\"preserve\">{}</w:t></w:r></w:p>", escape_xml(line)))
        .collect();
    let document = format!(
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n\
         <w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">\
         <w:body>{paragraphs}<w:sectPr/></w:body></w:document>"
    );
    zip_write(
        path,
        &[
            ("[Content_Types].xml", content_types),
            ("_rels/.rels", rels),
            ("word/document.xml", &document),
        ],
    )
}

// —— xlsx 最小包（-layout 行按 2+ 空格拆列；inlineStr 单元格） ——

fn write_xlsx(path: &Path, text: &str) -> Result<(), Error> {
    let content_types = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n\
         <Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">\
         <Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>\
         <Default Extension=\"xml\" ContentType=\"application/xml\"/>\
         <Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>\
         <Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>\
         </Types>";
    let rels = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n\
         <Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">\
         <Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>\
         </Relationships>";
    let workbook = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n\
         <workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" \
         xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">\
         <sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>";
    let wb_rels = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n\
         <Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">\
         <Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>\
         </Relationships>";

    let rows_xml: String = normalize_lines(text)
        .iter()
        .enumerate()
        .map(|(row_idx, line)| {
            let cells: Vec<&str> = split_columns(line);
            let cells_xml: String = cells
                .iter()
                .enumerate()
                .map(|(col_idx, cell)| {
                    format!(
                        "<c r=\"{}{}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{}</t></is></c>",
                        column_name(col_idx),
                        row_idx + 1,
                        escape_xml(cell)
                    )
                })
                .collect();
            format!("<row r=\"{}\">{}</row>", row_idx + 1, cells_xml)
        })
        .collect();
    let sheet = format!(
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n\
         <worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">\
         <sheetData>{rows_xml}</sheetData></worksheet>"
    );
    zip_write(
        path,
        &[
            ("[Content_Types].xml", content_types),
            ("_rels/.rels", rels),
            ("xl/workbook.xml", workbook),
            ("xl/_rels/workbook.xml.rels", wb_rels),
            ("xl/worksheets/sheet1.xml", &sheet),
        ],
    )
}

/// 按 2+ 连续空格拆列（对齐 C# Regex.Split(line, @" {2,}")）。
fn split_columns(line: &str) -> Vec<&str> {
    let mut cols = Vec::new();
    let mut start = 0usize;
    let bytes = line.as_bytes();
    let mut i = 0usize;
    while i < bytes.len() {
        if bytes[i] == b' ' {
            let mut j = i;
            while j < bytes.len() && bytes[j] == b' ' {
                j += 1;
            }
            if j - i >= 2 {
                cols.push(&line[start..i]);
                i = j;
                start = i;
                continue;
            }
        }
        i += 1;
    }
    cols.push(&line[start..]);
    cols
}

/// 列名（A…Z, AA…AZ…）——26 进制，与 Excel 单元格坐标一致。
fn column_name(index: usize) -> String {
    let mut name = String::new();
    let mut n = index;
    loop {
        name.insert(0, (b'A' + (n % 26) as u8) as char);
        if n < 26 {
            break;
        }
        n = n / 26 - 1;
    }
    name
}

fn truncate(text: &str) -> &str {
    if text.len() > 200 { &text[..200] } else { text }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn html_escapes_and_wraps_lines() {
        let html = build_html("a<b>&c\n二行");
        assert!(html.contains("<p>a&lt;b&gt;&amp;c</p>"));
        assert!(html.contains("<p>二行</p>"));
    }

    #[test]
    fn rtf_escapes_braces_and_newlines() {
        let rtf = build_rtf("a{b}\nc");
        assert!(rtf.contains("a\\{b\\}"));
        assert!(rtf.contains("\\par"));
        assert!(rtf.starts_with("{\\rtf1"));
    }

    #[test]
    fn docx_zip_contains_document_xml() {
        let dir = std::env::temp_dir().join("bdt-pdftext-test-docx.docx");
        write_docx(&dir, "行1\n行2").expect("docx 写入");
        let file = std::fs::File::open(&dir).expect("打开 docx");
        let mut zip = zip::ZipArchive::new(file).expect("解析 zip");
        let doc = zip.by_name("word/document.xml").expect("document.xml 存在");
        let content = std::io::read_to_string(doc).expect("读取");
        assert!(content.contains("<w:p><w:r><w:t xml:space=\"preserve\">行1</w:t></w:r></w:p>"));
        std::fs::remove_file(&dir).ok();
    }

    #[test]
    fn xlsx_column_name_and_rows() {
        assert_eq!(column_name(0), "A");
        assert_eq!(column_name(25), "Z");
        assert_eq!(column_name(26), "AA");
        assert_eq!(column_name(27), "AB");
        let cols = split_columns("名  值  备注");
        assert_eq!(cols, vec!["名", "值", "备注"]);
        let dir = std::env::temp_dir().join("bdt-pdftext-test.xlsx");
        write_xlsx(&dir, "名  值\n甲  1").expect("xlsx 写入");
        let file = std::fs::File::open(&dir).expect("打开 xlsx");
        let mut zip = zip::ZipArchive::new(file).expect("解析 zip");
        let sheet = zip.by_name("xl/worksheets/sheet1.xml").expect("sheet1 存在");
        let content = std::io::read_to_string(sheet).expect("读取");
        assert!(content.contains("r=\"A1\"")); // 第一格 名
        assert!(content.contains("r=\"B1\"")); // 第二格 值
        std::fs::remove_file(&dir).ok();
    }

    #[test]
    fn opendocument_xml_is_single_document() {
        let xml = build_opendocument_xml("a\nb");
        assert!(xml.contains("<office:document"));
        assert!(xml.contains("<text:p>a</text:p>"));
    }

    #[test]
    fn can_handle_accepts_pdf_source_only() {
        let engine = PdfTextEngine;
        let target = ConversionTarget {
            format: "txt".into(),
            label: "纯文本".into(),
            filter: None,
            hops: 1,
            prefer: EngineKind::PdfText,
            fallback: None,
            category: crate::contract::TargetCategory::Text,
            lossless: false,
        };
        assert!(engine.can_handle(&[PathBuf::from("a.pdf")], &target));
        assert!(!engine.can_handle(&[PathBuf::from("a.docx")], &target));
        assert!(!engine.can_handle(&[PathBuf::from("a.pdf"), PathBuf::from("b.pdf")], &target));
    }
}
