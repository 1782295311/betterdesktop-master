//! RST（reStructuredText）<-> Markdown（零外部二进制）。
//!
//! rst -> md：标题（下划线/上划线装饰）、段落、无序/有序列表（#. 保序）、
//!   行内（**粗**/*斜*/``code``/`链接 <url>`_）、`::` 指令代码块、
//!   simple table（==== 段式）。
//! md -> rst：标题转下划线装饰（#→=、##→-、###→~、####→^）、行内标记、
//!   链接转 `` `t <u>`_ ``、代码块转 `::` + 缩进、列表。
//!
//! 诚实边界：grid table（+---+）降级为原样文本；RST 指令（.. note:: 等）不解析
//!   （原样保留）；上标/下标/替换/脚注不处理；缩进列表仅 2 空格一档。

use std::path::Path;

fn read_text(path: &Path) -> Result<String, String> {
    let bytes = std::fs::read(path).map_err(|e| format!("读取失败：{e}"))?;
    let text = crate::txt_md::decode_text(&bytes);
    Ok(text.replace("\r\n", "\n").replace('\r', "\n"))
}

fn deco_char(c: char) -> Option<usize> {
    // 装饰字符 -> 标题级别
    match c {
        '=' => Some(1),
        '-' => Some(2),
        '~' => Some(3),
        '^' => Some(4),
        '"' => Some(5),
        '\'' => Some(6),
        _ => None,
    }
}

fn is_deco_line(s: &str) -> Option<char> {
    let t = s.trim();
    if t.is_empty() {
        return None;
    }
    let c = t.chars().next().unwrap();
    if !t.chars().all(|x| x == c) || t.chars().count() < 2 {
        return None;
    }
    // 纯装饰（数字/字母不是装饰）
    if c.is_alphanumeric() {
        return None;
    }
    Some(c)
}

/// 行内 RST 标记剥离：**b** *i* ``c`` `t <url>`_ 独立 URL_。
fn strip_inline(s: &str) -> String {
    let mut out = String::with_capacity(s.len());
    let mut it = s.chars().peekable();
    while let Some(c) = it.next() {
        match c {
            '*' => {
                // ** 粗 / * 斜
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
                        out.push_str(&format!("**{}**", inner.trim()));
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
                        out.push_str(&format!("*{}*", inner.trim()));
                    } else {
                        out.push('*');
                        out.push_str(&inner);
                    }
                }
            }
            '`' => {
                // ``code`` 或 `link <url>`_
                if it.peek() == Some(&'`') {
                    it.next();
                    let mut inner = String::new();
                    let mut closed = false;
                    while let Some(c2) = it.next() {
                        if c2 == '`' && it.peek() == Some(&'`') {
                            it.next();
                            closed = true;
                            break;
                        }
                        inner.push(c2);
                    }
                    if closed {
                        out.push_str(&format!("`{}`", inner.trim()));
                    } else {
                        out.push_str(&format!("``{inner}"));
                    }
                } else {
                    // `text <url>`_
                    let mut inner = String::new();
                    let mut closed = false;
                    while let Some(c2) = it.next() {
                        if c2 == '`' {
                            closed = true;
                            break;
                        }
                        inner.push(c2);
                    }
                    if closed {
                        // 期望 _ 结尾（URL 已内嵌或脚注）
                        if it.peek() == Some(&'_') {
                            it.next();
                        }
                        // text <url> 或 text_
                        if let Some(pos) = inner.find('<') {
                            let t = inner[..pos].trim();
                            let u = inner[pos + 1..].trim_end_matches('>').trim();
                            out.push_str(&format!("[{t}]({u})"));
                        } else if inner.starts_with("http") || inner.starts_with("https") {
                            out.push_str(&format!("[{inner}]({inner})"));
                        } else {
                            // 可能是名称引用：跳过
                            let _ = inner;
                        }
                    } else {
                        out.push('`');
                        out.push_str(&inner);
                    }
                }
            }
            _ => out.push(c),
        }
    }
    out
}

