//! MediaWiki 标记 -> Markdown（替代 Pandoc 的 mediawiki 输入）。
//!
//! 行级：表格块 / 代码块(<pre>,<syntaxhighlight>,<source>) / 标题(==x==) /
//!       列表(*,# 可多级) / 引用(>) / 水平线(----)。
//! 行内：'''粗体'''、''斜体''、[[内部链接]]、[外部链接 文本]、<ref>剥离、
//!       模板{{}}剥离、注释<!-- -->剥离、<s>/<b>/<i>/<br> 等简单标签。
//!
//! 诚实边界：不展开模板/解析器函数；File/Image 链接不提取图片；
//!           表格不处理 colspan/rowspan；<gallery>/<math> 标签内容剥离。

use std::path::Path;

/// 行内转换：处理粗体/斜体/链接/标签/模板/注释。
fn inline(s: &str) -> String {
    let bytes = s.as_bytes();
    let mut out = String::new();
    let mut i = 0usize;
    while i < bytes.len() {
        let rest = &s[i..];
        // 注释
        if rest.starts_with("<!--") {
            if let Some(j) = rest.find("-->") {
                i += j + 3;
                continue;
            }
        }
        // 粗体 '''x'''
        if rest.starts_with("'''") {
            if let Some(j) = rest[3..].find("'''") {
                let inner = inline(&rest[3..3 + j]);
                out.push_str(&format!("**{}**", inner));
                i += 3 + j + 3;
                continue;
            }
        }
        // 斜体 ''x''
        if rest.starts_with("''") {
            if let Some(j) = rest[2..].find("''") {
                let inner = inline(&rest[2..2 + j]);
                out.push_str(&format!("*{}*", inner));
                i += 2 + j + 2;
                continue;
            }
        }
        // 内部链接 [[Page|text]] / [[Page]] / [[Page#sec|text]]
        if rest.starts_with("[[") {
            if let Some(j) = rest[2..].find("]]") {
                let raw = &rest[2..2 + j];
                let (target, text) = match raw.find('|') {
                    Some(p) => (&raw[..p], &raw[p + 1..]),
                    None => (raw, raw),
                };
                let t = target.trim();
                let txt = text.trim();
                if t.starts_with("File:") || t.starts_with("Image:") {
                    // 图片链接：不提取，输出描述文本（若有）
                    if !txt.is_empty() && txt != t {
                        out.push_str(&inline(txt));
                    }
                } else if t.starts_with("Category:") {
                    // 分类：剥离
                } else {
                    let url = t.replace(' ', "_");
                    if txt == t {
                        out.push_str(&format!("[{}]({})", inline(txt), url));
                    } else {
                        out.push_str(&format!("[{}]({})", inline(txt), url));
                    }
                }
                i += 2 + j + 2;
                continue;
            }
        }
        // 外部链接 [url] / [url text]。j 已含 '[' 前缀偏移
        if rest.starts_with('[') {
            if let Some(j) = rest.find(']') {
                let inner = &rest[1..j];
                if let Some(sp) = inner.find(char::is_whitespace) {
                    let url = inner[..sp].trim();
                    let text = inner[sp..].trim();
                    if url.starts_with("http://") || url.starts_with("https://") {
                        out.push_str(&format!("[{}]({})", inline(text), url));
                        i += j + 1;
                        continue;
                    }
                } else if inner.starts_with("http://") || inner.starts_with("https://") {
                    out.push_str(&format!("<{}>", inner.trim()));
                    i += j + 1;
                    continue;
                }
            }
        }
        // 模板 {{...}}（不展开，剥离；不做嵌套）。j 已含 {{ 前缀偏移
        if rest.starts_with("{{") {
            if let Some(j) = rest.find("}}") {
                i += j + 2;
                continue;
            }
        }
        // ref 标签 <ref ...>...</ref> / <ref .../>
        if rest.starts_with("<ref") {
            if let Some(j) = rest.find('>') {
                let open_end = j + 1;
                if rest[..j].ends_with('/') {
                    i += open_end;
                    continue;
                }
                if let Some(k) = rest[open_end..].find("</ref>") {
                    i += open_end + k + 6;
                    continue;
                }
                i += open_end;
                continue;
            }
        }
        // nowiki <nowiki>...</nowiki> 原样
        if rest.starts_with("<nowiki>") {
            if let Some(j) = rest.find("</nowiki>") {
                out.push_str(&rest[8..j]);
                i += j + 9;
                continue;
            }
        }
        // 简单标签
        for (open, close, md_open, md_close) in [
            ("<b>", "</b>", "**", "**"),
            ("<i>", "</i>", "*", "*"),
            ("<s>", "</s>", "~~", "~~"),
            ("<strike>", "</strike>", "~~", "~~"),
            ("<u>", "</u>", "", ""),
            ("<code>", "</code>", "`", "`"),
        ] {
            if rest.starts_with(open) {
                let inner = &rest[open.len()..];
                if let Some(j) = inner.find(close) {
                    out.push_str(md_open);
                    out.push_str(&inline(&inner[..j]));
                    out.push_str(md_close);
                    i += open.len() + j + close.len();
                    continue;
                }
            }
        }
        if rest.starts_with("<br") {
            // <br> <br/> <br />
            if let Some(j) = rest.find('>') {
                out.push('\n');
                i += j + 1;
                continue;
            }
        }
        // 签名 ~~~~ / ~~~
        if rest.starts_with("~~~~") {
            i += 4;
            continue;
        }
        if rest.starts_with("~~~") {
            i += 3;
            continue;
        }
        // 未知标签：跳过标签保留内容
        if rest.starts_with('<') {
            if let Some(j) = rest.find('>') {
                i += j + 1;
                continue;
            }
        }
        // 普通字符
        let ch = rest.chars().next().unwrap();
        out.push(ch);
        i += ch.len_utf8();
    }
    out
}

