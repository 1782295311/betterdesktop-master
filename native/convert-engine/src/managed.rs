// S6 纯托管文本转换（C# ManagedEngine + MarkdownTransformer 等价，红线 9 能力诚实）：
// md/html/txt/log/json/xml/yaml/csv/tsv 互转，输出统一 UTF-8 无 BOM 落盘。
// html→md 为基础子集（ReverseMarkdown 等价，能力边界注释声明；差异接受度见计划 §12 O2）。
use std::path::Path;

use crate::error::Error;

// ———————————————————————— Markdown 基础（MarkdownTransformer 等价） ————————————————————————

/// YAML front matter 识别（文件开头 --- ... --- 块）并剥离；返回（正文, 是否有 front matter）。
pub fn strip_front_matter(markdown: &str) -> (&str, bool) {
    // 首行必须恰好 "---"（允许尾随空白），随后任意行，直到一行 "---" 结束
    let mut lines = markdown.split_inclusive('\n');
    let Some(first) = lines.next() else { return (markdown, false) };
    if first.trim_end().trim() != "---" {
        return (markdown, false);
    }
    let mut body_start = first.len();
    let mut found = false;
    for line in lines {
        if line.trim_end().trim() == "---" {
            found = true;
            body_start += line.len();
            break;
        }
        body_start += line.len();
    }
    if found { (&markdown[body_start..], true) } else { (markdown, false) }
}

/// front matter 文本（无则 None）。
pub fn extract_front_matter(markdown: &str) -> Option<String> {
    let (body, had) = strip_front_matter(markdown);
    if !had { return None; }
    let len = markdown.len() - body.len();
    Some(markdown[..len].to_string())
}

/// front matter → 标题区块（按 key: value 逐行；无冒号行忽略）。
pub fn front_matter_to_heading_block(front: &str) -> String {
    let mut out = String::new();
    for raw in front.split('\n') {
        let l = raw.trim();
        if l.is_empty() { continue; }
        if let Some(idx) = l.find(':') {
            out.push_str(&format!("- **{}**: {}\n", l[..idx].trim(), l[idx + 1..].trim()));
        }
    }
    out.push('\n');
    out
}

/// 按模式应用 front matter（默认 Strip；Heading 转标题区块）。
pub fn apply_front_matter(markdown: &str, heading: bool) -> String {
    let (body, had) = strip_front_matter(markdown);
    if !had || !heading {
        return body.to_string();
    }
    let front = extract_front_matter(markdown).unwrap_or_default();
    format!("{}{}", front_matter_to_heading_block(&front), body)
}

/// md → html 片段（pulldown-cmark，Options 全开：表格/任务列表/脚注等，对齐 Markdig Pipeline）。
pub fn markdown_to_html_body(markdown: &str) -> String {
    let parser = pulldown_cmark::Parser::new_ext(
        markdown,
        pulldown_cmark::Options::all(),
    );
    let mut html = String::new();
    pulldown_cmark::html::push_html(&mut html, parser);
    html
}

/// 独立 HTML 页（红线 8：内嵌 CSS 脱离程序可开；CJK 字体栈；与 C# BuildStandaloneHtml 同构）。
pub fn build_standalone_html(title: &str, body: &str) -> String {
    format!(
        "<!DOCTYPE html>\n<html lang=\"zh-CN\">\n<head>\n<meta charset=\"utf-8\">\n<title>{title}</title>\n\
         <style>\nbody {{ font-family: \"Segoe UI\", \"Microsoft YaHei\", \"PingFang SC\", sans-serif; line-height: 1.65; color: #1f2328; max-width: 860px; margin: 2em auto; padding: 0 1.2em; }}\n\
         h1, h2, h3, h4, h5, h6 {{ line-height: 1.3; margin-top: 1.4em; }}\n\
         code, pre {{ font-family: Consolas, \"Courier New\", monospace; background: #f6f8fa; border-radius: 6px; }}\n\
         code {{ padding: .15em .4em; }}\npre {{ padding: .8em 1em; overflow: auto; }}\npre code {{ background: none; padding: 0; }}\n\
         table {{ border-collapse: collapse; margin: 1em 0; }}\nth, td {{ border: 1px solid #d0d7de; padding: .4em .8em; }}\nth {{ background: #f6f8fa; }}\n\
         blockquote {{ border-left: 4px solid #d0d7de; margin: 1em 0; padding: .2em 1em; color: #59636e; }}\nimg {{ max-width: 100%; }}\n\
         ul.task-list {{ list-style: none; padding-left: 1.2em; }}\ninput[type=\"checkbox\"] {{ margin-right: .4em; }}\nhr {{ border: none; border-top: 1px solid #d0d7de; margin: 2em 0; }}\n\
         </style>\n</head>\n<body>\n{body}\n</body>\n</html>",
        title = html_escape(title),
    )
}

/// html → 纯文本（script/style 剔除；块级标签转行；实体解码；3+ 连续空行压为 2）。
pub fn html_to_text(html: &str) -> String {
    // 1) 剔除 script/style 块
    let mut text = remove_script_style(html);
    // 2) <br> → 换行
    let mut out = String::with_capacity(text.len());
    let chars: Vec<char> = text.chars().collect();
    let mut i = 0;
    while i < chars.len() {
        if chars[i] == '<' {
            let rest: String = chars[i..].iter().take(12).collect();
            let lower = rest.to_lowercase();
            if lower.starts_with("<br") || lower.starts_with("</p>") || lower.starts_with("</div>")
                || lower.starts_with("</h") || lower.starts_with("</li>") || lower.starts_with("</tr>")
                || lower.starts_with("</table>") || lower.starts_with("</blockquote>") || lower.starts_with("</pre>")
            {
                out.push('\n');
                i += 1;
                continue;
            }
        }
        out.push(chars[i]);
        i += 1;
    }
    // 3) 剩余标签剔除
    let mut text2 = String::new();
    let mut in_tag = false;
    for c in out.chars() {
        if c == '<' { in_tag = true; }
        if !in_tag { text2.push(c); }
        if c == '>' { in_tag = false; }
    }
    // 4) 实体解码
    let text3 = html_unescape(&text2);
    // 5) 3+ 连续空行压为 2
    let mut result = String::new();
    let mut blank = 0u32;
    for line in text3.split_inclusive('\n') {
        if line.trim().is_empty() {
            blank += 1;
            if blank <= 2 { result.push_str(line); }
        } else {
            blank = 0;
            result.push_str(line);
        }
    }
    format!("{}\n", result.trim())
}

