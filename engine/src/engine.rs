//! 引擎核心：全局状态（Store/Settings/订阅者）+ 捕获入库管线 + IPC 方法分派 + 事件广播。
//! 捕获管线：来源信息 → 隐私过滤 → 快照 → 条目构造（analyzer/图片落盘/缩略图/content.bin/HTML 提取）→
//!           指纹去重 upsert → 驱逐 → 事件推送（history_changed + clipboard_changed 轻量摘要）。

use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{mpsc::Sender, Arc, Mutex, OnceLock};

use serde_json::{json, Value};

use crate::capture::{self, Snapshot};
use crate::model::{Category, ClipboardEntry, ItemKind};
use crate::rules::CompiledRules;
use crate::settings::Settings;
use crate::store::{Store, UpsertOutcome};

/// 表情包允许的扩展名白名单（**只做容器识别，不做转码**；GIF/WebP 动图靠原文件保动画）。
pub const STICKER_EXTS: [&str; 7] = ["gif", "webp", "apng", "png", "jpg", "jpeg", "bmp"];

/// **会丢动画**的容器：历史里的图片条目是 PNG 重编码（只有首帧），若导入的是这些格式，
/// 必须把它改指**原文件副本**才放得出动画（静态 PNG/JPG/BMP 无此问题）。
pub const ANIMATED_EXTS: [&str; 3] = ["gif", "webp", "apng"];

pub static STORE: OnceLock<Mutex<Store>> = OnceLock::new();
pub static SETTINGS: OnceLock<Mutex<Settings>> = OnceLock::new();
pub static PAUSED: AtomicBool = AtomicBool::new(false);

/// 定时暂停的到期时刻（`None` = 无限期；`resume`、手动切换或到期后清空）。
///
/// 【2026-09-12 审计修复】旧实现只有 `PAUSED` 布尔：面板 `PauseTemporarily(60s)` 靠**客户端本地
/// Timer** 到点补发 `resume` —— 面板一旦崩溃/被杀，引擎就**永久暂停**，剪贴板从此静默不再记录
/// （用户只会觉得"它坏了"，且无任何提示）。现在秒数随 `pause` 一起传给引擎，引擎侧到期自动恢复。
pub static PAUSE_UNTIL: Mutex<Option<std::time::Instant>> = Mutex::new(None);

/// 全局监听器（窗口过程无法捕获闭包，经 OnceLock 共享）。
pub static LISTENER: OnceLock<std::sync::Arc<crate::listener::ClipboardListener>> = OnceLock::new();

pub static NEXT_SUB_ID: AtomicU64 = AtomicU64::new(1);/// 事件订阅者（每连接一个；subscribed 标记后引擎才推送，无订阅者零开销）。
pub struct Subscriber {
    pub id: u64,
    pub tx: Sender<Vec<u8>>,
    pub subscribed: Arc<AtomicBool>,
}

pub static SUBSCRIBERS: Mutex<Vec<Subscriber>> = Mutex::new(Vec::new());

/// 【P1-5 按应用清洗规则】编译后的规则缓存。启动编译一次；`apply_settings` 检测规则指纹变化后重编译。
pub static COMPILED_RULES: OnceLock<Mutex<CompiledRules>> = OnceLock::new();

/// 【P2-3 临时粘贴】最近一次"临时粘贴"前的剪贴板快照，供粘贴后还原。
pub static TEMP_CLIP: Mutex<Option<TempClip>> = Mutex::new(None);

/// 临时粘贴前的剪贴板内容（内存驻留，不落盘）。
///
/// 存**快照**而非条目 —— 快照自带全部原始数据（文本/HTML/RTF、内存 PNG、文件路径、命名格式），
/// 还原时无需落盘、无副作用（条目路径会依赖 store 落盘资源，不适合做"一次性"还原）。
pub struct TempClip {
    pub snapshot: Snapshot,
    pub created_at: std::time::Instant,
}

/// 临时粘贴快照有效期：超过则不还原（用户期间很可能已复制了新内容，还原反而会**覆盖用户的新复制**）。
pub const TEMP_CLIP_TTL: std::time::Duration = std::time::Duration::from_secs(10);

/// 【P1-5】取当前编译后的规则（首次访问未初始化时返回空集 = 零行为变化）。
fn compiled_rules() -> std::sync::MutexGuard<'static, CompiledRules> {
    COMPILED_RULES
        .get_or_init(|| Mutex::new(CompiledRules::default()))
        .lock()
        .unwrap_or_else(|e| e.into_inner())
}

/// 【P1-4】由设置折算命名格式采集上限；关闭时返回 `None`。
fn named_format_limits(settings: &Settings) -> Option<crate::formats::Limits> {
    if !settings.named_format_passthrough {
        return None;
    }
    Some(crate::formats::Limits {
        count: settings.named_format_max_count,
        each_bytes: settings.named_format_max_kb.saturating_mul(1024),
        total_bytes: settings.named_format_total_kb.saturating_mul(1024),
    })
}

/// 初始化全局状态。
pub fn init(root: std::path::PathBuf, settings_path: Option<&std::path::Path>) {
    let mut store = Store::new(root);
    store.load();
    // 【表情包迁移 · 2026-09-13】"表情包**分类**"→"表情包**标记**"（跟收藏同级的独立布尔）。
    // **必须排在 dedupe 之前**：dedupe 依据标记决定同像素重复对里保留哪一条（带标记的优先）。
    let sticker_flagged = store.migrate_sticker_flag();
    // 【图片去重迁移】旧指纹 = 含 id 的 image_path（永不命中）→ 补像素级指纹 + 合并历史遗留的重复图片。
    // 幂等：content_hash 已存在则不再解码；无变更则不落盘。
    let (merged, backfilled) = store.dedupe_images();
    // 【文本去重迁移】旧指纹带类型前缀（text:/html:/rtf:）→ 同一段文字从纯文本源与富文本源
    // 各复制一次就留两条，还被判成「文字」「富文本」两个分类（用户实测）。合并为一条并保留最全格式。
    let text_merged = store.dedupe_text_duplicates();
    // 【空条目清理迁移】修复前的漏网空快照（清空剪贴板/不支持的类型）会留下真空条目 → 清除
    let purged = store.purge_empty();
    // 【分类重算迁移】旧分类器缩进信号失效 + 关键词表偏 .NET/C → IDE 复制的 Rust/前端代码
    // 被判为 RichText，「代码」分类里看不到（用户实测的"真空地带"）。重算历史条目使其归位。
    let reclassified = store.reclassify_all();
    // 【CF_HTML 头迁移】捕获侧修复前入库的富文本条目带着 "Version:0.9 / StartHTML:…" 偏移头，
    // 粘贴时会被再包一层 → 应用显示整串元数据（用户实测）。存量条目一次性剥离。
    let stripped = store.strip_cf_html_headers();
    // 【HTML 纯文本回填】纯文本为空但含 HTML 的条目 → 回填近似纯文本（可搜索 + 粘贴兜底）
    let backfilled_text = store.backfill_html_text();
    if merged > 0
        || backfilled > 0
        || sticker_flagged > 0
        || text_merged > 0
        || purged > 0
        || reclassified > 0
        || stripped > 0
        || backfilled_text > 0
    {
        crate::log::info(format!(
            "startup migration: sticker-flagged={sticker_flagged}, image-dedupe(merged={merged}, backfilled={backfilled}), text-merged={text_merged}, purged-empty={purged}, reclassified={reclassified}, cf-html-stripped={stripped}, text-backfilled={backfilled_text}"
        ));
        if let Err(e) = store.save() {
            crate::log::error(format!("startup migration save failed: {e}"));
        }
    }
    let _ = STORE.set(Mutex::new(store));
    let settings = crate::settings::load(settings_path);
    crate::log::info(format!(
        "settings loaded: capacity={}, storage-mode={:?}, enabled={} (path={})",
        settings.capacity,
        settings.storage_mode,
        settings.enabled,
        settings_path.map(|p| p.display().to_string()).unwrap_or_else(|| "(none)".to_string())
    ));
    let _ = SETTINGS.set(Mutex::new(settings));
    // 【P1-5】启动即编译一次按应用清洗规则（非法正则只跳过该条并记日志，不阻断启动）。
    let app_rules = SETTINGS
        .get()
        .map(|s| s.lock().unwrap_or_else(|e| e.into_inner()).app_rules.clone())
        .unwrap_or_default();
    let _ = COMPILED_RULES.set(Mutex::new(crate::rules::compile(&app_rules)));
    // 【2026-09-12】启动即按当前设置收敛一次：清掉"上次退出至今"已经过期的条目。
    // 此后由定时线程（start_eviction_sweeper）持续兜底 —— 不再依赖"用户是否复制了新东西"。
    sweep_evict("startup");
    crate::log::info("engine core initialized");
}

// ---------------- 捕获管线 ----------------

