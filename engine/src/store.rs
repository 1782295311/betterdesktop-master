//! 存储层：CBENC1+DPAPI 的 clipboard_history.json 读写（对齐 C# SaveToFile/LoadFromFile）+ 存储目录布局。
//! 兼容性（D6）：
//! - CBENC1 头（`CBENC1\0`，7 字节）存在 → 剩余为 DPAPI 密文；
//! - 无头 → 明文 JSON 旧格式回退；
//! - 解密/解析失败 → 损坏自愈（空历史启动，不崩）。
//! S4 扩展：O(1) 指纹去重索引、驱逐/预算、content.bin 懒加载（>100KB Deflate+DPAPI）、级联清理。

use std::collections::HashMap;
use std::io::{self, Read, Write};
use std::path::{Path, PathBuf};

use flate2::read::DeflateDecoder;
use flate2::write::DeflateEncoder;
use flate2::Compression;

use crate::dpapi;
use crate::model::{Category, ClipboardEntry, ItemKind};
use crate::settings::{Settings, CONTENT_EXTERNAL_THRESHOLD};

pub const STORAGE_FILE: &str = "clipboard_history.json";
pub const ENC_HEADER: &[u8] = b"CBENC1\0";

/// 内容指纹哈希（非加密用途；与 C# SHA256 语义一致，hex 小写）。
pub fn sha256_hex(data: &[u8]) -> String {
    use sha2::{Digest, Sha256};
    let mut hasher = Sha256::new();
    hasher.update(data);
    let digest = hasher.finalize();
    let mut s = String::with_capacity(64);
    for b in digest {
        s.push_str(&format!("{b:02x}"));
    }
    s
}

/// 本地时间 ISO 8601（秒精度；C# DateTime 序列化字符串可排序，旧文件兼容）。
pub(crate) fn now_iso() -> String {
    use windows::Win32::System::SystemInformation::GetLocalTime;
    // 0.58：GetLocalTime() -> SYSTEMTIME（无参封装）
    let st = unsafe { GetLocalTime() };
    format!(
        "{:04}-{:02}-{:02}T{:02}:{:02}:{:02}",
        st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond
    )
}

/// upsert 结果。
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum UpsertOutcome {
    /// 新条目。
    New(String),
    /// 指纹命中已存在条目（CopyCount+1 并置顶）。
    Updated(String, i32),
}

/// 引擎存储（根目录 = %LOCALAPPDATA%\BetterDesktop，与宿主共享）。
pub struct Store {
    root: PathBuf,
    entries: Vec<ClipboardEntry>,
    /// 去重索引：fingerprint → id（O(1) 查重，修复现有 C# O(n) 扫描）。
    index: HashMap<String, String>,
    /// 磁盘内容是否已被可信载入。
    ///
    /// 【2026-09-12 审计修复 · 数据丢失级】此前的 `load()` 在 IO 错误 / DPAPI 解密失败 / JSON 损坏时
    /// 只记日志然后留**空 entries**，而 800ms autosave 紧接着就会把空库写回磁盘 ——
    /// 所谓"损坏自愈"实际是"损坏即清库"，用户历史瞬间消失且无备份。
    /// 现改为：载入失败 → `storage_trusted = false` → [`Store::save`] 一律拒绝写盘
    ///（磁盘原文件原样保留，供人工恢复），只有正常载入后才恢复写盘能力。
    storage_trusted: bool,
}

impl Store {
    pub fn new(root: impl Into<PathBuf>) -> Self {
        Store {
            root: root.into(),
            entries: Vec::new(),
            index: HashMap::new(),
            storage_trusted: true,
        }
    }

    /// 载入是否可信（false = 拒绝写盘，保护磁盘上的原始历史）。
    /// 供 autosave/flush 先判断再写：拒写状态不该每 800ms 刷一条 error 日志。
    pub fn storage_trusted(&self) -> bool {
        self.storage_trusted
    }

    pub fn root(&self) -> &Path {
        &self.root
    }

    pub fn entries(&self) -> &[ClipboardEntry] {
        &self.entries
    }

    /// 条目总数（含收藏）。
    /// 【2026-09-12】`query` 改为「先过滤再分页」后不再需要全库总数；保留供工具/诊断与 is_empty 成对使用。
    #[allow(dead_code)]
    pub fn len(&self) -> usize {
        self.entries.len()
    }

    #[allow(dead_code)] // 预留：IPC/工具侧空库判断
    pub fn is_empty(&self) -> bool {
        self.entries.is_empty()
    }

    /// 按 id 查条目。
    pub fn get_by_id(&self, id: &str) -> Option<&ClipboardEntry> {
        self.entries.iter().find(|e| e.id == id)
    }

    /// 按 id 取可变引用（IPC pin/tags 用）。
    pub fn entries_mut_ref(&mut self, id: &str) -> Option<&mut ClipboardEntry> {
        self.entries.iter_mut().find(|e| e.id == id)
    }

    /// 按指纹查条目。
    ///
    /// 【2026-09-14】已接入生产路径：`engine::build_entry` 在**落盘之前**用它判重
    /// （命中就不再写原图/缩略图/副本/content.bin —— 否则那些文件会成为永不回收的孤儿）。
    pub fn find_by_fingerprint(&self, fp: &str) -> Option<&ClipboardEntry> {
        self.index.get(fp).and_then(|id| self.get_by_id(id))
    }

    /// **图片类内容**（图片条目或表情包条目）是否已存在该像素哈希。O(1) —— 走去重索引，不遍历条目。
    ///
    /// 键必须由 `ClipboardEntry::image_fingerprint` 生成（**单一真相源**，否则永远查不到）。
    ///
    /// 【2026-09-13】由 `has_sticker_by_hash` 更名：图片与表情包已合并到同一键空间，本方法一次覆盖两种情况 ——
    /// 单独查"表情包集合"正是导致"已在历史里的图被重复导入"的原因（用户实测反馈）。
    #[allow(dead_code)] // 单测守门用（生产路径走 find_by_image_hash，语义相同）
    pub fn has_image_by_hash(&self, hash: &str) -> bool {
        self.index
            .contains_key(&ClipboardEntry::image_fingerprint(hash))
    }

    /// 按像素哈希取条目（导入时判断"这张图是不是已经在库里"）。O(1)。
    pub fn find_by_image_hash(&self, hash: &str) -> Option<&ClipboardEntry> {
        self.index
            .get(&ClipboardEntry::image_fingerprint(hash))
            .and_then(|id| self.get_by_id(id))
    }

    // ---- 存储目录布局（与 C# 现状及计划 §6.1 一致）----

    pub fn storage_file_path(&self) -> PathBuf {
        self.root.join(STORAGE_FILE)
    }

    /// clipboard\images\（图片原图，full 模式 PNG 无损）。
    pub fn images_dir(&self) -> PathBuf {
        self.root.join("clipboard").join("images")
    }

    /// clipboard\thumbs\（480px 缩略图，有 alpha 用 PNG / 无 alpha 用 JPEG）。
    pub fn thumbs_dir(&self) -> PathBuf {
        self.root.join("clipboard").join("thumbs")
    }

    /// clipboard\content\（>100KB 文本 Deflate 压缩 + DPAPI 加密，懒加载）。
    pub fn content_dir(&self) -> PathBuf {
        self.root.join("clipboard").join("content")
    }

    /// clipboard\files\（full 模式文件副本，≤64MB 单文件）。
    pub fn files_dir(&self) -> PathBuf {
        self.root.join("clipboard").join("files")
    }

    /// clipboard\html-images\{entryId}\（HTML data URI 提取图）。
    pub fn html_images_dir(&self) -> PathBuf {
        self.root.join("clipboard").join("html-images")
    }

    /// clipboard\stickers\（表情包**原文件珍藏**：字节级复制，绝不转码 —— 动图只有原文件能保动画）。
    pub fn stickers_dir(&self) -> PathBuf {
        self.root.join("clipboard").join("stickers")
    }

    // ---- 加载 / 保存 ----

    /// 加载历史（兼容 CBENC1/明文旧格式；损坏自愈为空历史）+ 重建去重索引。
    pub fn load(&mut self) {
        let path = self.storage_file_path();
        let content = match std::fs::read(&path) {
            Ok(c) => c,
            Err(e) if e.kind() == io::ErrorKind::NotFound => {
                crate::log::info("storage file not found; starting empty");
                return;
            }
            Err(e) => {
                // 读不到 ≠ 没有：禁止写盘，否则空库会覆盖磁盘上的真实历史
                self.storage_trusted = false;
                crate::log::error(format!(
                    "read storage failed: {e}; starting empty and DISABLING autosave (existing file preserved)"
                ));
                return;
            }
        };

        let json_bytes = if content.len() > ENC_HEADER.len() && content.starts_with(ENC_HEADER) {
            match dpapi::unprotect(&content[ENC_HEADER.len()..]) {
                Ok(b) => b,
                Err(e) => {
                    self.storage_trusted = false;
                    crate::log::error(format!(
                        "unprotect failed: {e}; starting empty and DISABLING autosave (existing file preserved)"
                    ));
                    self.entries.clear();
                    return;
                }
            }
        } else {
            // 明文旧格式回退（CBENC1 头缺失）
            content
        };

        match serde_json::from_slice::<Vec<ClipboardEntry>>(&json_bytes) {
            Ok(loaded) => {
                crate::log::info(format!("loaded {} entries from {}", loaded.len(), path.display()));
                self.entries = loaded;
                self.rebuild_index();
                self.storage_trusted = true;
            }
            Err(e) => {
                self.storage_trusted = false;
                crate::log::error(format!(
                    "json parse failed: {e}; starting empty and DISABLING autosave (existing file preserved)"
                ));
                self.entries.clear();
            }
        }
    }

    fn rebuild_index(&mut self) {
        self.index.clear();
        for e in &self.entries {
            self.index.insert(e.fingerprint(), e.id.clone());
        }
    }

