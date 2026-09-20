//! DokuWiki <-> Markdown（零外部二进制）。
//!
//! dokuwiki -> md：标题（====== h1 ====== 等号数 6→h1）、行内（**b** //i// ''code''
//!   __u__ 忽略）、链接 [[url|text]] / [[url]]、缩进列表（2 空格/级）、
//!   <code> 块、表格（^ 表头行）。
//! md -> dokuwiki：反向：标题包裹 =、粗体/斜体/代码转 // 与 ''、链接 [[u|t]]、
//!   代码块 <code>、表格。
//!
//! 诚实边界：图片 {{img}} 忽略（不提取二进制）；宏（~~TOCTOU~~ 等）不解析；
//!   转义 \n 表内换行压平；<HTML> 原始块不处理。

use std::path::Path;

fn read_text(path: &Path) -> Result<String, String> {
    let bytes = std::fs::read(path).map_err(|e| format!("读取失败：{e}"))?;
    let text = crate::txt_md::decode_text(&bytes);
    Ok(text.replace("\r\n", "\n").replace('\r', "\n"))
}

/// DokuWiki 行内 -> md 行内。
fn dw_inline(s: &str) -> String {
    let mut out = String::with_capacity(s.len());
    let mut it = s.chars().peekable();
    while let Some(c) = it.next() {
        match c {
            '[' => {
                // [[url|text]] 或 [[url]]
                if it.peek() == Some(&'[') {
                    it.next();
                    let mut inner = String::new();
                    let mut closed = false;
                    while let Some(c2) = it.next() {
                        if c2 == ']' && it.peek() == Some(&']') {
                            it.next();
                            closed = true;
                            break;
                        }
                        inner.push(c2);
                    }
                    if closed {
                        if let Some(bar) = inner.find('|') {
                            let u = &inner[..bar];
                            let t = &inner[bar + 1..];
                            out.push_str(&format!("[{t}]({u})"));
                        } else {
                            out.push_str(&format!("[{inner}]({inner})"));
                        }
                    } else {
                        out.push_str("[[");
                        out.push_str(&inner);
                    }
                } else {
                    out.push('[');
                }
            }
            '*' => {
                // **b**
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
                        out.push_str(&format!("**{}**", inner));
                    } else {
                        out.push_str(&format!("**{inner}"));
                    }
                } else {
                    out.push('*');
                }
            }
            '/' => {
                // //i//
                if it.peek() == Some(&'/') {
                    it.next();
                    let mut inner = String::new();
                    let mut closed = false;
                    while let Some(c2) = it.next() {
                        if c2 == '/' && it.peek() == Some(&'/') {
                            it.next();
                            closed = true;
                            break;
                        }
                        inner.push(c2);
                    }
                    if closed {
                        out.push_str(&format!("*{}*", inner));
                    } else {
                        out.push_str(&format!("//{inner}"));
                    }
                } else {
                    out.push('/');
                }
            }
            '\'' => {
                // ''code''
                if it.peek() == Some(&'\'') {
                    it.next();
                    let mut inner = String::new();
                    let mut closed = false;
                    while let Some(c2) = it.next() {
                        if c2 == '\'' && it.peek() == Some(&'\'') {
                            it.next();
                            closed = true;
                            break;
                        }
                        inner.push(c2);
                    }
                    if closed {
                        out.push_str(&format!("`{}`", inner));
                    } else {
                        out.push_str(&format!("''{inner}"));
                    }
                } else {
                    out.push('\'');
                }
            }
            '_' => {
                // __u__：md 无下划线，保留文本
                if it.peek() == Some(&'_') {
                    it.next();
                    let mut inner = String::new();
                    let mut closed = false;
                    while let Some(c2) = it.next() {
                        if c2 == '_' && it.peek() == Some(&'_') {
                            it.next();
                            closed = true;
                            break;
                        }
                        inner.push(c2);
                    }
                    if closed {
                        out.push_str(&inner);
                    } else {
                        out.push_str(&format!("__{inner}"));
                    }
                } else {
                    out.push('_');
                }
            }
            '{' => {
                // {{img...}}：跳过图片
                if it.peek() == Some(&'{') {
                    it.next();
                    let mut depth = 2usize;
                    let mut closed = false;
                    while let Some(c2) = it.next() {
                        if c2 == '}' {
                            depth -= 1;
                            if depth == 0 {
                                closed = true;
                                break;
                            }
                        }
                    }
                    if !closed {
                        out.push_str("{{");
                    }
                } else {
                    out.push('{');
                }
            }
            _ => out.push(c),
        }
    }
    out
}