/// 剪贴板更新回调（广播 + 轮询双通道汇聚于此；S4 起入库）。
pub fn on_clipboard_update() {
    if PAUSED.load(Ordering::Acquire) {
        return;
    }
    let (process, title) = capture::read_source_info();
    if crate::privacy::is_privacy_sensitive(&process, &title) {
        crate::log::info(format!("privacy-sensitive source skipped: {process} / {title}"));
        return;
    }

    // 【2026-09-13 · P1-4/P1-5/P2】取设置**快照**（clone）：命名格式上限、敏感识别开关、清洗规则
    // 都需要在读取剪贴板之前确定；后面不再二次锁 SETTINGS（避免与 STORE 形成交叉锁序）。
    let settings = SETTINGS
        .get()
        .map(|s| s.lock().unwrap_or_else(|e| e.into_inner()).clone())
        .unwrap_or_default();

    // 【P1-5】`ignore` 判定放在**读取剪贴板之前** —— 整个应用被忽略时省一次剪贴板读取，
    // 也避免"注定要丢的内容"进入快照/上限校验等后续路径。
    if {
        let rules = compiled_rules();
        crate::rules::decide_ignore(&process, &title, &rules)
    } {
        crate::log::info(format!("app-rule: 来源被忽略（{process} / {title}）→ 跳过捕获"));
        return;
    }

    // 【P2-1】Office/WPS 会**分步写剪贴板格式**（先纯文本、稍后补 HTML/RTF）→ 白名单应用走渐进探测；
    // 其余应用保持一次性读取（零额外延迟）。
    let limits = named_format_limits(&settings);
    let Some(mut snapshot) = (if capture::is_rich_text_app(&process) {
        capture::read_snapshot_progressive(&process, limits)
    } else {
        capture::read_snapshot(limits)
    }) else {
        return;
    };

    // 【P1-5】`drop` / `replace` 要读到内容后才能判定/应用（ignore 已在上方提前处理）。
    if !{
        let rules = compiled_rules();
        crate::rules::apply(&process, &title, &mut snapshot, &rules)
    } {
        return;
    }

    // 【2026-09-12 用户要求】空数据不入库 —— 按**有效内容长度**判定（见 effective_content_len：
    // 不能用字节数，纯空白文本与空壳 HTML 都有字节；而 paths-only 的文件条目字节数为 0）。
    if effective_content_len(&snapshot) == 0 {
        crate::log::info("empty snapshot skipped (nothing worth recording)");
        return;
    }

    // 【2026-09-12 落实设置项】单条上限（文本字节 / 图片字节 / 图片像素）。
    if let Some(reason) = capture_limit_violation(&snapshot, &settings) {
        crate::log::info(format!("snapshot rejected by settings limits: {reason}"));
        return;
    }

    let Some(store_guard) = STORE.get() else {
        return;
    };
    // 【2026-09-12 修复】改用 unwrap_or_else(into_inner)，与文件内其它所有加锁点一致。
    // 此处原来用裸 unwrap：一旦有 panic 毒化 STORE 锁，之后**每一次剪贴板更新**
    //（本函数跑在消息循环线程上）都会 panic → 捕获能力彻底失效、且主线程直接崩。
    let mut store = store_guard.lock().unwrap_or_else(|e| e.into_inner());

    let entry = build_entry(&snapshot, &process, &title, &store, &settings);

    // 【回环抑制 · 2026-09-13】"这次的内容就是我自己刚写回的那份" → 跳过入库。
    // 判据是**内容指纹**（与写回侧 `mark_echo` 用同一函数 `ClipboardEntry::fingerprint`，单一真相源），
    // 与"触发了几次捕获"无关 —— 旧计数令牌在广播失效（漏吞）或双通道重复（误吞）时都会数错。
    // 必须放在**构造 entry 之后**：指纹要等快照读出来才算得出（见 listener.rs 的 should_capture 注释）。
    if let Some(l) = LISTENER.get() {
        if l.take_echo(&entry.fingerprint()) {
            crate::log::info("echo suppressed: own write-back (fingerprint match)");
            return;
        }
    }

    match store.upsert(entry) {
        UpsertOutcome::New(id) => {
            crate::log::info(format!("new entry {id}"));
            let evicted = store.evict(&settings);
            if !evicted.is_empty() {
                crate::log::info(format!("evicted {} entries", evicted.len()));
            }
            let push_events = settings.push_events; // 剪贴板变化摘要（灵动岛等只读订阅方）的总开关
            let summary = clipboard_summary(&store, &id);
            drop(store);
            broadcast("history_changed", json!({ "kind": "added", "id": id }));
            if push_events {
                broadcast("clipboard_changed", summary);
            }
        }
        UpsertOutcome::Updated(id, count) => {
            crate::log::info(format!("updated entry {id} (copy_count={count})"));
            let push_events = settings.push_events;
            let summary = clipboard_summary(&store, &id);
            drop(store);
            broadcast("history_changed", json!({ "kind": "updated", "id": id }));
            if push_events {
                broadcast("clipboard_changed", summary);
            }
        }
    }
}

/// 单条内容是否超出设置上限（返回 `Some(原因)` = 应拒绝捕获）。
///
/// 覆盖 `max-text-bytes`（文本/HTML/RTF 合计原始字节）、`max-image-mb`（图片字节）、
/// `max-image-pixels`（宽×高）；任一项为 0 视为该维度不限制。
fn capture_limit_violation(
    snapshot: &crate::capture::Snapshot,
    settings: &Settings,
) -> Option<String> {
    let text_bytes = snapshot.text.len() + snapshot.html.len() + snapshot.rtf.len();
    if settings.max_text_bytes > 0 && text_bytes > settings.max_text_bytes {
        return Some(format!(
            "text {text_bytes}B > max-text-bytes {}B",
            settings.max_text_bytes
        ));
    }

    if snapshot.kind == ItemKind::Image {
        let limit = (settings.max_image_mb as u64) * 1024 * 1024;
        if let Some(png) = &snapshot.image_png {
            if limit > 0 && png.len() as u64 > limit {
                return Some(format!(
                    "image {}B > max-image-mb {}MB",
                    png.len(),
                    settings.max_image_mb
                ));
            }
        }
        let pixels = (snapshot.image_width as u64) * (snapshot.image_height as u64);
        if settings.max_image_pixels > 0 && pixels > settings.max_image_pixels {
            return Some(format!(
                "image {pixels}px > max-image-pixels {}",
                settings.max_image_pixels
            ));
        }
    }
    None
}

/// 构造条目并落盘外部资源（图片原图/缩略图、content.bin、HTML 提取图）。
fn build_entry(
    snapshot: &Snapshot,
    process: &str,
    title: &str,
    store: &Store,
    settings: &Settings,
) -> ClipboardEntry {
    let id = uuid_v4();
    let mut entry = ClipboardEntry::new(&id, snapshot.kind, "");

    let profile = crate::analyzer::analyze(snapshot.kind, &snapshot.html, &snapshot.text);
    entry.category = profile.category;
    entry.has_images = profile.has_images;
    entry.has_table = profile.has_table;
    entry.is_code = profile.is_code;
    entry.source_process_name = process.to_string();
    entry.source_window_title = title.to_string();

    // ---- ① 只填「内容字段」：纯计算、零磁盘写 ----
    //
    // 【2026-09-14 修复 · 孤儿文件泄漏】此前是「先落盘、再判重」：无论是否新条目，都用**新 id**
    // 写下原图/缩略图/文件副本/HTML 提取图/content.bin，然后交给 `upsert` 判重；命中 `Updated`
    // 时新条目被丢弃 —— 而它刚写下的文件**没有任何引用，也没有任何清理路径**
    //（`evict` 只清理被保留条目自己的路径），于是每次重复复制都在磁盘留一份垃圾，
    // 且被 `total_used_bytes()` 计入 → 存储横幅虚高、用户清理也删不掉。
    // 现在把「算指纹」提到落盘之前（见 ②），命中即直接返回、一个字节都不写。
    let mut extract_images: Vec<crate::html::ExtractedImage> = Vec::new();
    match snapshot.kind {
        ItemKind::Image => {
            if let Some(png) = &snapshot.image_png {
                entry.image_path = format!("clipboard\\images\\{id}.png");
                entry.image_width = snapshot.image_width;
                entry.image_height = snapshot.image_height;
                // 内容指纹（**像素级**）：截图工具一次复制会连续写 CF_PNG/CF_DIB，两次快照字节不同但像素相同。
                // 旧实现用含 id 的 image_path 作指纹 → 永不命中 → 同一次截图留下两条历史（2026-09-12 修复）。
                entry.content_hash = crate::fingerprint::image_content_hash(png);
            }
        }
        ItemKind::Files => {
            entry.file_paths = snapshot.files.clone();
        }
        ItemKind::Html | ItemKind::RichText => {
            // 三格式全存
            entry.html_content = snapshot.html.clone();
            entry.rtf_content = snapshot.rtf.clone();
            entry.content = snapshot.text.clone();
            // HTML data URI 提取（单张 ≤2MB、单条 ≤16MB）：**纯计算**，必须先做 ——
            // 它会改写 `html_content`，而「纯文本为空」的条目指纹退回 `html_content`，
            // 故替换必须在算指纹之前完成，否则判重会拿"未提取版本"的指纹去查。
            let max_single = (settings.max_html_image_mb as usize) * 1024 * 1024;
            let max_total = 16 * 1024 * 1024;
            let extracted = crate::html::extract_data_uris(&snapshot.html, max_single, max_total);
            if !extracted.skipped && !extracted.images.is_empty() {
                entry.html_content = extracted.html;
                extract_images = extracted.images;
            }
        }
        ItemKind::Text => {
            entry.content = snapshot.text.clone();
        }
    }

    // 【P1-4 命名格式透传 · 2026-09-13】采集侧（capture 层）已按上限收好，这里原样落到条目。
    entry.named_formats = snapshot.named_formats.clone();

    // 【P2-2 敏感信息 · 2026-09-13】识别并打标记（仅标记，**不改内容** —— 用户粘贴时仍是全文）。
    // 类别名同时追加进 `tags`，于是「敏感」内容天然可被 `tag:` 搜索命中，无需新搜索语法。
    if settings.sensitive_detection && !entry.content.is_empty() {
        if let Some(kind) = crate::privacy::detect_sensitive(&entry.content) {
            entry.is_sensitive = true;
            if !entry.tags.contains(kind) {
                entry.tags = if entry.tags.is_empty() {
                    kind.to_string()
                } else {
                    format!("{} {}", entry.tags, kind)
                };
            }
            crate::log::info(format!("sensitive content detected: {kind}"));
        }
    }

    // ---- ② 同内容已在库里？→ **本次不落盘**（见 ① 的孤儿文件说明）----
    //
    // 并发安全性：调用方 `on_clipboard_update` 全程持有 STORE 锁，本函数与紧随其后的
    // `store.upsert(entry)` 处于同一临界区，故「查重 → 落盘 → upsert」之间不会被别的插入打断
    //（`upsert` 用同一张 `index`，查到的结果必然一致）。
    if store.find_by_fingerprint(&entry.fingerprint()).is_some() {
        crate::log::info("duplicate fingerprint; skipping external file writes");
        return entry;
    }

    // ---- ③ 落盘外部资源（只有确认是新条目才写）----
    match snapshot.kind {
        ItemKind::Image => {
            if let Some(png) = &snapshot.image_png {
                let _ = std::fs::create_dir_all(store.images_dir());
                entry.size_bytes = std::fs::write(store.images_dir().join(format!("{id}.png")), png)
                    .map(|_| png.len() as i64)
                    .unwrap_or(0);
                // 缩略图（480px；有 alpha PNG / 无 alpha JPEG）
                if let Some((thumb, fmt)) = crate::thumb::generate(png, settings.thumb_width) {
                    let _ = std::fs::create_dir_all(store.thumbs_dir());
                    let ext = if fmt == crate::thumb::ThumbFormat::Png { "png" } else { "jpg" };
                    let _ = std::fs::write(store.thumbs_dir().join(format!("{id}.{ext}")), thumb);
                }
            }
        }
        ItemKind::Files => {
            // full 模式：文件副本 ≤64MB；paths-only：仅路径
            if settings.storage_mode == crate::settings::StorageMode::Full {
                let copy_limit = (settings.file_copy_max_mb as u64) * 1024 * 1024;
                let files_dir = store.files_dir().join(&id);
                let _ = std::fs::create_dir_all(&files_dir);
                for path in &snapshot.files {
                    if let Ok(meta) = std::fs::metadata(path) {
                        if meta.len() <= copy_limit && meta.is_file() {
                            if let Some(name) = std::path::Path::new(path).file_name() {
                                let _ = std::fs::copy(path, files_dir.join(name));
                            }
                        }
                    }
                }
                entry.size_bytes = files_dir_size(&files_dir) as i64;
            }
        }
        ItemKind::Html | ItemKind::RichText => {
            if !extract_images.is_empty() {
                let dir = store.html_images_dir().join(&id);
                let _ = std::fs::create_dir_all(&dir);
                for img in &extract_images {
                    let _ = std::fs::write(dir.join(&img.filename), &img.bytes);
                }
            }
        }
        ItemKind::Text => {}
    }

    // 大文本：>100KB → content.bin（Deflate+DPAPI 懒加载）
    let (in_bin, size) = store.store_content(&id, &entry.content);
    entry.content_in_bin = in_bin;
    entry.size_bytes = entry.size_bytes.max(size.max(entry.content.len() as i64));
    entry
}

