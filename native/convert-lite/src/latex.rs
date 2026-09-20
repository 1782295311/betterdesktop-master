//! Markdown <-> LaTeX（替代 Pandoc 的 latex reader/writer 实用子集）。
//!
//! md→latex：pulldown-cmark 事件流 → LaTeX（标题/粗斜体/代码块/列表/表格/链接/图片/引用）。
//! latex→md：手写实用子集解析（\section、\textbf、\emph、\texttt、itemize/enumerate、
//!           verbatim/lstlisting、tabular、\href、\url、quote、注释、转义还原）。
//!
//! 诚实边界：公式 $...$ / \[...\] 保留 LaTeX 原文（渲染需浏览器/KaTeX，本实验不做）；
//!           不解析自定义宏/命令参数嵌套；tabular 不处理合并列（multicolumn 忽略）；
//!           md→latex 的表格只输出 tabular 骨架。

use std::path::Path;

use pulldown_cmark::{Event, HeadingLevel, Options, Parser, Tag, TagEnd};

/// LaTeX 特殊字符转义（在代码块/verbatim 外）。
fn esc_latex(s: &str) -> String {
    let mut out = String::with_capacity(s.len());
    for c in s.chars() {
        match c {
            '\\' => out.push_str("\\textbackslash{}"),
            '{' => out.push_str("\\{"),
            '}' => out.push_str("\\}"),
            '$' => out.push_str("\\$"),
            '&' => out.push_str("\\&"),
            '#' => out.push_str("\\#"),
            '%' => out.push_str("\\%"),
            '_' => out.push_str("\\_"),
            '^' => out.push_str("\\textasciicircum{}"),
            '~' => out.push_str("\\textasciitilde{}"),
            _ => out.push(c),
        }
    }
    out
}