    /// 保存历史（CBENC1 + DPAPI）。失败返回 Err（调用方按策略重试/记录）。
    ///
    /// 【2026-09-12 修复·原子写】先写 `*.json.tmp` 再 rename 覆盖正式文件。
    /// 此前直接 `fs::write` 目标文件 —— 而 autosave 每 800ms 就会写一次，写盘途中进程被杀 / 断电 /
    /// 磁盘满会留下**截断的 CBENC1 文件**，下次 `load()` 解密或解析失败即"损坏自愈为空历史"
    /// → 整个剪贴板历史丢失。rename 在同一卷上是原子的（Windows 走 MoveFileEx 覆盖语义）。
    /// 轻量变更签名（条数 + 首条 id + 收藏数），供 autosave 判断"是否值得落盘"。
    ///
    /// 【为什么需要】autosave 原先每 800ms **无条件** `save()`：全量序列化 → DPAPI 加密 →
    /// 写临时文件 → rename。历史数万条时这是每秒 1.25 次的持续 CPU + 磁盘写（代码注释自认
    /// "dirty 标志后续优化"），是常驻耗电/风扇长转的来源之一。
    ///
    /// 【为什么用签名而不是在写方法里打 dirty 标志】在 `Store` 的每个 `&mut self` 方法里标记，
    /// 一旦漏标就会**丢用户剪贴板历史**（数据丢失级风险）；签名方案零侵入、不可能漏到"永不落盘"
    /// （autosave 另有 60 秒强制兜底）。
    ///
    /// 【代价】仅"内容改动但条数/首条/收藏数都不变"的写（例如同一内容重复复制只增 copy_count）
    /// 不会立即落盘，由 60 秒兜底覆盖——这类变更本身也不影响历史可读性。
    pub fn change_signature(&self) -> u64 {
        use std::hash::{Hash, Hasher};

        let mut hasher = std::collections::hash_map::DefaultHasher::new();
        self.entries.len().hash(&mut hasher);
        if let Some(first) = self.entries.first() {
            first.id.hash(&mut hasher);
        }
        self.entries.iter().filter(|e| e.is_pinned).count().hash(&mut hasher);
        hasher.finish()
    }

    pub fn save(&self) -> io::Result<()> {
        // 【数据丢失防线 · 2026-09-12 审计】载入不可信（IO 错误 / DPAPI 失败 / JSON 损坏）时**拒绝写盘**：
        // 否则空内存库会覆盖磁盘上的真实历史，把"这次读不到"升级成"永久丢失"。
        // 磁盘原文件原样保留 → 用户/我们仍可人工恢复；日志已明确记下这一状态。
        if !self.storage_trusted {
            return Err(io::Error::new(
                io::ErrorKind::Other,
                "storage not loaded safely; refusing to overwrite (see engine log)",
            ));
        }

        let json = serde_json::to_vec(&self.entries)
            .map_err(|e| io::Error::new(io::ErrorKind::InvalidData, e.to_string()))?;
        let cipher = dpapi::protect(&json)
            .map_err(|e| io::Error::new(io::ErrorKind::Other, format!("dpapi: {e}")))?;
        let mut with_header = Vec::with_capacity(ENC_HEADER.len() + cipher.len());
        with_header.extend_from_slice(ENC_HEADER);
        with_header.extend_from_slice(&cipher);
        let _ = std::fs::create_dir_all(&self.root);

        let final_path = self.storage_file_path();
        let tmp_path = final_path.with_extension("json.tmp");
        std::fs::write(&tmp_path, &with_header)?;
        std::fs::rename(&tmp_path, &final_path)
    }

    // ---- 去重 upsert（D7：O(1)）----

    /// 指纹去重入库：命中 → CopyCount+1 + 更新时间 + 置顶；未命中 → 追加（新条目在末尾，列表按新→旧输出）。
    ///
    /// 【2026-09-12】指纹已改为 Text/Html/RichText 统一按纯文本 —— 命中时需**合并格式**：
    /// 同一段文字可能先以纯文本入库、后以富文本再次复制（或反之）。若不补充格式，
    /// 之后再粘贴会丢格式（HTML/RTF 被吞）。规则：只做**升级**（纯文本 → 带 HTML/RTF），绝不降级。
    pub fn upsert(&mut self, mut entry: ClipboardEntry) -> UpsertOutcome {
        let fp = entry.fingerprint();
        if let Some(id) = self.index.get(&fp).cloned() {
            if let Some(pos) = self.entries.iter().position(|e| e.id == id) {
                let existing = &mut self.entries[pos];
                existing.copy_count += 1;
                existing.timestamp = now_iso();

                // 格式升级：已有内容缺 HTML 而新条目带 HTML → 补齐并升级类型/分类标记
                if existing.html_content.is_empty() && !entry.html_content.is_empty() {
                    existing.html_content = entry.html_content.clone();
                    existing.rtf_content = entry.rtf_content.clone();
                    existing.content_type = entry.content_type;
                    existing.category = entry.category;
                    existing.has_images = entry.has_images;
                    existing.has_table = entry.has_table;
                } else if existing.rtf_content.is_empty() && !entry.rtf_content.is_empty() {
                    // 仅有 RTF 的情形（如从 Word 复制但无 HTML 的源）
                    existing.rtf_content = entry.rtf_content.clone();
                    if existing.content_type == ItemKind::Text {
                        existing.content_type = ItemKind::RichText;
                    }
                }

                let copy_count = existing.copy_count;
                // 置顶：把该条目移到末尾（输出顺序 = 新→旧，末尾 = 最新）
                let e = self.entries.remove(pos);
                self.entries.push(e);
                return UpsertOutcome::Updated(id, copy_count);
            }
        }
        entry.timestamp = if entry.timestamp.is_empty() {
            now_iso()
        } else {
            entry.timestamp
        };
        let id = entry.id.clone();
        self.entries.push(entry);
        self.index.insert(fp, id.clone());
        UpsertOutcome::New(id)
    }

    /// 删除条目（含级联清理图片/缩略图/content.bin/文件副本/html 提取图）。
    pub fn remove_by_id(&mut self, id: &str) -> bool {
        let Some(pos) = self.entries.iter().position(|e| e.id == id) else {
            return false;
        };
        let entry = self.entries.remove(pos);
        self.index.retain(|_, v| v != id);
        self.remove_entry_files(&entry);
        true
    }

    /// 级联清理条目关联的外部文件。
    fn remove_entry_files(&self, entry: &ClipboardEntry) {
        let id = &entry.id;
        if entry.content_type == ItemKind::Image || !entry.image_path.is_empty() {
            let _ = std::fs::remove_file(self.root.join(&entry.image_path));
            // 缩略图：尝试 png/jpg 两种扩展名
            let _ = std::fs::remove_file(self.thumbs_dir().join(format!("{id}.png")));
            let _ = std::fs::remove_file(self.thumbs_dir().join(format!("{id}.jpg")));
        }
        if entry.content_in_bin {
            let _ = std::fs::remove_file(self.content_dir().join(format!("{id}.bin")));
        }
        let _ = std::fs::remove_dir_all(self.files_dir().join(id));
        let _ = std::fs::remove_dir_all(self.html_images_dir().join(id));

        // 【表情包 · 2026-09-12】只删**我们自己复制进 stickers 目录**的那份副本。
        // **安全红线**：`parent() == stickers_dir` 是唯一判据 —— 用户原始文件绝不能被我们删掉
        //（用户是"导入"，不是"移动"；原文件仍在原处）。
        //
        // 【2026-09-13 修正 · 判据从"标记"退回"路径"】此前写成 `if entry.is_sticker { …删副本… }`：
        // 标记是用户可随时取消的，而"我们复制进 stickers 的副本"是**客观事实**。
        // 一旦用标记决定回收，用户「取消表情包标记 → 删除该条目」就会留下**孤儿副本**：
        // 磁盘占用被 `total_used_bytes()`（扫描整个 clipboard 目录）计入 → 存储横幅长期虚高、无人回收。
        // 故此处**无条件**按路径回收（标记只决定"是否算表情包语义"，不决定"是否回收我们自己的副本"）。
        let stickers = self.stickers_dir();
        for p in &entry.file_paths {
            let path = std::path::Path::new(p);
            if path.parent() == Some(stickers.as_path()) {
                let _ = std::fs::remove_file(path);
            }
        }
    }

    // ---- 图片去重迁移（2026-09-12）----