/// clipboard_changed 轻量摘要（引擎侧截断：textPreview ≤200 字符，不推大内容）。
fn clipboard_summary(store: &Store, id: &str) -> Value {
    let Some(e) = store.get_by_id(id) else {
        return json!({ "id": id });
    };
    let mut preview = e.content.clone();
    if preview.is_empty() && !e.html_content.is_empty() {
        // 简单剥离标签
        preview = strip_tags(&e.html_content);
    }
    if preview.chars().count() > 200 {
        preview = preview.chars().take(200).collect();
    }
    let thumb_path = if e.content_type == ItemKind::Image {
        let png = format!("clipboard\\thumbs\\{id}.png");
        let jpg = format!("clipboard\\thumbs\\{id}.jpg");
        if store.root().join(&png).exists() {
            png
        } else if store.root().join(&jpg).exists() {
            jpg
        } else {
            String::new()
        }
    } else {
        String::new()
    };
    json!({
        "id": id,
        "contentType": e.content_type.as_i32(),
        "textPreview": preview,
        "thumbPath": thumb_path,
        "sourceApp": e.source_process_name,
        "copiedAt": e.timestamp,
    })
}

/// 快照的**有效内容长度**（"存储大小判定"的精确形式）：返回 0 表示没有任何值得记录的内容。
///
/// 【2026-09-12 用户提法】"不应该加入存储大小判定不就行了？这样不就避免 99% 的错误了？"
/// 方向正确 —— 但**不能直接用字节数**（`Snapshot` 各字段的 `len()` 或条目的 `size_bytes`），
/// 它在**两个相反方向**上都会误判：
///   ① **有字节但无内容**：纯空白文本 `"   "`（3 字节）、空壳 HTML `<html><body></body></html>`
///      （30 字节）—— 按字节判会照常入库，而它们恰恰是最常见的"空数据"
///      （复制空格 / 空单元格 / 网页空白区域）。
///   ② **无字节但有内容**：文件条目在 **paths-only 存储模式下 `size_bytes == 0`**（不复制文件副本），
///      图片条目的文本字段也都是空 —— 按字节判会把**截图与文件复制全部误杀**。
///
/// 故把"大小"定义为**语义有效长度**：文本取 `trim` 后字符数、HTML 取剥标签后字符数
/// （空壳 HTML 为 0，含图/表记 1）、图片/文件按"有无"计 1（不看字节）。
fn effective_content_len(s: &Snapshot) -> usize {
    // 图片 / 文件：天然没有文本，只看"有没有" —— 绝不能看字节（paths-only 下为 0）
    if s.image_png.is_some() || !s.files.is_empty() {
        return 1;
    }

    let text = s.text.trim();
    if !text.is_empty() {
        return text.chars().count();
    }

    let rtf = s.rtf.trim();
    if !rtf.is_empty() {
        return rtf.chars().count();
    }

    if s.html.trim().is_empty() {
        return 0;
    }

    // HTML：按**剥掉标签后的可见文字**计长 —— 空壳 HTML 得 0；只含图/表的 HTML 虽无文字但不算空
    let lower = s.html.to_lowercase();
    let visible = strip_tags(&s.html);
    let visible = visible.trim();
    if !visible.is_empty() {
        return visible.chars().count();
    }
    if lower.contains("<img") || lower.contains("<table") {
        return 1;
    }
    0
}

fn strip_tags(html: &str) -> String {
    let mut out = String::with_capacity(html.len());
    let mut in_tag = false;
    for ch in html.chars() {
        match ch {
            '<' => in_tag = true,
            '>' => in_tag = false,
            _ if !in_tag => out.push(ch),
            _ => {}
        }
    }
    out
}

// ---------------- 事件广播 ----------------

/// 广播事件到全部已订阅连接（无订阅者零开销）。
pub fn broadcast(event: &str, data: Value) {
    // 【2026-09-12 修复】锁中毒时取回内部值。此前 `Err(_) => return` 会让**所有事件永久静默**：
    // history_changed / clipboard_changed 全断 → 面板再不刷新，而 Subscriber 还在累积。
    let guard = SUBSCRIBERS.lock().unwrap_or_else(|e| e.into_inner());
    if guard.is_empty() {
        return;
    }
    let payload = json!({ "jsonrpc": "2.0", "method": event, "params": data });
    let mut line = serde_json::to_vec(&payload).unwrap_or_default();
    line.push(b'\n');
    for sub in guard.iter() {
        if sub.subscribed.load(Ordering::Acquire) {
            let _ = sub.tx.send(line.clone());
        }
    }
}

// ---------------- IPC 方法分派 ----------------

/// 分派 JSON-RPC method。返回 Result<result, (code, message)>。
pub fn dispatch(method: &str, params: Option<Value>) -> Result<Value, (i64, String)> {
    match method {
        "ping" => Ok(json!({ "ok": true, "version": env!("CARGO_PKG_VERSION"), "pid": std::process::id() })),
        "query" => cmd_query(params),
        "storage_status" => cmd_storage_status(),
        "get_last" => cmd_get_last(),
        "get_entry" => cmd_get_entry(params),
        "get_content" => cmd_get_content(params),
        "pin" => cmd_pin(params, true),
        "unpin" => cmd_pin(params, false),
        // 表情包标记（与收藏同级的独立标记；任意条目可标记，含文字颜文字 · 2026-09-13）
        "set_sticker" => cmd_set_sticker(params),
        "delete" => cmd_delete(params),
        "delete_many" => cmd_delete_many(params),
        "clear_unpinned" => cmd_clear_unpinned(),
        "set_tags" => cmd_set_tags(params),
        // 【截图 OCR · 2026-09-14】把 OCR 识别文本写回图片条目（供搜索与面板展示）。
        "set_ocr_text" => cmd_set_ocr_text(params),
        "copy_to_clipboard" => cmd_copy(params),
        // 【P2-3 临时粘贴】写回目标条目并暂存用户原剪贴板，供 restore_temp_clipboard 还原。
        "copy_temp_to_clipboard" => cmd_copy_temp(params),
        "restore_temp_clipboard" => cmd_restore_temp(),
        "add_sticker" => cmd_add_sticker(params),
        "pause" => {
            // seconds 可选：定时暂停（面板传 60s）→ 引擎侧到点自动恢复（见 PAUSE_UNTIL 注释）
            let seconds = params
                .as_ref()
                .and_then(|p| p.get("seconds"))
                .and_then(|v| v.as_u64())
                .filter(|s| *s > 0);
            PAUSED.store(true, Ordering::Release);
            *PAUSE_UNTIL.lock().unwrap_or_else(|e| e.into_inner()) =
                seconds.map(|s| std::time::Instant::now() + std::time::Duration::from_secs(s));
            broadcast("pause_changed", json!({ "paused": true, "seconds": seconds }));
            Ok(json!({ "ok": true, "seconds": seconds }))
        }
        "resume" => {
            PAUSED.store(false, Ordering::Release);
            *PAUSE_UNTIL.lock().unwrap_or_else(|e| e.into_inner()) = None;
            broadcast("pause_changed", json!({ "paused": false }));
            Ok(json!({ "ok": true }))
        }
        "open_panel" => {
            let launched = open_panel();
            Ok(json!({ "ok": launched }))
        }
        "apply_settings" => cmd_apply_settings(params),
        _ => Err((-32601, "Method not found.".to_string())),
    }
}

fn store_guard() -> Result<std::sync::MutexGuard<'static, Store>, (i64, String)> {
    STORE
        .get()
        .map(|s| s.lock().unwrap_or_else(|e| e.into_inner()))
        .ok_or((-32603, "store not initialized".to_string()))
}

/// query：分页拉取（offset/limit，limit 默认 100、上限 500）。
///
/// 【2026-09-12 搜索/筛选下沉引擎】原实现只支持 category —— keyword/kind/sourceApp/pinned 全靠客户端
/// `FetchAll` 全量拉取后本地 LINQ 过滤：打一个字就把整库（含 HTML 正文）序列化到 UI 线程，
/// 分页形同虚设（万条规模必卡）。现全部过滤在引擎侧完成，UI 永远只取一页。
///
/// 契约：
///   - `total` = **过滤后**命中总数（旧实现返回全库条数，带过滤时客户端分页判断失真）。
///   - keyword/sourceApp 为 trim + 忽略大小写子串匹配；keyword 覆盖
///     content / htmlContent / tags / sourceProcessName / filePaths（对齐 C# ClipboardFilterTests 语义）。
///   - 返回**列表摘要**（`entry_summary_json`）：剥离 htmlContent/rtfContent 大字段、content 截断。
///     完整内容走 `get_entry` / `copy_to_clipboard`（引擎侧直读 store，不依赖客户端持有正文）。
fn cmd_query(params: Option<Value>) -> Result<Value, (i64, String)> {
    let p = params.unwrap_or(json!({}));
    let offset = p.get("offset").and_then(|v| v.as_u64()).unwrap_or(0) as usize;
    let limit = p
        .get("limit")
        .and_then(|v| v.as_u64())
        .unwrap_or(100)
        .min(500) as usize;
    let filter = QueryFilter::from_params(&p);

    let store = store_guard()?;
    // 条目顺序 = 新→旧（push 顺序：末尾最新）→ 先按条件过滤，再在过滤结果上分页（offset 语义 = 命中集偏移）
    let matched: Vec<&ClipboardEntry> = store
        .entries()
        .iter()
        .rev()
        .filter(|e| filter.matches(e))
        .collect();
    let total = matched.len();
    let items: Vec<Value> = matched
        .into_iter()
        .skip(offset)
        .take(limit)
        .map(entry_summary_json)
        .collect();
    Ok(json!({ "total": total, "items": items }))
}

