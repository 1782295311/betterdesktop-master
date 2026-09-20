//! 数据模型：与 `BetterDesktop.Shell.Clipboard.Contracts.ClipboardEntry` 逐字段对齐（camelCase JSON）。
//! 兼容要点：C# system.text.json 默认把枚举序列化为**数字**（Text=0/Image=1/Files=2/Html=3/RichText=4；
//! Category Text=0/Code=1/RichText=2/Image=3/File=4）；DateTime 序列化为 ISO 8601 字符串 → timestamp 用 String 原样保留。
//! 旧文件加载兼容的关键：字段名与类型必须与 C# 输出一致，未知字段由 serde 忽略。

use serde::{Deserialize, Serialize};

/// 内容类型（与 ClipboardItemKind 数值对齐）。
#[derive(Clone, Copy, Debug, PartialEq, Eq, Default)]
pub enum ItemKind {
    #[default]
    Text = 0,
    Image = 1,
    Files = 2,
    Html = 3,
    RichText = 4,
}

impl ItemKind {
    pub fn from_i32(v: i32) -> ItemKind {
        match v {
            1 => ItemKind::Image,
            2 => ItemKind::Files,
            3 => ItemKind::Html,
            4 => ItemKind::RichText,
            _ => ItemKind::Text,
        }
    }

    pub fn as_i32(self) -> i32 {
        self as i32
    }
}

impl Serialize for ItemKind {
    fn serialize<S: serde::Serializer>(&self, s: S) -> Result<S::Ok, S::Error> {
        s.serialize_i32(self.as_i32())
    }
}

impl<'de> Deserialize<'de> for ItemKind {
    fn deserialize<D: serde::Deserializer<'de>>(d: D) -> Result<Self, D::Error> {
        let v = i32::deserialize(d)?;
        Ok(ItemKind::from_i32(v))
    }
}

/// 语义分类（与 ContentCategory 数值对齐）。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Category {
    Text = 0,
    Code = 1,
    RichText = 2,
    Image = 3,
    File = 4,
    /// 【遗留 · 2026-09-13】表情包曾是一种**分类**（且只能靠"导入文件"进入，因此文字颜文字永远无法成为表情包）。
    /// 现已改为 `ClipboardEntry::is_sticker` **标记** —— 用户口径："跟收藏一样的机制，
    /// 这样就不管是图片还是颜文字都可以了"。
    ///
    /// 数值 `5` 保留仅为**旧数据兼容**：启动迁移 `Store::migrate_sticker_flag` 会把 `category == Sticker`
    /// 的条目转成"标记 + 按内容重判分类"。此后引擎不再产生该分类值（C# 侧同名枚举用于**筛选标记**）。
    Sticker = 5,
}

impl Category {
    pub fn from_i32(v: i32) -> Category {
        match v {
            1 => Category::Code,
            2 => Category::RichText,
            3 => Category::Image,
            4 => Category::File,
            5 => Category::Sticker,
            _ => Category::Text,
        }
    }

    pub fn as_i32(self) -> i32 {
        self as i32
    }
}

impl Serialize for Category {
    fn serialize<S: serde::Serializer>(&self, s: S) -> Result<S::Ok, S::Error> {
        s.serialize_i32(self.as_i32())
    }
}

impl<'de> Deserialize<'de> for Category {
    fn deserialize<D: serde::Deserializer<'de>>(d: D) -> Result<Self, D::Error> {
        let v = i32::deserialize(d)?;
        Ok(Category::from_i32(v))
    }
}

/// 【P1-4 命名格式透传 · 2026-09-13】一条自定义（命名）剪贴板格式：格式名 + 原始字节（base64）。
///
/// **为什么需要它**：Excel / WPS 表格复制的"可编辑表格"并不在 `CF_UNICODETEXT` / `HTML Format` /
/// `Rich Text Format` 里，而是放在它们自己注册的**命名格式**（如 `Microsoft Excel Worksheet`）。
/// 我们此前只读写标准格式 → 用户把历史条目粘回表格时，Excel 拿不到自家格式 → **降级成纯文本**。
///
/// **安全边界**：只做字节搬运（不解释、不执行）；采集侧受「格式数 / 单项字节 / 总字节」三重上限约束，
/// 超限**跳过而非截断**（见 `crate::formats`）。持久化仍走全库 DPAPI 加密，base64 仅为 JSON 安全。
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct NamedFormat {
    /// 格式名（`GetClipboardFormatNameW` 反查所得，如 "Microsoft Excel Worksheet"）。
    pub name: String,
    /// 原始字节的 base64 编码（避免 JSON 里出现非法 UTF-8 字节）。
    pub data_base64: String,
}