    /// 一次性迁移：为图片条目补齐**像素级内容指纹**，并合并历史遗留的重复图片。
    ///
    /// 背景：旧指纹用 `image_path`（形如 `clipboard\images\{id}.png`，**含条目自己的 id**）——
    /// 每次捕获都不同 → 图片**永不命中去重** → 截图工具一次复制写多个格式（CF_PNG + CF_DIB）
    /// 就会留下两条相同历史（用户实测反馈）。
    ///
    /// 返回 `(合并删除数, 新计算指纹数)`。幂等：`content_hash` 已存在的条目只参与分组，不再解码。
    /// 保留策略：同指纹保留**最早**一条，并把被删条目的 `is_pinned` / `copy_count` / 更晚的 `timestamp`
    /// 合并过去（不因去重丢用户信息）；原图缺失的条目原样保留（绝不误删）。
    pub fn dedupe_images(&mut self) -> (usize, usize) {
        let entries = std::mem::take(&mut self.entries);
        let mut keep: Vec<ClipboardEntry> = Vec::with_capacity(entries.len());
        let mut seen: HashMap<String, usize> = HashMap::new(); // 指纹 → keep 下标
        let mut merged = 0usize;
        let mut backfilled = 0usize;

        for mut e in entries {
            // 【2026-09-13】表情包条目也纳入本迁移：它与图片条目共用**像素键空间**
            //（见 `ClipboardEntry::fingerprint`）—— 用户"先复制过这张图、之后又导入为表情包"时，
            // 库里会留下两条同像素条目（一条"图片"、一条"表情包"），正是用户反馈的重复现象。
            let is_image = e.content_type == ItemKind::Image;
            // 【2026-09-13】参与范围：图片条目 + **带表情包标记的文件条目**（导入的动图是 Files 类型，
            // file_paths 指向 stickers 副本）。判据由"分类是表情包"改为"**标记**是表情包"。
            let is_marked_sticker = e.is_sticker && !e.file_paths.is_empty();
            if !is_image && !is_marked_sticker {
                keep.push(e);
                continue;
            }

            // 指纹统一为**像素级** hash：
            //  · 图片条目：`content_hash` 本就是像素 hash（为空则读原图计算后回填）；
            //  · 表情包条目：旧数据的 `content_hash` 是**文件字节 sha256**（旧口径）→ 必须按副本重算像素 hash。
            //
            // ⚠️ **不能只靠长度判定**：legacy 后端（`ClipboardManager`）曾把文件 sha256 **截断成 16 hex** 落盘，
            // 与像素 hash 同长 → 那些条目会被误判为"已是像素口径"而**永远不重算**，
            // 结果是"legacy 导入过 → 切回引擎再导入同一张图"仍会新增重复条目（正是本次要消灭的现象）。
            // 追加"无尺寸"判据：引擎写入时一定带 `image_width/height`，legacy 不写 → 组合可识别。
            let need_pixel_hash = if is_image {
                e.content_hash.is_empty()
            } else {
                e.content_hash.len() != 16 || e.image_width == 0
            };
            if need_pixel_hash {
                let path = if is_image {
                    self.root.join(&e.image_path)
                } else {
                    std::path::PathBuf::from(&e.file_paths[0])
                };
                match std::fs::read(&path) {
                    Ok(bytes) => {
                        e.content_hash = crate::fingerprint::image_content_hash(&bytes);
                        backfilled += 1;
                    }
                    Err(err) => {
                        crate::log::warn(format!(
                            "dedupe_images: cannot read {path:?}: {err}; keep entry as-is"
                        ));
                        keep.push(e);
                        continue;
                    }
                }
            }

            match seen.get(&e.content_hash).copied() {
                Some(idx) => {
                    // 【保留策略 · 2026-09-13】表情包是用户**主动标记**的，优先保留它：
                    // 当"先到的图片条目"与"后到的表情包条目"同像素时，把图片合并进表情包（而不是反过来）。
                    if is_marked_sticker && !keep[idx].is_sticker {
                        let prev = std::mem::replace(&mut keep[idx], e);
                        if prev.is_pinned {
                            keep[idx].is_pinned = true;
                        }
                        keep[idx].copy_count += prev.copy_count;
                        if prev.timestamp > keep[idx].timestamp {
                            keep[idx].timestamp = prev.timestamp.clone();
                        }
                        // 被合并的是**图片条目**：其原图与缩略图随之级联清理；
                        // 表情包自己的副本与缩略图文件名各带 id，不受影响。
                        self.remove_entry_files(&prev);
                    } else {
                        if e.is_pinned && !keep[idx].is_pinned {
                            keep[idx].is_pinned = true;
                        }
                        keep[idx].copy_count += e.copy_count;
                        if e.timestamp > keep[idx].timestamp {
                            keep[idx].timestamp = e.timestamp.clone();
                        }
                        self.remove_entry_files(&e);
                    }
                    merged += 1;
                }
                None => {
                    seen.insert(e.content_hash.clone(), keep.len());
                    keep.push(e);
                }
            }
        }

        self.entries = keep;
        if merged > 0 || backfilled > 0 {
            // 必须重建索引：①剔除已合并条目；②把新回填的**像素级**指纹纳入 ——
            // 否则索引里仍是旧的路径指纹，新捕获的图片按 hash 查找会命不中，去重等于没修。
            self.rebuild_index();
        }
        (merged, backfilled)
    }

    // ---- 表情包：分类 → 标记 的迁移（2026-09-13）----

    /// 一次性迁移：把"表情包**分类**"的旧条目转成"表情包**标记**"。
    ///
    /// 【为什么必须改 · 用户口径 2026-09-13】旧模型里表情包是 `Category::Sticker`，且**只能靠"导入文件"进入** ——
    /// 于是**文字颜文字永远无法成为表情包**（它没有文件可导入）。用户原话："跟收藏一样的机制，
    /// 这样就不管是图片还是颜文字都可以了"。
    ///
    /// 转换两件事：
    /// ① `is_sticker = true` —— 此后"豁免驱逐/清理"与"出现在表情包筛选中"都由这个标记决定；
    /// ② `category` 按**实际内容**重判（旧数据一律是 Files）：图片扩展名 → `Image`，其余 → `File`；
    ///    这样面板的分类标签不会再出现"表情包"这种"既是分类又是标记"的混淆值。
    ///
    /// 幂等：转换后 `category != Sticker`，重跑不再命中。返回转换条数。
    pub fn migrate_sticker_flag(&mut self) -> usize {
        let mut converted = 0usize;
        for e in self.entries.iter_mut() {
            if e.category != Category::Sticker {
                continue;
            }

            e.is_sticker = true;
            let is_image_file = e
                .file_paths
                .first()
                .and_then(|p| std::path::Path::new(p).extension())
                .and_then(|x| x.to_str())
                .map(|x| crate::engine::STICKER_EXTS.contains(&x.to_ascii_lowercase().as_str()))
                .unwrap_or(false);
            e.category = if is_image_file {
                Category::Image
            } else {
                Category::File
            };
            converted += 1;
        }
        converted
    }

    // ---- 空条目清理（2026-09-12）----

    /// 判定"视觉上为空"的文本：纯空白 **加上各种零宽/方向控制字符**。
    ///
    /// 【为什么不能用 `trim()` · 2026-09-12 真机漏洞】`str::trim()` 只认 Unicode `White_Space`，
    /// `\u{200B}`（零宽空格）等**零宽字符**不属于空白字符 —— 于是 `" ".replace(..., "\u{200B}")`
    /// 之类的"看起来是空"的内容能绕过"空数据不入库"这条红线。
    /// 实测后果：按序粘贴的**哨兵条目**（用零宽空格做"空"）被当作有效内容入库 7 条。
    pub fn is_blank_text(s: &str) -> bool {
        s.chars().all(|c| {
            c.is_whitespace()
                || matches!(
                    c,
                    '\u{00AD}'                  // SOFT HYPHEN
                    | '\u{180E}'                // MONGOLIAN VOWEL SEPARATOR
                    | '\u{200B}'                // ZERO WIDTH SPACE
                    | '\u{200C}'                // ZERO WIDTH NON-JOINER
                    | '\u{200D}'                // ZERO WIDTH JOINER
                    | '\u{200E}'                // LEFT-TO-RIGHT MARK
                    | '\u{200F}'                // RIGHT-TO-LEFT MARK
                    | '\u{2060}'                // WORD JOINER
                    | '\u{FEFF}'                // ZERO WIDTH NO-BREAK SPACE / BOM
                )
                || ('\u{202A}'..='\u{202E}').contains(&c) // 方向嵌入/覆盖
                || ('\u{2066}'..='\u{2069}').contains(&c) // 方向隔离
        })
    }

    /// 清除**真空条目**：无文本、无 HTML、无 RTF、无文件、非图片、正文也不在 content.bin 里。
    ///
    /// 来源：修复前的漏网空快照 —— 剪贴板被清空、复制了引擎不支持的类型、复制纯空白文本。
    /// 用户要求："复制不要录入空数据，没有意义，只会占用历史记录的位置"。
    /// 返回清除条数。幂等。图片（`image_path`）与文件（`file_paths`）条目天然无文本，必须保留。
    pub fn purge_empty(&mut self) -> usize {
        let mut removed = 0usize;
        let mut keep = Vec::with_capacity(self.entries.len());

        for e in std::mem::take(&mut self.entries) {
            let vacuous = e.content_type != ItemKind::Image
                && e.file_paths.is_empty()
                && !e.content_in_bin // 大文本正文在 content.bin 里，content 字段本就为空
                && Self::is_blank_text(&e.content)
                && Self::is_blank_text(&e.html_content)
                && Self::is_blank_text(&e.rtf_content);
            if vacuous {
                self.remove_entry_files(&e);
                removed += 1;
            } else {
                keep.push(e);
            }
        }

        self.entries = keep;
        if removed > 0 {
            self.rebuild_index();
        }
        removed
    }

    // ---- 文本类去重迁移（2026-09-12）----

    /// 一次性迁移：合并"同一段文字、不同剪贴板格式"造成的重复条目。
    ///
    /// 背景：`fingerprint()` 旧实现带类型前缀（`text:` / `html:` / `rtf:`）→ 同一段文字从纯文本源
    /// 与富文本源各复制一次就留两条，且因一条带 HTML 一条不带而被判成不同分类
    /// （用户实测："他也被文本与富文本同时认证"）。指纹统一为纯文本后，历史遗留需合并。
    ///
    /// 保留策略：同指纹保留**最早出现**的那条（维持列表位置），但把**更丰富的格式**合并进去
    /// （有 HTML > 有 RTF > 纯文本），并合并 `is_pinned` / `copy_count` / 更晚的 `timestamp`；
    /// 被合并条目的外部文件级联清理。图片不走这里（另有像素级 `dedupe_images`）。
    pub fn dedupe_text_duplicates(&mut self) -> usize {
        let entries = std::mem::take(&mut self.entries);
        let mut keep: Vec<ClipboardEntry> = Vec::with_capacity(entries.len());
        let mut seen: HashMap<String, usize> = HashMap::new(); // 指纹 → keep 下标
        let mut merged = 0usize;

        for e in entries {
            if e.content_type == ItemKind::Image {
                keep.push(e); // 图片按像素指纹去重，不参与文本合并
                continue;
            }

            let fp = e.fingerprint();
            match seen.get(&fp).copied() {
                Some(idx) => {
                    if keep[idx].html_content.is_empty() && !e.html_content.is_empty() {
                        keep[idx].html_content = e.html_content.clone();
                        keep[idx].rtf_content = e.rtf_content.clone();
                        keep[idx].content_type = e.content_type;
                        keep[idx].category = e.category;
                        keep[idx].has_images = e.has_images;
                        keep[idx].has_table = e.has_table;
                    } else if keep[idx].rtf_content.is_empty() && !e.rtf_content.is_empty() {
                        keep[idx].rtf_content = e.rtf_content.clone();
                        if keep[idx].content_type == ItemKind::Text {
                            keep[idx].content_type = ItemKind::RichText;
                        }
                    }
                    if e.is_pinned {
                        keep[idx].is_pinned = true;
                    }
                    keep[idx].copy_count += e.copy_count;
                    if e.timestamp > keep[idx].timestamp {
                        keep[idx].timestamp = e.timestamp.clone();
                    }
                    self.remove_entry_files(&e);
                    merged += 1;
                }
                None => {
                    seen.insert(fp, keep.len());
                    keep.push(e);
                }
            }
        }

        self.entries = keep;
        if merged > 0 {
            self.rebuild_index();
        }
        merged
    }