/// query 过滤条件（IPC params → 纯值对象，无 IO，可单测）。
/// 字符串字段构造时已 trim + 小写；空串归一为 None（= 不过滤）。
#[derive(Debug, Default, Clone)]
pub struct QueryFilter {
    /// 语义分类（ContentCategory 数值）。
    /// 【2026-09-13】`5`（旧 `ContentCategory.Sticker`）**不再是有效分类值** —— 见 `from_params` 的兼容转换。
    pub category: Option<i32>,
    /// 内容类型（ClipboardItemKind 数值）。
    pub kind: Option<i32>,
    /// 仅收藏 / 仅非收藏。
    pub pinned: Option<bool>,
    /// 仅表情包 / 仅非表情包。
    /// 【2026-09-13】表情包已由"分类"改为与收藏同级的**标记**（`ClipboardEntry::is_sticker`），
    /// 筛选因此走本字段 —— 这样**任何类型**（文字颜文字/静态图/动图/文件）都能被筛出来。
    pub sticker: Option<bool>,
    /// 来源进程名子串（已小写）。
    pub source_app: Option<String>,
    /// 关键词子串（已小写），命中正文/HTML/标签/来源/文件路径任一即成。
    pub keyword: Option<String>,
    /// 【P2-4】关键词是否只按**标签**匹配（面板搜索 `tag:xxx` 时置真，`keyword` 存剥离前缀后的值）。
    pub tag_only: bool,
}

impl QueryFilter {
    fn from_params(p: &Value) -> Self {
        let mut category = p.get("category").and_then(|v| v.as_i64()).map(|v| v as i32);
        let mut sticker = p.get("sticker").and_then(|v| v.as_bool());

        // 兼容旧调用方：此前"表情包"是分类，客户端会传 `category = 5`。
        // 那个分类值已废弃 —— 若继续按分类匹配，只会**一条都查不到**（引擎不再产生该分类）。
        // 这里把它翻译成"按标记筛选"，老面板不改也能用。
        //
        // ⚠️ 只在调用方**没有显式给 sticker 条件**时翻译：否则 `{category:5, sticker:false}`
        // 这种"同时给新旧参数"的调用会被强行翻成 true，等于**静默忽略调用方的显式意图**。
        if category == Some(Category::Sticker.as_i32()) {
            category = None;
            sticker = sticker.or(Some(true));
        }

        // 【P2-4 标签体系 · 2026-09-13】`tag:xxx` 前缀 → **只按标签字段匹配**（面板搜索框直接支持）。
        // 解析只在引擎侧做一次，避免面板/引擎两处语义漂移；`tag:` 后为空则视为不过滤。
        let raw_keyword = normalize_filter(p.get("keyword").and_then(|v| v.as_str()));
        let (keyword, tag_only) = match raw_keyword {
            Some(k) if k.starts_with("tag:") => {
                let body = k["tag:".len()..].trim().to_string();
                (if body.is_empty() { None } else { Some(body) }, true)
            }
            other => (other, false),
        };

        QueryFilter {
            category,
            kind: p.get("kind").and_then(|v| v.as_i64()).map(|v| v as i32),
            pinned: p.get("pinned").and_then(|v| v.as_bool()),
            sticker,
            source_app: normalize_filter(p.get("sourceApp").and_then(|v| v.as_str())),
            keyword,
            tag_only,
        }
    }

    /// 是否命中：所有已给条件取「与」（未给的条件不参与）。
    pub fn matches(&self, e: &ClipboardEntry) -> bool {
        self.category.is_none_or(|c| e.category.as_i32() == c)
            && self.kind.is_none_or(|k| e.content_type.as_i32() == k)
            && self.pinned.is_none_or(|pin| e.is_pinned == pin)
            && self.sticker.is_none_or(|s| e.is_sticker == s)
            && self
                .source_app
                .as_ref()
                .is_none_or(|a| e.source_process_name.to_lowercase().contains(a.as_str()))
            && self.keyword.as_ref().is_none_or(|kw| {
                if self.tag_only {
                    // `tag:` 只匹配标签字段（无标签条目永不命中）
                    !e.tags.is_empty() && e.tags.to_lowercase().contains(kw)
                } else {
                    keyword_hits(e, kw)
                }
            })
    }
}

/// 过滤词归一化：trim + 小写；空串视为「不过滤」。
fn normalize_filter(raw: Option<&str>) -> Option<String> {
    raw.map(|s| s.trim().to_lowercase()).filter(|s| !s.is_empty())
}

/// keyword 命中判定（子串、忽略大小写）：正文 / HTML / 标签 / 来源进程 / 文件路径 / OCR 文本。
fn keyword_hits(e: &ClipboardEntry, kw: &str) -> bool {
    let hit = |s: &str| !s.is_empty() && s.to_lowercase().contains(kw);
    hit(&e.content)
        || hit(&e.html_content)
        || hit(&e.tags)
        || hit(&e.source_process_name)
        || hit(&e.ocr_text)
        || e.file_paths.iter().any(|p| hit(p))
}

/// storage_status：存储占用与预算（面板据此提醒用户清理）。
///
/// 【2026-09-12 语义变更】总存储预算由"硬驱逐"改为**软限制** —— 达到/超过预算**不再自动删除条目**
///（用户口径："总上限不是占位的上限，是历史数据达到存储上面也可以存储，但我们要发消息提醒用户及时清理"）。
/// 引擎只上报状态，由 UI 提醒并给出一键清理（`clear_unpinned`）：用户的历史不由引擎静默处置。
fn cmd_storage_status() -> Result<Value, (i64, String)> {
    // 先在块内取 settings 并 clone（出块即释放锁），再取 store —— 避免与其它路径形成锁序交叉。
    let settings = {
        SETTINGS
            .get()
            .map(|s| s.lock().unwrap_or_else(|e| e.into_inner()).clone())
            .ok_or((-32603i64, "settings not initialized".to_string()))?
    };
    let store = store_guard()?;
    let used = store.total_used_bytes();
    let budget = (settings.max_total_mb as u64) * 1024 * 1024;
    let entries = store.entries();
    // 【2026-09-13】与 `clear_unpinned` 口径对齐：表情包不会被清理，就不该被算进"未收藏"。
    let unpinned = entries.iter().filter(|e| !e.is_pinned && !e.is_sticker).count();
    Ok(json!({
        "usedBytes": used,
        "budgetBytes": budget,
        "usedMb": used / (1024 * 1024),
        "budgetMb": settings.max_total_mb,
        "overBudget": budget > 0 && used > budget,
        "entries": entries.len(),
        "unpinned": unpinned,
        "capacity": settings.capacity,
    }))
}

fn cmd_get_last() -> Result<Value, (i64, String)> {
    let store = store_guard()?;
    match store.entries().last() {
        Some(e) => Ok(entry_json(e)),
        None => Ok(json!(null)),
    }
}

fn cmd_get_entry(params: Option<Value>) -> Result<Value, (i64, String)> {
    let id = string_param(&params, "id")?;
    let store = store_guard()?;
    store
        .get_by_id(&id)
        .map(entry_json)
        .ok_or((-32602, format!("entry not found: {id}")))
}

fn cmd_get_content(params: Option<Value>) -> Result<Value, (i64, String)> {
    let id = string_param(&params, "id")?;
    let store = store_guard()?;
    let content = store
        .get_by_id(&id)
        .map(|e| store.get_content(e))
        .ok_or((-32602, format!("entry not found: {id}")))?;
    Ok(json!({ "id": id, "content": content }))
}

fn cmd_pin(params: Option<Value>, pin: bool) -> Result<Value, (i64, String)> {
    let id = string_param(&params, "id")?;
    let mut store = store_guard()?;
    let settings = SETTINGS
        .get()
        .map(|s| s.lock().unwrap_or_else(|e| e.into_inner()))
        .ok_or((-32603i64, "settings not initialized".to_string()))?;
    // 先算收藏数（避免与可变借用冲突）
    let pinned_count = store.entries().iter().filter(|x| x.is_pinned).count();
    let Some(e) = store.entries_mut_ref(&id) else {
        return Err((-32602, format!("entry not found: {id}")));
    };
    if pin {
        if !e.is_pinned && pinned_count >= settings.pinned_limit {
            return Err((-32602, "pinned limit reached".to_string()));
        }
        e.is_pinned = true;
    } else {
        e.is_pinned = false;
    }
    Ok(json!({ "ok": true, "id": id, "isPinned": e.is_pinned }))
}

/// 设置/取消「表情包」标记。
///
/// 【为什么是独立 IPC · 2026-09-13 用户口径】"跟收藏一样的机制，这样就不管是图片还是颜文字都可以了" ——
/// 既然**任何条目**都能是表情包（文字颜文字、静态图、动图、文件），就必须有"对任意条目打标记"的入口，
/// 而不是只能靠"导入本地文件"。本方法即该入口（面板行内按钮直接调它）。
fn cmd_set_sticker(params: Option<Value>) -> Result<Value, (i64, String)> {
    let id = string_param(&params, "id")?;
    // 缺省视为"打上标记"（调用方通常只想标记）；显式传 false 才是取消。
    let value = params
        .as_ref()
        .and_then(|p| p.get("value"))
        .and_then(|v| v.as_bool())
        .unwrap_or(true);

    let mut store = store_guard()?;
    let Some(e) = store.entries_mut_ref(&id) else {
        return Err((-32602, format!("entry not found: {id}")));
    };
    e.is_sticker = value;
    Ok(json!({ "ok": true, "id": id, "isSticker": e.is_sticker }))
}

