//! 内容语义分类器（逐行移植 C# ContentAnalyzer.cs：分类顺序 File > Image > Code > RichText > Text）。
//! 代码检测 = 缩进占比 + 关键字密度 + 注释特征 + 括号配对（行 <3 不判，不猜语言）。

use crate::model::{Category, ItemKind};

/// 分析结果（对齐 ContentProfile）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Profile {
    pub category: Category,
    pub has_images: bool,
    pub has_table: bool,
    pub is_code: bool,
}

/// 语义分类（kind 为 Files/Image 时短路；HTML 条目含图含表标记，不改变类型裁决）。
pub fn analyze(kind: ItemKind, html: &str, text: &str) -> Profile {
    if kind == ItemKind::Files {
        return Profile {
            category: Category::File,
            has_images: false,
            has_table: false,
            is_code: false,
        };
    }
    if kind == ItemKind::Image {
        return Profile {
            category: Category::Image,
            has_images: false,
            has_table: false,
            is_code: false,
        };
    }

    let has_images = contains_ignore_case(html, "<img");
    let has_table = contains_ignore_case(html, "<table") || contains_ignore_case(html, "<tr");

    let probe = if !text.trim().is_empty() { text } else { html };
    let is_code = is_code_text(probe);

    if is_code {
        return Profile {
            category: Category::Code,
            has_images,
            has_table,
            is_code: true,
        };
    }
    if has_rich_features(html) {
        return Profile {
            category: Category::RichText,
            has_images,
            has_table,
            is_code: false,
        };
    }
    Profile {
        category: Category::Text,
        has_images,
        has_table,
        is_code: false,
    }
}

/// 「实质富文本」判定：HTML 里是否含**视觉排版信息**（图 / 表 / 字体 / 字号 / 颜色）。
///
/// 【2026-09-12 用户质疑"分类模糊"】此前条件是 `html 非空` 即判 RichText —— 于是从 md 复制的
/// **纯文字全文**、网页上的普通段落都被归为"富文本"，而含图表的 docx 论文同样是"富文本"，
/// 两者毫无区分度（`has_images`/`has_table` 只当了徽标、没参与分类）。
///
/// 现收紧为：只有带图 / 表 / 字体样式的才算富文本；仅由结构标签（p / h1-6 / ul / li / br / div /
/// b / i …）包装的文字仍算「文字」。判据对齐用户描述：docx 论文"包含图片、格式、字体、大小、
/// 表格等等数据" → 富文本；md / 网页复制的纯文字全文 → 文字。
fn has_rich_features(html: &str) -> bool {
    if html.trim().is_empty() {
        return false;
    }
    let lower = html.to_lowercase();
    RICH_FEATURE_MARKERS.iter().any(|m| lower.contains(m))
}

/// 富文本特征标记（按小写比较）。刻意**不含** p / br / div / span / h1-6 / ul / li / b / i / strong
/// 等结构标签 —— 它们在任何富文本源（网页、md 渲染器）里都会出现，若算作特征等于退回
/// "有 HTML 格式即富文本"的旧行为。
const RICH_FEATURE_MARKERS: &[&str] = &[
    // 富媒体：docx 论文的图片 / 表格
    "<img", "<table", "<tr", "<td", "<th", "<svg", "<video", "<canvas",
    // 字体 / 字号 / 颜色：用户所说的"格式、字体、大小"
    "font-family", "font-size", "font-weight", "font-style",
    "color:", "background-color", "text-decoration",
];

const CODE_KEYWORDS: &[&str] = &[
    // C/C++/C#/Java（原表）
    "if ", "for ", "while ", "return ", "function", "def ", "class ", "import ", "using ", "var ",
    "const ", "let ", "public ", "private ", "protected ", "static ", "void ", "int ", "string ",
    // 注：`end` 已移除 —— 子串匹配下 append/depend/send/backend 全部命中，纯噪声。
    "bool ", "namespace ", "=>", "->", "interface ", "struct ",
    // 2026-09-12 扩充：Rust / JS-TS / Python / Go —— 原表偏 .NET/C，从 IDE 复制的 Rust/前端代码
    // 常常一个关键词都命不中，被落到"富文本"（用户实测：复制的 Rust 片段进不了「代码」分类）。
    "fn ", "use ", "impl ", "pub ", "mut ", "match ", "unwrap", "crate::", "std::",
    "async ", "await ", "export ", "from ", "func ", "package ", "val ", "elif ",
    "lambda", "self.", "#include", "console.", "print(",
];

const CODE_COMMENT_MARKERS: &[&str] = &["//", "/*", "*/", "#", "--", "<!--"];