/// 剪贴板历史条目（存储模型 = C# ClipboardEntry 的可序列化字段；计算属性 Preview/PlainText 等不入 JSON）。
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ClipboardEntry {
    /// 稳定标识（GUID N 格式）。
    pub id: String,
    /// 捕获内容类型（数字，对齐 ClipboardItemKind）。
    pub content_type: ItemKind,
    /// 语义分类（数字，对齐 ContentCategory）。
    pub category: Category,
    /// 纯文本内容（HTML 条目为剥离后的文本）。
    pub content: String,
    /// 完整 HTML（HTML/富文本条目保留，含 img/table 标签；data URI 提取后为占位符版本）。
    pub html_content: String,
    /// RTF 内容（三格式并存时保留）。
    pub rtf_content: String,
    /// 复制时间（C# DateTime 序列化的 ISO 8601 字符串，原样保留保证旧文件兼容）。
    pub timestamp: String,
    /// 是否收藏（收藏条目不参与驱逐/过期）。
    pub is_pinned: bool,
    /// 图片落盘相对路径（相对存储根，如 clipboard\images\{id}.png）。
    pub image_path: String,
    /// 图片宽度（px，0=非图片）。
    pub image_width: i32,
    /// 图片高度（px，0=非图片）。
    pub image_height: i32,
    /// 内容字节数（文本=UTF8 字节数；图片=落盘字节数）。
    pub size_bytes: i64,
    /// 复制次数（「常用」排序依据）。
    pub copy_count: i32,
    /// 来源进程名（去 .exe）。
    pub source_process_name: String,
    /// 来源窗口标题。
    pub source_window_title: String,
    /// 文件条目路径列表。
    pub file_paths: Vec<String>,
    /// 标签（可搜索，空格分隔）。
    pub tags: String,
    /// 混合内容标记：含图片（<img / data URI）。
    pub has_images: bool,
    /// 混合内容标记：含表格（<table / <tr）。
    pub has_table: bool,
    /// 代码标记（粘贴强制纯文本）。
    pub is_code: bool,
    /// 引擎私有扩展：内容 >100KB 时存 content\{id}.bin（Deflate+DPAPI），content 字段留空、按需懒加载。
    /// C# 旧代码读取 JSON 时忽略未知字段（system.text.json 默认），兼容。
    #[serde(default)]
    pub content_in_bin: bool,
    /// 引擎私有扩展：内容指纹（图片 = **像素级** hash，见 `crate::fingerprint`）。
    /// 图片去重依赖本字段 —— image_path 含条目自己的 id，每次捕获都不同，不能作指纹。
    /// 旧数据为空时由 `Store::dedupe_images` 在启动迁移中补齐。
    #[serde(default)]
    pub content_hash: String,
    /// 【表情包 · 2026-09-13】用户标记：本条是表情包。
    ///
    /// **与 `is_pinned` 同级的独立标记，不是内容分类。** 用户口径（2026-09-13）：
    /// "跟收藏一样的机制，这样就不管是图片还是颜文字都可以了" —— 任何条目
    ///（文字/颜文字、静态图、动图、文件）都可以被标记为表情包；标记不改变条目内容，
    /// 只让它：① 出现在「表情包」筛选里 ② 豁免驱逐与「清理未收藏」。
    ///
    /// 历史沿革：本字段出现前，表情包是 `Category::Sticker` 分类（且只能靠"导入文件"进入，
    /// 因此**文字颜文字永远无法成为表情包**）。旧数据由启动迁移 `migrate_sticker_flag` 转换。
    #[serde(default)]
    pub is_sticker: bool,
    /// 【P1-4 命名格式透传 · 2026-09-13】捕获到的自定义命名格式（Excel/WPS 表格等"可编辑表格"载体）。
    /// 写回时按同名 `RegisterClipboardFormatW` 还原 → 粘回原应用仍是可编辑表格。
    /// **不进列表摘要**（`entry_summary_json` 刻意剥离）——base64 会撑爆 IPC 载荷。
    #[serde(default)]
    pub named_formats: Vec<NamedFormat>,
    /// 【P2-2 敏感信息 · 2026-09-13】内容命中手机号 / 身份证 / 邮箱 / 银行卡 / 密钥正则。
    /// **只用于面板预览遮罩**，不改变内容（用户要能原样粘出全文）。
    #[serde(default)]
    pub is_sensitive: bool,
    /// 【截图 OCR · 2026-09-14】图片条目（截图）的 OCR 识别文本（Windows.Media.Ocr / L1）。
    /// 由 capture exe / 面板经 `set_ocr_text` 写回；参与 keyword 搜索（用户可按图片里的文字找条目）；
    /// 旧数据无此字段（`#[serde(default)]`）兼容。OCR 文本**绝不进诊断日志**（OCR 计划红线）。
    #[serde(default)]
    pub ocr_text: String,
}

