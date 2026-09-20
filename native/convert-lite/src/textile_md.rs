//! Textile <-> Markdown（零外部二进制）。
//!
//! textile -> md：标题 h1.-h6.、行内（*b* 粗 / _i_ 斜 / @code@ 代码 / +u+ 下划线 /
//!   -s- 删除线）、链接 "text":url、列表（* 无序 / # 有序，缩进层级）、
//!   表格（|_. 表头 / | 数据）。
//! md -> textile：反向：h1.、*x* / _x_ / @x@、"t":u、列表、表格。
//!
//! 诚实边界：脚注 [1]、注记 fn1.、定义列表 dl. 不解析；bc. 代码块不处理
//!   （降级为普通段落）；表格单元格对齐符 < > = 忽略；嵌套列表 2 空格一档。

use std::path::Path;

fn read_text(path: &Path) -> Result<String, String> {
    let bytes = std::fs::read(path).map_err(|e| format!("读取失败：{e}"))?;
    let text = crate::txt_md::decode_text(&bytes);
    Ok(text.replace("\r\n", "\n").replace('\r', "\n"))
}

/// Textile 行内 -> md 行内。处理 *b*（非行首空格后）/ _i_ / @code@ / +u+ / -s- / "t":u。
fn tx_inline(s: &str) -> String {
    let mut out = String::with_capacity(s.len());
    let mut it = s.chars().peekable();
    while let Some(c) = it.next() {
        match c {
            '*' => {
                // *b*：Textile 粗体（与列表区分：列表 * 后跟空格）
                if it.peek() == Some(&' ') {
                    out.push('*');
                    continue;
                }
                let mut inner = String::new();
                let mut closed = false;
                while let Some(c2) = it.next() {
                    if c2 == '*' {
                        closed = true;
                        break;
                    }
                    inner.push(c2);
                }
                if closed {
                    out.push_str(&format!("**{}**", inner));
                } else {
                    out.push('*');
                    out.push_str(&inner);
                }
            }
            '_' => {
                // _i_：斜体（非下划线单词连接符场景简单处理）
                let mut inner = String::new();
                let mut closed = false;
                while let Some(c2) = it.next() {
                    if c2 == '_' {
                        closed = true;
                        break;
                    }
                    inner.push(c2);
                }
                if closed && !inner.is_empty() {
                    out.push_str(&format!("*{}*", inner));
                } else {
                    out.push('_');
                    out.push_str(&inner);
                    if closed {
                        out.push('_');
                    }
                }
            }
            '@' => {
                let mut inner = String::new();
                let mut closed = false;
                while let Some(c2) = it.next() {
                    if c2 == '@' {
                        closed = true;
                        break;
                    }
                    inner.push(c2);
                }
                if closed {
                    out.push_str(&format!("`{}`", inner));
                } else {
                    out.push('@');
                    out.push_str(&inner);
                }
            }
            '+' => {
                // +u+：下划线 -> 原样文本
                let mut inner = String::new();
                let mut closed = false;
                while let Some(c2) = it.next() {
                    if c2 == '+' {
                        closed = true;
                        break;
                    }
                    inner.push(c2);
                }
                if closed {
                    out.push_str(&inner);
                } else {
                    out.push('+');
                    out.push_str(&inner);
                }
            }
            '-' => {
                // -s-：删除线
                let mut inner = String::new();
                let mut closed = false;
                while let Some(c2) = it.next() {
                    if c2 == '-' {
                        closed = true;
                        break;
                    }
                    inner.push(c2);
                }
                if closed {
                    out.push_str(&format!("~~{}~~", inner));
                } else {
                    out.push('-');
                    out.push_str(&inner);
                }
            }
            '"' => {
                // "text":url
                let mut t = String::new();
                let mut got_colon = false;
                let mut u = String::new();
                let mut closed = false;
                while let Some(c2) = it.next() {
                    if !got_colon {
                        if c2 == '"' {
                            if it.peek() == Some(&':') {
                                it.next();
                                got_colon = true;
                            } else {
                                t.push('"');
                                break;
                            }
                        } else {
                            t.push(c2);
                        }
                    } else if c2 == ' ' || c2 == '\t' || c2 == '\n' {
                        closed = true;
                        break;
                    } else {
                        u.push(c2);
                    }
                }
                if closed || (got_colon && !u.is_empty()) {
                    out.push_str(&format!("[{t}]({u})"));
                } else {
                    out.push('"');
                    out.push_str(&t);
                    out.push('"');
                    if got_colon {
                        out.push(':');
                        out.push_str(&u);
                    }
                }
            }
            _ => out.push(c),
        }
    }
    out
}

