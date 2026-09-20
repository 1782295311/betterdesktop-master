//! 引擎核心：全局状态（Settings / 运行时状态）+ JSON-RPC 方法分派。
//!
//! 已实现：`ping` / `status` / `apply_settings` / `shutdown`（M1）+ `list_apps`（M2 应用索引）
//! + `search_files`（M3 文件索引）+ `get_icons`（M4 图标）。
//!
//! **未知方法一律显式返回 -32601**（而不是返回空集）：空集会让消费者静默认为
//! 「系统里没有程序/文件」，比明确报错难排查得多（M10 纪律：禁止静默降级）。

use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Mutex, OnceLock};
use std::time::Instant;

use base64::Engine;
use serde_json::{json, Value};

/// base64 引擎（与剪贴板引擎同一选择：STANDARD）。
const BASE64: base64::engine::general_purpose::GeneralPurpose =
    base64::engine::general_purpose::STANDARD;

use crate::model::IndexStatus;
use crate::settings::{apply_patch, Settings, SettingsPatch};

pub const ERR_INVALID_PARAMS: i64 = -32602;
pub const ERR_METHOD_NOT_FOUND: i64 = -32601;
pub const ERR_INTERNAL: i64 = -32603;

/// `search_files` 缺省返回条数（消费者通常还会按自己的限流再截断）。
const DEFAULT_FILE_LIMIT: usize = 200;
/// `search_files` 允许的最大返回条数（防御消费者传入超大 limit 造成长阻塞）。
const MAX_FILE_LIMIT: usize = 1000;

/// `get_icons` 单次允许的最大键数。
///
/// 分批取是**契约要求**：消费者按可见区分批（复刻 503「可见区优先分批并发」），
/// 一次全量（应用提取器上千项）会让单帧膨胀到几十 MB 并长时间占住 IPC 线程。
const MAX_ICON_BATCH: usize = 64;

/// 单个图标字节上限：超出则**跳过并计入 missing**（异常大的图不进 IPC 帧）。
/// 正常 256px PNG 是 5–40KB，1MB 已经是两个数量级的异常值。
const MAX_ICON_BYTES: usize = 1024 * 1024;

/// 当前生效配置（apply_settings 可就地更新；消费者读快照）。
pub static SETTINGS: OnceLock<Mutex<Settings>> = OnceLock::new();

/// 引擎启动时刻（`status.uptimeSeconds`）。
static STARTED_AT: OnceLock<Instant> = OnceLock::new();

/// 构建计数（M2/M3 起由索引构建任务写入；M1 恒为 0）。
pub static APP_COUNT: AtomicU64 = AtomicU64::new(0);
pub static FILE_COUNT: AtomicU64 = AtomicU64::new(0);
static LAST_BUILD_MS: AtomicU64 = AtomicU64::new(0);

/// 补扫间隔（秒）：到期由 `main.rs` 的轮询线程触发重建（M3c）。
/// 300s = 5 分钟——对「新下载的文件要能搜到」足够及时，而重建开销（真机 ≈2.4s / 76k 条目）不会成为负担。
pub const RESCAN_INTERVAL_SECS: u64 = 300;

/// 上次构建完成时刻（Unix 毫秒；0 = 尚未构建）。
static LAST_BUILD_AT_MS: AtomicU64 = AtomicU64::new(0);
static BUILDING: std::sync::atomic::AtomicBool = std::sync::atomic::AtomicBool::new(false);
/// 降级原因（**按索引分开**：应用索引与文件索引互不牵连）。
///
/// 【2026-09-14 修复】此前是**单一全局标志**，而它只由文件索引的「触顶截断」写入 ——
/// 应用索引自身的失败（无可用根目录 / 一条都没扫到）根本没有出口，`list_apps` 会返回
/// `{apps: [], degraded: false}`，消费者（C#）据此把"没扫到"当成"系统里没有应用"。
/// 拆成两路后：`list_apps` 只报应用索引的降级、`search_files` 只报文件索引的降级、
/// `status` 报两者并集。
///
/// 为什么**不能**继续合并成一个标志：文件索引触顶（`maxEntries` 截断）与应用索引好坏无关，
/// 若把两者合并，消费者一旦按 `degraded` 回退，就会因为"文件索引触顶"而**永久绕过引擎的
/// 应用路径**（M2 功能静默失效，且结果看起来一样，无人察觉）。
static APP_DEGRADED: Mutex<Option<String>> = Mutex::new(None);
static FILE_DEGRADED: Mutex<Option<String>> = Mutex::new(None);