fn remove_script_style(s: &str) -> String {
    let mut out = String::new();
    let chars: Vec<char> = s.chars().collect();
    let mut i = 0;
    while i < chars.len() {
        if chars[i] == '<' {
            let rest: String = chars[i..].iter().take(8).map(|c| c.to_ascii_lowercase()).collect();
            if rest.starts_with("<script") || rest.starts_with("<style") {
                // 跳到闭合标签
                let closer = if rest.starts_with("<script") { "</script>" } else { "</style>" };
                if let Some(pos) = s[i..].to_lowercase().find(closer) {
                    i += pos + closer.len();
                    continue;
                }
            }
        }
        out.push(chars[i]);
        i += 1;
    }
    out
}

/// 纯文本 → 简单 html 段落（转义后按空行分段——诚实能力：无 md 语法解释）。
pub fn plain_text_to_html(text: &str) -> String {
    let norm = text.replace("\r\n", "\n");
    let mut out = String::new();
    let mut first = true;
    for para in norm.split("\n\n") {
        if para.trim().is_empty() { continue; }
        if !first { out.push('\n'); }
        first = false;
        let escaped = html_escape(para);
        let with_br = escaped.replace('\n', "<br>\n");
        out.push_str(&format!("<p>{with_br}</p>"));
    }
    out
}

/// md → 独立 HTML（final 导出与两跳中转共用；resolve_images_against 时相对图片转绝对 file URI，红线 7）。
pub fn markdown_to_standalone_html(markdown: &str, title: &str, resolve_images_against: Option<&Path>) -> String {
    let body = markdown_to_html_body(&apply_front_matter(markdown, false));
    let body = match resolve_images_against {
        Some(dir) => resolve_relative_image_srcs(&body, dir),
        None => body,
    };
    build_standalone_html(title, &body)
}

/// 解析 html 中相对图片路径（红线 7）：相对 src → 绝对 file URI；缺失图片保持原样（告警不阻断）。
/// 无 regex 依赖：手写扫描 <img ... src="...">。
pub fn resolve_relative_image_srcs(html: &str, base_dir: &Path) -> String {
    let mut out = String::new();
    let bytes: Vec<char> = html.chars().collect();
    let mut i = 0;
    while i < bytes.len() {
        if bytes[i] == '<' && matches_next(&bytes, i, "<img") {
            // 找该标签内 src="..."（同一 < > 范围内）
            let tag_end = find_char(&bytes, i, '>');
            let tag_end = tag_end.unwrap_or(bytes.len());
            let src_start = find_sub(&bytes, i, tag_end, "src=\"");
            if let Some(mut s) = src_start {
                s += "src=\"".len();
                let src_end = find_char(&bytes, s, '"').unwrap_or(s);
                let src: String = bytes[s..src_end].iter().collect();
                let resolved = resolve_one_src(&src, base_dir);
                // 输出标签前缀 + 替换后的 src + 继续
                let prefix: String = bytes[i..s].iter().collect();
                out.push_str(&prefix);
                out.push_str(&resolved);
                let tail_start = if src_end < bytes.len() { src_end } else { s };
                // 剩余 tag 部分（到 >）
                let rest: String = bytes[tail_start..tag_end].iter().collect();
                out.push_str(&rest);
                i = tag_end;
                continue;
            }
        }
        out.push(bytes[i]);
        i += 1;
    }
    out
}

fn resolve_one_src(src: &str, base_dir: &Path) -> String {
    let lower = src.to_lowercase();
    if src.starts_with('#') || lower.starts_with("data:") || is_absolute_uri(src) {
        return src.to_string();
    }
    let unescaped = percent_decode(src);
    let absolute = base_dir.join(&unescaped);
    if !absolute.is_file() {
        return src.to_string(); // 图片缺失(告警不阻断): 保持原样
    }
    let abs_str = absolute.to_string_lossy().replace('\\', "/");
    format!("file:///{}", abs_str.trim_start_matches('/'))
}

fn is_absolute_uri(s: &str) -> bool {
    let bytes = s.as_bytes();
    // scheme:// 或 file:/
    if s.starts_with("file:") { return true; }
    let mut i = 0;
    while i < bytes.len() && bytes[i].is_ascii_alphabetic() { i += 1; }
    i > 0 && i + 2 < bytes.len() && &s[i..i + 3] == "://"
}

fn percent_decode(s: &str) -> String {
    let bytes = s.as_bytes();
    let mut out: Vec<u8> = Vec::with_capacity(bytes.len());
    let mut i = 0;
    while i < bytes.len() {
        if bytes[i] == b'%' && i + 2 < bytes.len() {
            if let (Ok(h), Ok(l)) = (
                u8::from_str_radix(&s[i + 1..i + 2], 16),
                u8::from_str_radix(&s[i + 2..i + 3], 16),
            ) {
                out.push(h * 16 + l);
                i += 3;
                continue;
            }
        }
        out.push(bytes[i]);
        i += 1;
    }
    String::from_utf8_lossy(&out).to_string()
}