impl ClipboardEntry {
    pub fn new(id: impl Into<String>, content_type: ItemKind, content: impl Into<String>) -> Self {
        ClipboardEntry {
            id: id.into(),
            content_type,
            category: Category::Text,
            content: content.into(),
            html_content: String::new(),
            rtf_content: String::new(),
            timestamp: String::new(),
            is_pinned: false,
            image_path: String::new(),
            image_width: 0,
            image_height: 0,
            size_bytes: 0,
            copy_count: 1,
            source_process_name: String::new(),
            source_window_title: String::new(),
            file_paths: Vec::new(),
            tags: String::new(),
            has_images: false,
            has_table: false,
            is_code: false,
            content_in_bin: false,
            content_hash: String::new(),
            is_sticker: false,
            named_formats: Vec::new(),
            is_sensitive: false,
            ocr_text: String::new(),
        }
    }

    /// 去重指纹（与 C# ContentFingerprint 语义一致：类型前缀 + 内容，SHA256 前 16 hex）。
    /// 注意：Html 用 html_content（data URI 提取后为占位符版本，同内容复制两次指纹一致）。
    #[allow(dead_code)] // S4 去重索引使用
    pub fn fingerprint(&self) -> String {
        let payload: String = match self.content_type {
            ItemKind::Image => {
                // 像素级指纹（content_hash）；迁移前的旧数据退回路径指纹——不合并，
                // 但绝不会把两张不同的图误合并（安全性优先）。
                if self.content_hash.is_empty() {
                    format!("image:{}", self.image_path)
                } else {
                    format!("image:{}", self.content_hash)
                }
            }
            // 【2026-09-13 修复"表情包与历史图片各留一条"】此前 Sticker 用 `sticker:{文件字节hash}`，
            // 而图片条目用 `image:{像素hash}` —— 两个键**永不相同**。后果：用户先复制过某张图（历史里是
            // 图片条目），之后又把它导入为表情包 → 命中不了，列表里留下**两条同内容条目**
            //（一条"图片"、一条"表情包"）。用户实测反馈："你就没有考虑过有些表情包已经被我们的剪贴板历史给记录了吗？"
            //
            // 现在**统一到图片的键空间**：凡带 `content_hash` 的文件条目（含导入的动图 —— 其 file_paths 指向
            // `clipboard\stickers\{新id}.{ext}`，每次导入路径都不同，只有内容才是判据）都按**像素级** hash 判重，
            // 与图片条目共用 `image:` 前缀。于是三类路径全部收敛到同一条目：
            //   ① 复制过这张图 → 再导入 = 给已有条目**打上表情包标记**（不新增）；
            //   ② 标记过 → 再复制同图 = upsert 命中该条目（copy_count+1，不新增）；
            //   ③ 重复导入同一文件 = 命中同一条目（跳过）。
            // 取舍：动图按**首帧**像素判同 —— 两个"首帧相同、后续不同"的动图会被视为同一张（罕见，可接受）。
            //
            // 注意：这里的条件是 `!content_hash.is_empty()` 而**不再是** `category == Sticker` ——
            // 表情包已由"分类"改为**标记**（`is_sticker`），指纹不该再跟着标记走。
            ItemKind::Files if !self.content_hash.is_empty() => {
                format!("image:{}", self.content_hash)
            }
            ItemKind::Files => format!("files:{}", self.file_paths.join("\u{1f}")),
            // 【2026-09-12 修复"同一内容被文字与富文本同时认证"】Text / Html / RichText **统一按
            // 剥离后的纯文本**做指纹 —— 此前按类型前缀（`text:` / `html:` / `rtf:`），于是同一段文字
            // 从纯文本源（记事本、md 源码）与富文本源（网页、聊天）各复制一次就生成两个指纹、留下两条，
            // 且因一条带 HTML 一条不带而被判成不同分类（用户实测："他也被文本与富文本同时认证"）。
            // 去重依据应是**内容**，不是剪贴板格式。
            // 纯文本为空（只含图片/表格的 HTML）时退回 html/rtf 指纹。
            _ => {
                let key = if !self.content.trim().is_empty() {
                    self.content.as_str()
                } else if !self.html_content.trim().is_empty() {
                    self.html_content.as_str()
                } else {
                    self.rtf_content.as_str()
                };
                format!("text:{key}")
            }
        };
        let digest = crate::store::sha256_hex(payload.as_bytes());
        digest[..16.min(digest.len())].to_string()
    }

