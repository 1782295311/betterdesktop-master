//! Org-mode -> Markdown（替代 Pandoc 的 org 输入）。
//!
//! 行级：标题(* 1-6 级) / 无序列表(- +) / 有序列表(1. 1)) / 定义列表(- t :: d) /
//!       表格(|...|) / 代码块(#+BEGIN_SRC) / 引用(#+BEGIN_QUOTE) / 示例(#+BEGIN_EXAMPLE) /
//!       水平线(-----) / 属性抽屉(:PROPERTIES:) / 元数据(#+TITLE:) / 注释(# )。
//! 行内：*粗体* / /斜体/ / _下划线_ / =代码= / ~代码~ / +删除+ / [[url][text]] 链接。
//!
//! 诚实边界：不展开 #+INCLUDE；src 块带参数时只取 lang；时间戳/标签保留原文；
//!           嵌套列表用缩进近似，不解析编号序列。

use std::path::Path;

/// 行内转换（统一 byte 索引，中文安全）。
fn inline(s: &str) -> String {
    let b = s.as_bytes();
    let mut out = String::new();
    let mut i = 0usize;
    while i < b.len() {
        let rest = &s[i..];
        let c = rest.chars().next().unwrap();
        // 链接 [[url][text]] / [[url]]
        if c == '[' && i + 1 < b.len() && b[i + 1] == b'[' {
            if let Some(j) = rest[2..].find("]]") {
                let inner = &rest[2..2 + j];
                let (url, text) = match inner.find("][") {
                    Some(p) => (&inner[..p], &inner[p + 2..]),
                    None => (inner, inner),
                };
                out.push_str(&format!("[{}]({})", inline(text), inline(url)));
                i += 2 + j + 2;
                continue;
            }
        }
        // 成对标记：* / _ = ~ +
        if matches!(c, '*' | '/' | '_' | '=' | '~' | '+') {
            let prev_ok = i == 0
                || s[..i]
                    .chars()
                    .next_back()
                    .map(|p| p.is_whitespace() || (!p.is_alphanumeric() && p != '_'))
                    .unwrap_or(true);
            if prev_ok {
                let mut j = i + c.len_utf8();
                let mut found = false;
                while j < b.len() {
                    let ch = s[j..].chars().next().unwrap();
                    if ch == c {
                        let after_ok = s[j + ch.len_utf8()..]
                            .chars()
                            .next()
                            .map(|n| n.is_whitespace() || !n.is_alphanumeric())
                            .unwrap_or(true);
                        if after_ok {
                            found = true;
                            break;
                        }
                    }
                    j += ch.len_utf8();
                }
                if found && j > i + c.len_utf8() {
                    let inner = inline(&s[i + c.len_utf8()..j]);
                    match c {
                        '*' => out.push_str(&format!("**{}**", inner)),
                        '/' => out.push_str(&format!("*{}*", inner)),
                        '_' => out.push_str(&format!("<u>{}</u>", inner)),
                        '=' | '~' => out.push_str(&format!("`{}`", inner)),
                        '+' => out.push_str(&format!("~~{}~~", inner)),
                        _ => {}
                    }
                    i = j + c.len_utf8();
                    continue;
                }
            }
        }
        out.push(c);
        i += c.len_utf8();
    }
    out
}

