//! RTF -> Markdown（替代 Pandoc 的 rtf 输入）。
//!
//! 手写 RTF 控制字解析：段落(\par/\pard) / 换行(\line) / 制表(\tab) /
//!   粗体(\b) 斜体(\i) 下划线(\ul) 删除线(\strike) —— 行内范围精确切换 /
//!   Unicode(\uN?) / 八进制字节(\'hh, cp1252) / 符号控制字(emdash 等) /
//!   表格(\trowd ... \cell ... \row) / 目的不明组({\*...}) 与表组(fonttbl 等)跳过 /
//!   组嵌套状态恢复。
//!
//! 空格规则：普通控制字后的空格是分隔符（消费）；复位类(\b0 等)与符号类
//!   (\emdash 等)控制字后的空格是文本，保留——避免 "boldand" 粘连。
//!
//! 诚实边界：\bin 二进制数据跳过；\'hh 用 cp1252 近似；非 \u 转义的原始 GBK
//!   中文不支持（会乱码）；表格不做合并单元格；字体/颜色/字号控制字忽略。

use std::path::Path;

/// cp1252 0x80-0x9F 映射（其余与 Latin-1 相同）。
fn cp1252(b: u8) -> char {
    match b {
        0x80 => '€', 0x82 => '‚', 0x83 => 'ƒ', 0x84 => '„', 0x85 => '…',
        0x86 => '†', 0x87 => '‡', 0x88 => 'ˆ', 0x89 => '‰', 0x8A => 'Š',
        0x8B => '‹', 0x8C => 'Œ', 0x8E => 'Ž', 0x91 => '‘', 0x92 => '’',
        0x93 => '“', 0x94 => '”', 0x95 => '•', 0x96 => '–', 0x97 => '—',
        0x98 => '˜', 0x99 => '™', 0x9A => 'š', 0x9B => '›', 0x9C => 'œ',
        0x9E => 'ž', 0x9F => 'Ÿ',
        _ => b as char,
    }
}

#[derive(Clone)]
struct St {
    bold: bool,
    italic: bool,
    underline: bool,
    strike: bool,
}

fn wrap(t: &str, st: &St) -> String {
    let mut s = t.to_string();
    if st.italic {
        s = format!("*{s}*");
    }
    if st.bold {
        s = format!("**{s}**");
    }
    if st.strike {
        s = format!("~~{s}~~");
    }
    if st.underline {
        s = format!("<u>{s}</u>");
    }
    s
}

/// 行内 flush：用当前状态包裹已收集文本，不换行；首尾空格还原（防止单词粘连）。
fn flush_inline(para: &mut String, out: &mut String, st: &St) {
    let t = para.trim();
    let lead = para.len() - para.trim_start().len();
    if lead > 0 {
        out.push_str(&para[..lead]);
    }
    if !t.is_empty() {
        out.push_str(&wrap(t, st));
    }
    let trimmed_end = para.trim_end().len();
    if trimmed_end < para.len() {
        out.push_str(&para[trimmed_end..]);
    }
    para.clear();
}

/// 段落 flush：包裹并换行。
fn flush_para(para: &mut String, out: &mut String, st: &St) {
    flush_inline(para, out, st);
    out.push_str("\n\n");
}

/// 需要整组跳过的表组控制字。
fn is_skip_group(word: &str) -> bool {
    matches!(
        word,
        "fonttbl" | "colortbl" | "stylesheet" | "info" | "pict" | "object" | "header" | "footer"
            | "footnote" | "annotation" | "latentstyles" | "themedata" | "colorschememapping"
            | "listtable" | "listoverridetable" | "generator" | "revtbl" | "rsidtbl" | "datastore"
    )
}

/// 控制字后的空格应保留为文本（复位类/符号类）。
fn keep_space_after(word: &str, param: Option<i32>) -> bool {
    if matches!(word, "b" | "i" | "ul" | "strike") && param == Some(0) {
        return true;
    }
    matches!(
        word,
        "emdash" | "endash" | "lquote" | "rquote" | "ldblquote" | "rdblquote" | "bullet" | "line"
    )
}