/// dokuwiki -> md。
pub fn dokuwiki_to_md(path: &Path) -> Result<String, String> {
    let text = read_text(path)?;
    let lines: Vec<&str> = text.lines().collect();
    let mut out = String::new();
    let mut in_code = false;
    let mut i = 0usize;
    while i < lines.len() {
        let raw = lines[i];
        let l = raw.trim_end();
        let t = l.trim();
        if in_code {
            if t.starts_with("</code>") {
                in_code = false;
                out.push_str("```\n\n");
                i += 1;
                continue;
            }
            out.push_str(t);
            out.push('\n');
            i += 1;
            continue;
        }
        if t.starts_with("<code") {
            in_code = true;
            out.push_str("```\n");
            i += 1;
            continue;
        }
        if t.is_empty() {
            if !out.is_empty() && !out.ends_with("\n\n") {
                out.push('\n');
            }
            i += 1;
            continue;
        }
        // 标题 ====== x ======
        if t.starts_with('=') && t.ends_with('=') {
            let eq = t.chars().take_while(|&c| c == '=').count();
            if eq >= 1 && eq <= 6 {
                let inner = t.trim_matches('=').trim();
                if !inner.is_empty() {
                    let lv = (7 - eq).min(6).max(1);
                    out.push_str(&format!("{} {}\n\n", "#".repeat(lv), dw_inline(inner)));
                }
                i += 1;
                continue;
            }
        }
        // 表格：^ 表头行 / | 数据行
        if (t.starts_with('|') && t.ends_with('|')) || (t.starts_with('^') && t.ends_with('^')) {
            let is_header = t.starts_with('^');
            let body = t.trim_matches(|c| c == '|' || c == '^');
            let cells: Vec<String> = body
                .split(|c: char| c == '|' || c == '^')
                .map(|c| dw_inline(c.trim()).replace('\\', ""))
                .collect();
            // 表格需连续收集——先单行输出，遇到连续行合并
            // 简单实现：把相邻 | 行合并为一个表格
            let mut rows: Vec<(bool, Vec<String>)> = vec![(is_header, cells)];
            let mut j = i + 1;
            while j < lines.len() {
                let nj = lines[j].trim();
                if (nj.starts_with('|') && nj.ends_with('|')) || (nj.starts_with('^') && nj.ends_with('^')) {
                    let h2 = nj.starts_with('^');
                    let b2 = nj.trim_matches(|c| c == '|' || c == '^');
                    let c2: Vec<String> = b2
                        .split(|c: char| c == '|' || c == '^')
                        .map(|c| dw_inline(c.trim()).replace('\\', ""))
                        .collect();
                    rows.push((h2, c2));
                    j += 1;
                } else {
                    break;
                }
            }
            // 找第一个表头行（无则第一行当表头）
            let hdr_idx = rows.iter().position(|(h, _)| *h).unwrap_or(0);
            let ncol = rows.iter().map(|(_, c)| c.len()).max().unwrap_or(0);
            let mut h = String::new();
            h.push('|');
            for c in &rows[hdr_idx].1 {
                h.push_str(&format!(" {} |", c));
            }
            for _ in rows[hdr_idx].1.len()..ncol {
                h.push_str("  |");
            }
            h.push('\n');
            h.push('|');
            for _ in 0..ncol {
                h.push_str(" --- |");
            }
            h.push('\n');
            for (k, r) in rows.iter().enumerate() {
                if k == hdr_idx {
                    continue;
                }
                h.push('|');
                for c in &r.1 {
                    h.push_str(&format!(" {} |", c));
                }
                for _ in r.1.len()..ncol {
                    h.push_str("  |");
                }
                h.push('\n');
            }
            out.push_str(&h);
            out.push('\n');
            i = j;
            continue;
        }
        // 列表：缩进 + * / -
        if t.starts_with("* ") || t.starts_with("- ") {
            let indent = raw.len() - raw.trim_start().len();
            let level = indent / 2;
            let rest = t.trim_start_matches("* ").trim_start_matches("- ");
            out.push_str(&format!("{}- {}\n", "  ".repeat(level), dw_inline(rest)));
            i += 1;
            continue;
        }
        // 普通段落
        out.push_str(&dw_inline(t));
        out.push('\n');
        i += 1;
    }
    let out = out.trim_end().to_string();
    if out.is_empty() {
        return Err("未提取到内容（非 DokuWiki 或空文档）".into());
    }
    Ok(out)
}