fn cmd_delete(params: Option<Value>) -> Result<Value, (i64, String)> {
    let id = string_param(&params, "id")?;
    let mut store = store_guard()?;
    let ok = store.remove_by_id(&id);
    if ok {
        broadcast("history_changed", json!({ "kind": "deleted", "id": id }));
    }
    Ok(json!({ "ok": ok }))
}

fn cmd_delete_many(params: Option<Value>) -> Result<Value, (i64, String)> {
    let ids = array_param(&params, "ids")?;
    let mut store = store_guard()?;
    let mut removed = 0usize;
    for id in ids {
        if store.remove_by_id(&id) {
            removed += 1;
        }
    }
    Ok(json!({ "ok": true, "removed": removed }))
}

fn cmd_clear_unpinned() -> Result<Value, (i64, String)> {
    let mut store = store_guard()?;
    let ids: Vec<String> = store
        .entries()
        .iter()
        // 【表情包豁免 · 2026-09-12】用户点「清理未收藏」的意图是清历史噪声，不是清珍宝 ——
        // 表情包是主动标记的内容，必须与收藏条目一样受保护（否则用户的表情包会凭空消失）。
        // 【2026-09-13】判据由"分类是表情包"改为**标记**（任意类型条目都能被标记，含文字颜文字）。
        .filter(|e| !e.is_pinned && !e.is_sticker)
        .map(|e| e.id.clone())
        .collect();
    for id in &ids {
        store.remove_by_id(id);
    }
    broadcast("history_changed", json!({ "kind": "cleared_unpinned" }));
    Ok(json!({ "ok": true, "removed": ids.len() }))
}

fn cmd_set_tags(params: Option<Value>) -> Result<Value, (i64, String)> {
    let id = string_param(&params, "id")?;
    let tags = string_param(&params, "tags")?;
    let mut store = store_guard()?;
    let Some(e) = store.entries_mut_ref(&id) else {
        return Err((-32602, format!("entry not found: {id}")));
    };
    e.tags = tags;
    Ok(json!({ "ok": true }))
}

/// 【截图 OCR · 2026-09-14】写回 OCR 文本（仅图片条目；空文本=清空）。
/// 调用方（capture exe / 面板）经 IOcrService 间接调用，不直接碰引擎内部。
fn cmd_set_ocr_text(params: Option<Value>) -> Result<Value, (i64, String)> {
    let id = string_param(&params, "id")?;
    let ocr_text = string_param(&params, "ocr_text")?;
    let mut store = store_guard()?;
    let Some(e) = store.entries_mut_ref(&id) else {
        return Err((-32602, format!("entry not found: {id}")));
    };
    e.ocr_text = ocr_text;
    // 搜索可见性以条目维度计算，无需单独广播；查询/详情自然带上新字段。
    Ok(json!({ "ok": true }))
}

fn cmd_copy(params: Option<Value>) -> Result<Value, (i64, String)> {
    let id = string_param(&params, "id")?;
    // plain=true：强制纯文本写回（客户端 CopyEntryAsPlainText / 代码条目）
    let plain = params
        .as_ref()
        .and_then(|p| p.get("plain"))
        .and_then(|v| v.as_bool())
        .unwrap_or(false);
    let store = store_guard()?;
    let Some(entry) = store.get_by_id(&id).cloned() else {
        return Err((-32602, format!("entry not found: {id}")));
    };
    // 写回 + 回环抑制（2026-09-13 改为**内容指纹**比对，见 listener.rs 注释）。
    //
    // ⚠️ 顺序关键：**先登记指纹、再写回**。若反过来（写完再登记），广播/轮询可能在两者之间
    // 就把"我们自己的写回"当成新内容捕获了（捕获线程算指纹时标记还没落下 → 漏吞）。
    // 写回失败则立刻清掉标记，否则窗口内用户真的复制同一内容会被误吞。
    if let Some(l) = LISTENER.get() {
        l.mark_echo(&entry.fingerprint());
    }
    let ok = capture::write_back(&entry, &store, plain);
    if !ok {
        if let Some(l) = LISTENER.get() {
            l.clear_echo();
        }
    }
    if ok {
        let summary = clipboard_summary(&store, &id);
        drop(store);
        // 写回同样属于"剪贴板变化" → 受 push-events 开关门控（2026-09-12 落实设置项）
        if settings_push_events() {
            broadcast("clipboard_changed", summary);
        }
    }
    Ok(json!({ "ok": ok }))
}

/// 【P2-3 临时粘贴 · 2026-09-13】写回目标条目，并把**当前剪贴板内容**暂存起来。
///
/// 与 `cmd_copy` 的唯一区别：写回前先快照用户原剪贴板，随后可由 `restore_temp_clipboard` 还原，
/// 实现"粘完不污染剪贴板"。快照为纯内存（不落盘、不入库）。
///
/// 锁序注意：**先读剪贴板（不持 store 锁）、再锁 store 取条目**，避免持锁期间做剪贴板 IO。
fn cmd_copy_temp(params: Option<Value>) -> Result<Value, (i64, String)> {
    let id = string_param(&params, "id")?;

    // ① 快照用户当前剪贴板（读不到 / 空内容 → 记录"无可还原内容"，还原时直接返回 restored=false）。
    let snapshot = capture::read_snapshot(None).filter(|s| effective_content_len(s) > 0);
    let had_previous = snapshot.is_some();
    *TEMP_CLIP.lock().unwrap_or_else(|e| e.into_inner()) = snapshot.map(|snapshot| TempClip {
        snapshot,
        created_at: std::time::Instant::now(),
    });

    // ② 写回目标条目（与 cmd_copy 同样的回环抑制顺序：先登记指纹、再写回）。
    let store = store_guard()?;
    let Some(entry) = store.get_by_id(&id).cloned() else {
        return Err((-32602, format!("entry not found: {id}")));
    };
    if let Some(l) = LISTENER.get() {
        l.mark_echo(&entry.fingerprint());
    }
    let ok = capture::write_back(&entry, &store, false);
    if !ok {
        if let Some(l) = LISTENER.get() {
            l.clear_echo();
        }
    }
    Ok(json!({ "ok": ok, "hadPrevious": had_previous }))
}

/// 【P2-3】还原"临时粘贴"前的剪贴板内容。
///
/// **过期保护（红线）**：超过 [`TEMP_CLIP_TTL`] 不还原 —— 用户期间很可能已复制了新内容，
/// 此时还原反而会**覆盖用户的新复制**。只清空快照并记日志。
/// 无快照 / 已过期均为正常路径，返回 `restored=false`（不是错误）。
fn cmd_restore_temp() -> Result<Value, (i64, String)> {
    let taken = TEMP_CLIP.lock().unwrap_or_else(|e| e.into_inner()).take();
    let Some(clip) = taken else {
        return Ok(json!({ "ok": true, "restored": false }));
    };
    if clip.created_at.elapsed() > TEMP_CLIP_TTL {
        crate::log::info("temp-paste: 快照已过期（>10s），跳过还原以避免覆盖用户新复制");
        return Ok(json!({ "ok": true, "restored": false }));
    }

    if let Some(l) = LISTENER.get() {
        l.mark_echo(&capture::snapshot_fingerprint(&clip.snapshot));
    }
    let ok = capture::write_back_snapshot(&clip.snapshot);
    if !ok {
        if let Some(l) = LISTENER.get() {
            l.clear_echo();
        }
    }
    crate::log::info(format!("temp-paste: 剪贴板还原{}", if ok { "成功" } else { "失败" }));
    Ok(json!({ "ok": ok, "restored": ok }))
}

