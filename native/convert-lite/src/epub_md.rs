//! EPUB -> Markdown（反向：电子书提取文本）。
//!
//! EPUB 本质是 zip + XHTML。读 content.opf 找 spine 顺序，逐章 xhtml 转 md。
//! 诚实边界：不处理图片/字体/CSS；只提取文本流。

use std::io::Read;
use std::path::Path;

use zip::ZipArchive;

use crate::html_md;

/// EPUB 文件 -> Markdown。
pub fn epub_to_md(input: &Path) -> Result<String, String> {
    let file = std::fs::File::open(input).map_err(|e| e.to_string())?;
    let mut zip = ZipArchive::new(file).map_err(|e| e.to_string())?;

    // 读 META-INF/container.xml 找 opf 路径
    let opf_path = {
        let mut container = zip.by_name("META-INF/container.xml").map_err(|e| e.to_string())?;
        let mut s = String::new();
        container.read_to_string(&mut s).map_err(|e| e.to_string())?;
        let mut p = String::new();
        if let Some(i) = s.find("full-path=\"") {
            let rest = &s[i + 11..];
            if let Some(j) = rest.find('"') {
                p = rest[..j].to_string();
            }
        }
        p
    };
    if opf_path.is_empty() {
        return Err("找不到 OPF 路径".to_string());
    }

    // 读 OPF
    let opf_str = {
        let mut opf = zip.by_name(&opf_path).map_err(|e| e.to_string())?;
        let mut s = String::new();
        opf.read_to_string(&mut s).map_err(|e| e.to_string())?;
        s
    };

    // 简单解析：找所有 <item href="*.xhtml" ...> 和 <itemref idref="...">
    let mut xhtml_files: Vec<String> = Vec::new();
    // item id -> href
    let mut id_to_href: std::collections::HashMap<String, String> = std::collections::HashMap::new();
    for line in opf_str.split('<') {
        if let Some(rest) = line.strip_prefix("item ") {
            let mut id = String::new();
            let mut href = String::new();
            for attr in rest.split('"') {
                // 简单解析 id="..." href="..."
            }
            // 直接找 id= 和 href=
            if let Some(pos) = rest.find("id=\"") {
                let r = &rest[pos+4..];
                if let Some(e) = r.find('"') { id = r[..e].to_string(); }
            }
            if let Some(pos) = rest.find("href=\"") {
                let r = &rest[pos+6..];
                if let Some(e) = r.find('"') { href = r[..e].to_string(); }
            }
            if !href.is_empty() && (href.ends_with(".xhtml") || href.ends_with(".html") || href.ends_with(".htm")) {
                if !id.is_empty() { id_to_href.insert(id, href); }
            }
        }
    }
    // spine 顺序
    for line in opf_str.split('<') {
        if let Some(rest) = line.strip_prefix("itemref ") {
            if let Some(pos) = rest.find("idref=\"") {
                let r = &rest[pos+7..];
                if let Some(e) = r.find('"') {
                    let idref = &r[..e];
                    if let Some(href) = id_to_href.get(idref) {
                        xhtml_files.push(href.clone());
                    }
                }
            }
        }
    }
    // 如果 spine 没找到，按字母序所有 xhtml
    if xhtml_files.is_empty() {
        for (_, h) in &id_to_href {
            xhtml_files.push(h.clone());
        }
    }

    // OPF 所在目录
    let opf_dir = if let Some(i) = opf_path.rfind('/') { &opf_path[..i+1] } else { "" };

    let mut out = String::new();
    for xf in &xhtml_files {
        let full = format!("{opf_dir}{xf}");
        if let Ok(mut f) = zip.by_name(&full) {
            let mut s = String::new();
            f.read_to_string(&mut s).map_err(|e| e.to_string())?;
            // 复用 html->md
            let md = html_md::html_str_to_md(&s);
            out.push_str(&md);
            out.push_str("\n\n");
        }
    }
    if out.is_empty() {
        Err("EPUB 里没找到 XHTML 内容".to_string())
    } else {
        Ok(out)
    }
}