/// 初始化全局状态：配置 + 启动时刻。
pub fn init(settings_path: Option<&std::path::Path>) {
    let settings = crate::settings::load(settings_path);
    crate::log::info(format!(
        "settings: backend={} maxEntries={} enabled={} scanRoots={}",
        settings.app_source_backend,
        settings.max_entries,
        settings.enabled,
        settings.scan_roots.len()
    ));
    let _ = SETTINGS.set(Mutex::new(settings));
    let _ = STARTED_AT.set(Instant::now());
}

/// 读取当前配置的克隆（锁中毒取回内部值——配置不可因一次 panic 变成不可读）。
pub fn settings_snapshot() -> Settings {
    SETTINGS
        .get_or_init(|| Mutex::new(Settings::default()))
        .lock()
        .unwrap_or_else(|e| e.into_inner())
        .clone()
}

/// 标记**应用索引**降级（None = 恢复正常）。降级必须可见：同时落日志。
pub fn set_app_degraded(reason: Option<String>) {
    set_degraded_slot(&APP_DEGRADED, "app-index", reason);
}

/// 标记**文件索引**降级（None = 恢复正常）。降级必须可见：同时落日志。
pub fn set_file_degraded(reason: Option<String>) {
    set_degraded_slot(&FILE_DEGRADED, "file-index", reason);
}

fn set_degraded_slot(slot: &Mutex<Option<String>>, label: &str, reason: Option<String>) {
    let mut guard = slot.lock().unwrap_or_else(|e| e.into_inner());
    match &reason {
        Some(r) => crate::log::warn(format!("{label} degraded: {r}")),
        None => {
            if guard.is_some() {
                crate::log::info(format!("{label} degradation cleared"));
            }
        }
    }
    *guard = reason;
}

/// 应用索引的降级原因（`list_apps` 上报）。
fn app_degraded() -> Option<String> {
    APP_DEGRADED.lock().unwrap_or_else(|e| e.into_inner()).clone()
}

/// 文件索引的降级原因（`search_files` 上报）。
fn file_degraded() -> Option<String> {
    FILE_DEGRADED.lock().unwrap_or_else(|e| e.into_inner()).clone()
}

/// 两路降级的并集（`status` 上报；原因串以「；」连接）。
fn merged_degraded() -> Option<String> {
    match (app_degraded(), file_degraded()) {
        (Some(a), Some(f)) => Some(format!("{a}；{f}")),
        (Some(a), None) => Some(a),
        (None, f) => f,
    }
}

/// 记录一次构建结果（索引构建任务调用）。
pub fn record_build(app_count: usize, file_count: usize, elapsed_ms: u64) {
    APP_COUNT.store(app_count as u64, Ordering::Release);
    FILE_COUNT.store(file_count as u64, Ordering::Release);
    LAST_BUILD_MS.store(elapsed_ms, Ordering::Release);
    LAST_BUILD_AT_MS.store(now_unix_ms(), Ordering::Release);
}

/// 是否已到补扫时间。**未构建过时返回 false**——首次构建本身就会刷新，不必再触发一次。
pub fn needs_rescan() -> bool {
    let last = LAST_BUILD_AT_MS.load(Ordering::Acquire);
    if last == 0 {
        return false;
    }

    now_unix_ms().saturating_sub(last) >= RESCAN_INTERVAL_SECS * 1000
}

fn now_unix_ms() -> u64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_millis() as u64)
        .unwrap_or(0)
}

/// 构建进行中标记（构建期消费者应回退本地实现，不得阻塞等待）。
pub fn set_building(building: bool) {
    BUILDING.store(building, Ordering::Release);
}