/// 表情包导入（用户主动填入）：`params: { paths: [...] }`。
///
/// 语义（见 docs/plans/2026-09-12-clipboard-sticker-mode.md §3）：
/// - **原文件字节级复制**到 `clipboard\stickers\{id}.{ext}`（绝不转码 —— 动图只有原文件能保动画）；
/// - `content_hash = sha256(文件字节)` → 已有同内容表情包则计入 `skipped`（不产生副本堆积）；
/// - 条目 `content_type = Files` + `category = Sticker`，`file_paths = [副本绝对路径]`
///   （粘贴天然走 CF_HDROP；另见 `capture::set_sticker_entry` 的多格式写回）；
/// - **不参与驱逐、也不被「清理未收藏」删除**（见 `store::evict` 与 `cmd_clear_unpinned` 的 Sticker 豁免）。
///
/// 逐个文件给出失败原因（`errors`）—— 用户必须知道哪个文件为什么没进来（失败不得静默）。
fn cmd_add_sticker(params: Option<Value>) -> Result<Value, (i64, String)> {
    let paths: Vec<String> = params
        .as_ref()
        .and_then(|p| p.get("paths"))
        .and_then(|v| v.as_array())
        .map(|arr| {
            arr.iter()
                .filter_map(|v| v.as_str())
                .map(|s| s.trim().to_string())
                .filter(|s| !s.is_empty())
                .collect()
        })
        .unwrap_or_default();
    if paths.is_empty() {
        return Err((-32602, "paths required".to_string()));
    }

    let settings = SETTINGS
        .get()
        .map(|s| s.lock().unwrap_or_else(|e| e.into_inner()).clone())
        .unwrap_or_default();
    let max_bytes = (settings.file_copy_max_mb as u64) * 1024 * 1024;

    let mut store = store_guard()?;
    let mut added = 0usize;
    // 已在历史里（图片条目）→ 原地升级为表情包的条数（**不新增条目**，2026-09-13）。
    let mut upgraded = 0usize;
    let mut skipped = 0usize;
    let mut ids: Vec<String> = Vec::new();
    let mut errors: Vec<String> = Vec::new();

    for src in paths {
        let path = std::path::Path::new(&src);
        let ext = path
            .extension()
            .and_then(|e| e.to_str())
            .unwrap_or_default()
            .to_ascii_lowercase();
        if !STICKER_EXTS.contains(&ext.as_str()) {
            errors.push(format!("{src}：不支持的格式（支持 gif/webp/apng/png/jpg/bmp）"));
            continue;
        }
        let Ok(meta) = std::fs::metadata(path) else {
            errors.push(format!("{src}：文件不存在或不可读"));
            continue;
        };
        if !meta.is_file() {
            errors.push(format!("{src}：不是文件"));
            continue;
        }
        if max_bytes > 0 && meta.len() > max_bytes {
            errors.push(format!(
                "{src}：超过单文件上限 {}MB",
                settings.file_copy_max_mb
            ));
            continue;
        }
        let Ok(bytes) = std::fs::read(path) else {
            errors.push(format!("{src}：读取失败"));
            continue;
        };
        if bytes.is_empty() {
            errors.push(format!("{src}：空文件"));
            continue;
        }

        // 【2026-09-13 用户口径】"跟收藏一样的机制，这样就不管是图片还是颜文字都可以了"——
        // 判重不是为了"决定新增哪一条"，而是为了判断**这张图是不是已经在库里**：
        //  · 已在库里（无论它是复制来的图片条目，还是当初导入进来的）→ **只打表情包标记**，绝不新增；
        //  · 不在库里 → 走下方正常新增（新建条目 + 打标记）。
        //
        // 为什么以前会重复：图片条目键 = `image:{像素hash}`，表情包条目键 = `sticker:{文件字节hash}`，
        // **两个键永不相同** → 只能判成新条目（用户实测："你就没有考虑过有些表情包已经被我们的剪贴板历史给记录了吗？"）。
        // 现在两者共用像素级键空间（见 `ClipboardEntry::fingerprint`）。
        let pixel_hash = crate::fingerprint::image_content_hash(&bytes);
        if let Some(existing) = store.find_by_image_hash(&pixel_hash) {
            let existing_id = existing.id.clone();
            let was_sticker = existing.is_sticker;
            // 例外：库里那条是**图片条目**（引擎重编码的 PNG，只剩首帧），而本次导入的是**动图**
            // → 必须改指原文件副本，否则打了标记也放不出动画。静态图与其它情况只打标记、不动内容。
            let need_original_bytes =
                existing.content_type == ItemKind::Image && ANIMATED_EXTS.contains(&ext.as_str());

            if need_original_bytes {
                if let Err(e) = std::fs::create_dir_all(store.stickers_dir()) {
                    errors.push(format!("{src}：创建存储目录失败（{e}）"));
                    continue;
                }
                let dst = store.stickers_dir().join(format!("{existing_id}.{ext}"));
                if let Err(e) = std::fs::write(&dst, &bytes) {
                    errors.push(format!("{src}：写入副本失败（{e}）"));
                    continue;
                }
                let (w, h) = image_dimensions(&bytes);
                if let Some(entry) = store.entries_mut_ref(&existing_id) {
                    // Files + file_paths → 粘贴天然走 CF_HDROP（动画靠原文件保住）。
                    // `image_path`（历史那张 PNG）**故意保留**：条目删除时 `remove_entry_files` 会顺手回收它。
                    entry.content_type = ItemKind::Files;
                    entry.file_paths = vec![dst.to_string_lossy().to_string()];
                    entry.content_hash = pixel_hash.clone(); // 像素口径不变 → 去重索引无需重建
                    entry.size_bytes = bytes.len() as i64;
                    entry.image_width = w;
                    entry.image_height = h;
                }
            }

            if let Some(entry) = store.entries_mut_ref(&existing_id) {
                entry.is_sticker = true; // **标记**（分类不再承担表情包语义）
            }

            if need_original_bytes {
                // 内容被替换（为保动画而改指原文件副本）—— 即便它本来就是表情包，
                // 也必须让调用方知道"这次不是纯跳过"，否则用户与日志都无从得知内容已被替换。
                upgraded += 1;
                ids.push(existing_id);
            } else if was_sticker {
                skipped += 1; // 本来就是表情包、且无需替换内容：纯跳过
            } else {
                upgraded += 1;
                ids.push(existing_id);
            }
            continue;
        }

        let id = uuid_v4();
        if let Err(e) = std::fs::create_dir_all(store.stickers_dir()) {
            errors.push(format!("{src}：创建存储目录失败（{e}）"));
            continue;
        }
        let dst = store.stickers_dir().join(format!("{id}.{ext}"));
        if let Err(e) = std::fs::write(&dst, &bytes) {
            errors.push(format!("{src}：写入副本失败（{e}）"));
            continue;
        }

        // 首帧尺寸（展示用；GIF/WebP 取首帧）与缩略图（面板显示缩略图，永不解码原图）
        let (w, h) = image_dimensions(&bytes);
        if let Some((thumb, fmt)) = crate::thumb::generate(&bytes, settings.thumb_width) {
            let _ = std::fs::create_dir_all(store.thumbs_dir());
            let t_ext = if fmt == crate::thumb::ThumbFormat::Png {
                "png"
            } else {
                "jpg"
            };
            let _ = std::fs::write(store.thumbs_dir().join(format!("{id}.{t_ext}")), thumb);
        }

        let mut entry = ClipboardEntry::new(&id, ItemKind::Files, "");
        // 【2026-09-13】表情包不再是分类，而是**标记**：
        //  · `is_sticker = true` → 出现在「表情包」筛选里、并豁免驱逐与「清理未收藏」；
        //  · `category` 按**实际内容**给（导入白名单只允许图片扩展名 → Image），不再有"表情包"这个分类值。
        entry.is_sticker = true;
        entry.category = Category::Image;
        entry.file_paths = vec![dst.to_string_lossy().to_string()];
        entry.size_bytes = bytes.len() as i64;
        entry.image_width = w;
        entry.image_height = h;
        entry.content_hash = pixel_hash.clone(); // 像素口径（与图片条目同键空间）
        entry.source_process_name = "sticker".to_string();
        let outcome = store.upsert(entry);
        if matches!(outcome, UpsertOutcome::New(_)) {
            added += 1;
            ids.push(id);
        } else {
            // 兜底：前面已按内容哈希拦过，正常不会走到；真的命中就按"跳过"计，避免"报成功却没新增"
            skipped += 1;
        }
    }

    if added > 0 {
        if let Err(e) = store.save() {
            crate::log::error(format!("add_sticker save failed: {e}"));
        }
    }
    drop(store);

    crate::log::info(format!(
        "add_sticker: added={added} upgraded={upgraded} skipped={skipped} errors={}",
        errors.len()
    ));
    if added > 0 || upgraded > 0 {
        // 升级同样是"历史变化"：面板必须刷新，否则用户看不到那条图片已经变成表情包。
        broadcast(
            "history_changed",
            json!({ "kind": "added_sticker", "count": added, "upgraded": upgraded }),
        );
    }
    Ok(json!({
        "ok": true,
        "added": added,
        "upgraded": upgraded,
        "skipped": skipped,
        "ids": ids,
        "errors": errors
    }))
}

/// 图片首帧尺寸（只解析 header，不整图解码）；失败返回 (0,0) —— 尺寸仅用于展示。
fn image_dimensions(bytes: &[u8]) -> (i32, i32) {
    image::ImageReader::new(std::io::Cursor::new(bytes))
        .with_guessed_format()
        .ok()
        .and_then(|r| r.into_dimensions().ok())
        .map(|(w, h)| (w as i32, h as i32))
        .unwrap_or((0, 0))
}

fn cmd_apply_settings(params: Option<Value>) -> Result<Value, (i64, String)> {
    let Some(p) = params else {
        return Err((-32602, "params required".to_string()));
    };
    let mut settings = SETTINGS
        .get()
        .map(|s| s.lock().unwrap_or_else(|e| e.into_inner()))
        .ok_or((-32603i64, "settings not initialized".to_string()))?;
    // 部分字段合并（缺失保持现值）
    let current = serde_json::to_value(&*settings)
        .map_err(|e| (-32603i64, e.to_string()))?;
    let mut merged = current;
    // 【2026-09-12 审计】消除全库唯一的裸 unwrap：坏输入（merged 非对象）会在消息循环线程 panic
    if let (Some(obj), Some(target)) = (p.as_object(), merged.as_object_mut()) {
        for (k, v) in obj {
            target.insert(k.clone(), v.clone());
        }
    }
    let updated: Settings = serde_json::from_value(merged)
        .map_err(|e| (-32602i64, format!("invalid settings: {e}")))?;
    let enabled_changed = updated.enabled != settings.enabled;
    let new_app_rules = updated.app_rules.clone();
    *settings = updated;
    drop(settings);

    // 【P1-5】按应用清洗规则变更 → 重编译（按 JSON 指纹比对，无关设置变更不触发重编译）。
    {
        let fp = serde_json::to_string(&new_app_rules).unwrap_or_default();
        let mut compiled = compiled_rules();
        if compiled.source_fingerprint != fp {
            *compiled = crate::rules::compile(&new_app_rules);
        }
    }
    if enabled_changed {
        // 扩展中心开关 → 监听启停 + monitoring_changed 事件
        if let Some(l) = LISTENER.get() {
            l.set_enabled(settings_enabled());
        }
        broadcast("monitoring_changed", json!({ "enabled": settings_enabled() }));
    }
    // 【2026-09-12 用户要求"修改实时生效"】设置热更新后**立即按新规则收敛一次**：
    // 把「历史容量上限 / 保留天数」调小时，用户期望马上生效，而不是等到下次复制才触发 evict
    //（此前引擎只在 on_clipboard_update 里驱逐 → 改小容量后要复制一次才生效，观感像"没生效"）。
    // 锁序：上面已 drop(settings)，此处先 clone 快照再取 store，避免与其它路径形成交叉锁序。
    let evicted = {
        let snapshot = SETTINGS
            .get()
            .map(|s| s.lock().unwrap_or_else(|e| e.into_inner()).clone())
            .unwrap_or_default();
        let mut store = store_guard()?;
        let removed = store.evict(&snapshot);
        if !removed.is_empty() {
            if let Err(e) = store.save() {
                crate::log::error(format!("post-settings evict save failed: {e}"));
            }
        }
        removed.len()
    };
    if evicted > 0 {
        crate::log::info(format!("apply_settings 后立即收敛：按新规则驱逐 {evicted} 条"));
        broadcast("history_changed", json!({ "kind": "evicted_by_settings", "count": evicted }));
    }
    Ok(json!({ "ok": true, "evicted": evicted }))
}

fn settings_enabled() -> bool {
    SETTINGS
        .get()
        .map(|s| s.lock().unwrap_or_else(|e| e.into_inner()).enabled)
        .unwrap_or(true)
}