fn matches_next(chars: &[char], i: usize, pat: &str) -> bool {
    let p: Vec<char> = pat.chars().collect();
    if i + p.len() > chars.len() { return false; }
    chars[i..i + p.len()] == p[..]
}

fn find_char(chars: &[char], from: usize, c: char) -> Option<usize> {
    (from..chars.len()).find(|&i| chars[i] == c)
}

fn find_sub(chars: &[char], from: usize, to: usize, pat: &str) -> Option<usize> {
    let p: Vec<char> = pat.chars().collect();
    if to > chars.len() { return None; }
    (from..=to.saturating_sub(p.len())).find(|&i| chars[i..i + p.len()] == p[..])
}

pub fn html_escape(s: &str) -> String {
    let mut out = String::with_capacity(s.len());
    for c in s.chars() {
        match c {
            '&' => out.push_str("&amp;"),
            '<' => out.push_str("&lt;"),
            '>' => out.push_str("&gt;"),
            '"' => out.push_str("&quot;"),
            _ => out.push(c),
        }
    }
    out
}

fn html_unescape(s: &str) -> String {
    s.replace("&amp;", "&")
        .replace("&lt;", "<")
        .replace("&gt;", ">")
        .replace("&quot;", "\"")
        .replace("&#39;", "'")
        .replace("&nbsp;", " ")
}

// ———————————————————————— 分隔文本（CSV/TSV，RFC4180 引号感知） ————————————————————————

fn separator_of(ext: &str) -> u8 {
    if ext.eq_ignore_ascii_case(".tsv") { b'\t' } else { b',' }
}

/// RFC4180 解析（csv crate；引号感知、"" 转义、字段内换行；与 C# 手写语义等价）。
fn parse_delimited(text: &str, separator: u8) -> Vec<Vec<String>> {
    let mut rdr = csv::ReaderBuilder::new()
        .has_headers(false)
        .flexible(true)
        .delimiter(separator)
        .from_reader(text.as_bytes());
    let mut rows = Vec::new();
    for rec in rdr.records() {
        if let Ok(rec) = rec {
            rows.push(rec.iter().map(|f| f.to_string()).collect());
        }
    }
    rows
}

fn escape_pipe(s: &str) -> String { s.replace('|', "\\|") }

/// csv → md 表格。
fn csv_to_markdown(text: &str, separator: u8) -> String {
    let rows = parse_delimited(text, separator);
    if rows.is_empty() { return String::new(); }
    let mut out = String::new();
    let header: Vec<String> = rows[0].iter().map(|c| escape_pipe(c)).collect();
    out.push_str(&format!("|{}|\n", header.join("|")));
    out.push_str(&format!("|{}|\n", vec!["---"; rows[0].len()].join("|")));
    for row in rows.iter().skip(1) {
        let cells: Vec<String> = row.iter().map(|c| escape_pipe(c)).collect();
        out.push_str(&format!("|{}|\n", cells.join("|")));
    }
    out
}

/// csv → html 表格（首行 th）。
fn csv_to_html_table(text: &str, separator: u8) -> String {
    let rows = parse_delimited(text, separator);
    if rows.is_empty() { return String::new(); }
    let mut out = String::from("<table>");
    for (r, row) in rows.iter().enumerate() {
        out.push_str("<tr>");
        let tag = if r == 0 { "th" } else { "td" };
        for cell in row {
            out.push_str(&format!("<{tag}>{}</{tag}>", html_escape(cell)));
        }
        out.push_str("</tr>");
    }
    out.push_str("</table>");
    out
}

/// csv → 纯文本（" | " 连接）。
fn csv_to_plain_text(text: &str, separator: u8) -> String {
    let rows = parse_delimited(text, separator);
    let mut out = String::new();
    for row in rows {
        out.push_str(&format!("{}\n", row.join(" | ")));
    }
    out
}

/// RFC4180 字段转义：含分隔符/引号/换行/tab 时双引号包裹、内部引号翻倍。
fn escape_csv(s: &str, separator: char) -> String {
    let needs = s.chars().any(|c| c == separator || c == '"' || c == '\r' || c == '\n' || c == '\t');
    if !needs { return s.to_string(); }
    format!("\"{}\"", s.replace('"', "\"\""))
}

/// 标量类型推断（空→null、true/false→bool、整数→i64、小数→f64、否则字符串，同 YAML 语义）。
fn infer_scalar(s: &str) -> serde_json::Value {
    if s.is_empty() { return serde_json::Value::Null; }
    if s == "true" { return serde_json::Value::Bool(true); }
    if s == "false" { return serde_json::Value::Bool(false); }
    if let Ok(l) = s.parse::<i64>() { return serde_json::Value::Number(l.into()); }
    if let Ok(d) = s.parse::<f64>() { return serde_json::Value::Number(serde_json::Number::from_f64(d).unwrap_or(serde_json::Number::from(0))); }
    serde_json::Value::String(s.to_string())
}

/// csv/tsv → JSON 对象数组（列头=键）。
fn csv_to_json(text: &str, separator: u8) -> String {
    let rows = parse_delimited(text, separator);
    if rows.is_empty() { return "[]".to_string(); }
    let header = &rows[0];
    let arr: Vec<serde_json::Value> = rows
        .iter()
        .skip(1)
        .map(|row| {
            let mut obj = serde_json::Map::new();
            for (c, key) in header.iter().enumerate() {
                let cell = row.get(c).map(|s| s.as_str()).unwrap_or("");
                obj.insert(key.clone(), infer_scalar(cell));
            }
            serde_json::Value::Object(obj)
        })
        .collect();
    serde_json::to_string_pretty(&serde_json::Value::Array(arr)).unwrap_or_default()
}

