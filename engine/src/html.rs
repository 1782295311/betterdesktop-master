//! HTML data URI 图片提取（用户定稿升级：提取为独立图片文件 + HTML 引用替换）。
//! - `<img src="data:image/png;base64,...">` → `<img src="{{BD_IMG:n}}">` + 字节落盘
//! - 单张 > max_single_mb 或单条提取总量 > max_total_mb → 跳过提取（data URI 原样保留）
//! - 写回剪贴板时引擎还原为 base64 data URI（外部应用可见）

use base64::Engine;

pub const PLACEHOLDER_PREFIX: &str = "{{BD_IMG:";

/// CF_HTML 头里允许出现的字段名（剥离时按此白名单逐行跳过）。
/// `SourceURL` / `StartSelection` 等虽在 `EndFragment:` 之后，但同属头部元数据。
const HEADER_KEYS: [&str; 8] = [
    "Version:",
    "StartHTML:",
    "EndHTML:",
    "StartFragment:",
    "EndFragment:",
    "StartSelection:",
    "EndSelection:",
    "SourceURL:",
];

/// 剥离 CF_HTML 头（Windows 剪贴板 "HTML Format" 的偏移量元数据），只返回 HTML 本体。
///
/// 头形如：
/// ```text
/// Version:0.9
/// StartHTML:0000000105
/// EndHTML:0000008400
/// StartFragment:0000000141
/// EndFragment:0000007962
/// <html><body><!--StartFragment-->……<!--EndFragment--></body></html>
/// ```
///
/// 【2026-09-12 修复 · 用户实测"粘贴出来一堆 Version:0.9 / StartHTML:..."】
/// 此前捕获时把这个串**原样**存进 `html_content`，写回剪贴板时 `wrap_html_for_clipboard`
/// 又包一层 → 剪贴板里出现**双重 CF_HTML 头**，应用解析失败后回退显示整串元数据。
///
/// 策略：定位 `EndFragment:` 行的行尾，其后即本体；解析不到退化为首个 `<`；
/// 都不满足则原样返回（**绝不破坏内容**）。
pub fn strip_cf_html_header(raw: &str) -> String {
    // 策略：逐行跳过**开头连续出现的头字段行**（白名单），直到正文起点。
    //
    // 刻意**不要求** "以 Version: 开头"（原先那样写会有漏洞）：存量数据可能已被剥过一次，
    // 只剩尾部字段（如 `SourceURL:` 开头的残留），此时前置标记已不存在、条件不再匹配
    //（2026-09-12 真机：第 2 条 html 仍以 SourceURL 开头，就是踩了这个）。
    //
    // 干净 HTML 的第一行不是白名单字段 → 立即 break、原样返回，代价仅一次 find。
    let mut start = 0usize;
    for _ in 0..24 {
        let rest = &raw[start..];
        let line_end = rest.find('\n').map(|i| i + 1).unwrap_or(rest.len());
        let line = rest[..line_end].trim();
        let is_header = HEADER_KEYS
            .iter()
            .any(|k| line.get(..k.len()).is_some_and(|p| p.eq_ignore_ascii_case(k)));

        // 空行：头与正文之间可能夹着空行。仅在后面还有内容时才跳过。
        if line.is_empty() {
            if line_end >= rest.len() {
                break;
            }
            start += line_end;
            continue;
        }

        if !is_header {
            break; // 正文起点
        }

        if line_end >= rest.len() {
            return String::new(); // 通篇都是头字段、没有正文
        }
        start += line_end;
    }

    raw[start..].trim_start_matches(['\r', '\n']).to_string()
}