/// Markdown 文件 -> LaTeX。
pub fn md_to_latex(input: &Path) -> Result<String, String> {
    let md = std::fs::read_to_string(input)
        .map_err(|e| format!("读取 {} 失败：{e}", input.display()))?;
    let parser = Parser::new_ext(&md, Options::ENABLE_TABLES | Options::ENABLE_STRIKETHROUGH);
    let mut out = String::from("% 由 convert-lite 生成（md → latex 实用子集）\n");
    let mut list_stack: Vec<&str> = Vec::new(); // itemize / enumerate
    let mut table_buf: Vec<Vec<String>> = Vec::new();
    let mut cur_row: Vec<String> = Vec::new();
    let mut cur_cell = String::new();
    let mut in_table = false;
    let mut in_code = false;

    for ev in parser {
        match ev {
            Event::Start(Tag::Heading { level, .. }) => {
                let cmd = match level {
                    HeadingLevel::H1 => "section",
                    HeadingLevel::H2 => "subsection",
                    HeadingLevel::H3 => "subsubsection",
                    _ => "paragraph",
                };
                out.push_str(&format!("\\{cmd}{{"));
            }
            Event::End(TagEnd::Heading(_)) => {
                out.push_str("}\n\n");
            }
            Event::Start(Tag::Paragraph) => {}
            Event::End(TagEnd::Paragraph) => out.push_str("\n\n"),
            Event::Text(t) => {
                if in_table {
                    cur_cell.push_str(&esc_latex(&t));
                } else if in_code {
                    out.push_str(&t);
                } else {
                    out.push_str(&esc_latex(&t));
                }
            }
            Event::SoftBreak => out.push(' '),
            Event::HardBreak => out.push_str("\\\\\n"),
            Event::Start(Tag::Strong) => out.push_str("\\textbf{"),
            Event::End(TagEnd::Strong) => out.push('}'),
            Event::Start(Tag::Emphasis) => out.push_str("\\emph{"),
            Event::End(TagEnd::Emphasis) => out.push('}'),
            Event::Start(Tag::Strikethrough) => out.push_str("\\sout{"),
            Event::End(TagEnd::Strikethrough) => out.push('}'),
            Event::Code(t) => {
                if in_code {
                    out.push_str(&t);
                } else {
                    out.push_str(&format!("\\texttt{{{}}}", esc_latex(&t)));
                }
            }
            Event::Start(Tag::CodeBlock(_)) => {
                in_code = true;
                out.push_str("\\begin{verbatim}\n");
            }
            Event::End(TagEnd::CodeBlock) => {
                in_code = false;
                out.push_str("\n\\end{verbatim}\n\n");
            }
            Event::Start(Tag::Link { dest_url, .. }) => {
                out.push_str(&format!("\\href{{{}}}{{", esc_latex(&dest_url)));
            }
            Event::End(TagEnd::Link) => out.push('}'),
            Event::Start(Tag::Image { dest_url, .. }) => {
                out.push_str(&format!(
                    "\\includegraphics[width=0.8\\linewidth]{{{}}}",
                    esc_latex(&dest_url)
                ));
            }
            Event::End(TagEnd::Image) => {}
            Event::Start(Tag::BlockQuote(_)) => out.push_str("\\begin{quote}\n"),
            Event::End(TagEnd::BlockQuote(_)) => out.push_str("\n\\end{quote}\n\n"),
            Event::Rule => out.push_str("\\noindent\\rule{\\linewidth}{0.4pt}\n\n"),
            Event::Start(Tag::List(start)) => {
                if start.is_some() {
                    out.push_str("\\begin{enumerate}\n");
                    list_stack.push("enumerate");
                } else {
                    out.push_str("\\begin{itemize}\n");
                    list_stack.push("itemize");
                }
            }
            Event::End(TagEnd::List(_)) => {
                if let Some(kind) = list_stack.pop() {
                    out.push_str(&format!("\\end{{{kind}}}\n\n"));
                }
            }
            Event::Start(Tag::Item) => out.push_str("  \\item "),
            Event::End(TagEnd::Item) => out.push('\n'),
            Event::Start(Tag::Table(_)) => {
                table_buf.clear();
                cur_row.clear();
                in_table = true;
            }
            Event::End(TagEnd::Table) => {
                in_table = false;
                if !table_buf.is_empty() {
                    let cols = table_buf.iter().map(|r| r.len()).max().unwrap_or(1).max(1);
                    out.push_str(&format!("\\begin{{tabular}}{{{}}}\n", "c".repeat(cols)));
                    for (ri, row) in table_buf.iter().enumerate() {
                        let cells: Vec<String> =
                            (0..cols).map(|c| row.get(c).cloned().unwrap_or_default()).collect();
                        out.push_str(&format!("  {} \\\\\n", cells.join(" & ")));
                        if ri == 0 {
                            out.push_str("  \\hline\n");
                        }
                    }
                    out.push_str("\\end{tabular}\n\n");
                }
                table_buf.clear();
            }
            Event::Start(Tag::TableRow) => {
                cur_row.clear();
            }
            Event::End(TagEnd::TableRow) => {
                if !cur_row.is_empty() {
                    table_buf.push(std::mem::take(&mut cur_row));
                }
            }
            Event::Start(Tag::TableCell) => cur_cell.clear(),
            Event::End(TagEnd::TableCell) => cur_row.push(std::mem::take(&mut cur_cell)),
            _ => {}
        }
    }
    if out.trim().is_empty() {
        Err("没有可转换的 Markdown 内容".to_string())
    } else {
        Ok(out)
    }
}

/// 还原 LaTeX 转义。
fn unesc_tex(s: &str) -> String {
    s.replace("\\textbackslash{}", "\\")
        .replace("\\textasciicircum{}", "^")
        .replace("\\textasciitilde{}", "~")
        .replace("\\{", "{")
        .replace("\\}", "}")
        .replace("\\$", "$")
        .replace("\\&", "&")
        .replace("\\#", "#")
        .replace("\\%", "%")
        .replace("\\_", "_")
}

/// 解析 \cmd{...} 返回 (cmd, 内容, 剩余)。
fn take_command(s: &str) -> Option<(&str, String, &str)> {
    if !s.starts_with('\\') {
        return None;
    }
    let rest = &s[1..];
    let end = rest.find(|c: char| !c.is_ascii_alphabetic()).unwrap_or(rest.len());
    if end == 0 {
        return None;
    }
    let cmd = &rest[..end];
    let after = &rest[end..];
    if let Some(brace) = after.strip_prefix('{') {
        if let Some(close) = brace.find('}') {
            return Some((cmd, brace[..close].to_string(), &brace[close + 1..]));
        }
    }
    None
}