/// 解析 simple table（===== 段分隔，数据行空格对齐）。
fn parse_simple_table(lines: &[&str], i: usize) -> (usize, String) {
    // lines[i] 是起始 ==== 行；收集到空行或非表格行结束
    let mut j = i + 1;
    let mut rows: Vec<Vec<String>> = Vec::new();
    while j < lines.len() {
        let t = lines[j].trim();
        if t.is_empty() {
            break;
        }
        if t.chars().all(|c| c == '=' || c == '-' || c == ' ') {
            // 分隔行：忽略，继续
            j += 1;
            continue;
        }
        let cols = split_cols(t);
        if cols.len() < 2 {
            // 单列：表格已开始则视为结束；否则当一行
            if rows.is_empty() {
                rows.push(cols);
                j += 1;
                continue;
            }
            break;
        }
        rows.push(cols);
        j += 1;
    }
    if rows.is_empty() {
        return (j - i, String::new());
    }
    let esc = |x: &str| x.replace('|', "\\|").trim().to_string();
    let ncol = rows.iter().map(|r| r.len()).max().unwrap_or(0);
    let mut out = String::new();
    out.push('|');
    for c in &rows[0] {
        out.push_str(&format!(" {} |", esc(c)));
    }
    for _ in rows[0].len()..ncol {
        out.push_str("  |");
    }
    out.push('\n');
    out.push('|');
    for _ in 0..ncol {
        out.push_str(" --- |");
    }
    out.push('\n');
    for r in &rows[1..] {
        out.push('|');
        for c in r {
            out.push_str(&format!(" {} |", esc(c)));
        }
        for _ in r.len()..ncol {
            out.push_str("  |");
        }
        out.push('\n');
    }
    (j - i, out)
}

fn split_cols(l: &str) -> Vec<String> {
    // 按 2+ 空格切列（trim 后）
    let mut cols = Vec::new();
    let mut cur = String::new();
    let mut spaces = 0usize;
    for c in l.chars() {
        if c == ' ' {
            spaces += 1;
            if spaces >= 2 && !cur.is_empty() {
                cols.push(cur.trim().to_string());
                cur.clear();
                spaces = 0;
            }
        } else {
            cur.push(c);
            spaces = 0;
        }
    }
    if !cur.trim().is_empty() {
        cols.push(cur.trim().to_string());
    }
    cols
}

/// rst -> md。
pub fn rst_to_md(path: &Path) -> Result<String, String> {
    let text = read_text(path)?;
    let lines: Vec<&str> = text.lines().collect();
    let n = lines.len();
    // 第一遍：标记装饰行与标题行
    let mut deco = vec![false; n];
    let mut title_lv = vec![0usize; n];
    for (i, l) in lines.iter().enumerate() {
        let t = l.trim();
        if t.is_empty() {
            continue;
        }
        if let Some(c) = is_deco_line(t) {
            let lv = deco_char(c).unwrap_or(0);
            if lv == 0 {
                continue;
            }
            // 上划线样式：当前装饰 + 文本 + 同字符装饰
            if i + 2 < n && !lines[i + 1].trim().is_empty() {
                if let Some(c2) = is_deco_line(lines[i + 2]) {
                    if c2 == c {
                        deco[i] = true;
                        deco[i + 2] = true;
                        title_lv[i + 1] = lv;
                        continue;
                    }
                }
            }
            // 下划线样式：前一行文本
            if i > 0 && !lines[i - 1].trim().is_empty() && !deco[i - 1] {
                deco[i] = true;
                title_lv[i - 1] = lv;
            }
        }
    }
    // 第二遍：输出
    let mut out = String::new();
    let mut i = 0usize;
    while i < n {
        if deco[i] {
            i += 1;
            continue;
        }
        if title_lv[i] > 0 {
            let lv = title_lv[i].min(6);
            out.push_str(&format!("{} {}\n\n", "#".repeat(lv), strip_inline(lines[i].trim())));
            i += 1;
            continue;
        }
        let l = lines[i].trim_end();
        let t = l.trim();
        if t.is_empty() {
            // 空行：可能分隔代码块/段落
            if !out.is_empty() && !out.ends_with("\n\n") {
                out.push('\n');
            }
            i += 1;
            continue;
        }
        // simple table 起始行
        if l.chars().all(|c| c == '=' || c == ' ') && l.contains('=') && l.matches('=').count() >= 3 {
            let (consumed, table) = parse_simple_table(&lines, i);
            if !table.is_empty() {
                out.push_str(&table);
                out.push('\n');
                i += consumed;
                continue;
            }
            i += 1;
            continue;
        }
        // 代码块：:: 指令
        if t == "::" || t.ends_with("::") {
            let prefix = t.trim_end_matches("::").trim();
            if !prefix.is_empty() {
                out.push_str(&strip_inline(prefix));
                out.push('\n');
            }
            out.push_str("```\n");
            i += 1;
            while i < n && (lines[i].starts_with(' ') || lines[i].trim().is_empty()) {
                if !lines[i].trim().is_empty() {
                    out.push_str(lines[i].trim_start_matches(|c: char| c == ' ').trim_end());
                    out.push('\n');
                }
                i += 1;
            }
            out.push_str("```\n\n");
            continue;
        }
        // 列表
        if let Some(rest) = t.strip_prefix("* ")
            .or_else(|| t.strip_prefix("- "))
            .or_else(|| t.strip_prefix("+ "))
        {
            let indent = l.len() - l.trim_start().len();
            let level = indent / 2;
            let prefix = "  ".repeat(level);
            out.push_str(&format!("{prefix}- {}\n", strip_inline(rest)));
            i += 1;
            continue;
        }
        // 有序列表 #. 1.
        if (t.starts_with("#. ") || t.starts_with("#.  ") || t.starts_with("#.")) {
            let rest = t.trim_start_matches('#').trim_start_matches('.').trim();
            out.push_str(&format!("1. {}\n", strip_inline(rest)));
            i += 1;
            continue;
        }
        if t.len() > 2 {
            let mut digits = 0usize;
            for c in t.chars() {
                if c.is_ascii_digit() {
                    digits += 1;
                } else {
                    break;
                }
            }
            if digits > 0 && t[digits..].starts_with(". ") {
                let rest = &t[digits + 2..];
                out.push_str(&format!("1. {}\n", strip_inline(rest)));
                i += 1;
                continue;
            }
        }
        // 普通段落
        out.push_str(&strip_inline(t));
        out.push('\n');
        i += 1;
        // 合并连续非空行？RST 段落可跨行；简单处理：不合并，行间保持
    }
    let out = out.trim_end().to_string();
    if out.is_empty() {
        return Err("未提取到内容（非 RST 或空文档）".into());
    }
    Ok(out)
}