/// 从 HTML 提取**近似**纯文本（去标签 + 解常见实体 + 块级转行 + 压缩空白）。
///
/// 【2026-09-12】用途有二：
///   ① 剪贴板未提供 `CF_UNICODETEXT` 时（部分应用的富文本复制），给富文本条目补一份纯文本 ——
///      否则该条目既不可被搜索，也没有粘贴兜底格式（用户实测：HTML 条目 `content` 为空）；
///   ② 作为写回剪贴板时的兜底格式。
///
/// **近似实现**：不追求与浏览器渲染文本完全一致（不处理 CSS 隐藏、表格对齐等），够用即可。
pub fn html_to_text(html: &str) -> String {
    let mut s = html.to_string();

    // 1) 去掉 script / style / 注释（按出现顺序配对，避免误删正文）
    for (open, close) in [("<script", "</script>"), ("<style", "</style>")] {
        while let (Some(a), Some(b)) = (s.find(open), s.find(close)) {
            if b > a {
                s.replace_range(a..b + close.len(), " ");
            } else {
                break;
            }
        }
    }
    while let (Some(a), Some(b)) = (s.find("<!--"), s.find("-->")) {
        if b > a {
            s.replace_range(a..b + 3, " ");
        } else {
            break;
        }
    }

    // 2) 逐字符：标签整体丢弃，但**按标签名**决定是否换行
    //
    // 【2026-09-12 修复】此前用 `s.replace("</p", "\n")` 之类的**前缀**替换 ——
    // 结果 `</path>`（SVG）也被 `</p` 命中，残留 `ath` 混进正文（真机实测回填出
    // "ath / athimage.png / ath…" 这种乱码）。改为解析出标签名后精确匹配。
    let mut text = String::with_capacity(s.len());
    let mut tag = String::new();
    let mut in_tag = false;
    for ch in s.chars() {
        if ch == '<' {
            in_tag = true;
            tag.clear();
            continue;
        }
        if in_tag {
            if ch == '>' {
                in_tag = false;
                if is_block_tag(&tag) {
                    text.push('\n');
                }
            } else {
                tag.push(ch);
            }
            continue;
        }
        text.push(ch);
    }

    // 3) 解实体 + 压缩空白（行内多空格合一，去掉空行）
    decode_entities(&text)
        .lines()
        .map(|l| l.split_whitespace().collect::<Vec<_>>().join(" "))
        .filter(|l| !l.is_empty())
        .collect::<Vec<_>>()
        .join("\n")
}

/// 标签名是否为"块级/换行"语义（决定是否在文本里补一个换行）。
/// 注意按**完整标签名**匹配：`p` 与 `path` 是两个不同的标签。
fn is_block_tag(raw: &str) -> bool {
    let trimmed = raw.trim().trim_start_matches('/').trim();
    let name = trimmed
        .split(|c: char| c.is_whitespace() || c == '/')
        .next()
        .unwrap_or("")
        .to_ascii_lowercase();
    matches!(
        name.as_str(),
        "br" | "p"
            | "div"
            | "li"
            | "tr"
            | "table"
            | "ul"
            | "ol"
            | "h1"
            | "h2"
            | "h3"
            | "h4"
            | "h5"
            | "h6"
    )
}

fn decode_entities(s: &str) -> String {
    s.replace("&nbsp;", " ")
        .replace("&lt;", "<")
        .replace("&gt;", ">")
        .replace("&quot;", "\"")
        .replace("&#39;", "'")
        .replace("&apos;", "'")
        .replace("&amp;", "&")
}

#[cfg(test)]
mod header_tests {
    use super::*;

    #[test]
    fn strip_removes_offset_metadata() {
        let raw = "Version:0.9\r\nStartHTML:0000000105\r\nEndHTML:0000000200\r\nStartFragment:0000000141\r\nEndFragment:0000000160\r\n<html><body><!--StartFragment--><b>x</b><!--EndFragment--></body></html>";
        let out = strip_cf_html_header(raw);
        assert!(out.starts_with("<html>"), "头必须被剥掉，实得: {out}");
        assert!(!out.contains("StartHTML"), "偏移元数据不得残留");
        assert!(out.contains("<b>x</b>"), "正文不得丢失");
    }

    #[test]
    fn strip_passes_clean_html_through() {
        let clean = "<html><body>hi</body></html>";
        assert_eq!(strip_cf_html_header(clean), clean);
    }

    #[test]
    fn html_to_text_extracts_readable_text() {
        let html = "<html><body><!--StartFragment--><p>第一段 &amp; 实体</p><div>第二段<br>折行</div><script>var x=1;</script><!--EndFragment--></body></html>";
        let t = html_to_text(html);
        assert!(t.contains("第一段 & 实体"), "实体应解码，实得: {t}");
        assert!(t.contains("第二段"), "块级内容应保留");
        assert!(!t.contains('<'), "标签应清空");
        assert!(!t.contains("var x=1"), "script 内容不应出现");
    }

    #[test]
    fn html_to_text_on_slate_like_markup() {
        // 用户实测那类（Slate 编辑器）的富文本结构
        let html = r#"<html><body><!--StartFragment--><span data-slate-node="text"><span data-slate-leaf="true"><span data-slate-string="true">但 dock 必须是顶层窗口</span></span></span><!--EndFragment--></body></html>"#;
        let t = html_to_text(html);
        assert_eq!(t, "但 dock 必须是顶层窗口");
    }