/// Org 文本 -> Markdown。
pub fn org_to_md(input: &Path) -> Result<String, String> {
    let raw = std::fs::read_to_string(input)
        .map_err(|e| format!("读取 {} 失败：{e}", input.display()))?;
    let lines: Vec<&str> = raw.lines().collect();
    let mut out = String::new();
    let mut i = 0usize;
    let mut in_props = false;

    while i < lines.len() {
        let line = lines[i];
        let trimmed = line.trim();

        // 属性抽屉
        if in_props {
            if trimmed == ":END:" {
                in_props = false;
            }
            i += 1;
            continue;
        }
        if trimmed == ":PROPERTIES:" {
            in_props = true;
            i += 1;
            continue;
        }
        // BEGIN 块
        if trimmed.starts_with("#+BEGIN_") {
            let kind = trimmed[8..]
                .split_whitespace()
                .next()
                .unwrap_or("")
                .to_ascii_uppercase();
            let lang = if kind == "SRC" {
                trimmed
                    .split_whitespace()
                    .nth(1)
                    .unwrap_or("")
                    .to_string()
            } else {
                String::new()
            };
            let mut buf = String::new();
            i += 1;
            while i < lines.len() && !lines[i].trim().starts_with("#+END_") {
                buf.push_str(lines[i]);
                buf.push('\n');
                i += 1;
            }
            if i < lines.len() {
                i += 1; // 跳过 #+END_*
            }
            match kind.as_str() {
                "SRC" => out.push_str(&format!("```{lang}\n{}\n```\n\n", buf.trim_end())),
                "EXAMPLE" => out.push_str(&format!("```\n{}\n```\n\n", buf.trim_end())),
                "QUOTE" | "VERSE" => {
                    for l in buf.lines() {
                        out.push_str(&format!("> {}\n", inline(l)));
                    }
                    out.push('\n');
                }
                _ => {
                    // CENTER 等：原样输出
                    out.push_str(&buf);
                    out.push('\n');
                }
            }
            continue;
        }
        // 元数据
        if trimmed.starts_with("#+") {
            if trimmed.starts_with("#+TITLE:") || trimmed.starts_with("#+TITLE ") {
                let t = trimmed.splitn(2, ':').nth(1).unwrap_or("").trim();
                if !t.is_empty() {
                    out.push_str(&format!("# {}\n\n", inline(t)));
                }
            }
            // 其它 #+ 元数据剥离
            i += 1;
            continue;
        }
        // 注释行
        if trimmed.starts_with("# ") || trimmed == "#" {
            i += 1;
            continue;
        }
        // 水平线
        if trimmed.chars().all(|c| c == '-') && trimmed.len() >= 5 {
            out.push_str("---\n\n");
            i += 1;
            continue;
        }
        // 标题 * 1-6 级（org 中 * 后必须跟空格）
        if trimmed.starts_with('*') {
            let level = trimmed.chars().take_while(|c| *c == '*').count();
            if level <= 6 && trimmed[level..].starts_with(' ') {
                let title = trimmed[level..].trim();
                out.push_str(&format!("{} {}\n\n", "#".repeat(level), inline(title)));
                i += 1;
                continue;
            }
        }
        // 表格：| ... |（org 表格行）
        if trimmed.starts_with('|') {
            let mut rows: Vec<Vec<String>> = Vec::new();
            while i < lines.len() {
                let t = lines[i].trim();
                if !t.starts_with('|') {
                    break;
                }
                if t.starts_with("|-") || t.chars().all(|c| c == '|' || c == '-' || c == '+') {
                    i += 1;
                    continue;
                }
                let cells: Vec<String> = t
                    .trim_matches('|')
                    .split('|')
                    .map(|c| inline(c.trim()))
                    .collect();
                rows.push(cells);
                i += 1;
            }
            if rows.is_empty() {
                continue;
            }
            let max_cols = rows.iter().map(|r| r.len()).max().unwrap_or(0);
            let mut tbl = String::new();
            for (ri, row) in rows.iter().enumerate() {
                let cells: Vec<String> = (0..max_cols)
                    .map(|c| row.get(c).cloned().unwrap_or_default())
                    .collect();
                tbl.push_str(&format!("| {} |\n", cells.join(" | ")));
                if ri == 0 {
                    tbl.push_str(&format!("|{}\n", " --- |".repeat(max_cols)));
                }
            }
            out.push_str(&tbl);
            out.push('\n');
            continue;
        }
        // 定义列表 - term :: def
        if (trimmed.starts_with("- ") || trimmed.starts_with("+ ")) && trimmed.contains(" :: ") {
            let p = trimmed.find(" :: ").unwrap();
            let term = inline(trimmed[2..p].trim());
            let def = inline(trimmed[p + 4..].trim());
            out.push_str(&format!("- **{}**: {}\n", term, def));
            i += 1;
            continue;
        }
        // 无序列表 - / +
        if trimmed.starts_with("- ") || trimmed.starts_with("+ ") {
            // 计算缩进层级（2 空格一级）
            let indent = line.len() - line.trim_start().len();
            let level = indent / 2;
            let content = inline(trimmed[2..].trim());
            out.push_str(&format!("{}- {}\n", "    ".repeat(level), content));
            i += 1;
            continue;
        }
        // 有序列表 1. / 1)（支持多位数字）
        let num_end = trimmed.chars().take_while(|c| c.is_ascii_digit()).count();
        if num_end > 0
            && (trimmed[num_end..].starts_with(". ") || trimmed[num_end..].starts_with(") "))
        {
            let indent = line.len() - line.trim_start().len();
            let level = indent / 2;
            let content = inline(trimmed[num_end + 2..].trim());
            out.push_str(&format!("{}{}. {}\n", "    ".repeat(level), 1, content));
            i += 1;
            continue;
        }
        // 空行
        if trimmed.is_empty() {
            out.push('\n');
            i += 1;
            continue;
        }
        // 普通段落
        out.push_str(&inline(trimmed));
        out.push('\n');
        if i + 1 < lines.len() {
            let next = lines[i + 1].trim();
            let next_is_para = !next.is_empty()
                && !next.starts_with('#')
                && !next.starts_with('*')
                && !next.starts_with('|')
                && !next.starts_with('-')
                && !next.starts_with('+')
                && !next.starts_with("----");
            if next_is_para {
                out.push('\n');
            }
        }
        i += 1;
    }
    if out.trim().is_empty() {
        Err("Org 文本没有可提取的内容".to_string())
    } else {
        Ok(out.trim().to_string() + "\n")
    }
}
