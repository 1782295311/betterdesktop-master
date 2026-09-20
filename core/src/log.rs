//! core 独立日志：`%LOCALAPPDATA%\BetterDesktop\logs\core-yyyyMMdd.log`。
//!
//! 移植自 `engine/src/log.rs`（同一仓库已验证的写法），差异只有两点：
//!   1. 文件名带日期（core 是长期常驻进程，单文件会无限增长）；
//!   2. 按日切换（写每一行前比对日期，跨日自动换文件）。
//!
//! 纪律：**锁中毒不得让日志永久静默**（诊断能力是排查一切问题的地基）→ 全部 `unwrap_or_else(|e| e.into_inner())`。

use std::fs::{File, OpenOptions};
use std::io::Write;
use std::path::PathBuf;
use std::sync::Mutex;
use std::sync::atomic::{AtomicBool, Ordering};

static LOG: Mutex<Option<(String, File)>> = Mutex::new(None);

/// 是否已由 [`init`] 显式开启。
///
/// 【为什么需要这个开关】单测会触发 `components` / `settings` 的告警路径；若日志在首次写入时
/// **惰性自开**，跑一次 `cargo test` 就会往**生产日志**（`%LOCALAPPDATA%\...\core-*.log`）里
/// 灌测试噪声，污染真机排查。故只有进程入口调用 `init()` 后才真正落盘。
static ENABLED: AtomicBool = AtomicBool::new(false);

/// 初始化日志文件（追加写）。失败时静默降级为 no-op（日志不可用不阻塞 core 启动）。
pub fn init() {
    let mut guard = LOG.lock().unwrap_or_else(|e| e.into_inner());
    *guard = open_for(&today_stamp());
    ENABLED.store(true, Ordering::Relaxed);
}

pub fn info<S: AsRef<str>>(msg: S) {
    write_line("INFO", msg.as_ref());
}

pub fn warn<S: AsRef<str>>(msg: S) {
    write_line("WARN", msg.as_ref());
}

pub fn error<S: AsRef<str>>(msg: S) {
    write_line("ERROR", msg.as_ref());
}

fn write_line(level: &str, msg: &str) {
    if !ENABLED.load(Ordering::Relaxed) {
        return;
    }
    let today = today_stamp();
    let mut guard = LOG.lock().unwrap_or_else(|e| e.into_inner());
    // 跨日切换：日期变了就重开文件（长常驻进程必须按日切）
    let needs_rotate = matches!(guard.as_ref(), Some((d, _)) if d != &today);
    if guard.is_none() || needs_rotate {
        *guard = open_for(&today);
    }
    let ts = timestamp();
    let line = format!("[{ts}] [{level}] {msg}\r\n");
    if let Some((_, f)) = guard.as_mut() {
        let _ = f.write_all(line.as_bytes());
        let _ = f.flush();
    }
}

fn open_for(stamp: &str) -> Option<(String, File)> {
    let path = log_path(stamp);
    if let Some(dir) = path.parent() {
        let _ = std::fs::create_dir_all(dir);
    }
    OpenOptions::new()
        .create(true)
        .append(true)
        .open(path)
        .ok()
        .map(|f| (stamp.to_string(), f))
}

/// `yyyyMMdd`（本地时间的粗粒度近似：只用 UTC 秒换算，不做时区库依赖——日志分文件不需要精确到小时）。
fn today_stamp() -> String {
    let secs = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_secs())
        .unwrap_or(0);
    let days = secs / 86_400;
    let (y, m, d) = civil_from_days(days as i64);
    format!("{y:04}{m:02}{d:02}")
}

/// 完整时间戳（毫秒级，便于与 C# 侧日志对齐排查）。
fn timestamp() -> String {
    let ms = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_millis())
        .unwrap_or(0);
    format!("{ms}")
}

/// Howard Hinnant 的 days→(y,m,d) 算法（无依赖、无时区，公历）。
fn civil_from_days(z: i64) -> (i64, u32, u32) {
    let z = z + 719_468;
    let era = if z >= 0 { z } else { z - 146_096 } / 146_097;
    let doe = (z - era * 146_097) as u64;
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let y = yoe as i64 + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = (doy - (153 * mp + 2) / 5 + 1) as u32;
    let m = if mp < 10 { mp + 3 } else { mp - 9 } as u32;
    (if m <= 2 { y + 1 } else { y }, m, d)
}

fn log_path(stamp: &str) -> PathBuf {
    let base = std::env::var("LOCALAPPDATA").unwrap_or_else(|_| ".".to_string());
    PathBuf::from(base)
        .join("BetterDesktop")
        .join("logs")
        .join(format!("core-{stamp}.log"))
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 未初始化（＝单测上下文）时日志必须**完全不落盘** —— 否则 `cargo test` 会污染真机生产日志。
    #[test]
    fn writes_are_noop_before_init() {
        assert!(!ENABLED.load(Ordering::Relaxed), "tests must not enable the log");
        info("this line must never reach disk");
        warn("neither must this one");
        let guard = LOG.lock().unwrap_or_else(|e| e.into_inner());
        assert!(guard.is_none(), "log file must not be opened without init()");
    }

    #[test]
    fn civil_from_days_known_dates() {
        // 1970-01-01
        assert_eq!(civil_from_days(0), (1970, 1, 1));
        // 2026-09-19（本计划的锚点日期）
        assert_eq!(civil_from_days(20_715), (2026, 9, 19));
        // 2024-02-29（闰日边界）
        assert_eq!(civil_from_days(19_782), (2024, 2, 29));
    }
}