/// JSON → csv/tsv（根须为对象数组；列键取首对象并随后续对象扩展；嵌套对象/数组不导出为空）。
fn json_to_csv(json: &str, separator: u8) -> Result<String, Error> {
    let v: serde_json::Value = serde_json::from_str(json)
        .map_err(|e| Error::conversion_failed(format!("JSON 解析失败: {e}")))?;
    let arr = match v {
        serde_json::Value::Array(a) => a,
        _ => return Err(Error::conversion_failed("JSON 根必须是对象数组才能转换为表格".to_string())),
    };
    let sep = separator as char;
    let mut keys: Vec<String> = Vec::new();
    let mut rows: Vec<serde_json::Map<String, serde_json::Value>> = Vec::new();
    for item in arr {
        let serde_json::Value::Object(map) = item else { continue };
        for k in map.keys() {
            if !keys.contains(k) { keys.push(k.clone()); }
        }
        rows.push(map);
    }
    let mut out = String::new();
    let header: Vec<String> = keys.iter().map(|k| escape_csv(k, sep)).collect();
    out.push_str(&format!("{}\n", header.join(&sep.to_string())));
    for row in rows {
        let cells: Vec<String> = keys
            .iter()
            .map(|k| escape_csv(scalar_text(row.get(k)).as_str(), sep))
            .collect();
        out.push_str(&format!("{}\n", cells.join(&sep.to_string())));
    }
    Ok(out)
}

fn scalar_text(v: Option<&serde_json::Value>) -> String {
    match v {
        None => String::new(),
        Some(serde_json::Value::String(s)) => s.clone(),
        Some(serde_json::Value::Number(n)) => n.to_string(),
        Some(serde_json::Value::Bool(b)) => b.to_string(),
        Some(serde_json::Value::Null) => String::new(),
        Some(_) => String::new(), // 嵌套对象/数组不导出
    }
}

/// Markdown 表格 → csv/tsv（只取含 | 的表格行；分隔行 |---|---| 跳过；无表格报错提示）。
fn md_table_to_csv(text: &str, separator: u8) -> Result<String, Error> {
    let sep = separator as char;
    let mut out = String::new();
    let mut rows = 0u32;
    for line in text.split('\n') {
        if !line.contains('|') { continue; }
        let mut t = line.trim().to_string();
        if t.starts_with('|') && t.ends_with('|') {
            t = t[1..t.len().saturating_sub(1)].to_string();
        }
        let cells: Vec<String> = t.split('|').map(|c| c.trim().to_string()).collect();
        let is_sep = !cells.is_empty()
            && cells.iter().all(|c| !c.is_empty() && c.chars().all(|ch| ch == '-' || ch == ':' || ch == ' '));
        if is_sep { continue; }
        let escaped: Vec<String> = cells.iter().map(|c| escape_csv(c, sep)).collect();
        out.push_str(&format!("{}\n", escaped.join(&sep.to_string())));
        rows += 1;
    }
    if rows == 0 {
        return Err(Error::conversion_failed("未找到 Markdown 表格".to_string()));
    }
    Ok(out)
}

// ———————————————————————— JSON ↔ YAML（C# 手写递归等价） ————————————————————————

/// json → yaml（JSON 是 YAML 1.2 子集；字符串一律单引号保护——防止 "123"/"true" 被推断成标量）。
pub fn json_to_yaml(json: &str) -> Result<String, Error> {
    let v: serde_json::Value = serde_json::from_str(json)
        .map_err(|e| Error::conversion_failed(format!("JSON 解析失败: {e}")))?;
    let mut sb = String::new();
    append_yaml(&mut sb, &v, 0);
    Ok(sb)
}

fn append_yaml(sb: &mut String, el: &serde_json::Value, indent: usize) {
    let pad = " ".repeat(indent);
    match el {
        serde_json::Value::Object(map) => {
            if map.is_empty() { sb.push_str(&format!("{pad}{{}}\n")); return; }
            let mut first = true;
            for (k, v) in map {
                if !first { sb.push('\n'); }
                first = false;
                sb.push_str(&format!("{pad}{}:", quote_yaml(k)));
                append_yaml_value(sb, v, indent);
            }
            sb.push('\n');
        }
        serde_json::Value::Array(arr) => {
            if arr.is_empty() { sb.push_str(&format!("{pad}[]\n")); return; }
            for item in arr {
                sb.push_str(&format!("{pad}-"));
                if matches!(item, serde_json::Value::Object(_) | serde_json::Value::Array(_)) {
                    sb.push('\n');
                    append_yaml(sb, item, indent + 2);
                } else {
                    sb.push(' ');
                    sb.push_str(&yaml_scalar(item));
                    sb.push('\n');
                }
            }
        }
        other => {
            sb.push_str(&format!("{pad}{}\n", yaml_scalar(other)));
        }
    }
}

fn append_yaml_value(sb: &mut String, value: &serde_json::Value, indent: usize) {
    if matches!(value, serde_json::Value::Object(_) | serde_json::Value::Array(_)) {
        sb.push('\n');
        append_yaml(sb, value, indent + 2);
    } else {
        sb.push(' ');
        sb.push_str(&yaml_scalar(value));
        sb.push('\n');
    }
}

fn yaml_scalar(el: &serde_json::Value) -> String {
    match el {
        serde_json::Value::String(s) => quote_yaml(s),
        serde_json::Value::Number(n) => n.to_string(),
        serde_json::Value::Bool(b) => b.to_string(),
        serde_json::Value::Null => "null".to_string(),
        _ => "null".to_string(),
    }
}

fn quote_yaml(s: &str) -> String {
    format!("'{}'", s.replace('\'', "''"))
}

