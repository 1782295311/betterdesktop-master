//! IPYNB (Jupyter Notebook) -> Markdown（替代 Pandoc 的 ipynb 输入）。
//!
//! nbformat 4.x JSON 解析：markdown cell 原文直出；code cell 转代码块；
//! 输出（stream / execute_result / display_data / error）转 4 空格缩进块。
//!
//! 诚实边界：
//! - 只取文本输出（text/plain、text/markdown）；image/png 等二进制输出不提取，写注释行说明；
//! - nbformat < 4 明确报错；raw cell 跳过；
//! - source/输出文本若含 ``` 三反引号不转义（会破坏围栏代码块），已知限制。

use std::path::Path;

use serde_json::Value;

/// 拼接 ipynb 的 source/text 字段（nbformat 里既可能是字符串，也可能是行数组）。
fn join_lines(v: &Value) -> String {
    match v {
        Value::String(s) => s.clone(),
        Value::Array(arr) => {
            let mut out = String::new();
            for item in arr {
                if let Value::String(s) = item {
                    out.push_str(s);
                }
            }
            out
        }
        _ => String::new(),
    }
}

/// 拼接 traceback：数组元素间强制换行（元素可能不带 \n 结尾）。
fn join_traceback(v: &Value) -> String {
    match v {
        Value::String(s) => s.clone(),
        Value::Array(arr) => {
            let mut out = String::new();
            for item in arr {
                if let Value::String(s) = item {
                    out.push_str(s);
                    if !s.ends_with('\n') {
                        out.push('\n');
                    }
                }
            }
            out
        }
        _ => String::new(),
    }
}

/// 剥离 ANSI 转义色码（Jupyter traceback 常带 \x1b[0;31m 之类）。
fn strip_ansi(s: &str) -> String {
    let mut out = String::new();
    let mut chars = s.chars();
    while let Some(c) = chars.next() {
        if c == '\u{1b}' {
            // 跳过 CSI 序列：ESC [ 参数... 字母
            if chars.clone().next() == Some('[') {
                chars.next(); // '['
                for c2 in chars.by_ref() {
                    if c2.is_ascii_alphabetic() {
                        break;
                    }
                }
            }
        } else {
            out.push(c);
        }
    }
    out
}

/// 从 data map 里取文本输出：优先 text/plain，其次 text/markdown。
/// 返回 (选中文本, 其余非文本 mime 列表)。
fn text_output(data: &Value) -> (String, Vec<String>) {
    let mut plain = String::new();
    let mut md = String::new();
    let mut nontext: Vec<String> = Vec::new();
    if let Value::Object(map) = data {
        for (k, v) in map {
            let text = join_lines(v);
            if k == "text/plain" {
                plain = text;
            } else if k == "text/markdown" {
                md = text;
            } else if !text.is_empty() {
                nontext.push(k.clone());
            }
        }
    }
    let picked = if !plain.is_empty() { plain } else { md };
    (picked, nontext)
}

/// 文本行转 4 空格缩进块（空行不缩进，保持段落语义）。
fn indent_block(text: &str) -> String {
    text.trim_end()
        .lines()
        .map(|l| if l.is_empty() { String::new() } else { format!("    {l}") })
        .collect::<Vec<_>>()
        .join("\n")
}

fn output_to_md(o: &Value) -> String {
    let otype = o.get("output_type").and_then(|v| v.as_str()).unwrap_or("");
    match otype {
        "stream" => {
            let text = join_lines(o.get("text").unwrap_or(&Value::Null));
            if text.trim().is_empty() {
                String::new()
            } else {
                format!("{}\n\n", indent_block(&text))
            }
        }
        "execute_result" | "display_data" => {
            let (text, nontext) = text_output(o.get("data").unwrap_or(&Value::Null));
            let mut out = String::new();
            if let Some(n) = o.get("execution_count").and_then(|v| v.as_u64()) {
                out.push_str(&format!("Out[{n}]:\n\n"));
            }
            if !text.trim().is_empty() {
                out.push_str(&format!("{}\n\n", indent_block(&text)));
            }
            for m in &nontext {
                out.push_str(&format!("<!-- 非文本输出（{m}）未提取 -->\n\n"));
            }
            out
        }
        "error" => {
            let ename = o.get("ename").and_then(|v| v.as_str()).unwrap_or("");
            let evalue = o.get("evalue").and_then(|v| v.as_str()).unwrap_or("");
            let tb = strip_ansi(&join_traceback(o.get("traceback").unwrap_or(&Value::Null)));
            let body = if !tb.trim().is_empty() {
                tb
            } else {
                format!("{ename}: {evalue}")
            };
            if body.trim().is_empty() {
                String::new()
            } else {
                format!("{}\n\n", indent_block(&body))
            }
        }
        _ => String::new(),
    }
}

fn cell_to_md(cell: &Value, lang: &str) -> String {
    let ctype = cell.get("cell_type").and_then(|v| v.as_str()).unwrap_or("");
    match ctype {
        "markdown" => join_lines(cell.get("source").unwrap_or(&Value::Null)),
        // raw cell：诚实跳过（Jupyter 里 raw 多为 LaTeX/HTML 原文，无统一语义）
        "raw" => String::new(),
        "code" => {
            let src = join_lines(cell.get("source").unwrap_or(&Value::Null));
            let mut out = String::new();
            out.push_str(&format!("```{lang}\n{}\n```\n\n", src.trim_end()));
            if let Some(outputs) = cell.get("outputs").and_then(|v| v.as_array()) {
                for o in outputs {
                    out.push_str(&output_to_md(o));
                }
            }
            out
        }
        _ => String::new(),
    }
}

/// IPYNB 文件 -> Markdown 文本。
pub fn ipynb_to_md(input: &Path) -> Result<String, String> {
    let raw = std::fs::read_to_string(input)
        .map_err(|e| format!("读取 {} 失败：{e}", input.display()))?;
    let v: Value =
        serde_json::from_str(&raw).map_err(|e| format!("JSON 解析失败：{e}"))?;

    let nbformat = v.get("nbformat").and_then(|x| x.as_u64()).unwrap_or(0);
    if nbformat < 4 {
        return Err(format!(
            "nbformat {nbformat} 不支持，当前只支持 nbformat 4.x"
        ));
    }
    let cells = v
        .get("cells")
        .and_then(|c| c.as_array())
        .ok_or("找不到 cells 数组（不是有效的 nbformat 4 notebook）")?;

    // 语言在根 metadata.language_info.name（Jupyter 的标准位置）
    let lang = v
        .pointer("/metadata/language_info/name")
        .and_then(|x| x.as_str())
        .unwrap_or("");

    let mut out = String::new();
    for c in cells {
        let md = cell_to_md(c, lang);
        if !md.trim().is_empty() {
            out.push_str(md.trim());
            out.push_str("\n\n");
        }
    }
    if out.trim().is_empty() {
        Err("Notebook 没有可提取的文本内容".to_string())
    } else {
        Ok(out.trim().to_string() + "\n")
    }
}