    /// 图片类内容（图片条目 / 表情包条目）**共用**的内容指纹（像素级 hash → 指纹）。
    ///
    /// **单一真相源**：`fingerprint()` 的图片与 Sticker 分支、以及 `Store::has_image_by_hash` 必须共用本函数 ——
    /// 若某处各写一遍 key 构造，判重会**永远查不到**（2026-09-12 自测即抓到过一次该不一致：
    /// 索引里存的是 `sha256("sticker:{hash}")[..16]`，而查询侧当时用裸 `sticker:{hash}`）。
    ///
    /// 2026-09-13 由 `sticker_fingerprint` 更名而来：图片与表情包已合并到同一键空间（见 `fingerprint()` 注释）。
    pub fn image_fingerprint(content_hash: &str) -> String {
        let digest = crate::store::sha256_hex(format!("image:{content_hash}").as_bytes());
        digest[..16.min(digest.len())].to_string()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 【2026-09-13 回归 · 用户实测反馈】图片条目与表情包条目必须命中**同一个指纹**。
    ///
    /// 旧实现：图片 = `image:{像素hash}`，表情包 = `sticker:{文件hash}` —— 两个键永不相同，
    /// 于是"先复制过这张图、之后又导入为表情包"会留下**两条同内容条目**（一条"图片"、一条"表情包"）。
    /// 本用例锁死"同内容 = 同键"，任何人再把两者分家都会红。
    #[test]
    fn image_and_sticker_share_fingerprint_for_same_content_hash() {
        let hash = "0123456789abcdef";

        let mut image = ClipboardEntry::new("img", ItemKind::Image, "");
        image.content_hash = hash.to_string();

        let mut sticker = ClipboardEntry::new("stk", ItemKind::Files, "");
        sticker.category = Category::Sticker;
        sticker.content_hash = hash.to_string();
        sticker.file_paths = vec!["clipboard\\stickers\\stk.gif".to_string()];

        assert_eq!(
            image.fingerprint(),
            sticker.fingerprint(),
            "同像素内容：图片与表情包必须同键，否则导入表情包会另起一条（用户实测的重复现象）"
        );
        assert_eq!(
            image.fingerprint(),
            ClipboardEntry::image_fingerprint(hash),
            "两边都必须与单一真相源 image_fingerprint 一致"
        );
    }

    /// 表情包文件路径每次都变（是我们自己的副本），故**绝不能**参与指纹 —— 否则重复导入永远命中不了。
    #[test]
    fn sticker_fingerprint_ignores_file_paths() {
        let hash = "abcdefabcdefabcd";
        let mut a = ClipboardEntry::new("a", ItemKind::Files, "");
        a.category = Category::Sticker;
        a.content_hash = hash.to_string();
        a.file_paths = vec!["clipboard\\stickers\\a.gif".to_string()];

        let mut b = ClipboardEntry::new("b", ItemKind::Files, "");
        b.category = Category::Sticker;
        b.content_hash = hash.to_string();
        b.file_paths = vec!["clipboard\\stickers\\b.gif".to_string()];

        assert_eq!(a.fingerprint(), b.fingerprint(), "同内容不同副本路径 → 同键");
    }

    #[test]
    fn kind_roundtrip_numbers() {
        // C# 枚举数字序列化对齐
        assert_eq!(ItemKind::from_i32(0), ItemKind::Text);
        assert_eq!(ItemKind::from_i32(1), ItemKind::Image);
        assert_eq!(ItemKind::from_i32(2), ItemKind::Files);
        assert_eq!(ItemKind::from_i32(3), ItemKind::Html);
        assert_eq!(ItemKind::from_i32(4), ItemKind::RichText);
        assert_eq!(ItemKind::from_i32(99), ItemKind::Text); // 未知值回退 Text
        assert_eq!(Category::from_i32(4), Category::File);
    }

    #[test]
    fn json_camel_case_alignment() {
        let e = ClipboardEntry::new("id1", ItemKind::Html, "text");
        let json = serde_json::to_string(&e).unwrap();
        // 关键字段名必须与 C# system.text.json camelCase 输出一致（旧文件兼容）
        for key in [
            "contentType",
            "htmlContent",
            "rtfContent",
            "isPinned",
            "imagePath",
            "imageWidth",
            "sizeBytes",
            "copyCount",
            "sourceProcessName",
            "sourceWindowTitle",
            "filePaths",
            "hasImages",
            "hasTable",
            "isCode",
        ] {
            assert!(json.contains(&format!("\"{key}\"")), "missing key {key}: {json}");
        }
        // contentType 为数字
        assert!(json.contains("\"contentType\":3"), "contentType must be number: {json}");
    }
}