/// yaml → json（serde_yaml；未引号标量按语义推断，带引号字符串保持 string）。
pub fn yaml_to_json(yaml: &str) -> Result<String, Error> {
    let v: serde_json::Value = serde_yaml::from_str(yaml)
        .map_err(|e| Error::conversion_failed(format!("YAML 解析失败: {e}")))?;
    serde_json::to_string_pretty(&v).map_err(|e| Error::conversion_failed(format!("YAML→JSON 失败: {e}")))
}

// ———————————————————————— XML → JSON（quick-xml；C# XDocument 约定等价） ————————————————————————

/// xml → JSON（约定：元素→对象/标量、属性→@name、同名重复元素→数组、混合文本→#text；叶子与属性保持字符串）。
pub fn xml_to_json(xml: &str) -> Result<String, Error> {
    use quick_xml::events::Event;
    use quick_xml::Reader;

    let mut reader = Reader::from_str(xml);
    reader.config_mut().trim_text(false);

    let mut stack: Vec<xml_node::Node> = Vec::new();
    let mut buf = Vec::new();
    let mut root: Option<xml_node::Node> = None;

    loop {
        match reader.read_event_into(&mut buf) {
            Ok(Event::Start(e)) => {
                let name = String::from_utf8_lossy(e.name().as_ref()).to_string();
                let mut attrs = Vec::new();
                for a in e.attributes().flatten() {
                    let key = String::from_utf8_lossy(a.key.as_ref()).to_string();
                    // 跳过命名空间声明
                    if key.starts_with("xmlns") { continue; }
                    // 仅取本地名（去掉前缀）
                    let local = key.rsplit(':').next().unwrap_or(&key).to_string();
                    let value = String::from_utf8_lossy(&a.value).to_string();
                    attrs.push((local, value));
                }
                stack.push(xml_node::Node { name, attrs, children: Vec::new(), text: String::new() });
            }
            Ok(Event::End(_)) => {
                if let Some(node) = stack.pop() {
                    match stack.last_mut() {
                        Some(parent) => parent.children.push(node),
                        None => root = Some(node),
                    }
                }
            }
            Ok(Event::Text(t)) => {
                if let Some(top) = stack.last_mut() {
                    top.text.push_str(&String::from_utf8_lossy(t.as_ref()));
                }
            }
            Ok(Event::Eof) => break,
            Ok(_) => {}
            Err(e) => {
                return Err(Error::conversion_failed(format!("XML 解析失败: {e}")));
            }
        }
        buf.clear();
    }

    let root = root.ok_or_else(|| Error::conversion_failed("XML 根节点为空".to_string()))?;
    let v = xml_element_to_node(&root);
    serde_json::to_string_pretty(&v).map_err(|e| Error::conversion_failed(format!("XML→JSON 失败: {e}")))
}

fn xml_element_to_node(el: &xml_node::Node) -> serde_json::Value {
    if el.children.is_empty() && el.attrs.is_empty() {
        return serde_json::Value::String(el.text.trim().to_string());
    }
    let mut obj = serde_json::Map::new();
    for (k, v) in &el.attrs {
        obj.insert(format!("@{k}"), serde_json::Value::String(v.clone()));
    }
    // 同名子元素分组
    let mut groups: Vec<(String, Vec<&xml_node::Node>)> = Vec::new();
    for c in &el.children {
        match groups.iter_mut().find(|(n, _)| *n == c.name) {
            Some((_, list)) => list.push(c),
            None => groups.push((c.name.clone(), vec![c])),
        }
    }
    for (name, list) in groups {
        let v = if list.len() == 1 {
            xml_element_to_node(list[0])
        } else {
            serde_json::Value::Array(list.iter().map(|n| xml_element_to_node(n)).collect())
        };
        obj.insert(name, v);
    }
    let text = el.text.trim();
    if !text.is_empty() && !el.children.is_empty() {
        obj.insert("#text".to_string(), serde_json::Value::String(text.to_string()));
    }
    serde_json::Value::Object(obj)
}

// 轻量节点（避免与 quick_xml 类型纠缠）
mod xml_node {
    pub struct Node {
        pub name: String,
        pub attrs: Vec<(String, String)>,
        pub children: Vec<Node>,
        pub text: String,
    }
}

// ———————————————————————— html → md（ReverseMarkdown 基础子集；能力边界：表格/列表/行内语法，无复杂嵌套语义） ————————————————————————

