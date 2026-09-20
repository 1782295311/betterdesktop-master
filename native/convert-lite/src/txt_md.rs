//! TXT <-> Markdown（零外部二进制）。
//!
//! txt -> md：编码检测（UTF-8/UTF-16 BOM、UTF-8 严格校验、GBK 回退）后
//!   归一化为 UTF-8，按原样输出（txt 是 md 子集，空行即段落）。
//! md -> txt：轻量剥离常用 md 标记（标题/粗斜体/行内代码/链接/列表/引用/
//!   表格转 tab/分隔线/图片取 alt），输出纯文本。
//!
//! 诚实边界：md->txt 是轻量剥离——代码块保留原样（不重新缩进）、嵌套列表
//!   降级为行文本、HTML 片段不处理；txt->md 不做智能分段（保留原文换行）。

use std::path::Path;

fn utf16le_to_string(b: &[u8]) -> String {
    let units: Vec<u16> = b
        .chunks_exact(2)
        .map(|c| u16::from_le_bytes([c[0], c[1]]))
        .collect();
    String::from_utf16_lossy(&units)
}

fn utf16be_to_string(b: &[u8]) -> String {
    let units: Vec<u16> = b
        .chunks_exact(2)
        .map(|c| u16::from_be_bytes([c[0], c[1]]))
        .collect();
    String::from_utf16_lossy(&units)
}

/// 编码检测并解码为 UTF-8 String。
pub fn decode_text(bytes: &[u8]) -> String {
    if bytes.starts_with(&[0xEF, 0xBB, 0xBF]) {
        String::from_utf8_lossy(&bytes[3..]).into_owned()
    } else if bytes.starts_with(&[0xFF, 0xFE]) {
        utf16le_to_string(&bytes[2..])
    } else if bytes.starts_with(&[0xFE, 0xFF]) {
        utf16be_to_string(&bytes[2..])
    } else {
        match std::str::from_utf8(bytes) {
            Ok(s) => s.to_string(),
            Err(_) => {
                let (s, _, _) = encoding_rs::GBK.decode(bytes);
                s.into_owned()
            }
        }
    }
}

/// txt -> md：解码（UTF-8/UTF-16/GBK）后归一化行尾，原样输出。
pub fn txt_to_md(path: &Path) -> Result<String, String> {
    let bytes = std::fs::read(path).map_err(|e| format!("读取失败：{e}"))?;
    let mut text = decode_text(&bytes);
    // CRLF / CR -> LF 归一化
    text = text.replace("\r\n", "\n").replace('\r', "\n");
    if text.trim().is_empty() {
        return Err("文件为空".into());
    }
    Ok(text.trim_end().to_string())
}

/// 去掉行内标记：**b**/*i*/`c`/[t](u)/![a](u)。按 Unicode 字符迭代，
/// 非 ASCII 原样保留（避免按字节处理导致 UTF-8 双重编码）。
fn strip_inline(s: &str) -> String {
    let mut out = String::with_capacity(s.len());
    let mut it = s.chars().peekable();
    while let Some(c) = it.next() {
        match c {
            '`' => {
                // 行内代码：到下一个反引号
                let mut rest = String::new();
                for c2 in it.by_ref() {
                    if c2 == '`' {
                        break;
                    }
                    rest.push(c2);
                }
                out.push_str(&rest);
            }
            '*' | '_' => {
                // 双标记 ** __
                if it.peek() == Some(&c) {
                    it.next();
                    let mut rest = String::new();
                    let mut closed = false;
                    while let Some(c2) = it.next() {
                        if c2 == c && it.peek() == Some(&c) {
                            it.next();
                            closed = true;
                            break;
                        }
                        rest.push(c2);
                    }
                    if closed {
                        out.push_str(&rest);
                    } else {
                        out.push_str(&format!("{c}{c}{rest}"));
                    }
                    continue;
                }
                // 单标记 * _
                let mut rest = String::new();
                let mut closed = false;
                while let Some(c2) = it.next() {
                    if c2 == c {
                        closed = true;
                        break;
                    }
                    rest.push(c2);
                }
                if closed {
                    out.push_str(&rest);
                } else {
                    out.push(c);
                    out.push_str(&rest);
                }
            }
            '[' => {
                // [text](url) 或 ![alt](url)：找 ](
                let mut rest = String::new();
                let mut url = String::new();
                let mut got_paren = false;
                let mut closed = false;
                while let Some(c2) = it.next() {
                    if !got_paren {
                        if c2 == ']' {
                            match it.next() {
                                Some('(') => got_paren = true,
                                _ => {
                                    rest.push(']');
                                    break;
                                }
                            }
                        } else {
                            rest.push(c2);
                        }
                    } else if c2 == ')' {
                        closed = true;
                        break;
                    } else {
                        url.push(c2);
                    }
                }
                if closed {
                    out.push_str(&rest);
                } else {
                    out.push('[');
                    out.push_str(&rest);
                    out.push(']');
                    if got_paren {
                        out.push('(');
                        out.push_str(&url);
                        out.push(')');
                    }
                }
            }
            _ => out.push(c),
        }
    }
    out
}