/// md -> dokuwiki。
pub fn md_to_dokuwiki(path: &Path) -> Result<String, String> {
    let text = read_text(path)?;
    let mut out = String::new();
    let mut in_code = false;
    for line in text.lines() {
        let t = line.trim_end();
        if t.trim_start().starts_with("```") {
            if !in_code {
                in_code = true;
                out.push_str("<code>\n");
            } else {
                in_code = false;
                out.push_str("</code>\n");
            }
            continue;
        }
        if in_code {
            out.push_str(t);
            out.push('\n');
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
                let rest = md_inline_to_dw(trimmed[lv..].trim());
                let eq = 7 - lv;
                out.push_str(&format!("{} {} {}\n\n", "=".repeat(eq), rest, "=".repeat(eq)));
                continue;
            }
        }
        // 表格
        if trimmed.starts_with('|') && trimmed.ends_with('|') {
            let cells: Vec<&str> = trimmed.trim_matches('|').split('|').map(|c| c.trim()).collect();
            if cells.iter().all(|c| c.starts_with('-')) {
                continue; // 对齐行
            }
            let line_s: String = cells.iter().map(|c| format!("| {}", md_inline_to_dw(c))).collect();
            out.push_str(&format!("{line_s}|\n"));
            continue;
        }
        // 列表（带缩进）
        let indent = t.len() - t.trim_start().len();
        if let Some(rest) = trimmed.strip_prefix("- ").or_else(|| trimmed.strip_prefix("* ")) {
            let dw_indent = "  ".repeat(indent / 2);
            out.push_str(&format!("{dw_indent}* {}\n", md_inline_to_dw(rest)));
            continue;
        }
        if let Some(rest) = trimmed.strip_prefix("1. ") {
            let dw_indent = "  ".repeat(indent / 2);
            out.push_str(&format!("{dw_indent}- {}\n", md_inline_to_dw(rest)));
            continue;
        }
        out.push_str(&md_inline_to_dw(trimmed));
        out.push('\n');
    }
    let out = out.trim_end().to_string();
    if out.is_empty() {
        return Err("空文档".into());
    }
    Ok(out)
}

/// md 行内 -> dokuwiki 行内。
fn md_inline_to_dw(s: &str) -> String {
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
                out.push_str(&format!("''{inner}''"));
            }
            '*' => {
                // ** 粗
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
                        out.push_str(&format!("**{}**", inner));
                    } else {
                        out.push_str(&format!("**{inner}"));
                    }
                } else {
                    // * 斜
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
                        out.push_str(&format!("//{inner}//"));
                    } else {
                        out.push('*');
                        out.push_str(&inner);
                    }
                }
            }
            '[' => {
                // [t](u)
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
                    out.push_str(&format!("[[{u}|{t}]]"));
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
