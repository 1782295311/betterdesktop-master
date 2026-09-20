//! 文档清洗/规范化：解决 AI 生成文档的常见问题。
//!
//! 1. 中文硬换行合并：中文段落里的单换行（非段落分隔）合并
//! 2. 中英标点统一：中文上下文里的英文标点转中文
//! 3. 中英间距：中→英、英→中之间补空格
//! 4. 英文引号配对：中文上下文里的 "..." → "..."
//! 5. 多余空行合并：3+ 连续空行 → 1 空行
//! 6. 标题去粗体：# **xxx** → # xxx
//! 7. 表格行内空格清理

/// 判断是否 CJK 字符（含全角标点）。
fn is_cjk(c: char) -> bool {
    matches!(c,
        '\u{4E00}'..='\u{9FFF}'
        | '\u{3000}'..='\u{303F}'
        | '\u{FF00}'..='\u{FFEF}'
    )
}

/// 中文上下文里的英文标点转中文。
fn punct_to_cjk(c: char, prev: Option<char>, next: Option<char>) -> char {
    let near_cjk = prev.map(is_cjk).unwrap_or(false) || next.map(is_cjk).unwrap_or(false);
    if !near_cjk {
        return c;
    }
    match c {
        ',' => '，',
        '.' => '。',
        '!' => '！',
        '?' => '？',
        ':' => '：',
        ';' => '；',
        // ( 前一个是 ] 时是 markdown 链接语法，不转
        '(' => {
            if prev == Some(']') {
                '('
            } else {
                '（'
            }
        }
        // ) 后一个是行尾/空格时不转？简单：) 前一个不是 CJK 就不转
        ')' => {
            if prev.map(is_cjk).unwrap_or(false) {
                '）'
            } else {
                ')'
            }
        }
        _ => c,
    }
}

/// 清理表格行内多余空格：|  xxx  | → | xxx |
fn clean_table_line(line: &str) -> String {
    let t = line.trim();
    if !t.starts_with('|') {
        return line.to_string();
    }
    // 拆单元格
    let parts: Vec<&str> = t.split('|').collect();
    // parts: ["", " cell1 ", " cell2 ", ""]
    let cleaned: Vec<String> = parts.iter()
        .map(|s| s.trim().to_string())
        .collect();
    cleaned.join("|")
}