/// html → md（基础子集：h1-h6/p/strong/em/a/img/code/pre/ul/ol/li/blockquote/br/table；
/// script/style 剔除；属性值实体解码）。能力诚实：无 ReverseMarkdown 的深度语义（如嵌套列表缩进细节）。
pub fn html_to_markdown(html: &str) -> String {
    let mut out = String::new();
    let mut list_stack: Vec<char> = Vec::new(); // 'u' / 'o'
    let mut ordered = 0u32;
    let mut in_pre = false;
    let mut skip = 0usize;
    let chars: Vec<char> = html.chars().collect();
    let mut i = 0;
    while i < chars.len() {
        if skip > 0 { skip -= 1; i += 1; continue; }
        let c = chars[i];
        if c != '<' {
            out.push(c);
            i += 1;
            continue;
        }
        // 闭合标签：内容已在文本流中，按名补闭合符号（strong/em/code/pre）
        if i + 1 < chars.len() && chars[i + 1] == '/' {
            let close_name = tag_name(&chars, i);
            match close_name.as_str() {
                "strong" | "b" => out.push_str("**"),
                "em" | "i" => out.push('*'),
                "code" => {
                    if !in_pre {
                        out.push('`');
                    }
                }
                "pre" => {
                    in_pre = false;
                    out.push_str("\n```\n");
                }
                _ => {}
            }
            i = find_char(&chars, i, '>').map(|p| p + 1).unwrap_or(chars.len());
            continue;
        }
        let tag = tag_name(&chars, i);
        match tag.as_str() {
            "script" | "style" => {
                // 跳到闭合标签
                let closer = format!("</{}>", tag);
                if let Some(pos) = html[i..].to_lowercase().find(&closer) {
                    i += pos + closer.len();
                } else {
                    i = chars.len();
                }
            }
            "h1" | "h2" | "h3" | "h4" | "h5" | "h6" => {
                if !out.ends_with('\n') && !out.is_empty() { out.push('\n'); }
                let level = tag[1..].parse::<usize>().unwrap_or(1);
                out.push_str(&"#".repeat(level));
                out.push(' ');
                i = skip_tag(&chars, i);
            }
            "p" | "div" => {
                if !out.ends_with('\n') && !out.is_empty() { out.push('\n'); }
                i = skip_tag(&chars, i);
            }
            "br" => {
                out.push('\n');
                i = skip_tag(&chars, i);
            }
            "strong" | "b" => {
                out.push_str("**");
                i = skip_tag(&chars, i);
            }
            "em" | "i" => {
                out.push('*');
                i = skip_tag(&chars, i);
            }
            "code" => {
                if in_pre { i = skip_tag(&chars, i); }
                else {
                    out.push('`');
                    i = skip_tag(&chars, i);
                }
            }
            "pre" => {
                in_pre = !in_pre;
                if in_pre {
                    out.push_str("\n```\n");
                } else {
                    out.push_str("\n```\n");
                }
                i = skip_tag(&chars, i);
            }
            "a" => {
                let href = attr_value(&chars, i, "href");
                out.push('[');
                i = skip_tag(&chars, i);
                // 记下链接文本起点
                let text_start = out.len();
                // 在 </a> 后补 ](href)
                let (new_i, text) = consume_until_close(&chars, i, "a");
                let _ = text_start;
                out.push_str(&text);
                out.push_str(&format!("]({})", href.unwrap_or_default()));
                i = new_i;
                skip = 0;
            }
            "img" => {
                let src = attr_value(&chars, i, "src").unwrap_or_default();
                let alt = attr_value(&chars, i, "alt").unwrap_or_default();
                out.push_str(&format!("![{}]({})", alt, src));
                i = skip_tag(&chars, i);
            }
            "ul" => {
                list_stack.push('u');
                if !out.ends_with('\n') { out.push('\n'); }
                i = skip_tag(&chars, i);
            }
            "ol" => {
                list_stack.push('o');
                ordered = 0;
                if !out.ends_with('\n') { out.push('\n'); }
                i = skip_tag(&chars, i);
            }
            "li" => {
                if !out.ends_with('\n') { out.push('\n'); }
                match list_stack.last() {
                    Some('u') => out.push_str("- "),
                    Some('o') => {
                        ordered += 1;
                        out.push_str(&format!("{}. ", ordered));
                    }
                    _ => out.push_str("- "),
                }
                i = skip_tag(&chars, i);
            }
            "blockquote" => {
                if !out.ends_with('\n') { out.push('\n'); }
                out.push_str("> ");
                i = skip_tag(&chars, i);
            }
            "table" => {
                i = table_to_md(&chars, i, &mut out);
            }
            "hr" => {
                out.push_str("\n---\n");
                i = skip_tag(&chars, i);
            }
            _ => {
                // 未知标签：跳过开始标签，内容照抄
                i = skip_tag(&chars, i);
            }
        }
    }
    // 收尾清理：连续 3+ 空行压为 2
    let mut result = String::new();
    let mut blank = 0u32;
    for line in out.split_inclusive('\n') {
        if line.trim().is_empty() {
            blank += 1;
            if blank <= 2 { result.push_str(line); }
        } else {
            blank = 0;
            result.push_str(line);
        }
    }
    result.trim().to_string() + "\n"
}

/// 跳过开始标签（含属性，直到 >）。
fn skip_tag(chars: &[char], i: usize) -> usize {
    find_char(chars, i, '>').map(|p| p + 1).unwrap_or(chars.len())
}

fn tag_name(chars: &[char], i: usize) -> String {
    let mut name = String::new();
    let mut j = i + 1;
    // 闭合标签 </tag>：跳过 '/'
    if j < chars.len() && chars[j] == '/' {
        j += 1;
    }
    while j < chars.len() {
        let c = chars[j];
        if c == '>' || c == ' ' || c == '\t' || c == '\n' { break; }
        if c == '/' { break; }
        name.push(c.to_ascii_lowercase());
        j += 1;
    }
    name
}

fn attr_value(chars: &[char], i: usize, name: &str) -> Option<String> {
    let end = find_char(chars, i, '>').unwrap_or(chars.len());
    let text: String = chars[i..end].iter().collect();
    let lower = text.to_lowercase();
    let key = format!("{name}=\"");
    let pos = lower.find(&key)?;
    let rest = &text[pos + key.len()..];
    let quote_end = rest.find('"')?;
    Some(html_unescape(&rest[..quote_end]))
}

/// 消费直到闭合标签，返回（新位置, 标签内文本）。
fn consume_until_close(chars: &[char], i: usize, tag: &str) -> (usize, String) {
    let closer = format!("</{tag}");
    let text_end = (i..chars.len())
        .find(|&j| {
            j + closer.len() <= chars.len()
                && chars[j..j + closer.len()].iter().collect::<String>().to_lowercase() == closer
        })
        .unwrap_or(chars.len());
    let inner: String = chars[i..text_end].iter().collect();
    let after = find_char(chars, text_end, '>').map(|p| p + 1).unwrap_or(chars.len());
    (after, inner)
}