/// `push-events` 开关：剪贴板变化摘要（灵动岛等只读订阅方）是否推送；无设置时默认开。
fn settings_push_events() -> bool {
    SETTINGS
        .get()
        .map(|s| s.lock().unwrap_or_else(|e| e.into_inner()).push_events)
        .unwrap_or(true)
}

// ---------------- 热键动作（S5） ----------------

/// WM_HOTKEY 分派：wparam = 注册 id → 查动作 → 执行。
pub fn handle_hotkey(id: i32) {
    let Some(action) = crate::hotkey::HOTKEYS
        .get()
        .and_then(|h| h.lock().unwrap_or_else(|e| e.into_inner()).action_for(id))
    else {
        crate::log::warn(format!("unknown hotkey id {id}"));
        return;
    };
    crate::log::info(format!("hotkey triggered: {action:?}"));
    match action {
        crate::hotkey::HotkeyAction::OpenPanel => {
            let _ = open_panel_with(false);
        }
        // 【2026-09-14 语义对齐 legacy（用户拍板方案 A）】Ctrl+Shift+P = 收藏视图 ——
        // 与宿主内实现 `ClipboardManager.HandleHotKey(HotKeyIdFavorites)` 同义：
        // 打开面板并切到「收藏」筛选。引擎侧**不记忆**该状态：面板自己的筛选 chip 才是唯一真源
        //（legacy 的 `ShowFavoritesOnly` 开关之所以要有状态，是因为窗口在进程内、开关直接改列表）。
        crate::hotkey::HotkeyAction::OpenFavorites => {
            let _ = open_panel_with(true);
        }
        crate::hotkey::HotkeyAction::TogglePause => {
            let paused = PAUSED.fetch_xor(true, Ordering::AcqRel);
            let now_paused = !paused;
            // 手动切换一律清掉定时暂停：手动意图优先于此前设置的自动恢复时刻
            //（否则"手动恢复"后到点还会再广播一次"已恢复"，观感是面板状态自己跳了一下）
            *PAUSE_UNTIL.lock().unwrap_or_else(|e| e.into_inner()) = None;
            crate::log::info(format!("hotkey toggled pause: {now_paused}"));
            broadcast("pause_changed", json!({ "paused": now_paused }));
        }
    }
}

/// 拉起面板 exe（S7a 落地前 exe 不存在 → 返回 false 并告警，不崩）。
pub fn open_panel() -> bool {
    open_panel_with(false)
}

/// 拉起面板 exe；`favorites` = 以「收藏视图」打开（Ctrl+Shift+P）。
///
/// 传参约定与面板 `App.OnStartup` 对齐：`--open` 请求显示，`--favorites` 追加"切到收藏筛选"。
/// 面板已在运行时新进程会转发**对应的**命名事件（收藏 → OpenFavorites 事件），
/// 由主实例切筛选 —— 否则第二次按 P 只会把面板弹出来、筛选不动。
pub fn open_panel_with(favorites: bool) -> bool {
    let mut candidates: Vec<std::path::PathBuf> = Vec::new();
    if let Ok(exe) = std::env::current_exe() {
        if let Some(dir) = exe.parent() {
            candidates.push(dir.join("BetterDesktop.Clipboard.Panel.exe"));
        }
    }
    if let Ok(appdata) = std::env::var("LOCALAPPDATA") {
        candidates.push(
            std::path::Path::new(&appdata)
                .join("BetterDesktop")
                .join("BetterDesktop.Clipboard.Panel.exe"),
        );
    }
    for path in candidates {
        if path.is_file() {
            let mut cmd = std::process::Command::new(&path);
            cmd.arg("--open");
            if favorites {
                cmd.arg("--favorites");
            }
            match cmd.spawn() {
                Ok(_) => {
                    crate::log::info(format!(
                        "open_panel: launched {:?} (favorites={favorites})",
                        path
                    ));
                    return true;
                }
                Err(e) => {
                    crate::log::warn(format!("open_panel: spawn failed {:?}: {e}", path));
                }
            }
        }
    }
    crate::log::warn("open_panel: BetterDesktop.Clipboard.Panel.exe not found (S7a 落地后生效)");
    false
}

// ---------------- 工具 ----------------

fn entry_json(e: &ClipboardEntry) -> Value {
    serde_json::to_value(e).unwrap_or(Value::Null)
}

/// 列表页 content 截断上限（**字符**数）。客户端列表只用 Preview（≤200 字）+ 元数据。
const SUMMARY_CONTENT_MAX_CHARS: usize = 1024;

/// 列表页摘要 JSON：剥离 htmlContent/rtfContent（可能是 MB 级）并按字符截断 content。
/// 详情/复制都在引擎侧完成（get_entry / copy_to_clipboard 直读 store），故对客户端零信息损失。
fn entry_summary_json(e: &ClipboardEntry) -> Value {
    let mut v = entry_json(e);
    if let Some(obj) = v.as_object_mut() {
        obj.insert("htmlContent".to_string(), json!(""));
        obj.insert("rtfContent".to_string(), json!(""));
        // 【P1-4】命名格式是 base64 二进制，绝不能进列表载荷（会撑爆 IPC）——列表侧无需它，
        // 写回由引擎侧读 store 完成（与 htmlContent 同处置）。
        obj.insert("namedFormats".to_string(), json!([]));
        let content = obj
            .get("content")
            .and_then(|c| c.as_str())
            .unwrap_or_default()
            .to_string();
        obj.insert(
            "content".to_string(),
            json!(truncate_chars(&content, SUMMARY_CONTENT_MAX_CHARS)),
        );
        // 【截图 OCR · 2026-09-14】OCR 文本进列表摘要（面板展示识别结果）；同样按字符截断防大载荷。
        // 注意：OCR 文本**不进诊断日志**（OCR 计划红线）—— 引擎无内容日志路径，仅此摘要通道。
        let ocr = obj
            .get("ocrText")
            .and_then(|c| c.as_str())
            .unwrap_or_default()
            .to_string();
        if !ocr.is_empty() {
            obj.insert("ocrText".to_string(), json!(truncate_chars(&ocr, SUMMARY_CONTENT_MAX_CHARS)));
        }
    }
    v
}

/// 按**字符**边界截断（Rust 字符串索引按字节，直接切片遇中文会 panic）。
fn truncate_chars(s: &str, max_chars: usize) -> String {
    if s.chars().count() <= max_chars {
        return s.to_string();
    }
    s.chars().take(max_chars).collect()
}

fn string_param(params: &Option<Value>, key: &str) -> Result<String, (i64, String)> {
    params
        .as_ref()
        .and_then(|p| p.get(key))
        .and_then(|v| v.as_str())
        .map(|s| s.to_string())
        .ok_or((-32602, format!("missing string param '{key}'")))
}

fn array_param(params: &Option<Value>, key: &str) -> Result<Vec<String>, (i64, String)> {
    params
        .as_ref()
        .and_then(|p| p.get(key))
        .and_then(|v| v.as_array())
        .map(|a| {
            a.iter()
                .filter_map(|v| v.as_str().map(|s| s.to_string()))
                .collect()
        })
        .ok_or((-32602, format!("missing array param '{key}'")))
}

/// 简易 UUID v4（id 仅需唯一；不依赖额外 crate）。
fn uuid_v4() -> String {
    use std::time::{SystemTime, UNIX_EPOCH};
    let now = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_nanos())
        .unwrap_or(0);
    let rand = std::process::id() as u64;
    format!(
        "{:08x}-{:04x}-4{:03x}-{:04x}-{:012x}",
        (now & 0xFFFF_FFFF) as u32,
        (now >> 32) as u16 & 0xFFFF,
        (now >> 48) as u16 & 0xFFF,
        (rand >> 16) as u16 & 0xFFFF,
        rand & 0xFFFF_FFFF_FFFF
    )
}

fn files_dir_size(dir: &std::path::Path) -> u64 {
    let mut total = 0u64;
    if let Ok(rd) = std::fs::read_dir(dir) {
        for f in rd.flatten() {
            if let Ok(m) = f.metadata() {
                total += m.len();
            }
        }
    }
    total
}

/// 按当前设置收敛一次（"过时淘汰"的**统一执行入口**，供启动与定时线程共用）。
///
/// 【2026-09-12 补】此前 `evict` 只有两个调用点：新条目入库（`on_clipboard_update` 的 `New` 分支）
/// 与 `apply_settings`。若用户长时间不复制新内容 —— 或只重复复制同一内容（走 `Updated` 分支、
/// 不触发 evict）—— **过期条目永远不会被清理**，`retention-days` 形同虚设。
/// 真机验证：逻辑本身正确（导入一条 2020-01-01 的条目 → `evicted: 1`，被正确清掉），
/// 缺的只是"什么时候跑"。
fn sweep_evict(reason: &str) {
    let snapshot = SETTINGS
        .get()
        .map(|s| s.lock().unwrap_or_else(|e| e.into_inner()).clone())
        .unwrap_or_default();
    let Some(store_lock) = STORE.get() else {
        return;
    };
    let mut store = match store_lock.lock() {
        Ok(g) => g,
        Err(e) => e.into_inner(),
    };
    let removed = store.evict(&snapshot);
    if removed.is_empty() {
        return;
    }
    crate::log::info(format!("{reason} eviction removed {} entries", removed.len()));
    if let Err(e) = store.save() {
        crate::log::error(format!("{reason} eviction save failed: {e}"));
    }
    drop(store);
    broadcast("history_changed", json!({ "kind": "evicted", "count": removed.len() }));
}

/// 定时淘汰线程：间隔 30 分钟（与旧版 legacy 面板的"每小时 DispatcherTimer"同源思路，取更短间隔）。
/// 代价极低（一次遍历 + 仅在真发生驱逐时落盘），但让 `retention-days` 不再依赖"用户是否复制了东西"。
const EVICT_INTERVAL_SECS: u64 = 30 * 60;

/// 启动定时淘汰线程（main 在 init 之后调用；init 自身先跑一次 startup 收敛）。
pub fn start_eviction_sweeper() {
    std::thread::spawn(|| loop {
        std::thread::sleep(std::time::Duration::from_secs(EVICT_INTERVAL_SECS));
        sweep_evict("periodic");
    });
    crate::log::info(format!("eviction sweeper started (every {EVICT_INTERVAL_SECS}s)"));
}