/// 代码特征判定（与 C# IsCodeText 逐行等价）。
pub fn is_code_text(text: &str) -> bool {
    if text.trim().is_empty() {
        return false;
    }

    let lines: Vec<&str> = text.split('\n').collect();
    if lines.len() < 3 {
        return false;
    }

    let mut non_empty = 0i32;
    let mut indented = 0i32;
    for raw in &lines {
        let line = raw.trim();
        if line.is_empty() {
            continue;
        }
        non_empty += 1;
        // 【2026-09-12 修复】原实现先 `trim()` 再 `trim_start()` 比长度 → 二者恒等 → indented 永远 0，
        // 缩进信号**完全失效**（C# 原版同款 bug，此前"忠实移植"把缺陷也搬了过来）。
        // 现按**未 trim 的原始行**判断前导空白，缩进占比才真正参与判定。
        if raw.starts_with(' ') || raw.starts_with('\t') {
            indented += 1;
        }
    }

    if non_empty == 0 {
        return false;
    }

    let indent_ratio = indented as f64 / non_empty as f64;
    let lower = text.to_lowercase();
    let mut keyword_hits = 0i32;
    for kw in CODE_KEYWORDS {
        if lower.contains(kw) {
            keyword_hits += 1;
        }
    }

    let has_comment = CODE_COMMENT_MARKERS.iter().any(|m| text.contains(m));

    let open_paren = count_char(text, '{') + count_char(text, '(') + count_char(text, '[');
    let close_paren = count_char(text, '}') + count_char(text, ')') + count_char(text, ']');
    let balanced =
        (open_paren - close_paren).abs() <= i32::max(2, open_paren / 4);

    // 【2026-09-12 修复"技术文档被判代码"】关键词必须结合**密度**看，不能只看绝对数量：
    // 一份中文技术文档（说明段 + 代码示例 + 术语）轻松命中 10+ 个关键词
    // （import/interface/string/=>/crate::/std::/...），但被上百行正文摊薄；
    // 真代码则几乎**每行**都含关键词 —— 用户实测 12.6KB 文档（115 非空行、命中 11 词、
    // 密度 0.10）被误判为代码。密度门槛正是区分二者的判据。
    let kw_density = keyword_hits as f64 / non_empty as f64;

    let strong_signal = indent_ratio >= 0.4                                   // 缩进（Markdown 列表也缩进，但通常远低于 40%）
        || (keyword_hits >= 2 && kw_density >= 0.15)                          // 多关键词 + 足够密集
        || (has_comment && keyword_hits >= 1 && kw_density >= 0.10);          // 注释 + 关键词 + 密度

    strong_signal && balanced
}

fn count_char(text: &str, c: char) -> i32 {
    text.chars().filter(|&ch| ch == c).count() as i32
}