/// 行内 LaTeX -> Markdown（\textbf \emph \textit \texttt \sout \href \url 与公式保留）。
fn inline_latex_to_md(s: &str) -> String {
    let mut out = String::new();
    let mut rest = s;
    while !rest.is_empty() {
        if let Some((cmd, content, tail)) = take_command(rest) {
            match cmd {
                "textbf" => {
                    out.push_str(&format!("**{}**", inline_latex_to_md(&content)));
                    rest = tail;
                    continue;
                }
                "textit" | "emph" => {
                    out.push_str(&format!("*{}*", inline_latex_to_md(&content)));
                    rest = tail;
                    continue;
                }
                "texttt" => {
                    out.push_str(&format!("`{}`", inline_latex_to_md(&content)));
                    rest = tail;
                    continue;
                }
                "sout" => {
                    out.push_str(&format!("~~{}~~", inline_latex_to_md(&content)));
                    rest = tail;
                    continue;
                }
                "href" => {
                    // \href{url}{text}
                    let url = content.trim().to_string();
                    let mut tail2 = tail;
                    if let Some((text, tail3)) = brace_content(tail2) {
                        out.push_str(&format!("[{}]({})", inline_latex_to_md(&text), url));
                        tail2 = tail3;
                    } else {
                        out.push_str(&format!("[{}]({})", url, url));
                    }
                    rest = tail2;
                    continue;
                }
                "url" => {
                    out.push_str(&format!("<{}>", content.trim()));
                    rest = tail;
                    continue;
                }
                _ => {
                    // 未知命令（含 \ref \cite 等）：输出参数内容
                    out.push_str(&inline_latex_to_md(&content));
                    rest = tail;
                    continue;
                }
            }
        }
        let ch = rest.chars().next().unwrap();
        out.push(ch);
        rest = &rest[ch.len_utf8()..];
    }
    unesc_tex(&out)
}

/// 抽取命令参数内容（不做嵌套）。
fn brace_content(s: &str) -> Option<(String, &str)> {
    let t = s.trim_start();
    if let Some(rest) = t.strip_prefix('{') {
        if let Some(close) = rest.find('}') {
            return Some((rest[..close].to_string(), &rest[close + 1..]));
        }
    }
    None
}