/// table → md 表格（th/td 行，| 分隔；分隔行 ---）。
fn table_to_md(chars: &[char], i: usize, out: &mut String) -> usize {
    let table_end = (i..chars.len()).find(|&j| {
        j + 7 <= chars.len() && chars[j..j + 7].iter().collect::<String>().to_lowercase() == "</table>"
    }).unwrap_or(chars.len());
    let inner: String = chars[i..table_end].iter().collect();
    let mut rows: Vec<Vec<String>> = Vec::new();
    for tr in split_tag(&inner, "tr") {
        let mut cells: Vec<String> = Vec::new();
        for td in split_tag(&tr, "td").into_iter().chain(split_tag(&tr, "th")) {
            cells.push(strip_inline_tags(&td));
        }
        if !cells.is_empty() { rows.push(cells); }
    }
    if !rows.is_empty() {
        if !out.ends_with('\n') { out.push('\n'); }
        let header = rows[0].iter().map(|c| c.trim().replace('|', "\\|")).collect::<Vec<_>>();
        out.push_str(&format!("|{}|\n", header.join("|")));
        out.push_str(&format!("|{}|\n", vec!["---"; header.len()].join("|")));
        for row in rows.iter().skip(1) {
            let cells: Vec<String> = row.iter().map(|c| c.trim().replace('|', "\\|")).collect();
            out.push_str(&format!("|{}|\n", cells.join("|")));
        }
        out.push('\n');
    }
    find_char(chars, table_end, '>').map(|p| p + 1).unwrap_or(table_end)
}

fn split_tag(s: &str, tag: &str) -> Vec<String> {
    let open = format!("<{tag}");
    let close = format!("</{tag}>");
    let lower = s.to_lowercase();
    let mut out = Vec::new();
    let mut i = 0;
    while i < s.len() {
        if lower[i..].starts_with(&open) {
            if let Some(end) = lower[i..].find(&close) {
                let seg_start = i + s[i..].find('>').map(|p| p + 1).unwrap_or(0);
                let seg_end = i + end;
                if seg_end > seg_start {
                    out.push(s[seg_start..seg_end].to_string());
                }
                i += end + close.len();
                continue;
            }
        }
        i += 1;
    }
    out
}

fn strip_inline_tags(s: &str) -> String {
    let mut out = String::new();
    let mut in_tag = false;
    for c in s.chars() {
        if c == '<' { in_tag = true; }
        if !in_tag { out.push(c); }
        if c == '>' { in_tag = false; }
    }
    html_unescape(&out)
}

// ———————————————————————— 引擎主入口 ————————————————————————

/// 托管文本转换分派（C# ManagedEngine.RunAsync 25 分支等价）。
/// 返回（产物内容）。front_matter_heading = 设置 convert.front-matter=heading。
pub fn convert_managed(input: &Path, format: &str, front_matter_heading: bool) -> Result<String, Error> {
    let ext = input
        .extension()
        .map(|e| format!(".{}", e.to_string_lossy().to_lowercase()))
        .unwrap_or_default();
    let text = read_utf8(input)?;
    let name = input
        .file_stem()
        .map(|s| s.to_string_lossy().to_string())
        .unwrap_or_else(|| "output".to_string());

    match (ext.as_str(), format) {
        (".md", "html") => Ok(markdown_to_standalone_html(
            &text,
            &name,
            input.parent(),
        )),
        (".md", "txt") => Ok(html_to_text(&markdown_to_html_body(&apply_front_matter(&text, front_matter_heading)))),
        (".txt", "md") | (".log", "md") => Ok(text),
        (".txt", "html") | (".log", "html") => Ok(build_standalone_html(&name, &plain_text_to_html(&text))),
        (".json", "md") | (".xml", "md") => Ok(text),
        (".json", "html") | (".xml", "html") => Ok(build_standalone_html(&name, &plain_text_to_html(&text))),
        (".yaml", "md") | (".yml", "md") => Ok(text),
        (".yaml", "html") | (".yml", "html") => Ok(build_standalone_html(&name, &plain_text_to_html(&text))),
        (".json", "txt") | (".xml", "txt") | (".yaml", "txt") | (".yml", "txt") => Ok(text),
        (".csv", "md") | (".tsv", "md") => Ok(csv_to_markdown(&text, separator_of(&ext))),
        (".csv", "html") | (".tsv", "html") => Ok(build_standalone_html(&name, &csv_to_html_table(&text, separator_of(&ext)))),
        (".csv", "txt") | (".tsv", "txt") => Ok(csv_to_plain_text(&text, separator_of(&ext))),
        (".json", "yaml") => json_to_yaml(&text),
        (".yaml", "json") | (".yml", "json") => yaml_to_json(&text),
        (".csv", "json") | (".tsv", "json") => Ok(csv_to_json(&text, separator_of(&ext))),
        (".csv", "yaml") | (".tsv", "yaml") => json_to_yaml(&csv_to_json(&text, separator_of(&ext))),
        (".json", "csv") | (".json", "tsv") => json_to_csv(&text, separator_of(&ext)),
        (".yaml", "csv") | (".yml", "csv") | (".yaml", "tsv") | (".yml", "tsv") => {
            let json = yaml_to_json(&text)?;
            json_to_csv(&json, separator_of(&ext))
        }
        (".md", "csv") | (".md", "tsv") => md_table_to_csv(&text, separator_of(&ext)),
        (".xml", "json") => xml_to_json(&text),
        (".xml", "yaml") => json_to_yaml(&xml_to_json(&text)?),
        (".html", "md") | (".htm", "md") => Ok(html_to_markdown(&text)),
        _ => Err(Error::input_invalid(format!(
            "纯托管引擎不支持的转换: {ext} → {format}"
        ))),
    }
}

