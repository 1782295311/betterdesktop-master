//! 索引引擎独立日志（与剪贴板引擎分文件，避免多进程写同一文件的锁竞争）。
//! 路径：%LOCALAPPDATA%\BetterDesktop\logs\index-engine.log
//!
//! 约定照抄 engine/src/log.rs：写失败静默降级（日志不可用不阻塞引擎启动）；
//! 锁中毒用 `unwrap_or_else(|e| e.into_inner())` 取回内部值——日志是全链路诊断地基，
//! 不得因一次 panic 永久静默（2026-09-12 剪贴板引擎审计结论）。

use std::fs::{File, OpenOptions};
use std::io::Write;
use std::path::PathBuf;
use std::sync::Mutex;
use std::time::{SystemTime, UNIX_EPOCH};

static LOG: Mutex<Option<File>> = Mutex::new(None);

/// 初始化日志文件（追加写）。失败时静默降级为 no-op。
pub fn init() {
    let path = log_path();
    if let Some(dir) = path.parent() {
        let _ = std::fs::create_dir_all(dir);
    }
    let file = OpenOptions::new().create(true).append(true).open(path).ok();
    let mut guard = LOG.lock().unwrap_or_else(|e| e.into_inner());
    *guard = file;
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
    let ts = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_millis().to_string())
        .unwrap_or_else(|_| "?".to_string());
    let line = format!("[{ts}] [{level}] {msg}\r\n");
    let mut guard = LOG.lock().unwrap_or_else(|e| e.into_inner());
    if let Some(f) = guard.as_mut() {
        let _ = f.write_all(line.as_bytes());
        let _ = f.flush();
    }
}

fn log_path() -> PathBuf {
    let base = std::env::var("LOCALAPPDATA").unwrap_or_else(|_| ".".to_string());
    PathBuf::from(base)
        .join("BetterDesktop")
        .join("logs")
        .join("index-engine.log")
}

#[cfg(test)]
mod tests {
    /// 日志路径必须落在 %LOCALAPPDATA%\BetterDesktop\logs\ 下，且文件名与剪贴板引擎不同（分文件纪律）。
    #[test]
    fn log_path_is_separate_from_clipboard_engine() {
        let path = super::log_path();
        let name = path.file_name().unwrap().to_string_lossy().to_string();
        assert_eq!(name, "index-engine.log");
        assert_ne!(name, "engine.log");
        assert!(path.to_string_lossy().contains("BetterDesktop"));
    }
}