/// 解析一个表格块（从 `{|` 所在行开始），返回 (markdown, 消费到行尾的索引)。
fn table_to_md(lines: &[&str], start: usize) -> (String, usize) {
    let mut rows: Vec<Vec<(String, bool)>> = Vec::new(); // (text, is_header)
    let mut i = start + 1;
    while i < lines.len() {
        let line = lines[i].trim();
        if line.starts_with("|}") {
            i += 1;
            break;
        }
        if line.starts_with("|-") || line.is_empty() || line.starts_with("|+") {
            i += 1;
            continue;
        }
        // 表头行 ! h1 !! h2
        if line.starts_with('!') {
            let cells = split_cells(&line[1..]);
            rows.push(cells.into_iter().map(|c| (inline(&c), true)).collect());
            i += 1;
            continue;
        }
        // 数据行 | c1 || c2
        if line.starts_with('|') {
            let cells = split_cells(&line[1..]);
            rows.push(cells.into_iter().map(|c| (inline(&c), false)).collect());
            i += 1;
            continue;
        }
        // 表格内其它内容（如 <caption>）跳过
        i += 1;
    }
    let end = i.min(lines.len());
    if rows.is_empty() {
        return (String::new(), end);
    }
    // 找是否有表头行
    let has_header = rows.first().map(|r| r.iter().all(|(_, h)| *h)).unwrap_or(false);
    let max_cols = rows.iter().map(|r| r.len()).max().unwrap_or(0);
    if max_cols == 0 {
        return (String::new(), end);
    }
    let mut out = String::new();
    let body_start = if has_header { 1 } else { 0 };
    for (ri, row) in rows.iter().enumerate() {
        if ri == 0 && !has_header {
            // 无表头：直接输出数据行（不造表头）
            let cells: Vec<String> = (0..max_cols)
                .map(|c| row.get(c).map(|x| x.0.clone()).unwrap_or_default())
                .collect();
            out.push_str(&format!("| {} |\n", cells.join(" | ")));
            continue;
        }
        let cells: Vec<String> = (0..max_cols)
            .map(|c| row.get(c).map(|x| x.0.clone()).unwrap_or_default())
            .collect();
        out.push_str(&format!("| {} |\n", cells.join(" | ")));
        if ri == 0 && has_header {
            out.push_str(&format!("|{}\n", " --- |".repeat(max_cols)));
        }
    }
    if body_start > 0 {
        let _ = body_start;
    }
    (out + "\n", end)
}

fn split_cells(s: &str) -> Vec<String> {
    // 按 || 或 !! 分割，但跳过 [[...]] 内的 |（链接内可能含 |）
    let mut cells = Vec::new();
    let mut cur = String::new();
    let bytes = s.as_bytes();
    let mut i = 0usize;
    let mut link = false;
    while i < bytes.len() {
        let rest = &s[i..];
        if !link && (rest.starts_with("||") || rest.starts_with("!!")) {
            cells.push(cur.trim().to_string());
            cur.clear();
            i += 2;
            continue;
        }
        if rest.starts_with("[[") {
            link = true;
        } else if link && rest.starts_with("]]") {
            link = false;
        }
        let ch = rest.chars().next().unwrap();
        cur.push(ch);
        i += ch.len_utf8();
    }
    cells.push(cur.trim().to_string());
    cells
}

