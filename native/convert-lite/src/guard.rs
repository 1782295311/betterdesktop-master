//! 大文件稳定性护栏。
//!
//! 目标：无论输入多大，进程都不能 OOM，产物不能是半截文件。
//! 手段：
//!  - 输入先 stat，按字节数做硬上限，超限直接拒绝（而不是吃到一半才崩）。
//!  - 输出先写 `<out>.part-<pid>.tmp`，成功后 rename 原子替换；失败删临时文件。
//!  - 文本类一律 reader/writer 流式，绝不 read_to_string。

use std::fmt;
use std::fs;
use std::io;
use std::path::{Path, PathBuf};

/// 单文件输入硬上限：1 GiB。超过就拒绝——轻量化实验不承诺处理超大二进制。
pub const MAX_INPUT_BYTES: u64 = 1 << 30;
/// 图片解码后像素硬上限：~50 MP。超过直接拒绝，避免 image crate 解码爆内存。
pub const MAX_IMAGE_PIXELS: u64 = 50_000_000;

#[derive(Debug)]
pub enum GuardError {
    /// 输入文件过大。
    TooLarge { size: u64, max: u64 },
    /// 图片像素过多。
    TooManyPixels { pixels: u64, max: u64 },
    Io(String),
}

impl fmt::Display for GuardError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            GuardError::TooLarge { size, max } => {
                write!(f, "输入文件过大：{size} bytes > 上限 {max}")
            }
            GuardError::TooManyPixels { pixels, max } => {
                write!(f, "图片像素过多：{pixels} > 上限 {max}")
            }
            GuardError::Io(s) => write!(f, "IO 错误：{s}"),
        }
    }
}

impl std::error::Error for GuardError {}

impl From<io::Error> for GuardError {
    fn from(e: io::Error) -> Self {
        GuardError::Io(e.to_string())
    }
}

/// 探测输入文件大小；超上限返回错误。
pub fn probe_input(path: &Path) -> Result<u64, GuardError> {
    let meta = fs::metadata(path)?;
    let size = meta.len();
    if size > MAX_INPUT_BYTES {
        return Err(GuardError::TooLarge {
            size,
            max: MAX_INPUT_BYTES,
        });
    }
    Ok(size)
}

/// 校验图片像素数。pixels = width*height。
pub fn check_pixels(width: u32, height: u32) -> Result<(), GuardError> {
    let pixels = width as u64 * height as u64;
    if pixels > MAX_IMAGE_PIXELS {
        return Err(GuardError::TooManyPixels {
            pixels,
            max: MAX_IMAGE_PIXELS,
        });
    }
    Ok(())
}

/// 为目标输出文件生成同目录临时路径。
pub fn tmp_path(out: &Path) -> PathBuf {
    let pid = std::process::id();
    let name = format!(
        ".{}.part-{}.tmp",
        out.file_name().and_then(|n| n.to_str()).unwrap_or("out"),
        pid
    );
    out.with_file_name(name)
}

/// 把临时文件原子改名到最终目标。临时文件与目标同目录，避免跨盘符 rename 失败。
pub fn commit(tmp: &Path, final_out: &Path) -> io::Result<()> {
    fs::rename(tmp, final_out)
}

/// 尽力删除临时文件（失败不抛——清理路径不应掩盖原始错误）。
pub fn discard(tmp: &Path) {
    let _ = fs::remove_file(tmp);
}

/// 判断扩展名是否为音视频。
pub fn is_av_ext(ext: &str) -> bool {
    matches!(ext, "mp4" | "mkv" | "avi" | "mov" | "webm" | "flv" | "wmv"
        | "mp3" | "aac" | "flac" | "ogg" | "wav" | "m4a" | "opus")
}