/// 后台防抖保存线程（800ms 轮询；**内容变化才落盘**，另有 60 秒强制兜底；退出/WM_ENDSESSION 立即 flush）。
pub fn start_autosave() {
    std::thread::spawn(|| {
        // 签名未变时的强制落盘间隔：防"签名漏判"导致历史长期不写盘（宁可多写一次，不可丢数据）。
        const FORCE_SAVE_INTERVAL: std::time::Duration = std::time::Duration::from_secs(60);

        let mut last_signature = u64::MAX; // 首轮必定落盘一次（等价于原行为）
        let mut last_save = std::time::Instant::now();

        loop {
            std::thread::sleep(std::time::Duration::from_millis(800));

            // 定时暂停到期 → 自动恢复（复用本周期线程，零新线程；最多延迟 800ms 恢复）
            if PAUSED.load(Ordering::Acquire) {
                let deadline = *PAUSE_UNTIL.lock().unwrap_or_else(|e| e.into_inner());
                if deadline.is_some_and(|t| std::time::Instant::now() >= t) {
                    PAUSED.store(false, Ordering::Release);
                    *PAUSE_UNTIL.lock().unwrap_or_else(|e| e.into_inner()) = None;
                    crate::log::info("temporary pause expired; auto-resumed");
                    broadcast("pause_changed", json!({ "paused": false }));
                }
            }

            // 【2026-09-18】改为签名驱动：原实现每 800ms 无条件 save()（全量序列化 + DPAPI 加密 +
            // 写临时文件 + rename）。历史数万条时那就是每秒 1.25 次的持续 CPU 与磁盘写，
            // 用户能感知为"风扇一直转、程序关了才停"。现在无变更时直接跳过，只在真的改动了
            // （入库/删除/置顶/驱逐）才落盘，并保留 60 秒强制兜底。
            if let Some(s) = STORE.get() {
                // 【2026-09-12 审计】锁中毒不得静默跳过落盘（统一 poison-safe）
                let store = s.lock().unwrap_or_else(|e| e.into_inner());
                if !store.storage_trusted() {
                    // 载入失败 → 拒绝写盘（保护磁盘原文件）；load 已记 error，此处静默跳过，避免刷屏
                    continue;
                }

                let signature = store.change_signature();
                if signature == last_signature && last_save.elapsed() < FORCE_SAVE_INTERVAL {
                    continue;
                }

                if let Err(e) = store.save() {
                    crate::log::error(format!("autosave failed: {e}"));
                    continue; // 失败不更新签名 → 下一轮重试
                }

                last_signature = signature;
                last_save = std::time::Instant::now();
            }
        }
    });
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::model::Category;

    fn entry(id: &str, kind: ItemKind, content: &str) -> ClipboardEntry {
        ClipboardEntry::new(id, kind, content)
    }

    /// 构造只带 keyword 的过滤器（其余条件缺省）。
    fn by_keyword(kw: &str) -> QueryFilter {
        QueryFilter {
            keyword: normalize_filter(Some(kw)),
            ..Default::default()
        }
    }

    #[test]
    fn keyword_hits_content_html_tags_source_and_paths() {
        let mut e = entry("a", ItemKind::Text, "Hello 世界");
        e.tags = "重要 标星".to_string();
        e.source_process_name = "wechat".to_string();

        assert!(by_keyword("hello").matches(&e), "英文大小写不敏感");
        assert!(by_keyword("世界").matches(&e));
        assert!(by_keyword("重要").matches(&e), "tags 参与匹配");
        assert!(by_keyword("WECHAT").matches(&e), "来源进程名参与匹配");
        assert!(!by_keyword("不存在xyz").matches(&e));
        assert!(by_keyword("   ").matches(&e), "空白串归一为不过滤");

        let mut f = entry("b", ItemKind::Files, "");
        f.file_paths = vec!["C:/报告/2026.docx".to_string()];
        assert!(by_keyword("报告").matches(&f), "文件路径参与匹配");

        let mut h = entry("c", ItemKind::Html, "");
        h.html_content = "<p>富文本正文</p>".to_string();
        assert!(by_keyword("富文本").matches(&h), "htmlContent 参与匹配");

        let mut o = entry("d", ItemKind::Image, "");
        o.ocr_text = "接口返回 500 错误截图".to_string();
        assert!(by_keyword("接口返回").matches(&o), "OCR 文本参与匹配（截图按内容检索）");
        assert!(by_keyword("500").matches(&o));
        assert!(!by_keyword("未命中词").matches(&o));
    }

    #[test]
    fn ocr_text_roundtrip_via_set_ocr_text_and_summary() {
        // 独立引擎实例（临时根目录）；STORE 为 OnceLock，首个 init 生效。
        let root = std::env::temp_dir().join(format!("betterdt-ocr-test-{}", std::process::id()));
        std::fs::create_dir_all(&root).ok();
        init(root, None);
        let mut e = entry("ocr1", ItemKind::Image, "");
        e.image_path = "clipboard/images/ocr1.png".to_string();
        {
            let mut store = store_guard().unwrap();
            store.upsert(e);
        }

        let resp = cmd_set_ocr_text(Some(json!({ "id": "ocr1", "ocr_text": "扫码关注 领取优惠券" })));
        assert!(resp.is_ok());

        // 摘要带 OCR 文本（面板展示）
        {
            let store = store_guard().unwrap();
            let saved = store.get_by_id("ocr1").unwrap().clone();
            assert_eq!(saved.ocr_text, "扫码关注 领取优惠券");
            let v = entry_summary_json(&saved);
            assert_eq!(v["ocrText"], json!("扫码关注 领取优惠券"));
        }

        // 清空语义：空文本 = 清空
        let resp2 = cmd_set_ocr_text(Some(json!({ "id": "ocr1", "ocr_text": "" })));
        assert!(resp2.is_ok());
        {
            let store = store_guard().unwrap();
            assert_eq!(store.get_by_id("ocr1").unwrap().ocr_text, "");
        }

        // 不存在的条目 → 错误（-32602）
        let resp3 = cmd_set_ocr_text(Some(json!({ "id": "nope", "ocr_text": "x" })));
        assert!(resp3.is_err());
        assert_eq!(resp3.unwrap_err().0, -32602);
    }

    #[test]
    fn kind_pinned_source_and_category_compose_as_and() {
        let mut e = entry("a", ItemKind::Image, "x");
        e.category = Category::Image;
        e.is_pinned = true;
        e.source_process_name = "snippingtool".to_string();

        assert!(QueryFilter { kind: Some(1), ..Default::default() }.matches(&e));
        assert!(!QueryFilter { kind: Some(0), ..Default::default() }.matches(&e));
        assert!(QueryFilter { pinned: Some(true), ..Default::default() }.matches(&e));
        assert!(!QueryFilter { pinned: Some(false), ..Default::default() }.matches(&e));
        assert!(QueryFilter { category: Some(3), ..Default::default() }.matches(&e));
        assert!(QueryFilter { source_app: normalize_filter(Some("SNIP")), ..Default::default() }.matches(&e));
        assert!(!QueryFilter { source_app: normalize_filter(Some("wechat")), ..Default::default() }.matches(&e));

        // 多条件取「与」：全命中 vs 任一不命中
        let all_hit = QueryFilter {
            kind: Some(1),
            pinned: Some(true),
            source_app: normalize_filter(Some("snip")),
            ..Default::default()
        };
        assert!(all_hit.matches(&e));
        let conflict = QueryFilter {
            kind: Some(1),
            pinned: Some(false),
            ..Default::default()
        };
        assert!(!conflict.matches(&e), "kind 命中但 pinned 不命中 → 整体不命中");
    }

    #[test]
    fn summary_strips_big_fields_and_truncates_content_by_chars() {
        let big = "中".repeat(SUMMARY_CONTENT_MAX_CHARS + 50);
        let mut e = entry("a", ItemKind::Html, &big);
        e.html_content = "x".repeat(1_000_000);
        e.rtf_content = "{\\rtf1 ...}".to_string();

        let v = entry_summary_json(&e);
        assert_eq!(v["htmlContent"], json!(""), "大字段必须剥离");
        assert_eq!(v["rtfContent"], json!(""));
        let content = v["content"].as_str().unwrap();
        // 中文按字符截断（按字节切片会 panic）
        assert_eq!(content.chars().count(), SUMMARY_CONTENT_MAX_CHARS);
        // 元数据保持完整（列表要显示类型/来源/时间/收藏）
        assert_eq!(v["contentType"], json!(3));
        assert_eq!(v["id"], json!("a"));
        assert!(v.get("timestamp").is_some());

        // 短内容原样返回
        let short = entry("b", ItemKind::Text, "短文本");
        assert_eq!(entry_summary_json(&short)["content"], json!("短文本"));
    }

    #[test]
    fn effective_content_len_gates_empty_data() {
        assert_eq!(effective_content_len(&Snapshot::default()), 0, "全空快照 → 不录");

        // ① 有字节但无内容：这正是"按字节数判空"漏掉的一类（纯空白 / 空壳 HTML）
        let mut blank = Snapshot::default();
        blank.text = "   \n\t  ".to_string();
        assert!(
            !blank.text.is_empty() && effective_content_len(&blank) == 0,
            "纯空白文本：字节数 > 0 但有效长度为 0"
        );

        let mut shell = Snapshot::default();
        shell.html = "<html><body></body></html>".to_string();
        assert!(
            !shell.html.is_empty() && effective_content_len(&shell) == 0,
            "空壳 HTML：字节数 > 0 但有效长度为 0"
        );

        // ② 无字节但有内容：这是"按字节数判空"会误杀的一类（图片 / paths-only 的文件条目）
        let mut img = Snapshot::default();
        img.image_png = Some(vec![1, 2, 3]);
        assert_eq!(effective_content_len(&img), 1, "图片条目（文本字段全空）必须保留");

        let mut files = Snapshot::default();
        files.files = vec!["C:/a.txt".to_string()];
        assert_eq!(effective_content_len(&files), 1, "文件条目（paths-only 下字节数为 0）必须保留");

        // 正常内容
        let mut txt = Snapshot::default();
        txt.text = "有内容".to_string();
        assert_eq!(effective_content_len(&txt), 3);

        let mut rtf = Snapshot::default();
        rtf.rtf = "{\\rtf1 x}".to_string();
        assert!(effective_content_len(&rtf) > 0);

        let mut img_html = Snapshot::default();
        img_html.html = "<html><body><img src='a.png'></body></html>".to_string();
        assert!(effective_content_len(&img_html) > 0, "只含图/表的 HTML 不算空");
    }
}