/// UTF-8 读取（BOM 剥离；C# File.ReadAllText 默认行为等价）。
pub fn read_utf8(path: &Path) -> Result<String, Error> {
    let bytes = std::fs::read(path)
        .map_err(|e| Error::input_invalid(format!("读取失败: {e}")))?;
    let bytes = if bytes.starts_with(&[0xEF, 0xBB, 0xBF]) {
        &bytes[3..]
    } else {
        &bytes[..]
    };
    Ok(String::from_utf8_lossy(bytes).to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn strip_front_matter_basic() {
        let md = "---\ntitle: x\n---\n# 正文";
        let (body, had) = strip_front_matter(md);
        assert!(had);
        assert_eq!(body, "# 正文");
        let (body2, had2) = strip_front_matter("# 无 front matter");
        assert!(!had2);
        assert_eq!(body2, "# 无 front matter");
    }

    #[test]
    fn markdown_to_html_has_table() {
        let html = markdown_to_html_body("| a | b |\n|---|---|\n| 1 | 2 |");
        assert!(html.contains("<table>"));
        assert!(html.contains("<th>a</th>"));
    }

    #[test]
    fn standalone_html_embeds_css() {
        let h = build_standalone_html("t", "<p>x</p>");
        assert!(h.starts_with("<!DOCTYPE html>"));
        assert!(h.contains("Microsoft YaHei"));
        assert!(h.contains("<title>t</title>"));
        assert!(h.contains("<p>x</p>"));
    }

    #[test]
    fn csv_to_markdown_roundtrip() {
        let md = csv_to_markdown("a,b\n1,2\n", b',');
        assert!(md.contains("|a|b|"));
        assert!(md.contains("|---|"));
        assert!(md.contains("|1|2|"));
    }

    #[test]
    fn csv_quoted_field_rfc4180() {
        let rows = parse_delimited("a,b\n\"x,y\",\"he said \"\"hi\"\"\"\n", b',');
        assert_eq!(rows[1][0], "x,y");
        assert_eq!(rows[1][1], "he said \"hi\"");
    }

    #[test]
    fn json_yaml_roundtrip_types() {
        let y = json_to_yaml(r#"{"a":1,"b":"true","c":true}"#).unwrap();
        assert!(y.contains("'a': 1")); // key 也单引号保护（C# QuoteYaml 语义）
        assert!(y.contains("'b': 'true'")); // 字符串保护
        assert!(y.contains("'c': true"));
        let back = yaml_to_json(&y).unwrap();
        assert!(back.contains("\"a\": 1"));
    }

    #[test]
    fn csv_to_json_infers_scalars() {
        let j = csv_to_json("name,age,active\n张三,30,true\n李四,,false\n", b',');
        assert!(j.contains("\"name\": \"张三\""));
        assert!(j.contains("\"age\": 30"));
        assert!(j.contains("\"active\": true"));
        assert!(j.contains("\"age\": null"));
    }

    #[test]
    fn json_to_csv_requires_array() {
        assert!(json_to_csv("{\"a\":1}", b',').is_err());
        let csv = json_to_csv("[{\"a\":1,\"b\":\"x,y\"},{\"a\":2}]", b',').unwrap();
        assert!(csv.starts_with("a,b\n"));
        assert!(csv.contains("\"x,y\""));
    }

    #[test]
    fn md_table_to_csv_skips_separator() {
        let csv = md_table_to_csv("| a | b |\n|---|---|\n| 1 | 2 |", b',').unwrap();
        assert_eq!(csv, "a,b\n1,2\n");
        assert!(md_table_to_csv("没有表格", b',').is_err());
    }

    #[test]
    fn xml_to_json_attributes_and_arrays() {
        let j = xml_to_json("<root a=\"1\"><item>1</item><item>2</item></root>").unwrap();
        assert!(j.contains("\"@a\": \"1\""));
        assert!(j.contains("\"item\": ["));
    }

    #[test]
    fn xml_leaf_keeps_string() {
        let j = xml_to_json("<root><n>42</n></root>").unwrap();
        assert!(j.contains("\"n\": \"42\"")); // 叶子保持字符串
    }

    #[test]
    fn html_to_markdown_basic() {
        let md = html_to_markdown("<h1>标题</h1><p>正文 <strong>加粗</strong> <a href=\"https://x\">链接</a></p>");
        assert!(md.contains("# 标题"));
        assert!(md.contains("**加粗**"));
        assert!(md.contains("[链接](https://x)"));
    }

    #[test]
    fn html_to_text_strips_tags() {
        let t = html_to_text("<p>a<br>b</p><script>var x;</script><style>.a{}</style>");
        assert!(t.contains("a\nb"));
        assert!(!t.contains("var x"));
    }

    #[test]
    fn resolve_images_relative_to_file_uri() {
        let dir = std::env::temp_dir().join(format!("bdt-md-test-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let img = dir.join("pic.png");
        std::fs::write(&img, b"png").unwrap();
        let html = resolve_relative_image_srcs("<img src=\"pic.png\">", &dir);
        assert!(html.contains("file:///"), "应转为绝对 file URI: {html}");
        let html2 = resolve_relative_image_srcs("<img src=\"https://x/a.png\">", &dir);
        assert!(html2.contains("https://x/a.png"));
        std::fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn unicode_roundtrip_utf8() {
        let dir = std::env::temp_dir().join(format!("bdt-utf8-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let f = dir.join("中文 报告.md");
        std::fs::write(&f, "---\ntitle: 测试\n---\n# 你好").unwrap();
        let html = convert_managed(&f, "html", false).unwrap();
        assert!(html.contains("<h1>你好</h1>"));
        std::fs::remove_dir_all(&dir).ok();
    }
}