/// RTF 文本 -> Markdown。
pub fn rtf_to_md(input: &Path) -> Result<String, String> {
    let bytes = std::fs::read(input).map_err(|e| format!("读取 {} 失败：{e}", input.display()))?;
    let s = String::from_utf8_lossy(&bytes).into_owned();

    let b = s.as_bytes();
    let mut out = String::new();
    let mut para = String::new();
    let mut st = St { bold: false, italic: false, underline: false, strike: false };
    let mut stack: Vec<St> = Vec::new();
    let mut skip_depth: usize = 0; // \* 目的不明组 / 表组深度
    // 表格状态
    let mut in_table = false;
    let mut table_rows: Vec<Vec<String>> = Vec::new();
    let mut cur_cells: Vec<String> = Vec::new();
    let mut cur_cell = String::new();

    let mut i = 0usize;
    while i < b.len() {
        let c = b[i];
        if c == b'\\' {
            i += 1;
            if i >= b.len() {
                break;
            }
            match b[i] {
                b'{' => { if skip_depth == 0 { para.push('{'); } i += 1; }
                b'}' => { if skip_depth == 0 { para.push('}'); } i += 1; }
                b'\\' => { if skip_depth == 0 { para.push('\\'); } i += 1; }
                b'~' => { if skip_depth == 0 { para.push('\u{a0}'); } i += 1; }
                b'_' | b'-' => { if skip_depth == 0 { para.push('-'); } i += 1; }
                b'*' => { skip_depth = 1; i += 1; }
                b'\'' => {
                    // \'hh 单字节
                    if i + 2 < b.len() {
                        if let Ok(h) = u8::from_str_radix(&s[i + 1..i + 3], 16) {
                            if skip_depth == 0 {
                                para.push(cp1252(h));
                            }
                        }
                        i += 3;
                    } else {
                        i += 1;
                    }
                }
                b'u' if i + 1 < b.len() && (b[i + 1] == b'-' || b[i + 1].is_ascii_digit()) => {
                    // \uN? Unicode（N 是有符号 UTF-16 码元，后跟一个备用字符 ?）
                    let mut j = i + 1;
                    let start = j;
                    if b[j] == b'-' {
                        j += 1;
                    }
                    while j < b.len() && b[j].is_ascii_digit() {
                        j += 1;
                    }
                    let n: i32 = s[start..j].parse().unwrap_or(0);
                    let mut after = j;
                    if after < b.len() && b[after] == b'?' {
                        after += 1;
                    }
                    if skip_depth == 0 {
                        if let Some(ch) = char::from_u32(n as u32) {
                            if in_table {
                                cur_cell.push(ch);
                            } else {
                                para.push(ch);
                            }
                        }
                    }
                    i = after;
                }
                _ if b[i].is_ascii_alphabetic() => {
                    let mut j = i;
                    while j < b.len() && b[j].is_ascii_alphabetic() {
                        j += 1;
                    }
                    let word = &s[i..j];
                    // 数字参数（可负）
                    let mut k = j;
                    let mut param: Option<i32> = None;
                    if k < b.len() && (b[k] == b'-' || b[k].is_ascii_digit()) {
                        let start = k;
                        while k < b.len() && b[k].is_ascii_digit() {
                            k += 1;
                        }
                        param = s[start..k].parse().ok();
                    }
                    // 分隔空格：普通控制字消费；复位/符号类保留为文本
                    let mut after = k;
                    if after < b.len() && b[after] == b' ' && !keep_space_after(word, param) {
                        after += 1;
                    }

                    if skip_depth == 0 {
                        match word {
                            "par" | "pard" | "sect" | "page" => {
                                if in_table {
                                    cur_cells.push(cur_cell.trim().to_string());
                                    cur_cell.clear();
                                } else {
                                    flush_para(&mut para, &mut out, &st);
                                }
                            }
                            "line" => { if in_table { cur_cell.push('\n'); } else { para.push('\n'); } }
                            "tab" => { if in_table { cur_cell.push_str("    "); } else { para.push_str("    "); } }
                            "b" | "i" | "strike" => {
                                let v = param.unwrap_or(1) != 0;
                                let changed = match word {
                                    "b" => v != st.bold,
                                    "i" => v != st.italic,
                                    _ => v != st.strike,
                                };
                                if changed {
                                    flush_inline(&mut para, &mut out, &st);
                                    match word {
                                        "b" => st.bold = v,
                                        "i" => st.italic = v,
                                        _ => st.strike = v,
                                    }
                                }
                            }
                            "ul" | "uld" | "uldash" | "uldashd" | "uldb" | "ulth" | "ulwave"
                            | "ulw" => {
                                if !st.underline {
                                    flush_inline(&mut para, &mut out, &st);
                                    st.underline = true;
                                }
                            }
                            "ul0" => {
                                if st.underline {
                                    flush_inline(&mut para, &mut out, &st);
                                    st.underline = false;
                                }
                            }
                            "emdash" => para.push('—'),
                            "endash" => para.push('–'),
                            "lquote" => para.push('\''),
                            "rquote" => para.push('\''),
                            "ldblquote" => para.push('"'),
                            "rdblquote" => para.push('"'),
                            "bullet" => para.push('•'),
                            "trowd" | "intbl" => {
                                in_table = true;
                                cur_cells.clear();
                                cur_cell.clear();
                            }
                            "cell" => {
                                cur_cells.push(cur_cell.trim().to_string());
                                cur_cell.clear();
                            }
                            "row" => {
                                cur_cells.push(cur_cell.trim().to_string());
                                cur_cell.clear();
                                if !cur_cells.is_empty() {
                                    table_rows.push(std::mem::take(&mut cur_cells));
                                }
                                in_table = false;
                                // 输出表格行
                                if let Some(last) = table_rows.last() {
                                    let line = format!("| {} |", last.join(" | "));
                                    out.push_str(&line);
                                    out.push('\n');
                                    if table_rows.len() == 1 {
                                        out.push_str(&format!("|{}\n", " --- |".repeat(last.len())));
                                    }
                                }
                            }
                            "bin" => {
                                if let Some(n) = param {
                                    let start = after;
                                    let end = (start + n as usize).min(b.len());
                                    after = end;
                                }
                            }
                            _ => {
                                // 表组控制字：跳过其后的组内容
                                if is_skip_group(word) {
                                    skip_depth = 1;
                                }
                            }
                        }
                    }
                    i = after;
                }
                _ => { i += 1; }
            }
        } else if c == b'{' {
            if skip_depth > 0 {
                skip_depth += 1;
            } else {
                stack.push(st.clone());
            }
            i += 1;
        } else if c == b'}' {
            if skip_depth > 0 {
                skip_depth -= 1;
            } else if let Some(sv) = stack.pop() {
                st = sv;
            }
            i += 1;
        } else {
            // 普通文本（UTF-8 字符）
            let ch = s[i..].chars().next().unwrap();
            if skip_depth == 0 {
                if in_table {
                    cur_cell.push(ch);
                } else {
                    para.push(ch);
                }
            }
            i += ch.len_utf8();
        }
    }

    // 收尾
    if in_table && !cur_cell.is_empty() {
        cur_cells.push(cur_cell.trim().to_string());
        if !cur_cells.is_empty() {
            table_rows.push(cur_cells);
            let last = table_rows.last().unwrap();
            out.push_str(&format!("| {} |\n", last.join(" | ")));
        }
    }
    flush_para(&mut para, &mut out, &st);

    if out.trim().is_empty() {
        Err("RTF 没有可提取的文本内容".to_string())
    } else {
        Ok(out.trim().to_string() + "\n")
    }
}