fn contains_ignore_case(haystack: &str, needle: &str) -> bool {
    haystack.to_lowercase().contains(&needle.to_lowercase())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn kind_short_circuits() {
        let f = analyze(ItemKind::Files, "", "");
        assert_eq!(f.category, Category::File);
        assert!(!f.is_code);
        let img = analyze(ItemKind::Image, "", "");
        assert_eq!(img.category, Category::Image);
    }

    #[test]
    fn code_detected() {
        // 注意：C# 原版「缩进占比」因 Trim() 后 TrimStart() 恒无效果而恒为 0，
        // 代码检测实际靠 keywordHits>=2 或 注释+1 命中。忠实移植。
        let code = "fn main() {\n    if (a) {\n        return value;\n    }\n}\n";
        assert!(is_code_text(code));
        let profile = analyze(ItemKind::Text, "", code);
        assert_eq!(profile.category, Category::Code);
        assert!(profile.is_code);
    }

    #[test]
    fn prose_not_code() {
        // 行数 <3 不判
        assert!(!is_code_text("if x"));
        assert!(!is_code_text(""));
        let prose = "今天天气不错\n我们出去散步\n顺便买点水果回来\n路上遇到了邻居家的狗";
        assert!(!is_code_text(prose));
        let profile = analyze(ItemKind::Text, "", prose);
        assert_eq!(profile.category, Category::Text);
    }

    #[test]
    fn html_meta_detected() {
        let html = "<html><body><img src=\"a.png\"><table><tr><td>x</td></tr></table></body></html>";
        let profile = analyze(ItemKind::Html, html, "");
        assert!(profile.has_images);
        assert!(profile.has_table);
        assert_eq!(profile.category, Category::RichText); // 非代码 HTML → RichText
    }

    #[test]
    fn comment_signal_with_keyword() {
        // hasComment && keywordHits >= 1 → strong
        let code = "// todo\nif (a) {\n    do_work();\n}\n";
        assert!(is_code_text(code));
    }

    #[test]
    fn rust_snippet_detected() {
        // 用户实测场景：从 IDE 复制 Rust 片段（字面量中不能含 "use " 之外的干扰）
        let code = "pub fn upsert(&mut self, entry: ClipboardEntry) -> UpsertOutcome {\n    let fp = entry.fingerprint();\n    if let Some(id) = self.index.get(&fp) {\n        return UpsertOutcome::Updated(id);\n    }\n    UpsertOutcome::New(id)\n}\n";
        assert!(is_code_text(code), "Rust 片段应被识别为代码");
        assert_eq!(
            analyze(ItemKind::Html, "<pre><code>x</code></pre>", code).category,
            Category::Code,
            "带 HTML 格式的代码仍须归类为 Code（而非 RichText）—— 这正是此前的真空地带"
        );
    }

    #[test]
    fn indent_signal_now_actually_works() {
        // 修复前 indented 恒为 0（trim 后再 trim_start 比长度），缩进信号完全失效。
        // 这里刻意不给关键词、不给注释，只能靠缩进占比判出来。
        let code = "alpha = 1\n    beta = 2\n    gamma = 3\n    delta = 4\n";
        assert!(is_code_text(code), "缩进占比 ≥40% 应判为代码（回归：缩进信号失效 bug）");
    }

    #[test]
    fn html_prose_without_format_is_text() {
        // 只有裸 <p> 包装的纯文字 → 「文字」而非「富文本」：
        // 这是 2026-09-12 分类收紧（has_rich_features）的预期行为，也是用户质疑的焦点。
        assert_eq!(
            analyze(ItemKind::Html, "<p>今天天气不错</p>", "今天天气不错").category,
            Category::Text
        );
    }

    #[test]
    fn plain_html_wrapping_is_text_not_richtext() {
        // md / 网页复制的**纯文字全文**：只有结构标签，没有图表/字体样式 → 文字。
        // 旧实现"html 非空即富文本"，导致 md 全文与 docx 论文同分类（用户实测质疑）。
        let md = "<h1>标题</h1><p>第一段。</p><ul><li>要点一</li><li>要点二</li></ul><p><strong>加粗</strong>结尾</p>";
        let profile = analyze(ItemKind::Html, md, "标题 第一段。要点一 要点二 加粗结尾");
        assert_eq!(profile.category, Category::Text, "纯文字（仅结构标签）应为 Text");
        assert!(!profile.has_images && !profile.has_table);
    }

    #[test]
    fn docx_like_content_is_richtext() {
        // docx 论文：图片 + 表格 + 字体字号 → 富文本（与"纯文字"区分开）
        let doc = "<html><body><h1 style=\"font-family:Times New Roman;font-size:24px\">论文标题</h1><p>正文</p><table><tr><td>表1</td></tr></table><img src=\"a.png\"></body></html>";
        let profile = analyze(ItemKind::Html, doc, "论文标题 正文 表1");
        assert_eq!(profile.category, Category::RichText);
        assert!(profile.has_images && profile.has_table);
    }

    #[test]
    fn web_article_with_image_is_richtext() {
        let web = "<p>一段说明文字</p><img src=\"https://x/y.png\">";
        assert_eq!(
            analyze(ItemKind::Html, web, "一段说明文字").category,
            Category::RichText
        );
    }

    #[test]
    fn technical_doc_with_code_samples_is_not_code() {
        // 用户实测：12.6KB 中文技术文档（说明 + 代码示例 + 术语）被判为代码。
        // 它命中 11 个关键词，但摊薄在 115 行正文里（密度 0.10）→ 不应判代码。
        let mut doc = String::new();
        for i in 0..60 {
            doc.push_str(&format!(
                "第{i}段说明：本功能用于记录剪贴板历史，支持检索、去重与容量限制，参数见下表。\n"
            ));
        }
        doc.push_str("interface ClipboardItem {\n  id: string;\n  hash: string;\n}\n");
        doc.push_str("import { ClipMonitor } from 'native-addon-bridge';\n");

        assert!(
            !is_code_text(&doc),
            "技术文档 + 少量代码示例 → 不判代码（关键词被正文摊薄，密度不足）"
        );
        assert_eq!(
            analyze(ItemKind::Text, "", &doc).category,
            Category::Text,
            "整篇文档应归为「文字」"
        );

        // 对照：关键词密集的纯代码片段仍判代码
        let code = "import { a } from 'b';\ninterface X { id: string; }\nconst y = (v) => v;\n";
        assert!(is_code_text(code), "纯代码（关键词密集）仍须判代码");
    }
}
