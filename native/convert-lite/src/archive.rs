//! 压缩/解压域（纯 Rust，替代外部 7z/zip 等）：
//! - zip：单文件打包/解包（zip crate，deflate）
//! - gz：Gzip 单文件压缩/解压（flate2）
//! - tar：tar 归档（tar crate）；tgz = tar+gz
//!
//! 解压语义（右键菜单单文件场景）：压缩包内单文件 -> 直接解到目标路径；
//! 多文件 -> 解到目标目录（目标路径不存在则创建）。
//! 诚实边界：不解 7z / rar / bz2 / xz（需 LZMA/Brotli 等编码器，体积成本高）；
//! 不做 zip 加密条目（zipcrypto/AES-zip）的写入。

use std::io::{Read, Write};
use std::path::Path;

// ---------- 分发 ----------

/// 按目标格式压缩单文件：zip / gz / tar / tgz。
pub fn compress(input: &Path, target: &str) -> Result<Vec<u8>, String> {
    match target {
        "zip" => compress_zip(input),
        "gz" => compress_gz(input),
        "tar" => compress_tar(input, false),
        "tgz" => compress_tar(input, true),
        _ => Err(format!("不支持的压缩格式：{target}")),
    }
}

// ---------- zip ----------

/// 单文件 -> zip 包字节（条目名为输入文件名）。
pub fn compress_zip(input: &Path) -> Result<Vec<u8>, String> {
    let data = std::fs::read(input).map_err(|e| format!("读输入失败：{e}"))?;
    let name = input
        .file_name()
        .and_then(|s| s.to_str())
        .unwrap_or("file")
        .to_string();
    let mut buf = Vec::new();
    {
        let mut zw = zip::ZipWriter::new(std::io::Cursor::new(&mut buf));
        let opts = zip::write::SimpleFileOptions::default();
        zw.start_file(name, opts).map_err(|e| e.to_string())?;
        zw.write_all(&data).map_err(|e| e.to_string())?;
        zw.finish().map_err(|e| e.to_string())?;
    }
    Ok(buf)
}

/// zip 包解出：单条目解到目标文件，多条目解到目标目录。
pub fn decompress_zip(input: &Path, out: &Path) -> Result<(), String> {
    let file = std::fs::File::open(input).map_err(|e| format!("打开 zip 失败：{e}"))?;
    let mut arc = zip::ZipArchive::new(file).map_err(|e| format!("解析 zip 失败：{e}"))?;
    let n = arc.len();
    if n == 0 {
        return Err("zip 包为空".to_string());
    }
    if n == 1 {
        let mut entry = arc
            .by_index(0)
            .map_err(|e| format!("读 zip 条目失败：{e}"))?;
        let mut data = Vec::new();
        entry.read_to_end(&mut data).map_err(|e| e.to_string())?;
        std::fs::write(out, &data).map_err(|e| e.to_string())?;
        return Ok(());
    }
    // 多条目：解到目录（防目录穿越）
    if !out.exists() {
        std::fs::create_dir_all(out).map_err(|e| e.to_string())?;
    }
    for i in 0..n {
        let mut entry = arc.by_index(i).map_err(|e| format!("读 zip 条目失败：{e}"))?;
        let name = entry.name().replace('\\', "/");
        let target = out.join(name.trim_start_matches('/'));
        // 目录穿越防护
        if !target.starts_with(out) {
            return Err(format!("zip 条目路径非法：{name}"));
        }
        if entry.is_dir() {
            std::fs::create_dir_all(&target).map_err(|e| e.to_string())?;
            continue;
        }
        if let Some(parent) = target.parent() {
            std::fs::create_dir_all(parent).map_err(|e| e.to_string())?;
        }
        let mut data = Vec::new();
        entry.read_to_end(&mut data).map_err(|e| e.to_string())?;
        std::fs::write(&target, &data).map_err(|e| e.to_string())?;
    }
    Ok(())
}

// ---------- gz ----------

/// 单文件 -> gzip 字节。
pub fn compress_gz(input: &Path) -> Result<Vec<u8>, String> {
    let data = std::fs::read(input).map_err(|e| format!("读输入失败：{e}"))?;
    let mut enc = flate2::write::GzEncoder::new(Vec::new(), flate2::Compression::default());
    enc.write_all(&data).map_err(|e| e.to_string())?;
    enc.finish().map_err(|e| e.to_string())
}