/// 组装状态快照。
pub fn status() -> IndexStatus {
    // status 上报**两路并集**（任一路降级都要让设置中心的状态行看得见）
    let degraded = merged_degraded();
    // 【2026-09-14 修复】一次取锁取回「条目数 + 字节数」：此前连调两次 `cache_stats()`
    //（各自取锁），两次之间可能有并发写入 → 上报的两个数字来自不同瞬间，快照自相矛盾。
    let icon_stats = crate::icon::cache_stats();
    IndexStatus {
        version: env!("CARGO_PKG_VERSION").to_string(),
        pid: unsafe { windows::Win32::System::Threading::GetCurrentProcessId() },
        uptime_seconds: STARTED_AT
            .get()
            .map(|t| t.elapsed().as_secs())
            .unwrap_or(0),
        building: BUILDING.load(Ordering::Acquire),
        app_count: APP_COUNT.load(Ordering::Acquire) as usize,
        file_count: FILE_COUNT.load(Ordering::Acquire) as usize,
        last_build_ms: LAST_BUILD_MS.load(Ordering::Acquire),
        last_build_at_ms: LAST_BUILD_AT_MS.load(Ordering::Acquire),
        degraded: degraded.is_some(),
        degrade_reason: degraded,
        rss_bytes: rss_bytes(),
        // 图标缓存占用随取图增长，必须可见（§5.4：引擎自管内存，设置中心要看得到）。
        icon_cache_entries: icon_stats.0,
        icon_cache_bytes: icon_stats.1 as u64,
    }
}

/// 进程工作集（字节）。采集失败返回 0（不阻塞 status）。
fn rss_bytes() -> u64 {
    use windows::Win32::System::ProcessStatus::{GetProcessMemoryInfo, PROCESS_MEMORY_COUNTERS};
    use windows::Win32::System::Threading::GetCurrentProcess;
    unsafe {
        let mut counters = PROCESS_MEMORY_COUNTERS::default();
        let cb = std::mem::size_of::<PROCESS_MEMORY_COUNTERS>() as u32;
        match GetProcessMemoryInfo(GetCurrentProcess(), &mut counters, cb) {
            Ok(()) => counters.WorkingSetSize as u64,
            Err(e) => {
                crate::log::warn(format!("GetProcessMemoryInfo failed: {e}"));
                0
            }
        }
    }
}

// ===== 空闲即退（2026-09-17 内存预算 · 用户定调"本质是工作不是灯泡"） =====
//
// 索引引擎私有内存 66MB（实测），无条件常驻纯属浪费：**无人检索就退场**，
// 下次检索由 .NET 侧 IndexEngineLauncher.EnsureEngine 按需拉起（重启后首个 build 的成本用户可接受，
// 换来的是"平时零占用"）。
//
// 判据：**只有 IPC 请求算活动**（见 ipc.rs 的 touch_activity 调用点）；
// 后台周期补扫**故意不算** —— 否则永远"非空闲"，退场机制形同不存在。

/// 最后一次 IPC 活动时刻（epoch 秒；0 = 尚无请求）。
static LAST_ACTIVITY_SECS: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);

/// 引擎启动时刻（epoch 秒）——空闲判定的兜底基线（起了之后没人来连，同样该退）。
static STARTED_AT_SECS: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);

fn now_secs() -> u64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_secs())
        .unwrap_or(0)
}

/// 初始化空闲基线（main 启动时调一次）。
pub fn init_idle_clock() {
    STARTED_AT_SECS.store(now_secs(), std::sync::atomic::Ordering::Relaxed);
}

/// 记一次 IPC 活动（ipc.rs 每帧请求调用）。
pub fn touch_activity() {
    LAST_ACTIVITY_SECS.store(now_secs(), std::sync::atomic::Ordering::Relaxed);
}