/// md -> rst：标题下划线 + 行内/链接/代码块。
pub fn md_to_rst(path: &Path) -> Result<String, String> {
    let text = read_text(path)?;
    let deco = |lv: usize| match lv {
        1 => "=",
        2 => "-",
        3 => "~",
        4 => "^",
        5 => "\"",
        _ => "'",
    };
    let mut out = String::new();
    let mut in_code = false;
    for line in text.lines() {
        let t = line.trim_end();
        if t.trim_start().starts_with("```") {
            if !in_code {
                in_code = true;
                out.push_str("::\n\n");
            } else {
                in_code = false;
                out.push('\n');
            }
            continue;
        }
        if in_code {
            out.push_str(&format!("  {}\n", t));
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
                let rest = trimmed[lv..].trim();
                let d = deco(lv);
                let uline = d.repeat(rest.chars().count().max(2));
                out.push_str(rest);
                out.push('\n');
                out.push_str(&uline);
                out.push_str("\n\n");
                continue;
            }
        }
        // 代码行内/粗斜体/链接
        let conv = convert_inline_to_rst(trimmed);
        // 列表
        if let Some(rest) = trimmed.strip_prefix("- ")
            .or_else(|| trimmed.strip_prefix("* "))
            .or_else(|| trimmed.strip_prefix("+ "))
        {
            out.push_str(&format!("* {}\n", convert_inline_to_rst(rest)));
            continue;
        }
        if let Some(rest) = trimmed.strip_prefix("1. ") {
            out.push_str(&format!("#. {}\n", convert_inline_to_rst(rest)));
            continue;
        }
        out.push_str(&conv);
        out.push('\n');
    }
    let out = out.trim_end().to_string();
    if out.is_empty() {
        return Err("空文档".into());
    }
    Ok(out)
}

/// md 行内 -> rst 行内：`code` -> ``code``、[t](u) -> `t <u>`_。
fn convert_inline_to_rst(s: &str) -> String {
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
                out.push_str(&format!("``{}``", inner));
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
                    out.push_str(&format!("`{t} <{u}>`_"));
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