    #[test]
    fn html_to_text_does_not_mangle_svg_path_tag() {
        // 回归：`</path>` 不得被 `</p` 前缀替换误吃（真机回填曾出现 "ath / athimage.png"）
        let html = "<p>前</p><svg><path d=\"M0 0\"/></svg><p>后</p>";
        let t = html_to_text(html);
        assert!(!t.contains("ath"), "标签名残渣不得出现，实得: {t}");
        assert!(t.contains('前') && t.contains('后'), "正文应保留");
    }

    #[test]
    fn strip_skips_source_url_after_endfragment() {
        // 标准里 SourceURL 位于 EndFragment 之后，属头部元数据，不得残留在正文里
        let raw = "Version:0.9\r\nStartHTML:0000000105\r\nEndHTML:0000000300\r\nStartFragment:0000000141\r\nEndFragment:0000000160\r\nSourceURL:file:///c:/x.html\r\n<html><body><!--StartFragment-->正文<!--EndFragment--></body></html>";
        let out = strip_cf_html_header(raw);
        assert!(out.starts_with("<html>"), "SourceURL 行也必须被跳过，实得: {out}");
        assert!(!out.contains("SourceURL"), "SourceURL 不得残留");
        assert!(out.contains("正文"));
    }

    #[test]
    fn strip_keeps_colon_lines_in_body() {
        // 正文里合法的「注意：xxx」不能被当成头字段吃掉（白名单之外一律保留）
        let raw = "Version:0.9\r\nStartHTML:0000000105\r\nEndHTML:0000000200\r\nStartFragment:0000000141\r\nEndFragment:0000000160\r\n注意：这行是正文\r\n<html>x</html>";
        let out = strip_cf_html_header(raw);
        assert!(out.contains("注意："), "正文中的冒号行必须保留，实得: {out}");
    }

    #[test]
    fn strip_handles_source_url_only_remainder() {
        // 存量场景：已被剥过一次，只剩尾部字段开头（无 Version / EndFragment 可依赖）
        let raw = "SourceURL:file:///c:/x.html\r\n<html><body>正文</body></html>";
        let out = strip_cf_html_header(raw);
        assert!(out.starts_with("<html>"), "只剩尾部字段也要能剥净，实得: {out}");
        assert!(!out.contains("SourceURL"));
    }

    #[test]
    fn strip_is_idempotent() {
        let raw = "Version:0.9\r\nStartHTML:0000000105\r\nEndHTML:0000000200\r\nStartFragment:0000000141\r\nEndFragment:0000000160\r\n<html>a</html>";
        let once = strip_cf_html_header(raw);
        assert_eq!(strip_cf_html_header(&once), once, "重复剥离不得再变化");
    }
}

/// 提取结果。
pub struct ExtractResult {
    /// 替换占位符后的 HTML。
    pub html: String,
    /// 提取出的图片（文件名 + 字节 + 扩展名）。
    pub images: Vec<ExtractedImage>,
    /// true = 超过阈值跳过（html 原样返回）。
    pub skipped: bool,
}

#[derive(Debug)]
pub struct ExtractedImage {
    /// 相对文件名（如 `0.png`）。
    pub filename: String,
    /// 图片字节（原始 data URI 解码，不做图像解码）。
    pub bytes: Vec<u8>,
    /// 扩展名（png/jpg/gif/webp/svg 等）。
    pub ext: String,
}