/// 已空闲秒数（距最后一次 IPC 请求）。
pub fn idle_secs() -> u64 {
    let last = LAST_ACTIVITY_SECS.load(std::sync::atomic::Ordering::Relaxed);
    let base = if last > 0 {
        last
    } else {
        STARTED_AT_SECS.load(std::sync::atomic::Ordering::Relaxed)
    };
    now_secs().saturating_sub(base)
}

/// 请求优雅退出：给隐藏窗口投递 WM_CLOSE（主线程消息循环收到后销毁窗口 → 退出循环）。
///
/// 返回 `true` = 已成功投递。**调用方必须据此判断成败**（2026-09-14 修复：此前返回 `()`，
/// 投递失败只记日志，`dispatch("shutdown")` 却恒回 `ok: true` —— 把失败当成功，
/// 调用方以为"已请求退出"而不再兜底）。
pub fn request_shutdown() -> bool {
    match crate::HIDDEN_WINDOW.get() {
        Some(&hwnd) => {
            use windows::Win32::Foundation::{HWND, LPARAM, WPARAM};
            use windows::Win32::UI::WindowsAndMessaging::{PostMessageW, WM_CLOSE};
            let hwnd = HWND(hwnd as *mut _);
            match unsafe { PostMessageW(hwnd, WM_CLOSE, WPARAM(0), LPARAM(0)) } {
                Ok(()) => true,
                Err(e) => {
                    crate::log::error(format!("PostMessageW(WM_CLOSE) failed: {e}"));
                    false
                }
            }
        }
        None => {
            crate::log::warn("shutdown requested before hidden window existed");
            false
        }
    }
}