/// gzip 解压到目标文件。
pub fn decompress_gz(input: &Path, out: &Path) -> Result<(), String> {
    let file = std::fs::File::open(input).map_err(|e| format!("打开 gz 失败：{e}"))?;
    let mut dec = flate2::read::GzDecoder::new(file);
    let mut data = Vec::new();
    dec.read_to_end(&mut data).map_err(|e| format!("解压失败：{e}"))?;
    std::fs::write(out, &data).map_err(|e| e.to_string())
}

// ---------- tar / tgz ----------

/// 单文件 -> tar 归档字节。
pub fn compress_tar(input: &Path, gzip: bool) -> Result<Vec<u8>, String> {
    let data = std::fs::read(input).map_err(|e| format!("读输入失败：{e}"))?;
    let name = input
        .file_name()
        .and_then(|s| s.to_str())
        .unwrap_or("file")
        .to_string();
    let mut bytes = Vec::new();
    {
        let mut header = tar::Header::new_gnu();
        header.set_size(data.len() as u64);
        header.set_mode(0o644);
        header.set_cksum();
        let mut tar = tar::Builder::new(&mut bytes);
        tar.append_data(&mut header, name, std::io::Cursor::new(&data))
            .map_err(|e| e.to_string())?;
        tar.finish().map_err(|e| e.to_string())?;
    }
    if gzip {
        let mut enc = flate2::write::GzEncoder::new(Vec::new(), flate2::Compression::default());
        enc.write_all(&bytes).map_err(|e| e.to_string())?;
        enc.finish().map_err(|e| e.to_string())
    } else {
        Ok(bytes)
    }
}

/// tar/tgz 解出：单条目解到目标文件，多条目解到目标目录。
pub fn decompress_tar(input: &Path, out: &Path) -> Result<(), String> {
    let file = std::fs::File::open(input).map_err(|e| format!("打开 tar 失败：{e}"))?;
    let reader: Box<dyn Read> = if input
        .extension()
        .and_then(|s| s.to_str())
        .map(|e| e.eq_ignore_ascii_case("tgz") || e.eq_ignore_ascii_case("gz"))
        .unwrap_or(false)
    {
        Box::new(flate2::read::GzDecoder::new(file))
    } else {
        Box::new(file)
    };
    let mut ar = tar::Archive::new(reader);
    // 注意：tar::Entries 是惰性迭代器——不能先 collect 条目再读 body（后续条目
    // 创建时会跳过前一条目的 body 读到 0），必须边迭代边读并收集 (name, data)。
    let mut items: Vec<(String, Vec<u8>)> = Vec::new();
    let mut dirs: Vec<String> = Vec::new();
    for entry in ar.entries().map_err(|e| e.to_string())? {
        let mut entry = entry.map_err(|e| e.to_string())?;
        let path = entry.path().map_err(|e| e.to_string())?;
        let name = path.to_string_lossy().replace('\\', "/");
        if entry.header().entry_type().is_dir() {
            dirs.push(name);
            continue;
        }
        let mut data = Vec::new();
        entry.read_to_end(&mut data).map_err(|e| e.to_string())?;
        items.push((name, data));
    }
    if items.is_empty() && dirs.is_empty() {
        return Err("tar 包为空".to_string());
    }
    // 单文件条目且无目录条目：直接解到目标文件
    if items.len() == 1 && dirs.is_empty() {
        let (_, data) = &items[0];
        std::fs::write(out, data).map_err(|e| e.to_string())?;
        return Ok(());
    }
    // 多条目 / 含目录：解到目标目录
    if !out.exists() {
        std::fs::create_dir_all(out).map_err(|e| e.to_string())?;
    }
    for d in &dirs {
        let target = out.join(d.trim_start_matches('/'));
        if !target.starts_with(out) {
            return Err(format!("tar 条目路径非法：{d}"));
        }
        std::fs::create_dir_all(&target).map_err(|e| e.to_string())?;
    }
    for (name, data) in &items {
        let target = out.join(name.trim_start_matches('/'));
        if !target.starts_with(out) {
            return Err(format!("tar 条目路径非法：{name}"));
        }
        if let Some(parent) = target.parent() {
            std::fs::create_dir_all(parent).map_err(|e| e.to_string())?;
        }
        std::fs::write(&target, data).map_err(|e| e.to_string())?;
    }
    Ok(())
}