/// 提取 HTML 中的 data URI 图片并替换为占位符。
pub fn extract_data_uris(
    html: &str,
    max_single_bytes: usize,
    max_total_bytes: usize,
) -> ExtractResult {
    let mut result = String::with_capacity(html.len());
    let mut images: Vec<ExtractedImage> = Vec::new();
    let mut total = 0usize;
    let mut skipped = false;
    let mut rest = html;

    while let Some(start) = find_data_uri_start(rest) {
        result.push_str(&rest[..start]);
        let uri_start = start + "data:image/".len();
        // 定位到引号结束
        let after = &rest[uri_start..];
        let end_rel = after.find(['"', '\'', ' ']).unwrap_or(after.len());
        let (mime_and_data, remainder) = after.split_at(end_rel);
        rest = remainder;

        // mime_and_data = "png;base64,...." 
        if let Some(b64_start) = mime_and_data.find(";base64,") {
            let ext = mime_and_data[..b64_start].to_lowercase();
            let b64 = &mime_and_data[b64_start + ";base64,".len()..];
            let ext = normalize_ext(&ext);
            let decoded = base64::engine::general_purpose::STANDARD
                .decode(b64.trim())
                .unwrap_or_default();
            let too_big = decoded.len() > max_single_bytes
                || total.saturating_add(decoded.len()) > max_total_bytes;
            if too_big || decoded.is_empty() {
                skipped = true;
                // 超阈值：跳过提取，data URI 原样保留
                result.push_str("data:image/");
                result.push_str(mime_and_data);
                continue;
            }
            let n = images.len();
            let filename = format!("{n}.{ext}");
            total += decoded.len();
            result.push_str(PLACEHOLDER_PREFIX);
            result.push_str(&n.to_string());
            result.push_str("}}"); // 计划定稿占位符 {{BD_IMG:n}}
            images.push(ExtractedImage {
                filename,
                bytes: decoded,
                ext,
            });
        } else {
            // 非 base64（少见）或格式不符：原样保留
            result.push_str("data:image/");
            result.push_str(mime_and_data);
        }
    }
    result.push_str(rest);

    ExtractResult {
        html: result,
        images,
        skipped,
    }
}

/// 还原占位符为 base64 data URI。
pub fn restore_data_uris(html: &str, images: &[ExtractedImage]) -> String {
    let mut out = String::with_capacity(html.len() + 1024);
    let mut rest = html;
    while let Some(pos) = rest.find(PLACEHOLDER_PREFIX) {
        out.push_str(&rest[..pos]);
        let after = &rest[pos + PLACEHOLDER_PREFIX.len()..];
        let end = after.find('}').unwrap_or(after.len());
        let idx: usize = after[..end].trim().parse().unwrap_or(usize::MAX);
        rest = &after[end.min(after.len())..];
        if let Some(img) = images.get(idx) {
            let encoded =
                base64::engine::general_purpose::STANDARD.encode(&img.bytes);
            // 必须用扩展名的**逆映射**还原 MIME（svg→svg+xml / ico→x-icon / jpg→jpeg），
            // 否则消费方按错误 MIME 处理 data URI（见 normalize_ext 的修复说明）。
            out.push_str(&format!(
                "data:image/{};base64,{}",
                mime_of_ext(&img.ext),
                encoded
            ));
        } else {
            out.push_str("{{BD_IMG:");
            out.push_str(&idx.to_string());
            out.push('}');
        }
    }
    out.push_str(rest);
    out
}

/// 定位 `data:image/` 起始位置（<img src="data:image/...）。
fn find_data_uri_start(haystack: &str) -> Option<usize> {
    haystack.find("data:image/")
}

/// MIME 子类型 → 磁盘扩展名（与 [`mime_of_ext`] 互逆）。
///
/// 【2026-09-14 修复】入参是 **MIME 子类型**（取自 `mime_and_data[..b64_start]`），而真实的
/// `image/svg+xml` 拿到的是 `svg+xml`、`image/x-icon` 拿到的是 `x-icon` —— 此前两者都落到
/// `_ => "png"` 兜底：SVG/ICO 的字节被当 PNG 存盘，还原时又拼成 `data:image/png;base64,<svg 字节>`
/// → **粘贴出去是坏图**（`svg` / `ico` 两个分支因此形同虚设）。现在先按 `+` 取主类型再匹配。
fn normalize_ext(ext: &str) -> String {
    let base = ext.split('+').next().unwrap_or(ext).trim().to_ascii_lowercase();
    match base.as_str() {
        "jpeg" => "jpg".to_string(),
        "jpg" | "png" | "gif" | "webp" | "bmp" | "svg" | "avif" | "tiff" => base,
        "x-icon" | "vnd.microsoft.icon" => "ico".to_string(),
        _ => "png".to_string(),
    }
}