    // ---- 分类重算迁移（2026-09-12）----

    /// 一次性迁移：重算全部条目的语义分类（`category` / `is_code` / `has_images` / `has_table`）。
    ///
    /// 背景：`analyzer::is_code_text` 的缩进信号曾**完全失效**（trim 后再 trim_start 比长度恒相等），
    /// 且关键词表偏 .NET/C —— 从 IDE 复制的 Rust/前端代码一个词都命不中，被落成 `RichText`，
    /// 面板「代码」分类里永远看不到它们（用户实测的"真空地带"）。修复分类器后历史条目需重算归位。
    ///
    /// 返回发生变化的条数。幂等：已归位条目重算结果相同、不计变化。
    pub fn reclassify_all(&mut self) -> usize {
        let mut changed = 0usize;
        for e in &mut self.entries {
            // 图片/文件：分类由类型直接决定（analyzer 短路），无需重算
            if matches!(e.content_type, ItemKind::Image | ItemKind::Files) {
                continue;
            }
            // 大文本正文在 content.bin 里（未加载）→ 重算会基于空文本退化，跳过以免误降级
            if e.content_in_bin {
                continue;
            }

            let profile =
                crate::analyzer::analyze(e.content_type, &e.html_content, &e.content);
            if profile.category != e.category
                || profile.is_code != e.is_code
                || profile.has_images != e.has_images
                || profile.has_table != e.has_table
            {
                e.category = profile.category;
                e.is_code = profile.is_code;
                e.has_images = profile.has_images;
                e.has_table = profile.has_table;
                changed += 1;
            }
        }
        // category 不参与指纹（指纹只看内容）→ 无需重建索引
        changed
    }

    /// 清理全部条目外部文件（清空历史时调用）。
    #[allow(dead_code)] // 预留：clear_all IPC 接入时使用
    pub fn clear_all_files(&self) {
        let _ = std::fs::remove_dir_all(self.images_dir());
        let _ = std::fs::remove_dir_all(self.thumbs_dir());
        let _ = std::fs::remove_dir_all(self.content_dir());
        let _ = std::fs::remove_dir_all(self.files_dir());
        let _ = std::fs::remove_dir_all(self.html_images_dir());
    }

    // ---- 大文本 content.bin（Deflate + DPAPI，懒加载）----

    /// 保存大文本内容到 content\{id}.bin（>100KB 调用）；成功返回压缩前字节数。
    pub fn save_external_content(&self, id: &str, content: &str) -> io::Result<()> {
        let mut encoder = DeflateEncoder::new(Vec::new(), Compression::default());
        encoder.write_all(content.as_bytes())?;
        let compressed = encoder.finish()?;
        let cipher = dpapi::protect(&compressed)
            .map_err(|e| io::Error::new(io::ErrorKind::Other, format!("dpapi: {e}")))?;
        let dir = self.content_dir();
        std::fs::create_dir_all(&dir)?;
        std::fs::write(dir.join(format!("{id}.bin")), cipher)
    }

    /// 懒加载大文本内容（content_in_bin 条目命中才读；失败返回 None 不崩）。
    pub fn load_external_content(&self, id: &str) -> Option<String> {
        let path = self.content_dir().join(format!("{id}.bin"));
        let cipher = std::fs::read(path).ok()?;
        let compressed = dpapi::unprotect(&cipher).ok()?;
        let mut decoder = DeflateDecoder::new(&compressed[..]);
        let mut text = String::new();
        decoder.read_to_string(&mut text).ok()?;
        Some(text)
    }

    /// 获取条目完整内容（含懒加载 bin）。
    pub fn get_content(&self, entry: &ClipboardEntry) -> String {
        if entry.content_in_bin {
            self.load_external_content(&entry.id)
                .unwrap_or_else(|| format!("[content unavailable: {}]", entry.id))
        } else {
            entry.content.clone()
        }
    }

    /// 判定并落盘大文本（>100KB → bin + content_in_bin；否则内联）。
    /// 返回 (content_in_bin, size_bytes)。
    pub fn store_content(&self, id: &str, content: &str) -> (bool, i64) {
        let bytes = content.len() as i64;
        if content.len() > CONTENT_EXTERNAL_THRESHOLD {
            match self.save_external_content(id, content) {
                Ok(()) => (true, bytes),
                Err(e) => {
                    crate::log::error(format!("content.bin write failed, inlining: {e}"));
                    (false, bytes)
                }
            }
        } else {
            (false, bytes)
        }
    }

    // ---- 驱逐 / 预算 ----

    /// 执行全部驱逐规则（条数/天数/总量预算），返回被驱逐的 id 列表。
    /// 收藏条目不参与驱逐（pinned_limit 保护见 pin 调用方）。
    pub fn evict(&mut self, settings: &Settings) -> Vec<String> {
        // 【2026-09-12 修复】收集**条目本身**而非仅 id —— 下方级联清理需要条目信息。
        // 此前只存 id 再回 `self.entries` 回查，而条目已在上面 `remove` 掉 → 永远 find 不到（死代码）。
        let mut evicted: Vec<ClipboardEntry> = Vec::new();
        let old_enough = |e: &ClipboardEntry| {
            e.timestamp.is_empty() || {
                let cutoff = now_iso_minus_days(settings.retention_days);
                e.timestamp.as_str() < cutoff.as_str()
            }
        };

        let evict_oldest_unpinned = |entries: &mut Vec<ClipboardEntry>, evicted: &mut Vec<ClipboardEntry>, condition: &dyn Fn(&ClipboardEntry) -> bool| {
            if let Some(idx) = entries
                .iter()
                .enumerate()
                // 【表情包豁免 · 2026-09-12】表情包是用户**主动标记**的内容，天数与条数驱逐一律跳过；
                // 被后台静默删掉是最伤信任的失败模式（对齐"用户历史不能静默丢弃"口径）。
                // 【2026-09-13】判据由"分类是表情包"改为**标记** —— 与"收藏"同级的独立布尔，
                // 任意类型条目（含文字颜文字）都能受此保护。
                .filter(|(_, e)| !e.is_pinned && !e.is_sticker && condition(e))
                .min_by_key(|(_, e)| e.timestamp.clone())
                .map(|(i, _)| i)
            {
                let e = entries.remove(idx);
                evicted.push(e);
                true
            } else {
                false
            }
        };

        // 1) 天数驱逐（保留至少 1 条）
        while self.entries.len() > 1 {
            if !evict_oldest_unpinned(&mut self.entries, &mut evicted, &old_enough) {
                break;
            }
        }

        // 2) 条数驱逐
        while self.entries.len() > settings.capacity {
            if !evict_oldest_unpinned(&mut self.entries, &mut evicted, &|_| true) {
                break; // 全收藏：不再驱逐
            }
        }

        // 3) 总量预算（max_total_mb）：**已由"硬驱逐"改为"软限制"，此处不再删除任何条目**。
        //    2026-09-12 用户口径："总上限不是占位的上限，是历史数据达到存储上面也可以存储，
        //    但我们要发消息提醒用户及时清理" —— 超预算时用户的历史属于用户，不能静默丢弃。
        //    现由 `cmd_storage_status` 上报占用与超限状态，UI（面板横幅）负责提醒并给出一键清理。
        //    注意：本条曾因"孤儿文件计入 total_used_bytes"造成"越驱越占、几乎删光仍不达预算"，
        //    改软限制同时也消除了该风险面。

        // 级联清理 + 索引重建
        if !evicted.is_empty() {
            for e in &evicted {
                // 【2026-09-12 修复】直接清理被驱逐条目的外部文件。
                // 此前按 id 回查 entries（条目已删）→ 该清理是**死代码**：原图 / 缩略图 / content.bin /
                // files 副本 / html-images 全部残留磁盘；更糟的是 total_used_bytes() 会把孤儿文件计入，
                // 于是总量预算驱逐"越驱越占"，可能把未收藏条目几乎删光仍不达预算。
                self.remove_entry_files(e);
            }
            self.rebuild_index();
        }
        evicted.into_iter().map(|e| e.id).collect()
    }

    /// 迁移：剥离历史条目 `html_content` 里残留的 CF_HTML 头（幂等），返回处理条数。
    ///
    /// 【2026-09-12】捕获侧修复前入库的富文本条目，其 `html_content` 带着
    /// `Version:0.9 / StartHTML:…` 偏移头；若不迁移，这些**存量条目**每次粘贴仍会
    /// 出现"双重 CF_HTML 头 → 应用显示整串元数据"（用户实测报告的现象）。
    pub fn strip_cf_html_headers(&mut self) -> usize {
        let mut n = 0usize;
        for e in self.entries.iter_mut() {
            // 无条件尝试（幂等）：`strip_cf_html_header` 对干净 HTML 只做一次首行判断即返回，
            // 代价极小；而"有条件才剥"会漏掉已被剥过一半、只剩尾部字段的存量条目
            //（2026-09-12 真机：第 2 条 html 仍以 SourceURL 开头，正是踩了这个）。
            let cleaned = crate::html::strip_cf_html_header(&e.html_content);
            if cleaned != e.html_content {
                e.html_content = cleaned;
                n += 1;
            }
        }
        n
    }