/// MediaWiki 文本 -> Markdown。
pub fn mediawiki_to_md(input: &Path) -> Result<String, String> {
    let raw = std::fs::read_to_string(input)
        .map_err(|e| format!("读取 {} 失败：{e}", input.display()))?;
    let lines: Vec<&str> = raw.lines().collect();
    let mut out = String::new();
    let mut i = 0usize;
    let mut list_stack: Vec<usize> = Vec::new(); // 列表缩进栈（* 或 #）

    while i < lines.len() {
        let line = lines[i];
        let trimmed = line.trim();

        // 表格块
        if trimmed.starts_with("{|") {
            let (tbl, next) = table_to_md(&lines, i);
            if !tbl.trim().is_empty() {
                out.push_str(&tbl);
            }
            i = next;
            continue;
        }
        // 代码块
        if trimmed.starts_with("<pre") || trimmed.starts_with("<syntaxhighlight")
            || trimmed.starts_with("<source") || trimmed.starts_with("<code")
        {
            let mut buf = String::new();
            i += 1;
            let mut closed = false;
            while i < lines.len() {
                let l = lines[i].trim();
                if l.starts_with("</pre>") || l.starts_with("</syntaxhighlight>")
                    || l.starts_with("</source>") || l.starts_with("</code>")
                {
                    closed = true;
                    i += 1;
                    break;
                }
                buf.push_str(lines[i]);
                buf.push('\n');
                i += 1;
            }
            let lang = if trimmed.starts_with("<syntaxhighlight") {
                trimmed
                    .split_once("lang=\"")
                    .and_then(|(_, r)| r.split('"').next())
                    .unwrap_or("")
            } else if trimmed.starts_with("<source") {
                trimmed
                    .split_once("lang=\"")
                    .and_then(|(_, r)| r.split('"').next())
                    .unwrap_or("")
            } else {
                ""
            };
            out.push_str(&format!("```{lang}\n{}\n```\n\n", buf.trim_end()));
            if !closed {
                // 未闭合：诚实处理，已输出已见内容
            }
            continue;
        }
        // 标题 == x ==
        if trimmed.starts_with('=') {
            let end_eq = trimmed.find(|c: char| c != '=').unwrap_or(trimmed.len());
            let level = end_eq;
            if level >= 1 && level <= 6 && trimmed.ends_with(&"=".repeat(level)) && trimmed.len() > level * 2 {
                let inner = trimmed[end_eq..trimmed.len() - level].trim();
                out.push_str(&format!("{} {}\n\n", "#".repeat(level), inline(inner)));
                i += 1;
                continue;
            }
        }
        // 水平线
        if trimmed == "----" || (trimmed.starts_with("----") && trimmed.trim_matches('-').is_empty()) {
            out.push_str("---\n\n");
            i += 1;
            continue;
        }
        // 引用块 >
        if trimmed.starts_with('>') {
            let inner = inline(trimmed[1..].trim());
            if !inner.is_empty() {
                out.push_str(&format!("> {}\n", inner));
            }
            i += 1;
            continue;
        }
        // 列表 * #（多级）
        if trimmed.starts_with('*') || trimmed.starts_with('#') {
            let mut level = 0usize;
            let mut ordered = false;
            let mut chars = trimmed.chars();
            while let Some(c) = chars.next() {
                if c == '*' {
                    level += 1;
                } else if c == '#' {
                    level += 1;
                    ordered = true;
                } else {
                    break;
                }
            }
            let rest = trimmed[level..].trim();
            let indent = "    ".repeat(level.saturating_sub(1));
            let bullet = if ordered { "1." } else { "-" };
            out.push_str(&format!("{}{} {}\n", indent, bullet, inline(rest)));
            i += 1;
            continue;
        }
        // 空行：分隔段落
        if trimmed.is_empty() {
            out.push('\n');
            i += 1;
            continue;
        }
        // 普通段落行
        out.push_str(&inline(trimmed));
        out.push('\n');
        if i + 1 < lines.len() {
            let next = lines[i + 1].trim();
            let next_is_para = !next.is_empty()
                && !next.starts_with('=')
                && !next.starts_with('{')
                && !next.starts_with('|')
                && !next.starts_with('*')
                && !next.starts_with('#')
                && !next.starts_with('>')
                && !next.starts_with("----");
            if next_is_para {
                out.push('\n');
            }
        }
        i += 1;
    }
    let _ = &mut list_stack;
    if out.trim().is_empty() {
        Err("MediaWiki 文本没有可提取的内容".to_string())
    } else {
        Ok(out.trim().to_string() + "\n")
    }
}
