//! 引擎独立日志（Q5：独立文件避免与宿主 FileLogSink 锁竞争）。
//! 路径：%LOCALAPPDATA%\BetterDesktop\logs\engine.log（S5 升级为按日轮转 engine-yyyyMMdd.log）

use std::fs::{File, OpenOptions};
use std::io::Write;
use std::path::PathBuf;
use std::sync::Mutex;
use std::time::{SystemTime, UNIX_EPOCH};

static LOG: Mutex<Option<File>> = Mutex::new(None);

/// 初始化日志文件（追加写）。失败时静默降级为 no-op（日志不可用不阻塞引擎启动）。
pub fn init() {
    let path = log_path();
    if let Some(dir) = path.parent() {
        let _ = std::fs::create_dir_all(dir);
    }
    let file = OpenOptions::new().create(true).append(true).open(path).ok();
    // 【2026-09-12 审计】锁中毒不得让日志永久静默（诊断能力是排查一切问题的地基）→ poison-safe
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
    // 日志锁中毒时报文仍要能写出（poison-safe，见 init 注释）
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
        .join("engine.log")
}