    /// 迁移：为「纯文本为空但含 HTML」的历史条目回填近似纯文本（幂等），返回处理条数。
    /// 见 `html::html_to_text`：修复前此类条目没有可搜索文本、也没有粘贴兜底格式，
    /// 一次性合并粘贴会拼出空串（用户实测）。
    pub fn backfill_html_text(&mut self) -> usize {
        let mut n = 0usize;
        for e in self.entries.iter_mut() {
            if e.html_content.is_empty() {
                continue;
            }
            // 不只是"content 为空"时才补 —— 提取算法本身修正后，旧回填结果需要被**重算覆盖**
            //（真机：首版 html_to_text 会把 `</path>` 误当 `</p>`，回填出 "ath / athimage.png"）。
            // 结果相同则不写（幂等），因此每次启动重算的代价仅是一次字符串处理。
            let t = crate::html::html_to_text(&e.html_content);
            if !t.is_empty() && t != e.content {
                e.content = t;
                n += 1;
            }
        }
        n
    }

    /// 统计存储占用（clipboard 目录全部文件字节）。
    pub fn total_used_bytes(&self) -> u64 {
        let clip = self.root.join("clipboard");
        let mut total = 0u64;
        if let Ok(rd) = std::fs::read_dir(&clip) {
            for dir in rd.flatten() {
                total += dir_size(&dir.path());
            }
        }
        total
    }

    /// 复制条目（id 列表 → 新条目；用于 IPC import 等）。
    #[allow(dead_code)] // 预留：import 批量注入时使用
    pub fn push_entry(&mut self, entry: ClipboardEntry) {
        let fp = entry.fingerprint();
        self.index.insert(fp, entry.id.clone());
        self.entries.push(entry);
    }

    /// 测试专用：直接 push（绕过 index；upsert 路径不走这里）。
    #[cfg(test)]
    fn entries_mut_for_test(&mut self) -> &mut Vec<ClipboardEntry> {
        &mut self.entries
    }
}

/// 当前时间往前 N 天的 ISO 字符串（驱逐天数比较用；无时钟依赖的朴素实现）。
fn now_iso_minus_days(days: u64) -> String {
    // 以日期前缀比较（忽略时分秒）；使用系统时间 - days 天
    let now = now_iso();
    let (date, _) = now.split_once('T').unwrap_or((&now, ""));
    let y: i32 = date[0..4].parse().unwrap_or(0);
    let m: u32 = date[5..7].parse().unwrap_or(1);
    let d: i32 = date[8..10].parse().unwrap_or(1);
    let days = days as i32;
    let mut julian = (1461 * (y + 4800 + (m as i32 - 14) / 12)) / 4
        + (367 * (m as i32 - 2 - 12 * ((m as i32 - 14) / 12))) / 12
        - (3 * ((y + 4900 + (m as i32 - 14) / 12) / 100)) / 4
        + d
        - 32075;
    julian -= days;
    let l = julian + 68569;
    let n = (4 * l) / 146097;
    let l = l - (146097 * n + 3) / 4;
    let i = (4000 * (l + 1)) / 1461001;
    let l = l - (1461 * i) / 4 + 31;
    let j = (80 * l) / 2447;
    let d = l - (2447 * j) / 80;
    let l = j / 11;
    let m = j + 2 - 12 * l;
    let y = 100 * (n - 49) + i + l;
    format!("{y:04}-{m:02}-{d:02}")
}

