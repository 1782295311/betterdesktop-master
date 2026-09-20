//! Markdown -> IPYNB（Jupyter Notebook，替代 Pandoc 的 ipynb writer）。
//!
//! 按 ``` 围栏代码块切分：代码块 → code cell（无输出，execution_count null），
//! 其余连续文本 → markdown cell。四空格缩进代码块保留在 markdown cell 原文中。
//!
//! 诚实边界：md 内的图片/链接/表格等原样留在 markdown cell（Jupyter 可渲染 md）；
//!           不生成 outputs/执行计数；kernelspec 用默认 Python3 占位（metadata 可改）。

use std::path::Path;

use serde_json::{json, Value};

/// 行是否围栏代码块开始/闭合，返回语言（仅开行有）。
fn fence_lang(line: &str) -> Option<&str> {
    let t = line.trim_start();
    if t.starts_with("```") {
        Some(t[3..].trim())
    } else {
        None
    }
}

fn split_cells(md: &str) -> Vec<Value> {
    let lines: Vec<&str> = md.lines().collect();
    let mut cells: Vec<Value> = Vec::new();
    let mut md_buf = String::new();
    let mut i = 0usize;

    macro_rules! flush_md {
        () => {{
            if !md_buf.trim().is_empty() {
                cells.push(json!({
                    "cell_type": "markdown",
                    "metadata": {},
                    "source": md_buf.trim().to_string()
                }));
            }
            md_buf.clear();
        }};
    }

    while i < lines.len() {
        let line = lines[i];
        if fence_lang(line).is_some() {
            flush_md!();
            let lang = fence_lang(line).unwrap_or("").to_string();
            let mut code = String::new();
            i += 1;
            while i < lines.len() && fence_lang(lines[i]).is_none() {
                code.push_str(lines[i]);
                code.push('\n');
                i += 1;
            }
            if i < lines.len() {
                i += 1; // 跳过闭合围栏
            }
            while code.ends_with('\n') {
                code.pop();
            }
            let mut meta = json!({});
            if !lang.is_empty() {
                meta = json!({ "language": lang });
            }
            cells.push(json!({
                "cell_type": "code",
                "execution_count": null,
                "metadata": meta,
                "outputs": [],
                "source": code
            }));
        } else {
            md_buf.push_str(line);
            md_buf.push('\n');
            i += 1;
        }
    }
    flush_md!();
    cells
}

/// Markdown 文件 -> IPYNB 文本（nbformat 4）。
pub fn md_to_ipynb(input: &Path) -> Result<String, String> {
    let md = std::fs::read_to_string(input)
        .map_err(|e| format!("读取 {} 失败：{e}", input.display()))?;
    let cells = split_cells(&md);
    let nb = json!({
        "cells": cells,
        "metadata": {
            "kernelspec": {
                "display_name": "Python 3",
                "language": "python",
                "name": "python3"
            },
            "language_info": {
                "name": "python",
                "version": "3.12.1"
            }
        },
        "nbformat": 4,
        "nbformat_minor": 5
    });
    serde_json::to_string_pretty(&nb).map_err(|e| e.to_string()).map(|s| s + "\n")
}