/// 磁盘扩展名 → 还原 data URI 所用的 MIME 子类型（[`normalize_ext`] 的逆映射）。
///
/// 必须还原成**原来的 MIME**：`svg` → `svg+xml`、`ico` → `x-icon`、`jpg` → `jpeg`；
/// 否则消费方（浏览器 / 富文本编辑器）会按错误的 MIME 处理 data URI，同样表现为坏图。
fn mime_of_ext(ext: &str) -> &str {
    match ext {
        "svg" => "svg+xml",
        "ico" => "x-icon",
        "jpg" => "jpeg",
        other => other,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn extract_and_restore_roundtrip() {
        let b64 = base64::engine::general_purpose::STANDARD.encode([0x89, b'P', b'N', b'G', 1, 2, 3]);
        let html = format!(r#"<html><img src="data:image/png;base64,{b64}"><p>x</p></html>"#);
        let r = extract_data_uris(&html, 1024 * 1024, 16 * 1024 * 1024);
        assert!(!r.skipped, "skipped unexpectedly");
        assert_eq!(r.images.len(), 1, "images: {:?}, html: {:?}", r.images, r.html);
        assert_eq!(r.images[0].filename, "0.png");
        assert_eq!(r.images[0].bytes, vec![0x89, b'P', b'N', b'G', 1, 2, 3]);
        assert!(r.html.contains("{{BD_IMG:0}}"), "html: {:?}", r.html);
        assert!(!r.html.contains("data:image/"));

        let restored = restore_data_uris(&r.html, &r.images);
        assert!(restored.contains(&format!("data:image/png;base64,{b64}")));
    }

    #[test]
    fn oversize_skips_extraction() {
        let b64 = base64::engine::general_purpose::STANDARD.encode([1u8; 100]);
        let html = format!(r#"<img src="data:image/png;base64,{b64}">"#);
        let r = extract_data_uris(&html, 10, 100); // 单张 10B 上限 < 100B
        assert!(r.skipped);
        assert!(r.images.is_empty());
        assert!(r.html.contains("data:image/png;base64,"));
    }

    #[test]
    fn jpeg_ext_normalized() {
        let b64 = base64::engine::general_purpose::STANDARD.encode([1, 2, 3]);
        let html = format!(r#"<img src="data:image/jpeg;base64,{b64}">"#);
        let r = extract_data_uris(&html, 1024, 4096);
        assert_eq!(r.images[0].filename, "0.jpg");
    }

    /// 【2026-09-14 回归】`svg+xml` / `x-icon` 这两个"带 + 的 MIME 子类型"曾双双落到 `png` 兜底：
    /// SVG/ICO 字节按 PNG 存盘、再按 `image/png` 还原 → 粘贴出去是坏图。本用例锁死
    /// 「磁盘扩展名正确 + 还原 MIME 与原 MIME 一致」。
    #[test]
    fn svg_and_ico_keep_their_mime() {
        let b64 = base64::engine::general_purpose::STANDARD.encode(b"<svg/>");
        let html = format!(r#"<img src="data:image/svg+xml;base64,{b64}">"#);
        let r = extract_data_uris(&html, 1024, 4096);
        assert_eq!(r.images[0].filename, "0.svg", "SVG 磁盘扩展名必须保留（不是 png）");
        assert_eq!(r.images[0].ext, "svg");
        let restored = restore_data_uris(&r.html, &r.images);
        assert!(
            restored.contains(&format!("data:image/svg+xml;base64,{b64}")),
            "SVG 必须还原为 image/svg+xml，实得: {restored}"
        );

        let b64 = base64::engine::general_purpose::STANDARD.encode([1, 2, 3]);
        let html = format!(r#"<img src="data:image/x-icon;base64,{b64}">"#);
        let r = extract_data_uris(&html, 1024, 4096);
        assert_eq!(r.images[0].filename, "0.ico");
        let restored = restore_data_uris(&r.html, &r.images);
        assert!(
            restored.contains(&format!("data:image/x-icon;base64,{b64}")),
            "ICO 必须还原为 image/x-icon，实得: {restored}"
        );

        // jpeg 的往返必须回到 jpeg（而不是 jpg）
        let b64 = base64::engine::general_purpose::STANDARD.encode([9, 9]);
        let html = format!(r#"<img src="data:image/jpeg;base64,{b64}">"#);
        let r = extract_data_uris(&html, 1024, 4096);
        assert!(
            restore_data_uris(&r.html, &r.images)
                .contains(&format!("data:image/jpeg;base64,{b64}")),
            "jpeg 往返必须仍是 image/jpeg"
        );
    }

    #[test]
    fn non_base64_kept() {
        let html = r#"<img src="data:image/png;base64,@@@not-base64@@@">"#;
        let r = extract_data_uris(html, 1024, 4096);
        assert!(r.skipped || r.images.is_empty());
    }
}