/// JSON-RPC 方法分派。Err = (错误码, 错误消息)。
pub fn dispatch(method: &str, params: Option<Value>) -> Result<Value, (i64, String)> {
    match method {
        "ping" => Ok(json!({
            "pong": true,
            "version": env!("CARGO_PKG_VERSION"),
        })),
        "status" => serde_json::to_value(status())
            .map_err(|e| (ERR_INTERNAL, format!("status 序列化失败：{e}"))),
        "apply_settings" => {
            let patch: SettingsPatch = match params {
                Some(v) if v.is_null() => SettingsPatch::default(),
                Some(v) => serde_json::from_value(v)
                    .map_err(|e| (ERR_INVALID_PARAMS, format!("apply_settings 参数非法：{e}")))?,
                None => SettingsPatch::default(),
            };
            let mut guard = SETTINGS
                .get_or_init(|| Mutex::new(Settings::default()))
                .lock()
                .unwrap_or_else(|e| e.into_inner());
            match apply_patch(&mut guard, &patch) {
                Ok(()) => {
                    crate::log::info(format!(
                        "apply_settings: backend={} maxEntries={} enabled={}",
                        guard.app_source_backend, guard.max_entries, guard.enabled
                    ));
                    Ok(json!({ "ok": true }))
                }
                Err(msg) => Err((ERR_INVALID_PARAMS, msg)),
            }
        }
        "shutdown" => {
            crate::log::info("shutdown requested via IPC");
            // 【2026-09-14 修复】投递失败必须报错：隐藏窗口尚未创建（启动竞态）或
            // PostMessageW 失败时，退出请求根本没送到，回 ok:true 会让调用方不再兜底。
            if request_shutdown() {
                Ok(json!({ "ok": true }))
            } else {
                Err((
                    ERR_INTERNAL,
                    "退出请求未能投递（隐藏窗口未就绪或 PostMessageW 失败）".to_string(),
                ))
            }
        }
        // M2：应用索引快照（raw candidate；消费者经 C# 升格为 AppItem）。
        // 携带 building/degraded 以便消费者在「构建中/降级」时回退本地实现，不阻塞等待。
        "list_apps" => {
            let apps = crate::appindex::snapshot();
            let building = BUILDING.load(Ordering::Acquire);
            // 只报**应用索引**的降级：消费者据此决定"是否回退本地应用扫描"（不得被文件索引牵连）
            let degraded = app_degraded();
            serde_json::to_value(json!({
                "apps": apps,
                "count": apps.len(),
                "building": building,
                "degraded": degraded.is_some(),
                "degradeReason": degraded,
            }))
            .map_err(|e| (ERR_INTERNAL, format!("list_apps 序列化失败：{e}")))
        }
        // M3：文件索引查询。只返回**文件事实**（路径/名称/大小/修改时间）——
        // 不排序、不分类（排名仍在 C# `ScoreFileName`，计划 §5.6）。
        // 随附 building/degraded：构建中或降级时消费者回退本地搜索（Windows Search + 文件系统兜底）。
        "search_files" => {
            #[derive(serde::Deserialize)]
            #[serde(rename_all = "camelCase")]
            struct SearchParams {
                query: String,
                #[serde(default)]
                limit: Option<usize>,
            }

            let parsed: SearchParams = match params {
                Some(v) if !v.is_null() => serde_json::from_value(v)
                    .map_err(|e| (ERR_INVALID_PARAMS, format!("search_files 参数非法：{e}")))?,
                _ => {
                    return Err((
                        ERR_INVALID_PARAMS,
                        "search_files 需要参数 { query, limit? }".to_string(),
                    ));
                }
            };

            let limit = parsed
                .limit
                .unwrap_or(DEFAULT_FILE_LIMIT)
                .clamp(1, MAX_FILE_LIMIT);
            let files = crate::fileindex::search(&parsed.query, limit);
            let building = BUILDING.load(Ordering::Acquire);
            // 只报**文件索引**的降级（与 C# `TrySearchFilesAsync` 的 Building||Degraded 对应）
            let degraded = file_degraded();
            serde_json::to_value(json!({
                "files": files,
                "count": files.len(),
                "building": building,
                "degraded": degraded.is_some(),
                "degradeReason": degraded,
            }))
            .map_err(|e| (ERR_INTERNAL, format!("search_files 序列化失败：{e}")))
        }
        // M4：图标字节（**一档 256×256 PNG**；2026-09-14 用户硬约束：不做「用多大就申请多大」的多档）。
        //
        // 传输形态 = base64 in JSON 行：与本仓**既有二进制搬运做法完全一致**（剪贴板引擎的
        // `NamedFormat.data_base64` / html data URI 均如此），且它那条纪律同样适用 ——
        // 「base64 二进制绝不能进列表载荷」：故本方法**按需单独调用**，绝不塞进 `list_apps`
        //（一次 2119 条会把 IPC 撑爆）。
        //
        // 失败/超限的键**不出现在返回里**，由 `missing` 计数暴露（不静默），调用方据此走 glyph 兜底。
        "get_icons" => {
            #[derive(serde::Deserialize)]
            #[serde(rename_all = "camelCase")]
            struct IconParams {
                keys: Vec<String>,
                /// `true` = 先清空图标缓存再取。
                /// **为什么必需**：缓存键是路径，而「应用更新 / 换了图标」时路径通常不变 ——
                /// 没有这个入口，旧图标会在常驻进程里永久驻留。
                #[serde(default)]
                refresh: bool,
            }

            let parsed: IconParams = match params {
                Some(v) if !v.is_null() => serde_json::from_value(v)
                    .map_err(|e| (ERR_INVALID_PARAMS, format!("get_icons 参数非法：{e}")))?,
                _ => {
                    return Err((
                        ERR_INVALID_PARAMS,
                        "get_icons 需要参数 { keys: [...] }".to_string(),
                    ));
                }
            };

            if parsed.keys.is_empty() {
                return Err((ERR_INVALID_PARAMS, "get_icons 的 keys 不能为空".to_string()));
            }
            if parsed.keys.len() > MAX_ICON_BATCH {
                return Err((
                    ERR_INVALID_PARAMS,
                    format!("get_icons 单次最多 {MAX_ICON_BATCH} 个键（请分批取）"),
                ));
            }

            if parsed.refresh {
                crate::icon::clear_cache();
                crate::log::info("icon cache cleared by refresh request");
            }

            let mut icons: Vec<Value> = Vec::with_capacity(parsed.keys.len());
            let mut missing = 0usize;
            for key in parsed.keys {
                match crate::icon::png_bytes(&key) {
                    Some(png) if png.len() <= MAX_ICON_BYTES => {
                        icons.push(json!({
                            "key": key,
                            "pngBase64": BASE64.encode(&png),
                        }));
                    }
                    Some(png) => {
                        crate::log::warn(format!(
                            "icon skipped (too large): {key} ({} bytes)",
                            png.len()
                        ));
                        missing += 1;
                    }
                    None => missing += 1,
                }
            }

            Ok(json!({
                "icons": icons,
                "count": icons.len(),
                "missing": missing,
            }))
        }
        // 未知方法：显式报错，绝不返回空集（见模块头注释）
        other => {
            crate::log::warn(format!("unimplemented method: {other}"));
            Err((ERR_METHOD_NOT_FOUND, format!("Method not found: {other}")))
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ping_returns_pong() {
        let v = dispatch("ping", None).unwrap();
        assert_eq!(v["pong"], true);
        assert!(v["version"].is_string());
    }

    #[test]
    fn status_has_all_contract_fields() {
        let v = dispatch("status", None).unwrap();
        for key in [
            "version",
            "pid",
            "uptimeSeconds",
            "building",
            "appCount",
            "fileCount",
            "lastBuildMs",
            "lastBuildAtMs",
            "degraded",
            "degradeReason",
            "rssBytes",
        ] {
            assert!(v.get(key).is_some(), "status 缺少字段 {key}");
        }
    }

    /// M3c 补扫判定：刚构建完不得立即再扫；未构建过也不扫（首次构建本身会刷新）。
    #[test]
    fn needs_rescan_is_false_right_after_build() {
        record_build(1, 2, 3);
        assert!(!needs_rescan(), "刚构建完不应立即触发补扫");
    }

    /// 补扫间隔必须是个正数（否则轮询线程会变成忙循环）。
    #[test]
    fn rescan_interval_is_sane() {
        assert!(RESCAN_INTERVAL_SECS >= 60, "补扫间隔过短会退化成持续重建");
        assert!(RESCAN_INTERVAL_SECS <= 3600, "补扫间隔过长会让新文件迟迟搜不到");
    }

    #[test]
    fn apply_settings_rejects_invalid_backend_with_invalid_params() {
        let err = dispatch(
            "apply_settings",
            Some(json!({ "app-source-backend": "bogus" })),
        )
        .unwrap_err();
        assert_eq!(err.0, ERR_INVALID_PARAMS);
        assert!(err.1.contains("bogus"));
    }

    #[test]
    fn apply_settings_accepts_valid_backend() {
        let v = dispatch(
            "apply_settings",
            Some(json!({ "app-source-backend": "local" })),
        )
        .unwrap();
        assert_eq!(v["ok"], true);
        assert_eq!(settings_snapshot().app_source_backend, "local");
        // 复位，避免影响其它用例（测试同进程共享全局状态）
        let _ = dispatch(
            "apply_settings",
            Some(json!({ "app-source-backend": "engine" })),
        );
    }

    /// 未知方法必须明确报错，不得返回空集（静默降级禁令）。
    #[test]
    fn unknown_methods_report_method_not_found() {
        for m in ["no_such_method", "get_icons_v2", ""] {
            let err = dispatch(m, None).unwrap_err();
            assert_eq!(err.0, ERR_METHOD_NOT_FOUND, "{m} 必须返回 -32601");
        }
    }

    /// get_icons 缺 keys 必须报 -32602。
    /// 若静默返回空集，消费者会读成「这些应用都没有图标」→ 全部退化成占位字形，且无人能排查。
    #[test]
    fn get_icons_requires_keys() {
        assert_eq!(
            dispatch("get_icons", None).unwrap_err().0,
            ERR_INVALID_PARAMS
        );
        assert_eq!(
            dispatch("get_icons", Some(json!({}))).unwrap_err().0,
            ERR_INVALID_PARAMS
        );
        assert_eq!(
            dispatch("get_icons", Some(json!({ "keys": [] }))).unwrap_err().0,
            ERR_INVALID_PARAMS
        );
    }

    /// 超过分批上限必须报 -32602（一次上千键会撑爆单帧并长时间占住 IPC 线程）。
    #[test]
    fn get_icons_rejects_oversized_batch() {
        let keys: Vec<String> = (0..=MAX_ICON_BATCH).map(|i| format!("k{i}")).collect();
        assert_eq!(
            dispatch("get_icons", Some(json!({ "keys": keys })))
                .unwrap_err()
                .0,
            ERR_INVALID_PARAMS
        );
    }

    /// get_icons 返回契约：icons/count/missing；提取失败的键**不进 icons**，由 missing 暴露。
    #[test]
    fn get_icons_has_contract_fields_and_reports_missing() {
        let v = dispatch(
            "get_icons",
            Some(json!({ "keys": [r"C:\__no_such_app__.exe"] })),
        )
        .unwrap();
        assert!(v.get("icons").is_some_and(|i| i.is_array()));
        assert_eq!(v["count"], 0);
        assert_eq!(v["missing"], 1, "提取失败必须计入 missing，不能静默当成功");
    }

    /// 真机：系统自带 exe 取回的必须是可解码的 256×256 PNG（跨语言线格式实证）。
    #[test]
    fn get_icons_returns_decodable_256_png() {
        let path = r"C:\Windows\System32\notepad.exe";
        if !std::path::Path::new(path).exists() {
            return;
        }

        crate::icon::clear_cache();
        let v = dispatch("get_icons", Some(json!({ "keys": [path] }))).unwrap();
        assert_eq!(v["count"], 1, "系统自带 exe 必须取到图标");

        let b64 = v["icons"][0]["pngBase64"]
            .as_str()
            .expect("必须带 pngBase64 字段");
        let bytes = BASE64.decode(b64).expect("base64 必须可解码");
        assert!(bytes.starts_with(&[0x89, b'P', b'N', b'G']), "必须是 PNG magic");

        let decoded = image::load_from_memory(&bytes).expect("PNG 必须可解码");
        assert_eq!(decoded.width(), crate::icon::ICON_SIZE as u32);
        assert_eq!(decoded.height(), crate::icon::ICON_SIZE as u32);
    }

    /// `refresh: true` 必须先清缓存再取（应用更新后路径不变但图标已变 —— 否则永久发旧图）。
    #[test]
    fn get_icons_refresh_bypasses_cache() {
        let path = r"C:\Windows\System32\notepad.exe";
        if !std::path::Path::new(path).exists() {
            return;
        }

        crate::icon::clear_cache();
        let _ = dispatch("get_icons", Some(json!({ "keys": [path] }))).unwrap();
        assert_eq!(crate::icon::cache_stats().0, 1, "首次取图应落缓存");

        let v = dispatch(
            "get_icons",
            Some(json!({ "keys": [path], "refresh": true })),
        )
        .unwrap();
        assert_eq!(v["count"], 1, "refresh 之后仍必须取到图标");
        assert_eq!(
            crate::icon::cache_stats().0,
            1,
            "refresh = 先清空再重填 → 缓存仍只有一份"
        );
    }

    /// status 必须上报图标缓存占用（§5.4 内存治理可见性）。
    #[test]
    fn status_reports_icon_cache_usage() {
        let v = dispatch("status", None).unwrap();
        assert!(v.get("iconCacheEntries").is_some());
        assert!(v.get("iconCacheBytes").is_some());
    }

    /// search_files 返回契约：files/count/building/degraded/degradeReason，且 files 为数组。
    #[test]
    fn search_files_has_contract_fields() {
        let v = dispatch("search_files", Some(json!({ "query": "zzz-no-such-file" }))).unwrap();
        assert!(v.get("files").is_some_and(|f| f.is_array()));
        assert!(v.get("count").is_some());
        assert!(v.get("building").is_some());
        assert!(v.get("degraded").is_some());
        assert!(v.get("degradeReason").is_some());
    }

    /// 缺 query 参数必须报 -32602（不得静默返回空集）。
    #[test]
    fn search_files_requires_query() {
        assert_eq!(dispatch("search_files", None).unwrap_err().0, ERR_INVALID_PARAMS);
        assert_eq!(
            dispatch("search_files", Some(json!({}))).unwrap_err().0,
            ERR_INVALID_PARAMS
        );
    }

    /// 非法参数类型必须报 -32602。
    #[test]
    fn search_files_rejects_invalid_params() {
        let err = dispatch("search_files", Some(json!({ "query": 123 }))).unwrap_err();
        assert_eq!(err.0, ERR_INVALID_PARAMS);
    }

    /// 空查询返回空集（不是全量）——全量回传会瞬间打爆跨进程帧。
    #[test]
    fn search_files_empty_query_returns_empty() {
        let v = dispatch("search_files", Some(json!({ "query": "   " }))).unwrap();
        assert_eq!(v["count"], 0);
    }

    /// list_apps 返回契约：含 apps/count/building/degraded/degradeReason，且 apps 为数组。
    #[test]
    fn list_apps_has_contract_fields() {
        let v = dispatch("list_apps", None).unwrap();
        assert!(v.get("apps").is_some_and(|a| a.is_array()));
        assert!(v.get("count").is_some());
        assert!(v.get("building").is_some());
        assert!(v.get("degraded").is_some());
        assert!(v.get("degradeReason").is_some());
    }

    /// 降级位往返 + **按索引隔离**（2026-09-14）。
    ///
    /// 隔离的意义：消费者按 `degraded` 决定"是否回退本地实现"。若两路合并成一个标志，
    /// "文件索引触顶"会把应用索引也判成降级 → 消费者**永久绕过引擎的应用路径**
    ///（M2 功能静默失效，而结果看起来一样，无人察觉）；反之应用索引失败也不该让文件检索回退。
    ///
    /// 注：本用例是全局降级位的**唯一**写入者（其余用例只断言字段存在、不读值），故无并发竞争。
    #[test]
    fn degraded_flag_round_trips_and_is_scoped_per_index() {
        // ① 只有应用索引降级
        set_app_degraded(Some("应用索引为空".into()));
        let s = status();
        assert!(s.degraded, "status 取并集：任一路降级都要可见");
        assert_eq!(s.degrade_reason.as_deref(), Some("应用索引为空"));
        assert_eq!(dispatch("list_apps", None).unwrap()["degraded"], true);
        assert_eq!(
            dispatch("search_files", Some(json!({ "query": "x" }))).unwrap()["degraded"],
            false,
            "应用索引降级不得牵连文件索引"
        );

        // ② 只有文件索引降级
        set_app_degraded(None);
        set_file_degraded(Some("文件索引触顶".into()));
        assert_eq!(
            dispatch("list_apps", None).unwrap()["degraded"],
            false,
            "文件索引降级不得牵连应用索引（否则应用路径会被永久绕过）"
        );
        assert_eq!(
            dispatch("search_files", Some(json!({ "query": "x" }))).unwrap()["degraded"],
            true
        );

        // ③ 两路同时降级 → status 原因串并集
        set_app_degraded(Some("应用".into()));
        set_file_degraded(Some("文件".into()));
        assert_eq!(status().degrade_reason.as_deref(), Some("应用；文件"));

        // ④ 全部恢复
        set_app_degraded(None);
        set_file_degraded(None);
        assert!(!status().degraded);
        assert!(status().degrade_reason.is_none());
    }

    /// 构建中标记：构建期消费者据此回退本地实现（计划 §9 风险缓解）。
    #[test]
    fn building_flag_round_trips() {
        assert!(!status().building);
        set_building(true);
        assert!(status().building);
        set_building(false);
        assert!(!status().building);
    }

    /// 构建结果落账（M2/M3 写入 → status 反映）。
    #[test]
    fn record_build_reflects_in_status() {
        record_build(123, 4567, 890);
        let s = status();
        assert_eq!(s.app_count, 123);
        assert_eq!(s.file_count, 4567);
        assert_eq!(s.last_build_ms, 890);
        record_build(0, 0, 0);
    }
}