/// textile -> md。
pub fn textile_to_md(path: &Path) -> Result<String, String> {
    let text = read_text(path)?;
    let lines: Vec<&str> = text.lines().collect();
    let mut out = String::new();
    let mut i = 0usize;
    while i < lines.len() {
        let raw = lines[i];
        let t = raw.trim_end();
        let trimmed = t.trim();
        if trimmed.is_empty() {
            if !out.is_empty() && !out.ends_with("\n\n") {
                out.push('\n');
            }
            i += 1;
            continue;
        }
        // 标题 h1.-h6.
        if trimmed.len() >= 3 && trimmed.starts_with('h') {
            let b = trimmed.as_bytes();
            if (b[1].is_ascii_digit()) && (b[2] == b'.' || (b[2] == b' ' && b.len() > 3 && b[3] == b'.')) {
                let lv = (b[1] - b'0') as usize;
                let rest = if b[2] == b'.' {
                    &trimmed[3..]
                } else {
                    &trimmed[4..]
                };
                if lv >= 1 && lv <= 6 {
                    out.push_str(&format!("{} {}\n\n", "#".repeat(lv), tx_inline(rest.trim())));
                    i += 1;
                    continue;
                }
            }
        }
        // 表格
        if trimmed.starts_with('|') && trimmed.ends_with('|') {
            let body = trimmed.trim_matches('|');
            let mut rows: Vec<Vec<String>> = Vec::new();
            let mut is_hdr: Vec<bool> = Vec::new();
            let mut j = i;
            while j < lines.len() {
                let lj = lines[j].trim();
                if lj.starts_with('|') && lj.ends_with('|') {
                    let bj = lj.trim_matches('|');
                    let mut hdr = false;
                    let cells: Vec<String> = bj
                        .split('|')
                        .map(|c| {
                            let c = c.trim();
                            if let Some(rest) = c.strip_prefix("_.") {
                                hdr = true;
                                tx_inline(rest.trim())
                            } else {
                                tx_inline(c)
                            }
                        })
                        .collect();
                    rows.push(cells);
                    is_hdr.push(hdr);
                    j += 1;
                } else {
                    break;
                }
            }
            if !rows.is_empty() {
                let ncol = rows.iter().map(|r| r.len()).max().unwrap_or(0);
                let mut tbl = String::new();
                tbl.push('|');
                for c in &rows[0] {
                    tbl.push_str(&format!(" {} |", c));
                }
                tbl.push('\n');
                tbl.push('|');
                for _ in 0..ncol {
                    tbl.push_str(" --- |");
                }
                tbl.push('\n');
                for r in &rows[1..] {
                    tbl.push('|');
                    for c in r {
                        tbl.push_str(&format!(" {} |", c));
                    }
                    tbl.push('\n');
                }
                out.push_str(&tbl);
                out.push('\n');
                i = j;
                continue;
            }
        }
        // 列表 * / #
        let indent = raw.len() - raw.trim_start().len();
        let level = indent / 2;
        if let Some(rest) = trimmed.strip_prefix("* ") {
            out.push_str(&format!("{}- {}\n", "  ".repeat(level), tx_inline(rest)));
            i += 1;
            continue;
        }
        if let Some(rest) = trimmed.strip_prefix("# ") {
            out.push_str(&format!("{}1. {}\n", "  ".repeat(level), tx_inline(rest)));
            i += 1;
            continue;
        }
        // 普通段落
        out.push_str(&tx_inline(trimmed));
        out.push('\n');
        i += 1;
    }
    let out = out.trim_end().to_string();
    if out.is_empty() {
        return Err("未提取到内容（非 Textile 或空文档）".into());
    }
    Ok(out)
}