/// 清洗文本。
pub fn clean_text(input: &str) -> String {
    // 第负一步：移除不可见字符（零宽空格、BOM、软连字符等）
    let input: String = input.chars()
        .filter(|&ch| {
            !matches!(ch,
                '\u{200B}' // 零宽空格
                | '\u{200C}' // 零宽非连接符
                | '\u{200D}' // 零宽连接符
                | '\u{FEFF}' // BOM
                | '\u{00AD}' // 软连字符
            )
        })
        .collect();
    let input = &input;

    // 第负二步：行尾空格移除 + 标题后加空格 + 列表标记标准化
    let lines: Vec<&str> = input.split('\n').collect();
    let preprocessed: Vec<String> = lines.iter().map(|line| {
        let t = line.trim_end(); // 行尾空格移除
        let t = t.trim_start().to_string();
        // 标题后加空格：###Heading → ### Heading
        let t = if t.starts_with('#') {
            let hash_end = t.find(|c| c != '#').unwrap_or(0);
            let hashes = &t[..hash_end];
            let rest = &t[hash_end..];
            if rest.is_empty() { t }
            else if rest.starts_with(' ') { t }
            else { format!("{hashes} {rest}") }
        } else { t };
        // 列表标记标准化：• → -，1) → 1.，1． → 1.
        let t = if t.starts_with("• ") {
            format!("- {}", &t[2..])
        } else { t };
        let t = {
            // 1) xxx → 1. xxx
            let trimmed = &t;
            if trimmed.len() > 2 {
                let bytes = trimmed.as_bytes();
                if bytes[0].is_ascii_digit() && bytes[1] == b')' {
                    format!("{}. {}", trimmed.chars().next().unwrap(), &trimmed[2..])
                } else { trimmed.to_string() }
            } else { trimmed.to_string() }
        };
        t
    }).collect();
    let input = preprocessed.join("\n");

    // 第负三步：反斜杠转义移除：\[ → [，\] → ]，\( → (，\) → )
    let input = input.replace("\\[", "[").replace("\\]", "]")
        .replace("\\(", "(").replace("\\)", ")");

    // 第负四步：中文破折号统一：-- → ——（中文上下文）
    let input = {
        let chars: Vec<char> = input.chars().collect();
        let mut s = String::new();
        let mut i = 0;
        while i < chars.len() {
            if i + 1 < chars.len() && chars[i] == '-' && chars[i+1] == '-' {
                // 看前后是否 CJK
                let prev_cjk = if i > 0 { is_cjk(chars[i-1]) } else { false };
                let next_cjk = if i + 2 < chars.len() { is_cjk(chars[i+2]) } else { false };
                if prev_cjk || next_cjk {
                    s.push('—');
                    s.push('—');
                    i += 2;
                    continue;
                }
            }
            s.push(chars[i]);
            i += 1;
        }
        s
    };
    let input = &input;

    // 第零步：代码块保护——把 ``` 块提取成占位符
    let mut code_blocks: Vec<String> = Vec::new();
    let mut protected_lines: Vec<String> = Vec::new();
    for line in input.split('\n') {
        if line.trim_start().starts_with("```") {
            code_blocks.push(String::new());
            let idx = code_blocks.len() - 1;
            protected_lines.push(format!("__CODEBLOCK_{idx}__"));
            // 后续行是代码内容，push 到 code_blocks
            // 注意：这里只处理 ``` 行本身，内容在下面的循环里
        } else if !code_blocks.is_empty() && protected_lines.last().map(|l| l.starts_with("__CODEBLOCK")).unwrap_or(false) {
            // 这行是代码内容
            let last = code_blocks.len() - 1;
            code_blocks[last].push_str(line);
            code_blocks[last].push('\n');
        } else {
            protected_lines.push(line.to_string());
        }
    }
    let input = protected_lines.join("\n");

    // // 第零步：代码块/行内代码保护——提取成占位符
//     let mut placeholders: Vec<String> = Vec::new();
//     let mut protected = String::new();
//     let mut chars_iter = input.chars().peekable();
//     let mut in_code_block = false;
//     let mut in_inline_code = false;
//     while let Some(ch) = chars_iter.next() {
//         if ch == '`' {
//             // 看是否三连
//             if chars_iter.peek() == Some(&'`') && chars_iter.clone().nth(1) == Some('`') {
//                 chars_iter.next();
//                 chars_iter.next();
//                 if in_code_block {
//                     // 结束
//                     protected.push_str("__CLOSE_CODE__");
//                     in_code_block = false;
//                 } else {
//                     // 开始
//                     placeholders.push(String::new());
//                     protected.push_str(&format!("__CODEBLOCK_{}__", placeholders.len() - 1));
//                     in_code_block = true;
//                 }
//             } else if !in_code_block {
//                 // 行内代码
//                 if in_inline_code {
//                     protected.push_str("__INLINE_END__");
//                     in_inline_code = false;
//                 } else {
//                     placeholders.push(String::new());
//                     protected.push_str(&format!("__INLINE_{}__", placeholders.len() - 1));
//                     in_inline_code = true;
//                 }
//             } else {
//                 protected.push(ch);
//             }
//         } else {
//             if in_code_block || in_inline_code {
//                 // 收集到 placeholder
//                 let last_idx = placeholders.len() - 1;
//                 placeholders[last_idx].push(ch);
//             } else {
//                 protected.push(ch);
//             }
//         }
//     }
//     let input = protected;
// 
//     // 第一步：按行拆分，合并中文硬换行（靠空行分段）
    let lines: Vec<&str> = input.split('\n').collect();
    let mut merged: Vec<String> = Vec::new();
    let mut i = 0;
    while i < lines.len() {
        let line = lines[i];
        let trimmed = line.trim();
        let is_heading = trimmed.starts_with('#');
        let is_list = trimmed.starts_with("- ") || trimmed.starts_with("* ") || trimmed.starts_with("+ ")
            || trimmed.starts_with("> ");
        let is_table = trimmed.starts_with('|');
        let is_sep = trimmed.starts_with("---") || trimmed.starts_with("***");
        if trimmed.is_empty() || is_heading || is_list || is_sep {
            merged.push(line.to_string());
            i += 1;
            continue;
        }
        // 表格行单独处理，不参与合并
        if is_table {
            merged.push(clean_table_line(line));
            i += 1;
            continue;
        }
        let mut cur = line.trim_end().to_string();
        while i + 1 < lines.len() {
            let next_line = lines[i + 1];
            let next_trimmed = next_line.trim();
            if next_trimmed.is_empty() || next_trimmed.starts_with('#')
                || next_trimmed.starts_with("- ") || next_trimmed.starts_with("* ")
                || next_trimmed.starts_with("> ") || next_trimmed.starts_with('|') {
                break;
            }
            cur = format!("{cur}{next_trimmed}");
            i += 1;
        }
        merged.push(cur);
        i += 1;
    }

    let mut text = merged.join("\n");

    // 第二步：标题去粗体：# **xxx** → # xxx
    // 只处理行首是 # 开头的
    let titled: Vec<String> = text.split('\n').map(|line| {
        let t = line.trim_start();
        if t.starts_with('#') {
            // 提取 # 部分和内容
            let hash_end = t.find(|c| c != '#').unwrap_or(0);
            let hashes = &t[..hash_end];
            let rest = t[hash_end..].trim();
            // rest 去掉首尾 **
            let rest = rest.strip_prefix("**").unwrap_or(rest);
            let rest = rest.strip_suffix("**").unwrap_or(rest);
            format!("{hashes} {rest}")
        } else {
            line.to_string()
        }
    }).collect();
    text = titled.join("\n");

    // 第二步半半：标题编号半角括号转全角：# (一)xxx → # （一）xxx
    let titled2: Vec<String> = text.split('\n').map(|line| {
        let t = line.trim_start();
        if t.starts_with('#') {
            let hash_end = t.find(|c| c != '#').unwrap_or(0);
            let hashes = &t[..hash_end];
            let rest = t[hash_end..].trim();
            // (一) → （一）
            let rest = rest.replace("(", "（").replace(")", "）");
            format!("{hashes} {rest}")
        } else {
            line.to_string()
        }
    }).collect();
    text = titled2.join("\n");

    // 第二步半：HTML <img> 转 md ![](src)
    {
        let mut s = String::new();
        let bytes: Vec<char> = text.chars().collect();
        let mut i = 0;
        while i < bytes.len() {
            if i + 4 < bytes.len() && bytes[i] == '<' && bytes[i+1] == 'i' && bytes[i+2] == 'm' && bytes[i+3] == 'g' {
                let mut j = i + 4;
                while j < bytes.len() && !(bytes[j] == '/' && j + 1 < bytes.len() && bytes[j+1] == '>') {
                    j += 1;
                }
                if j < bytes.len() {
                    let tag: String = bytes[i..j].iter().collect();
                    let src = tag.find("src=\"").and_then(|si| {
                        let q = &tag[si + 5..];
                        q.find('"').map(|ei| &q[..ei])
                    });
                    match src {
                        Some(src) => s.push_str(&format!("![]({src})")),
                        None => s.push_str(&tag),
                    }
                    i = j + 2;
                } else {
                    s.push(bytes[i]);
                    i += 1;
                }
            } else {
                s.push(bytes[i]);
                i += 1;
            }
        }
        text = s;
    }
// 
    // 第二步三：目录嵌套链接修复 [text [n](#anchor)](#anchor) → [text](#anchor)
    {
        let lines: Vec<&str> = text.split("\n").collect();
        let fixed: Vec<String> = lines.iter().map(|line| {
            let t = line.trim();
            if t.starts_with('[') && t.matches("](#").count() >= 2 {
                if let Some(first) = t.find("](#") {
                    let text_part_raw = &t[1..first];
                    // 去掉末尾的 [n（数字）
                    let text_clean = if let Some(b) = text_part_raw.rfind('[') {
                        let after = &text_part_raw[b + 1..];
                        if after.chars().next().map(|c| c.is_ascii_digit()).unwrap_or(false) {
                            text_part_raw[..b].trim_end()
                        } else {
                            text_part_raw
                        }
                    } else {
                        text_part_raw
                    };
                    let after_first = &t[first + 3..];
                    if let Some(close) = after_first.find(')') {
                        let anchor = &after_first[..close];
                        return format!("[{text_clean}](#{anchor})");
                    }
                }
            }
            line.to_string()
        }).collect();
        text = fixed.join("\n");
    }

//     // 第二步四：参考文献条目紧凑化
    {
        let lines: Vec<&str> = text.split("\n").collect();
        let mut result: Vec<String> = Vec::new();
        let mut i = 0;
        while i < lines.len() {
            let line = lines[i];
            let trimmed = line.trim();
            let is_ref = trimmed.starts_with('[') && trimmed.chars().nth(1).map(|c| c.is_ascii_digit()).unwrap_or(false);
            if is_ref {
                result.push(line.to_string());
                i += 1;
                while i < lines.len() {
                    let next = lines[i];
                    let next_t = next.trim();
                    if next_t.is_empty() {
                        if i + 1 < lines.len() && lines[i + 1].trim().starts_with('[') {
                            i += 1;
                            continue;
                        } else {
                            break;
                        }
                    }
                    if !next_t.starts_with('[') {
                        let last = result.pop().unwrap_or_default();
                        result.push(format!("{last}{next_t}"));
                        i += 1;
                    } else {
                        break;
                    }
                }
            } else {
                result.push(line.to_string());
                i += 1;
            }
        }
        text = result.join("\n");
    }
// 
//     // 第三步：标点转换 + 英文引号配对（状态机：引号奇偶配对 + 括号配对）
    let chars: Vec<char> = text.chars().collect();
    let mut out = String::new();
    let mut quote_open = false;
    let mut paren_open = false; // ( 已转成 （，等待 ) 配对
    for (idx, &c) in chars.iter().enumerate() {
        let prev = if idx > 0 { Some(chars[idx - 1]) } else { None };
        let next = if idx + 1 < chars.len() { Some(chars[idx + 1]) } else { None };
        if c == '"' {
            let near_cjk = prev.map(is_cjk).unwrap_or(false) || next.map(is_cjk).unwrap_or(false);
            if near_cjk {
                if quote_open {
                    out.push('”');
                    quote_open = false;
                } else {
                    out.push('“');
                    quote_open = true;
                }
                continue;
            }
        }
        // 括号配对：( 转 （ 后，下一个 ) 转 ）
        if c == '(' {
            if prev == Some(']') {
                // markdown 链接语法
                out.push('(');
            } else {
                let near_cjk = prev.map(is_cjk).unwrap_or(false) || next.map(is_cjk).unwrap_or(false);
                if near_cjk {
                    out.push('（');
                    paren_open = true;
                } else {
                    out.push('(');
                }
            }
            continue;
        }
        if c == ')' {
            if paren_open {
                out.push('）');
                paren_open = false;
            } else {
                out.push(')');
            }
            continue;
        }
        out.push(punct_to_cjk(c, prev, next));
    }

    // 第四步：中英间距
    let spaced = {
        let mut s = String::new();
        let ch: Vec<char> = out.chars().collect();
        for (idx, &c) in ch.iter().enumerate() {
            if idx > 0 {
                let prev = ch[idx - 1];
                let prev_cjk = is_cjk(prev);
                let cur_cjk = is_cjk(c);
                let is_md_punct = |ch: char| matches!(ch, '*' | '[' | ']' | '(' | ')' | '#' | '`' | '-' | '"' | '“' | '”');
                if prev_cjk != cur_cjk && !prev.is_whitespace() && !c.is_whitespace()
                    && !is_md_punct(prev) && !is_md_punct(c) {
                    s.push(' ');
                }
            }
            s.push(c);
        }
        s
    };

    // 第五步：多余空行合并（3+ 连续空行 → 1 空行）
    let lines: Vec<&str> = spaced.split('\n').collect();
    let mut result: Vec<String> = Vec::new();
    let mut blank_count = 0;
    for line in lines {
        if line.trim().is_empty() {
            blank_count += 1;
            if blank_count <= 1 {
                result.push(String::new());
            }
        } else {
            blank_count = 0;
            result.push(line.to_string());
        }
    }

    let final_text = result.join("\n");
    let mut out = final_text;
    for (i, block) in code_blocks.iter().enumerate() {
        out = out.replace(&format!("__CODEBLOCK_{i}__"), &format!("```\n{block}```"));
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_merge_hardbreak() {
        let input = "这是第一行\n这是第二行\n这是第三行。";
        assert_eq!(clean_text(input), "这是第一行这是第二行这是第三行。");
    }

    #[test]
    fn test_quote() {
        let input = r#"这是"十五五"规划"#;
        let out = clean_text(input);
        assert!(out.contains("“十五五”"), "应含中文引号: {out}");
    }

    #[test]
    fn test_heading_bold() {
        let input = "# **一、前言**";
        let out = clean_text(input);
        assert_eq!(out, "# 一、前言");
    }

    #[test]
    fn test_blank_lines() {
        let input = "第一行\n\n\n\n第二行";
        let out = clean_text(input);
        assert!(out.matches('\n').count() == 2, "空行应压缩: {out:?}");
    }
}