fn dir_size(path: &Path) -> u64 {
    let mut total = 0u64;
    if path.is_file() {
        return std::fs::metadata(path).map(|m| m.len()).unwrap_or(0);
    }
    if let Ok(rd) = std::fs::read_dir(path) {
        for f in rd.flatten() {
            total += dir_size(&f.path());
        }
    }
    total
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::model::Category;

    fn temp_root(tag: &str) -> PathBuf {
        let dir = std::env::temp_dir().join(format!("bd-engine-test-{tag}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        dir
    }

    fn sample_entry() -> ClipboardEntry {
        ClipboardEntry::new("test-id", ItemKind::Html, "hello")
    }

    fn text_entry(id: &str, text: &str) -> ClipboardEntry {
        let mut e = ClipboardEntry::new(id, ItemKind::Text, text);
        e.category = Category::Text;
        e
    }

    fn image_entry(id: &str) -> ClipboardEntry {
        let mut e = ClipboardEntry::new(id, ItemKind::Image, "");
        e.image_path = format!("clipboard\\images\\{id}.png");
        e
    }

    /// 图片去重迁移：旧指纹含 id（永不命中）→ 补像素指纹 + 合并历史重复 + 级联清理。
    #[test]
    fn dedupe_images_merges_same_pixels_and_backfills_hash() {
        let root = temp_root("dedupe-images");
        let images = root.join("clipboard").join("images");
        std::fs::create_dir_all(&images).unwrap();

        // 模拟截图工具写 CF_PNG/CF_DIB：两次落盘内容相同（像素相同）；c 为另一张图。
        // 注：此处两份文件字节相同 → 像素指纹必然相同（真实场景里字节可能不同，像素仍相同）。
        let same = b"identical-image-bytes";
        std::fs::write(images.join("a.png"), same).unwrap();
        std::fs::write(images.join("b.png"), same).unwrap();
        std::fs::write(images.join("c.png"), b"another-image").unwrap();

        let mut store = Store::new(&root);
        let mut a = image_entry("a");
        a.copy_count = 2;
        let b = image_entry("b");
        let c = image_entry("c");
        {
            let entries = store.entries_mut_for_test();
            entries.push(a);
            entries.push(b);
            entries.push(c);
        }

        let (merged, backfilled) = store.dedupe_images();

        assert_eq!(merged, 1, "同内容图片应合并为一条");
        assert_eq!(backfilled, 3, "三条图片都应被回填像素指纹");
        assert_eq!(store.entries().len(), 2, "a/c 保留，b 被合并");
        assert!(store.entries().iter().all(|e| !e.content_hash.is_empty()));
        assert!(!images.join("b.png").exists(), "被合并条目的原图级联清理");
        assert!(images.join("a.png").exists(), "保留条目原图不动");
        let kept_a = store.entries().iter().find(|e| e.id == "a").unwrap();
        assert_eq!(kept_a.copy_count, 3, "copy_count 合并（2 + 1）");

        // 幂等：再跑一次无变更、不重复解码
        let (merged2, backfilled2) = store.dedupe_images();
        assert_eq!((merged2, backfilled2), (0, 0));

        let _ = std::fs::remove_dir_all(&root);
    }

    /// 合并时收藏状态转移：被合并项是收藏而保留项不是 → 保留项变为收藏（不因去重丢用户标记）。
    #[test]
    fn dedupe_images_merges_pinned_flag() {
        let root = temp_root("dedupe-pinned");
        let images = root.join("clipboard").join("images");
        std::fs::create_dir_all(&images).unwrap();
        std::fs::write(images.join("p1.png"), b"pinned-image").unwrap();
        std::fs::write(images.join("p2.png"), b"pinned-image").unwrap();

        let mut store = Store::new(&root);
        let p1 = image_entry("p1");
        let mut p2 = image_entry("p2");
        p2.is_pinned = true;
        {
            let entries = store.entries_mut_for_test();
            entries.push(p1);
            entries.push(p2);
        }

        let (merged, _) = store.dedupe_images();

        assert_eq!(merged, 1);
        assert_eq!(store.entries().len(), 1);
        assert!(store.entries()[0].is_pinned, "收藏状态应转移到保留条目");

        let _ = std::fs::remove_dir_all(&root);
    }

    /// 【2026-09-13 回归 · 用户实测反馈】"先复制过这张图（历史里是图片条目）、之后又把它导入为表情包"
    /// → 迁移必须合并为**一条**，且保留**表情包**那条（用户主动标记的意图优先于"先来的图片"）。
    #[test]
    fn dedupe_merges_image_into_sticker_and_keeps_sticker() {
        let root = temp_root("dedupe-sticker");
        let images = root.join("clipboard").join("images");
        let stickers = root.join("clipboard").join("stickers");
        std::fs::create_dir_all(&images).unwrap();
        std::fs::create_dir_all(&stickers).unwrap();

        // 同一份字节（此处不可解码 → image_content_hash 走字节哈希回退，两份一致即同指纹）
        let same = b"same-sticker-bytes";
        std::fs::write(images.join("img.png"), same).unwrap();
        std::fs::write(stickers.join("stk.gif"), same).unwrap();

        let mut store = Store::new(&root);
        let img = image_entry("img"); // 复制得到的历史图片条目
        let mut stk = ClipboardEntry::new("stk", ItemKind::Files, "");
        stk.is_sticker = true; // 【2026-09-13】标记制
        stk.file_paths = vec![stickers.join("stk.gif").to_string_lossy().to_string()];
        stk.content_hash = "f".repeat(64); // 旧口径：文件字节 sha256（64 hex）→ 迁移应重算为像素口径
        {
            let entries = store.entries_mut_for_test();
            entries.push(img);
            entries.push(stk);
        }

        let (merged, backfilled) = store.dedupe_images();

        assert_eq!(merged, 1, "同像素的 图片+表情包 必须合并为一条");
        assert_eq!(
            backfilled, 2,
            "图片条目回填像素指纹 + 表情包旧口径（64 位文件 sha256）重算为像素口径"
        );
        assert_eq!(store.entries().len(), 1);
        let kept = &store.entries()[0];
        assert_eq!(kept.id, "stk", "必须保留表情包那条（用户主动标记优先）");
        assert!(kept.is_sticker, "保留项的**标记**必须还在（表情包语义已由分类改为标记）");
        assert_eq!(kept.content_hash.len(), 16, "统一为像素口径（16 hex）");
        assert!(!images.join("img.png").exists(), "被合并图片条目的原图应级联清理");
        assert!(stickers.join("stk.gif").exists(), "保留的表情包副本不动");

        let _ = std::fs::remove_dir_all(&root);
    }

    /// 原图缺失：无法判定指纹 → 原样保留（绝不误删用户数据）。
    #[test]
    fn dedupe_images_keeps_entries_with_missing_files() {
        let root = temp_root("dedupe-missing");
        let mut store = Store::new(&root);
        {
            let entries = store.entries_mut_for_test();
            entries.push(image_entry("ghost"));
        }

        let (merged, backfilled) = store.dedupe_images();

        assert_eq!(merged, 0);
        assert_eq!(backfilled, 0);
        assert_eq!(store.entries().len(), 1, "读不到原图时保留条目");

        let _ = std::fs::remove_dir_all(&root);
    }

    /// 分类重算迁移：历史上被判为 RichText 的代码条目应纠正为 Code（消除分类真空地带）。
    #[test]
    fn reclassify_fixes_legacy_richtext_code() {
        let root = temp_root("reclassify");
        let mut store = Store::new(&root);

        // 旧分类器（缩进信号失效 + 关键词表偏 .NET/C）会把这段 Rust 代码判成 RichText
        let code = "pub fn main() {\n    let x = 1;\n    return x;\n}\n";
        let mut legacy = ClipboardEntry::new("legacy-code", ItemKind::Html, code);
        legacy.html_content = "<pre>code</pre>".to_string();
        legacy.category = Category::RichText;
        {
            let entries = store.entries_mut_for_test();
            entries.push(legacy);
            entries.push(text_entry("prose", "今天天气不错\n我们出去散步\n顺便买点水果"));
        }

        let changed = store.reclassify_all();

        assert_eq!(changed, 1, "仅代码条目需要纠正");
        let fixed = store.entries().iter().find(|e| e.id == "legacy-code").unwrap();
        assert_eq!(fixed.category, Category::Code, "RichText → Code");
        assert!(fixed.is_code);
        let prose = store.entries().iter().find(|e| e.id == "prose").unwrap();
        assert_eq!(prose.category, Category::Text, "普通文本不受影响");

        // 幂等：再跑无变化
        assert_eq!(store.reclassify_all(), 0);

        let _ = std::fs::remove_dir_all(&root);
    }

    /// 同一段文字、不同剪贴板格式 → 必须只有一条（旧指纹 text:/html: 前缀不同 → 两条并存）。
    #[test]
    fn same_text_different_format_dedupes() {
        let plain = ClipboardEntry::new("plain", ItemKind::Text, "hello world");
        let mut rich = ClipboardEntry::new("rich", ItemKind::Html, "hello world");
        rich.html_content = "<p>hello world</p>".to_string();

        assert_eq!(
            plain.fingerprint(),
            rich.fingerprint(),
            "同一纯文本 → 不论剪贴板格式，指纹必须一致"
        );

        let root = temp_root("text-dedupe");
        let mut store = Store::new(&root);
        {
            let entries = store.entries_mut_for_test();
            entries.push(plain);
            entries.push(rich);
        }

        let merged = store.dedupe_text_duplicates();

        assert_eq!(merged, 1, "同文本的两条应合并");
        assert_eq!(store.entries().len(), 1);
        let kept = &store.entries()[0];
        assert_eq!(kept.id, "plain", "保留最早出现的一条（维持列表位置）");
        assert_eq!(kept.content_type, ItemKind::Html, "但格式升级为更丰富的一侧");
        assert!(!kept.html_content.is_empty(), "HTML 已补入，避免后续粘贴丢格式");
        assert_eq!(kept.copy_count, 2, "复制次数合并");
        assert_eq!(store.dedupe_text_duplicates(), 0, "幂等");

        let _ = std::fs::remove_dir_all(&root);
    }

    /// upsert 命中合并时也要做格式升级（先纯文本后富文本 → 补齐 HTML，不新增条目）。
    #[test]
    fn upsert_upgrades_format_on_dedupe_hit() {
        let root = temp_root("upsert-upgrade");
        let mut store = Store::new(&root);

        store.upsert(ClipboardEntry::new("p1", ItemKind::Text, "shared text"));

        let mut rich = ClipboardEntry::new("p2", ItemKind::Html, "shared text");
        rich.html_content = "<b>shared text</b>".to_string();
        let outcome = store.upsert(rich);

        assert!(
            matches!(outcome, UpsertOutcome::Updated(_, _)),
            "同一纯文本应命中去重而不是新增"
        );
        assert_eq!(store.entries().len(), 1, "不得新增第二条");
        assert_eq!(store.entries()[0].content_type, ItemKind::Html, "格式升级");
        assert!(
            !store.entries()[0].html_content.is_empty(),
            "HTML 被补入（否则粘贴丢格式）"
        );
        assert_eq!(store.entries()[0].copy_count, 2);

        let _ = std::fs::remove_dir_all(&root);
    }

    /// 【表情包】分类数值往返 + 指纹用内容哈希（副本路径每次导入都不同，路径指纹永不命中）。
    #[test]
    fn sticker_category_and_fingerprint() {
        assert_eq!(Category::from_i32(5), Category::Sticker);
        assert_eq!(Category::Sticker.as_i32(), 5);

        let mut s = text_entry("s1", "");
        s.content_type = ItemKind::Files;
        s.is_sticker = true;
        s.file_paths = vec!["C:\\x\\stickers\\a1.gif".to_string()];
        s.content_hash = "deadbeef".to_string();
        // 与判重键同源（单一真相源）：Store::has_image_by_hash 用的就是这个指纹
        assert_eq!(s.fingerprint(), ClipboardEntry::image_fingerprint("deadbeef"));

        // 同一张图换了副本路径（再次导入）→ 指纹仍相同（这就是去重的依据）
        let mut other = s.clone();
        other.file_paths = vec!["C:\\x\\stickers\\b2.gif".to_string()];
        assert_eq!(s.fingerprint(), other.fingerprint());

        // 无 content_hash 的旧数据退回路径指纹 → 与"有哈希的"**不同指纹**（不误合并，安全性优先）
        let mut legacy = s.clone();
        legacy.content_hash = String::new();
        assert_ne!(
            legacy.fingerprint(),
            s.fingerprint(),
            "缺内容哈希的旧数据不得与内容哈希指纹混为一谈"
        );
    }

    /// 【图片类内容】像素哈希去重查询（导入判重的 O(1) 依据）。
    /// 【2026-09-13】查询范围已从"表情包集合"扩大到"图片类内容"（图片条目 + 表情包共用同一键空间）。
    #[test]
    fn image_hash_lookup() {
        let root = temp_root("sticker-hash");
        let mut store = Store::new(&root);
        assert!(!store.has_image_by_hash("hash1"));

        let mut s = text_entry("s1", "");
        s.content_type = ItemKind::Files;
        s.is_sticker = true;
        s.content_hash = "hash1".to_string();
        store.upsert(s);

        assert!(store.has_image_by_hash("hash1"), "应能按内容哈希查到表情包");

        // **关键回归**：历史里的**图片条目**也必须能被同一个查询命中 ——
        // 旧实现只查表情包集合，于是"已复制过的图再导入表情包"永远命中不了、只能新增一条。
        let mut img = image_entry("img1");
        img.content_hash = "hash2".to_string();
        store.upsert(img);
        assert!(
            store.has_image_by_hash("hash2"),
            "图片条目必须命中同一键空间（否则导入表情包会另起一条）"
        );
        assert!(store.find_by_image_hash("hash2").is_some(), "应能取到该图片条目以做升级");

        assert!(!store.has_image_by_hash("hash3"));
        let _ = std::fs::remove_dir_all(&root);
    }

    /// 【表情包 · 生死线】驱逐**永不删除** Sticker（用户珍藏），即便天数过期 + 容量超限。
    #[test]
    fn evict_never_drops_stickers() {
        let root = temp_root("sticker-evict");
        let mut store = Store::new(&root);

        let mut noise = text_entry("noise", "history noise");
        noise.timestamp = "2020-01-01T00:00:00".to_string();

        let mut sticker = text_entry("sticker-1", "");
        sticker.content_type = ItemKind::Files;
        sticker.is_sticker = true; // 【2026-09-13】标记制
        sticker.timestamp = "2020-01-01T00:00:00".to_string(); // 同样"过期"
        sticker.content_hash = "hash1".to_string();
        sticker.file_paths = vec!["C:\\x\\stickers\\s1.gif".to_string()];

        store.upsert(noise);
        store.upsert(sticker);

        let mut settings = crate::settings::load(None);
        settings.capacity = 1; // 触发条数驱逐
        settings.retention_days = 1; // 触发天数驱逐（两条都"过期"）

        let evicted = store.evict(&settings);

        assert!(evicted.contains(&"noise".to_string()), "普通条目应按规则被驱逐");
        assert!(
            !evicted.contains(&"sticker-1".to_string()),
            "表情包绝不能被驱逐（用户珍藏）"
        );
        assert!(
            store.get_by_id("sticker-1").is_some(),
            "驱逐后表情包必须仍在库里"
        );
        let _ = std::fs::remove_dir_all(&root);
    }

    /// 【2026-09-13 用户口径】"跟收藏一样的机制，这样就不管是图片还是颜文字都可以了" ——
    /// **文字颜文字**打上标记后，必须与图片表情包同等受保护（豁免驱逐）。
    /// 这在旧的"分类制"下**根本不可能**：分类只能靠"导入本地文件"产生，而颜文字没有文件可导入。
    #[test]
    fn sticker_flag_protects_text_emoji() {
        let root = temp_root("sticker-text-emoji");
        let mut store = Store::new(&root);

        let mut noise = text_entry("noise", "history noise");
        noise.timestamp = "2020-01-01T00:00:00".to_string();

        let mut emoji = text_entry("kaomoji", "(๑•̀ㅂ•́)و✧");
        emoji.is_sticker = true; // 只靠**标记**成为表情包
        emoji.timestamp = "2020-01-01T00:00:00".to_string();

        store.upsert(noise);
        store.upsert(emoji);

        let mut settings = crate::settings::load(None);
        settings.capacity = 1; // 触发条数驱逐
        let evicted = store.evict(&settings);

        assert!(evicted.contains(&"noise".to_string()));
        assert!(
            !evicted.contains(&"kaomoji".to_string()),
            "文字表情包同样不能被驱逐（受保护的是标记，与内容类型无关）"
        );
        assert!(store.get_by_id("kaomoji").is_some());

        let _ = std::fs::remove_dir_all(&root);
    }

    /// 【2026-09-13】旧数据迁移：`category == Sticker` → `is_sticker = true` + 分类按**实际内容**重判。
    #[test]
    fn migrate_sticker_flag_converts_legacy_category() {
        let root = temp_root("sticker-flag-migrate");
        let mut store = Store::new(&root);

        let mut gif = ClipboardEntry::new("g1", ItemKind::Files, "");
        gif.category = Category::Sticker; // 旧模型：表情包是个"分类"
        gif.file_paths = vec!["C:\\x\\stickers\\a.gif".to_string()];

        let mut weird = ClipboardEntry::new("w1", ItemKind::Files, "");
        weird.category = Category::Sticker;
        weird.file_paths = vec!["C:\\x\\stickers\\a.bin".to_string()];

        let normal = text_entry("t1", "just text");

        {
            let entries = store.entries_mut_for_test();
            entries.push(gif);
            entries.push(weird);
            entries.push(normal);
        }

        let converted = store.migrate_sticker_flag();
        assert_eq!(converted, 2, "两条旧分类条目应被转换为标记");

        let g = store.get_by_id("g1").unwrap();
        assert!(g.is_sticker);
        assert_eq!(g.category, Category::Image, "图片扩展名 → Image 分类");
        let w = store.get_by_id("w1").unwrap();
        assert!(w.is_sticker);
        assert_eq!(w.category, Category::File, "非图片扩展名 → File 分类");
        let t = store.get_by_id("t1").unwrap();
        assert!(!t.is_sticker, "普通条目不受影响");

        assert_eq!(store.migrate_sticker_flag(), 0, "迁移必须幂等");

        let _ = std::fs::remove_dir_all(&root);
    }

    /// 【表情包 · 安全红线】删除条目只清**我们复制进 stickers 的副本**，绝不碰用户原始文件
    ///（用户是"导入"不是"移动"；误删原图是不可挽回的数据事故）。
    #[test]
    fn remove_sticker_deletes_copies_only() {
        let root = temp_root("sticker-delete");
        let stickers = root.join("clipboard").join("stickers");
        std::fs::create_dir_all(&stickers).unwrap();
        let user_dir = root.join("user");
        std::fs::create_dir_all(&user_dir).unwrap();
        let user_file = user_dir.join("favorite.gif");
        std::fs::write(&user_file, b"gif-bytes").unwrap();
        let copy = stickers.join("abc.gif");
        std::fs::write(&copy, b"gif-bytes").unwrap();

        let mut store = Store::new(&root);
        let mut sticker = text_entry("sticker-x", "");
        sticker.content_type = ItemKind::Files;
        sticker.is_sticker = true; // 【2026-09-13】表情包已是**标记**，不再是分类
        sticker.file_paths = vec![
            copy.to_string_lossy().to_string(),
            user_file.to_string_lossy().to_string(), // 混入用户路径也不能删
        ];
        store.upsert(sticker);

        assert!(store.remove_by_id("sticker-x"));
        assert!(
            !copy.exists(),
            "stickers 目录下的副本必须被级联清理（否则孤儿文件堆积）"
        );
        assert!(user_file.exists(), "用户原始文件绝不能被删除（安全红线）");
        let _ = std::fs::remove_dir_all(&root);
    }

    /// 驱逐必须**级联清理外部文件**（此前按 id 回查已删条目 → 死代码 → 孤儿文件堆积 + 预算驱逐失效）。
    #[test]
    fn evict_removes_orphan_files() {
        let root = temp_root("evict-files");
        let images = root.join("clipboard").join("images");
        std::fs::create_dir_all(&images).unwrap();

        let mut store = Store::new(&root);
        let mut old = image_entry("old");
        old.timestamp = "2026-01-01T00:00:00".to_string();
        let mut new = image_entry("new");
        new.timestamp = "2026-09-12T00:00:00".to_string();
        std::fs::write(images.join("old.png"), b"old-img").unwrap();
        std::fs::write(images.join("new.png"), b"new-img").unwrap();
        {
            let entries = store.entries_mut_for_test();
            entries.push(old);
            entries.push(new);
        }

        let mut settings = crate::settings::load(None);
        settings.capacity = 1; // 触发条数驱逐（保留最新一条）
        settings.retention_days = 36500; // 不触发天数驱逐

        let evicted = store.evict(&settings);

        assert_eq!(evicted, vec!["old".to_string()], "应驱逐最旧的一条");
        assert_eq!(store.entries().len(), 1);
        assert!(
            !images.join("old.png").exists(),
            "被驱逐条目的原图必须级联清理（否则孤儿文件堆积、预算驱逐越驱越占）"
        );
        assert!(images.join("new.png").exists(), "保留条目的文件不能被误删");

        let _ = std::fs::remove_dir_all(&root);
    }

    /// 【2026-09-12 审计】载入失败必须**禁用写盘** —— 否则空内存库会覆盖用户真实历史（"损坏即清库"）。
    #[test]
    fn save_refuses_after_failed_load() {
        let root = temp_root("load-fail-guard");
        let mut store = Store::new(&root);
        // 把存储文件位置做成**目录** → fs::read 稳定失败，且不是 NotFound（= "读不到" 而非 "没有"）
        let storage = store.storage_file_path();
        std::fs::create_dir_all(&storage).unwrap();

        store.load();

        assert!(!store.storage_trusted(), "载入失败必须标记为不可信");
        let err = store.save().expect_err("载入失败后不得写盘");
        assert!(
            err.to_string().contains("refusing"),
            "错误信息应说明拒写原因，实际: {err}"
        );
        assert!(storage.is_dir(), "磁盘上的原路径不得被覆盖");

        let _ = std::fs::remove_dir_all(&root);
    }

    /// 守卫不得误伤正常路径：文件不存在（新起空库）与正常载入都必须保持可写。
    #[test]
    fn save_allowed_after_clean_load() {
        let root = temp_root("load-ok-guard");
        let mut store = Store::new(&root);
        store.load(); // 文件不存在 → 新起空库
        assert!(store.storage_trusted(), "空库新起必须可写");

        store.upsert(text_entry("a", "hello"));
        store.save().expect("正常载入后必须能写盘");

        let _ = std::fs::remove_dir_all(&root);
    }

    /// save 必须原子写（临时文件 + rename）：写完可正常读回，且不残留 .tmp。
    #[test]
    fn save_is_atomic_and_reloadable() {
        let root = temp_root("atomic-save");
        let mut store = Store::new(&root);
        store.upsert(text_entry("a", "hello"));
        store.save().expect("save");

        let final_path = store.storage_file_path();
        assert!(final_path.exists(), "正式文件应存在");
        assert!(
            !final_path.with_extension("json.tmp").exists(),
            "临时文件必须已被 rename 消费，不得残留"
        );

        let mut reloaded = Store::new(&root);
        reloaded.load();
        assert_eq!(reloaded.entries().len(), 1, "写出的文件必须能正常读回");

        let _ = std::fs::remove_dir_all(&root);
    }

    /// 零宽字符必须被视为"空"：`trim()` 认不出 `\u{200B}`，会让空快照绕过红线入库存活。
    #[test]
    fn blank_text_treats_zero_width_as_empty() {
        assert!(Store::is_blank_text(""));
        assert!(Store::is_blank_text("   \n\t "));
        assert!(Store::is_blank_text("\u{200B}"));
        assert!(Store::is_blank_text("\u{200B}\u{FEFF}\u{200D}"));
        assert!(Store::is_blank_text(" \u{200B} \n"));
        assert!(!Store::is_blank_text("a"));
        assert!(!Store::is_blank_text("\u{200B}a\u{200B}"));
    }

    /// 零宽哨兵条目必须被 purge_empty 清掉（2026-09-12 真机：按序粘贴哨兵入库 7 条）。
    #[test]
    fn purge_empty_removes_zero_width_only_entries() {
        let root = temp_root("purge-zw");
        let mut store = Store::new(&root);
        {
            let entries = store.entries_mut_for_test();
            entries.push(ClipboardEntry::new("zw", ItemKind::Text, "\u{200B}"));
            entries.push(ClipboardEntry::new("zw2", ItemKind::Text, "\u{FEFF}\u{200B}"));
            entries.push(text_entry("keep", "真内容"));
        }

        assert_eq!(store.purge_empty(), 2, "零宽条目必须算空");
        assert_eq!(store.entries().len(), 1);

        let _ = std::fs::remove_dir_all(&root);
    }

    /// 真空条目清理：无文本/HTML/RTF/文件的条目清除；图片、文件、大文本（content.bin）条目保留。
    #[test]
    fn purge_empty_removes_vacuous_entries() {
        let root = temp_root("purge-empty");
        let mut store = Store::new(&root);
        {
            let entries = store.entries_mut_for_test();
            entries.push(ClipboardEntry::new("blank", ItemKind::Text, "   \n  ")); // 真空
            entries.push(text_entry("real", "有内容"));

            let mut img = ClipboardEntry::new("img", ItemKind::Image, "");
            img.image_path = "clipboard\\images\\img.png".to_string();
            entries.push(img); // 图片：无文本但必须保留

            let mut big = ClipboardEntry::new("big", ItemKind::Text, "");
            big.content_in_bin = true; // 正文在 content.bin 里，content 字段本就为空
            entries.push(big);

            let mut file_entry = ClipboardEntry::new("f", ItemKind::Files, "");
            file_entry.file_paths = vec!["C:/a.txt".to_string()];
            entries.push(file_entry);
        }

        assert_eq!(store.purge_empty(), 1, "只清除真空条目");
        assert_eq!(store.entries().len(), 4);
        assert!(store.entries().iter().all(|e| e.id != "blank"));
        assert_eq!(store.purge_empty(), 0, "幂等");

        let _ = std::fs::remove_dir_all(&root);
    }

    #[test]
    fn save_load_roundtrip_cbenc1() {
        let root = temp_root("roundtrip");
        let mut store = Store::new(&root);
        store.entries_mut_for_test().push(sample_entry());
        store.save().expect("save");

        let mut store2 = Store::new(&root);
        store2.load();
        assert_eq!(store2.entries().len(), 1);
        let e = &store2.entries()[0];
        assert_eq!(e.id, "test-id");
        assert_eq!(e.content_type, ItemKind::Html);

        // 落盘文件必须以 CBENC1\0 开头且含密文（非明文）
        let raw = std::fs::read(root.join(STORAGE_FILE)).unwrap();
        assert!(raw.starts_with(ENC_HEADER));
        assert!(!String::from_utf8_lossy(&raw).contains("test-id"));

        let _ = std::fs::remove_dir_all(&root);
    }

    #[test]
    fn load_plaintext_legacy_format() {
        let root = temp_root("plain");
        std::fs::create_dir_all(&root).unwrap();
        let legacy = r#"[{"id":"legacy-1","contentType":0,"category":0,"content":"plain text","htmlContent":"","rtfContent":"","timestamp":"2026-09-01T10:00:00","isPinned":false,"imagePath":"","imageWidth":0,"imageHeight":0,"sizeBytes":10,"copyCount":3,"sourceProcessName":"notepad","sourceWindowTitle":"","filePaths":[],"tags":"","hasImages":false,"hasTable":false,"isCode":false}]"#;
        std::fs::write(root.join(STORAGE_FILE), legacy).unwrap();

        let mut store = Store::new(&root);
        store.load();
        assert_eq!(store.entries().len(), 1);
        let e = &store.entries()[0];
        assert_eq!(e.id, "legacy-1");
        assert_eq!(e.content_type, ItemKind::Text);
        assert_eq!(e.content, "plain text");
        assert_eq!(e.copy_count, 3);
        assert_eq!(e.timestamp, "2026-09-01T10:00:00");
        assert!(!e.content_in_bin); // 旧文件无此字段 → default false

        let _ = std::fs::remove_dir_all(&root);
    }

    #[test]
    fn load_cbenc1_manual() {
        let root = temp_root("manual");
        std::fs::create_dir_all(&root).unwrap();
        let json = r#"[{"id":"enc-1","contentType":1,"category":3,"content":"","htmlContent":"","rtfContent":"","timestamp":"","isPinned":true,"imagePath":"clipboard/images/enc-1.png","imageWidth":100,"imageHeight":50,"sizeBytes":0,"copyCount":1,"sourceProcessName":"","sourceWindowTitle":"","filePaths":[],"tags":"","hasImages":false,"hasTable":false,"isCode":false}]"#;
        let cipher = dpapi::protect(json.as_bytes()).unwrap();
        let mut file = Vec::new();
        file.extend_from_slice(ENC_HEADER);
        file.extend_from_slice(&cipher);
        std::fs::write(root.join(STORAGE_FILE), file).unwrap();

        let mut store = Store::new(&root);
        store.load();
        assert_eq!(store.entries().len(), 1);
        let e = &store.entries()[0];
        assert_eq!(e.content_type, ItemKind::Image);
        assert_eq!(e.category, Category::Image);
        assert!(e.is_pinned);

        let _ = std::fs::remove_dir_all(&root);
    }

    #[test]
    fn corrupt_file_self_heals_to_empty() {
        let root = temp_root("corrupt");
        std::fs::create_dir_all(&root).unwrap();
        std::fs::write(root.join(STORAGE_FILE), b"CBENC1\0garbage-not-dpapi").unwrap();
        let mut store = Store::new(&root);
        store.load();
        assert!(store.entries().is_empty());

        std::fs::write(root.join(STORAGE_FILE), b"{not json").unwrap();
        let mut store2 = Store::new(&root);
        store2.load();
        assert!(store2.entries().is_empty());

        let _ = std::fs::remove_dir_all(&root);
    }

    #[test]
    fn directory_layout() {
        let root = temp_root("layout");
        let store = Store::new(&root);
        assert_eq!(store.images_dir(), root.join("clipboard").join("images"));
        assert_eq!(store.thumbs_dir(), root.join("clipboard").join("thumbs"));
        assert_eq!(store.content_dir(), root.join("clipboard").join("content"));
        assert_eq!(store.files_dir(), root.join("clipboard").join("files"));
        assert_eq!(
            store.html_images_dir(),
            root.join("clipboard").join("html-images")
        );
        let _ = std::fs::remove_dir_all(&root);
    }

    #[test]
    fn upsert_dedupes_by_fingerprint() {
        let mut store = Store::new(temp_root("upsert"));
        let mut e1 = text_entry("a1", "hello world");
        e1.timestamp = "2026-09-10T10:00:00".into();
        let r1 = store.upsert(e1);
        assert_eq!(r1, UpsertOutcome::New("a1".to_string()));

        // 同内容不同 id → 指纹命中 → Updated（copy_count=2），不新增
        let mut e2 = text_entry("a2", "hello world");
        e2.timestamp = "2026-09-10T10:00:00".into();
        let r2 = store.upsert(e2);
        assert!(matches!(r2, UpsertOutcome::Updated(id, 2) if id == "a1"));
        assert_eq!(store.len(), 1);
        assert_eq!(store.entries()[0].copy_count, 2);
        // 置顶：条目在末尾（最新）
        assert_eq!(store.entries()[0].id, "a1");

        // 不同内容 → 新增
        let r3 = store.upsert(text_entry("a3", "different"));
        assert!(matches!(r3, UpsertOutcome::New(_)));
        assert_eq!(store.len(), 2);

        let _ = std::fs::remove_dir_all(temp_root("upsert"));
    }

    #[test]
    fn external_content_roundtrip() {
        let root = temp_root("ext");
        let store = Store::new(&root);
        let big = "x".repeat(200 * 1024);
        let (in_bin, size) = store.store_content("c1", &big);
        assert!(in_bin);
        assert_eq!(size as usize, big.len());
        let loaded = store.load_external_content("c1").expect("load");
        assert_eq!(loaded, big);
        let _ = std::fs::remove_dir_all(&root);
    }

    /// 总存储预算已由"硬驱逐"改为**软限制**：超预算**不得删除任何条目**
    ///（2026-09-12 用户口径："历史数据达到存储上面也可以存储，但我们要发消息提醒用户及时清理"）。
    /// 若此测试失败，说明有人把预算驱逐逻辑加回来了 —— 那会静默丢弃用户历史。
    #[test]
    fn evict_never_drops_entries_for_total_budget() {
        let root = temp_root("evict-budget-soft");
        let images = root.join("clipboard").join("images");
        std::fs::create_dir_all(&images).unwrap();

        let mut store = Store::new(&root);
        for (id, ts) in [("old", "2026-01-01T00:00:00"), ("new", "2026-09-12T00:00:00")] {
            let mut e = image_entry(id);
            e.timestamp = ts.to_string();
            std::fs::write(images.join(format!("{id}.png")), vec![0u8; 4096]).unwrap();
            store.upsert(e);
        }

        let mut settings = crate::settings::load(None);
        settings.capacity = 10_000; // 不触发条数驱逐
        settings.retention_days = 36_500; // 不触发天数驱逐
        settings.max_total_mb = 0; // 预算近乎为零：旧实现会把条目删光

        let evicted = store.evict(&settings);

        assert!(evicted.is_empty(), "总预算超限不得删除条目（已改软限制）");
        assert_eq!(store.entries().len(), 2, "条目必须原样保留");
        assert!(store.total_used_bytes() > 0, "占用仍应被统计（供 UI 提醒）");
        let _ = std::fs::remove_dir_all(&root);
    }

    #[test]
    fn evict_respects_pinned_and_capacity() {
        let root = temp_root("evict");
        let mut store = Store::new(&root);
        // 3 条：一条收藏、两条未收藏
        let mut pinned = text_entry("p1", "pinned");
        pinned.is_pinned = true;
        pinned.timestamp = "2026-09-01T00:00:00".into();
        store.upsert(pinned);
        let mut old = text_entry("o1", "old");
        old.timestamp = "2020-01-01T00:00:00".into();
        store.upsert(old);
        let mut fresh = text_entry("f1", "fresh");
        fresh.timestamp = now_iso();
        store.upsert(fresh);

        let mut settings = Settings::default();
        settings.capacity = 2; // 触发条数驱逐
        let evicted = store.evict(&settings);
        // 最旧未收藏（o1, 2020）被驱逐；pinned 和 fresh 保留
        assert_eq!(evicted, vec!["o1"]);
        assert_eq!(store.len(), 2);
        assert!(store.get_by_id("p1").is_some());
        assert!(store.get_by_id("f1").is_some());
        let _ = std::fs::remove_dir_all(&root);
    }

    #[test]
    fn remove_by_id_cascades_files() {
        let root = temp_root("cascade");
        let mut store = Store::new(&root);
        let mut img = ClipboardEntry::new("i1", ItemKind::Image, "");
        img.image_path = format!("clipboard\\images\\i1.png");
        store.upsert(img);
        // 伪造外部文件
        std::fs::create_dir_all(store.images_dir()).unwrap();
        std::fs::write(store.images_dir().join("i1.png"), b"png").unwrap();
        std::fs::create_dir_all(store.thumbs_dir()).unwrap();
        std::fs::write(store.thumbs_dir().join("i1.jpg"), b"jpg").unwrap();

        assert!(store.remove_by_id("i1"));
        assert_eq!(store.len(), 0);
        assert!(!store.images_dir().join("i1.png").exists());
        assert!(!store.thumbs_dir().join("i1.jpg").exists());
        let _ = std::fs::remove_dir_all(&root);
    }

    #[test]
    fn now_iso_format() {
        let s = now_iso();
        assert!(s.len() >= 19, "{s}");
        assert_eq!(&s[4..5], "-");
        assert_eq!(&s[10..11], "T");
    }
}