/// md -> textile。
pub fn md_to_textile(path: &Path) -> Result<String, String> {
    let text = read_text(path)?;
    let mut out = String::new();
    let mut in_code = false;
    for line in text.lines() {
        let t = line.trim_end();
        if t.trim_start().starts_with("```") {
            if !in_code {
                in_code = true;
                out.push_str("bc. 代码块（Textile 不支持 md 代码块，降级为引用）\n\n");
            } else {
                in_code = false;
                out.push('\n');
            }
            continue;
        }
        if in_code {
            out.push_str(&format!(">{}\n", t));
            continue;
        }
        let trimmed = t.trim();
        if trimmed.is_empty() {
            out.push('\n');
            continue;
        }
        // 标题
        if trimmed.starts_with('#') {
            let lv = trimmed.chars().take_while(|&c| c == '#').count();
            if lv <= 6 {
                let rest = md_inline_to_tx(trimmed[lv..].trim());
                out.push_str(&format!("h{lv}. {rest}\n\n"));
                continue;
            }
        }
        // 表格
        if trimmed.starts_with('|') && trimmed.ends_with('|') {
            let cells: Vec<&str> = trimmed.trim_matches('|').split('|').map(|c| c.trim()).collect();
            if cells.iter().all(|c| c.starts_with('-')) {
                continue;
            }
            out.push('|');
            for c in &cells {
                out.push_str(&format!(" {} |", md_inline_to_tx(c)));
            }
            out.push('\n');
            continue;
        }
        // 列表
        let indent = t.len() - t.trim_start().len();
        let lv = indent / 2;
        if let Some(rest) = trimmed.strip_prefix("- ").or_else(|| trimmed.strip_prefix("* ")) {
            out.push_str(&format!("{}* {}\n", "  ".repeat(lv), md_inline_to_tx(rest)));
            continue;
        }
        if let Some(rest) = trimmed.strip_prefix("1. ") {
            out.push_str(&format!("{}# {}\n", "  ".repeat(lv), md_inline_to_tx(rest)));
            continue;
        }
        out.push_str(&md_inline_to_tx(trimmed));
        out.push('\n');
    }
    let out = out.trim_end().to_string();
    if out.is_empty() {
        return Err("空文档".into());
    }
    Ok(out)
}

/// md 行内 -> textile 行内。
fn md_inline_to_tx(s: &str) -> String {
    let mut out = String::with_capacity(s.len());
    let mut it = s.chars().peekable();
    while let Some(c) = it.next() {
        match c {
            '`' => {
                let mut inner = String::new();
                for c2 in it.by_ref() {
                    if c2 == '`' {
                        break;
                    }
                    inner.push(c2);
                }
                out.push_str(&format!("@{inner}@"));
            }
            '*' => {
                if it.peek() == Some(&'*') {
                    it.next();
                    let mut inner = String::new();
                    let mut closed = false;
                    while let Some(c2) = it.next() {
                        if c2 == '*' && it.peek() == Some(&'*') {
                            it.next();
                            closed = true;
                            break;
                        }
                        inner.push(c2);
                    }
                    if closed {
                        out.push_str(&format!("*{}*", inner));
                    } else {
                        out.push_str(&format!("**{inner}"));
                    }
                } else {
                    let mut inner = String::new();
                    let mut closed = false;
                    while let Some(c2) = it.next() {
                        if c2 == '*' {
                            closed = true;
                            break;
                        }
                        inner.push(c2);
                    }
                    if closed {
                        out.push_str(&format!("_{}_", inner));
                    } else {
                        out.push('*');
                        out.push_str(&inner);
                    }
                }
            }
            '[' => {
                let mut t = String::new();
                let mut got_paren = false;
                let mut u = String::new();
                let mut closed = false;
                while let Some(c2) = it.next() {
                    if !got_paren {
                        if c2 == ']' {
                            if it.next() == Some('(') {
                                got_paren = true;
                            } else {
                                t.push(']');
                                break;
                            }
                        } else {
                            t.push(c2);
                        }
                    } else if c2 == ')' {
                        closed = true;
                        break;
                    } else {
                        u.push(c2);
                    }
                }
                if closed {
                    out.push_str(&format!("\"{t}\":{u}"));
                } else {
                    out.push('[');
                    out.push_str(&t);
                    out.push(']');
                    if got_paren {
                        out.push('(');
                        out.push_str(&u);
                        out.push(')');
                    }
                }
            }
            _ => out.push(c),
        }
    }
    out
}