/// md -> txt：轻量剥离标记。
pub fn md_to_txt(path: &Path) -> Result<String, String> {
    let bytes = std::fs::read(path).map_err(|e| format!("读取失败：{e}"))?;
    let mut text = decode_text(&bytes);
    text = text.replace("\r\n", "\n").replace('\r', "\n");

    let mut out = String::new();
    let mut in_code = false;
    for raw_line in text.lines() {
        let line = raw_line.trim_end();
        if line.trim_start().starts_with("```") {
            in_code = !in_code;
            out.push('\n');
            continue;
        }
        if in_code {
            out.push_str(line);
            out.push('\n');
            continue;
        }
        let trimmed = line.trim_start();
        // 标题
        if let Some(rest) = trimmed.strip_prefix('#') {
            let rest = rest.trim_start_matches('#').trim();
            out.push_str(&strip_inline(rest));
            out.push('\n');
            continue;
        }
        // 分隔线
        if trimmed == "---" || trimmed == "***" || trimmed == "___" {
            out.push('\n');
            continue;
        }
        // 引用
        if let Some(rest) = trimmed.strip_prefix('>') {
            out.push_str(&strip_inline(rest.trim_start()));
            out.push('\n');
            continue;
        }
        // 列表
        if let Some(rest) = trimmed.strip_prefix("- ")
            .or_else(|| trimmed.strip_prefix("* "))
            .or_else(|| trimmed.strip_prefix("+ "))
        {
            out.push_str(&strip_inline(rest));
            out.push('\n');
            continue;
        }
        // 有序列表
        let mut handled = false;
        let mut digits = String::new();
        for (idx, ch) in trimmed.char_indices() {
            if ch.is_ascii_digit() {
                digits.push(ch);
                continue;
            }
            if (ch == '.' || ch == ')') && !digits.is_empty() {
                if trimmed[idx + 1..].starts_with(' ') {
                    out.push_str(&strip_inline(trimmed[idx + 1..].trim_start()));
                    out.push('\n');
                    handled = true;
                }
                break;
            }
            break;
        }
        if handled {
            continue;
        }
        if trimmed.starts_with('|') && trimmed.ends_with('|') {
            // 表格行 -> tab 分隔（去掉对齐行）
            if trimmed.contains("---") && trimmed.matches('|').count() >= 3 && !trimmed.chars().any(|c| c.is_alphanumeric()) {
                out.push('\n');
                continue;
            }
            let cells: Vec<String> = trimmed
                .trim_matches('|')
                .split('|')
                .map(|c| strip_inline(c.trim()))
                .collect();
            out.push_str(&cells.join("\t"));
            out.push('\n');
            continue;
        }
        out.push_str(&strip_inline(line));
        out.push('\n');
    }
    let mut out = out.trim_end().to_string();
    while out.contains("\n\n\n") {
        out = out.replace("\n\n\n", "\n\n");
    }
    if out.is_empty() {
        return Err("剥离后为空".into());
    }
    Ok(out)
}

/// csv -> md 表格（复用 csv crate 读取）。
pub fn csv_to_md(path: &Path) -> Result<String, String> {
    let mut rdr = csv::Reader::from_path(path).map_err(|e| format!("CSV 解析失败：{e}"))?;
    let headers: Vec<String> = rdr
        .headers()
        .map_err(|e| format!("CSV 表头失败：{e}"))?
        .iter()
        .map(|h| h.to_string())
        .collect();
    if headers.is_empty() {
        return Err("CSV 无表头".into());
    }
    let esc = |s: &str| s.replace('|', "\\|").replace('\n', " ").replace('\r', " ");
    let mut out = String::new();
    out.push('|');
    for h in &headers {
        out.push_str(&format!(" {} |", esc(h)));
    }
    out.push('\n');
    out.push('|');
    for _ in &headers {
        out.push_str(" --- |");
    }
    out.push('\n');
    let mut rows = 0usize;
    for rec in rdr.records() {
        let rec = rec.map_err(|e| format!("CSV 记录失败：{e}"))?;
        out.push('|');
        for i in 0..headers.len() {
            let v = rec.get(i).unwrap_or("");
            out.push_str(&format!(" {} |", esc(v)));
        }
        out.push('\n');
        rows += 1;
    }
    if rows == 0 {
        return Err("CSV 无数据行".into());
    }
    Ok(out.trim_end().to_string())
}