/// LaTeX 文件 -> Markdown。
pub fn latex_to_md(input: &Path) -> Result<String, String> {
    let tex = std::fs::read_to_string(input)
        .map_err(|e| format!("读取 {} 失败：{e}", input.display()))?;
    let lines: Vec<&str> = tex.lines().collect();
    let mut out = String::new();
    let mut i = 0usize;

    while i < lines.len() {
        let line = lines[i].trim();
        // 注释行（% 开头，非 " % " 段落文本）
        if line.starts_with('%') {
            i += 1;
            continue;
        }
        // 文档头/结束
        if line.starts_with("\\documentclass") || line.starts_with("\\usepackage")
            || line.starts_with("\\begin{document}") || line.starts_with("\\end{document}")
            || line.starts_with("\\author{") || line.starts_with("\\date{")
            || line.starts_with("\\maketitle") || line.starts_with("\\begin{abstract}")
            || line.starts_with("\\end{abstract}")
        {
            i += 1;
            continue;
        }
        if line.starts_with("\\title{") {
            if let Some((t, _)) = brace_content(&line[6..]) {
                out.push_str(&format!("# {}\n\n", inline_latex_to_md(&t)));
            }
            i += 1;
            continue;
        }
        // 标题（支持 * 变体）
        let mut heading_done = false;
        for (cmd, level) in [
            ("section", 1usize),
            ("subsection", 2),
            ("subsubsection", 3),
            ("paragraph", 4),
        ] {
            let star = format!("\\{cmd}*{{");
            let plain = format!("\\{cmd}{{");
            if line.starts_with(&star) || line.starts_with(&plain) {
                let body = if line.starts_with(&star) { &line[star.len()..] } else { &line[plain.len()..] };
                let t = body.trim_end_matches('}').trim();
                if !t.is_empty() {
                    out.push_str(&format!("{} {}\n\n", "#".repeat(level), inline_latex_to_md(t)));
                }
                heading_done = true;
                break;
            }
        }
        if heading_done {
            i += 1;
            continue;
        }
        // verbatim / lstlisting 代码块
        if line.starts_with("\\begin{verbatim}") || line.starts_with("\\begin{lstlisting}")
            || line.starts_with("\\begin{Verbatim}")
        {
            let mut buf = String::new();
            i += 1;
            while i < lines.len()
                && !lines[i].trim().starts_with("\\end{verbatim}")
                && !lines[i].trim().starts_with("\\end{lstlisting}")
                && !lines[i].trim().starts_with("\\end{Verbatim}")
            {
                buf.push_str(lines[i]);
                buf.push('\n');
                i += 1;
            }
            if i < lines.len() {
                i += 1;
            }
            let body = buf.trim_end();
            out.push_str("```\n");
            out.push_str(body);
            out.push_str("\n```\n\n");
            continue;
        }
        // quote 引用
        if line.starts_with("\\begin{quote}") {
            i += 1;
            while i < lines.len() && !lines[i].trim().starts_with("\\end{quote}") {
                out.push_str(&format!("> {}\n", inline_latex_to_md(lines[i].trim())));
                i += 1;
            }
            if i < lines.len() {
                i += 1;
            }
            out.push('\n');
            continue;
        }
        // itemize / enumerate
        if line.starts_with("\\begin{itemize}") || line.starts_with("\\begin{enumerate}") {
            let ordered = line.starts_with("\\begin{enumerate}");
            let end_env = if ordered { "\\end{enumerate}" } else { "\\end{itemize}" };
            i += 1;
            while i < lines.len() && !lines[i].trim().starts_with(end_env) {
                let l = lines[i].trim();
                if let Some(item) = l.strip_prefix("\\item") {
                    let content = item.trim();
                    let bullet = if ordered { "1." } else { "-" };
                    out.push_str(&format!("{bullet} {}\n", inline_latex_to_md(content)));
                }
                i += 1;
            }
            if i < lines.len() {
                i += 1;
            }
            out.push('\n');
            continue;
        }
        // tabular 表格
        if line.starts_with("\\begin{tabular}") {
            let mut rows: Vec<Vec<String>> = Vec::new();
            i += 1;
            while i < lines.len() && !lines[i].trim().starts_with("\\end{tabular}") {
                let l = lines[i].trim();
                if l.starts_with("\\hline") || l.is_empty() {
                    i += 1;
                    continue;
                }
                let body = l.trim_end_matches('\\').trim();
                let cells: Vec<String> = body
                    .split('&')
                    .map(|c| inline_latex_to_md(c.trim()))
                    .collect();
                if !cells.is_empty() {
                    rows.push(cells);
                }
                i += 1;
            }
            if i < lines.len() {
                i += 1;
            }
            if !rows.is_empty() {
                let max_cols = rows.iter().map(|r| r.len()).max().unwrap_or(0);
                for (ri, row) in rows.iter().enumerate() {
                    let cells: Vec<String> = (0..max_cols)
                        .map(|c| row.get(c).cloned().unwrap_or_default())
                        .collect();
                    out.push_str(&format!("| {} |\n", cells.join(" | ")));
                    if ri == 0 {
                        out.push_str(&format!("|{}\n", " --- |".repeat(max_cols)));
                    }
                }
                out.push('\n');
            }
            continue;
        }
        // 公式环境：原样保留为 ```latex 块（诚实边界：不渲染）
        if line.starts_with("\\[") || line.starts_with("$$")
            || line.starts_with("\\begin{equation}") || line.starts_with("\\begin{align")
        {
            let end_markers = ["\\]", "$$", "\\end{equation}", "\\end{align}"];
            let mut buf = String::new();
            buf.push_str(line);
            buf.push('\n');
            i += 1;
            while i < lines.len() {
                let l = lines[i].trim();
                if end_markers.iter().any(|m| l.starts_with(m)) {
                    buf.push_str(l);
                    buf.push('\n');
                    i += 1;
                    break;
                }
                buf.push_str(lines[i]);
                buf.push('\n');
                i += 1;
            }
            out.push_str(&format!("```latex\n{}```\n\n", buf.trim_end()));
            continue;
        }
        // 空行
        if line.is_empty() {
            out.push('\n');
            i += 1;
            continue;
        }
        // 普通行
        out.push_str(&inline_latex_to_md(line));
        out.push('\n');
        if i + 1 < lines.len() {
            let next = lines[i + 1].trim();
            if !next.is_empty() && !next.starts_with('\\') && !next.starts_with('%') {
                out.push('\n');
            }
        }
        i += 1;
    }
    let collapsed = out.split('\n').fold(String::new(), |mut acc, line| {
        if line.trim().is_empty() && acc.ends_with("\n\n") {
            return acc;
        }
        acc.push_str(line);
        acc.push('\n');
        acc
    });
    if collapsed.trim().is_empty() {
        Err("LaTeX 没有可提取的文本内容".to_string())
    } else {
        Ok(collapsed.trim().to_string() + "\n")
    }
}
