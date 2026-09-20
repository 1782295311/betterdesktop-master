//! MOBI（PalmDB 容器 + PalmDOC 文本）-> Markdown（零外部二进制）。
//!
//! 解析：PDB 头（78B）+ 记录表（8B×N）→ 记录 0（PalmDOC 头 + MOBI 头，
//!   textEncoding 判定编码）→ 文本记录拼接（compression=0 原样 / =2 PalmDOC LZ77）
//!   → 得到 HTML 正文 → 复用 html_str_to_md 转 Markdown。
//!
//! 诚实边界：compression=1(HUFF/COOKIE) 与加密（encryptionType≠0）报错不支持；
//!   KF8/AZW3（mobiType=2）尽力解析（记录布局同 PalmDOC）；图片/元数据不提取；
//!   HTML 中 `<mbp:pagebreak>` 等私有标签忽略。

use std::path::Path;

use crate::html_md::html_str_to_md;

fn u16(b: &[u8], i: usize) -> u16 {
    u16::from_be_bytes([b[i], b[i + 1]])
}
fn u32(b: &[u8], i: usize) -> u32 {
    u32::from_be_bytes([b[i], b[i + 1], b[i + 2], b[i + 3]])
}

/// PalmDOC LZ77 解压（compression=2）。
fn palmdoc_decompress(data: &[u8]) -> Vec<u8> {
    let mut out: Vec<u8> = Vec::with_capacity(data.len() * 2);
    let mut i = 0usize;
    while i < data.len() {
        let ctrl = data[i];
        i += 1;
        for bit in (0..8).rev() {
            if i >= data.len() {
                break;
            }
            if (ctrl >> bit) & 1 == 1 {
                // LZ 对：distance 14bit + length 2bit
                if i + 1 >= data.len() {
                    break;
                }
                let d = (u16::from_be_bytes([data[i], data[i + 1]])) as usize;
                i += 2;
                let offset = d >> 2;
                let length = (d & 0x03) + 3;
                if offset == 0 || offset > out.len() {
                    break;
                }
                let start = out.len() - offset;
                for k in 0..length {
                    let idx = start + k;
                    if idx < out.len() {
                        out.push(out[idx]);
                    }
                }
            } else {
                out.push(data[i]);
                i += 1;
            }
        }
    }
    out
}

/// MOBI -> Markdown。
pub fn mobi_to_md(path: &Path) -> Result<String, String> {
    let bytes = std::fs::read(path).map_err(|e| format!("读取失败：{e}"))?;
    if bytes.len() < 78 {
        return Err("文件过短，非 PalmDB 容器".into());
    }
    let num_records = u16(&bytes, 76) as usize;
    if num_records == 0 {
        return Err("无记录（非 MOBI）".into());
    }
    let mut records: Vec<&[u8]> = Vec::with_capacity(num_records);
    for r in 0..num_records {
        let off = 78 + r * 8;
        if off + 4 > bytes.len() {
            break;
        }
        let start = u32(&bytes, off) as usize;
        let end = if r + 1 < num_records {
            u32(&bytes, off + 8) as usize
        } else {
            bytes.len()
        };
        if start >= bytes.len() || end > bytes.len() || end <= start {
            continue;
        }
        records.push(&bytes[start..end]);
    }
    if records.is_empty() {
        return Err("记录表解析失败".into());
    }
    let hdr = records[0];
    if hdr.len() < 16 {
        return Err("记录 0 过短".into());
    }
    let compression = u16(hdr, 0);
    let _text_length = u32(hdr, 4);
    let record_count = u16(hdr, 8) as usize;
    let _record_size = u16(hdr, 10);
    let encryption = u16(hdr, 12);
    if encryption != 0 {
        return Err(format!("加密 MOBI（encryptionType={encryption}）不支持"));
    }
    if compression == 1 {
        return Err("Huffman 压缩 MOBI 不支持".into());
    }
    // MOBI 头：textEncoding（1252=windows-1252, 65001=UTF-8）
    let mut encoding = 1252u32;
    if hdr.len() >= 32 && &hdr[16..20] == b"MOBI" {
        let hl = u32(hdr, 20) as usize;
        if hdr.len() >= hl && hl >= 28 {
            encoding = u32(hdr, 28);
        }
    }
    // 拼接文本记录 1..=record_count
    let mut text: Vec<u8> = Vec::new();
    let max_rec = (record_count + 1).min(records.len());
    for r in 1..max_rec {
        let rec = records[r];
        if compression == 2 {
            text.extend_from_slice(&palmdoc_decompress(rec));
        } else {
            text.extend_from_slice(rec);
        }
    }
    if text.is_empty() {
        return Err("未提取到文本记录".into());
    }
    let html = match encoding {
        65001 => String::from_utf8_lossy(&text).into_owned(),
        _ => {
            let (s, _, _) = encoding_rs::WINDOWS_1252.decode(&text);
            s.into_owned()
        }
    };
    // 清理 MOBI 私有标签（保持 HTML 结构）
    let html = html
        .replace("<mbp:pagebreak/>", "\n")
        .replace("<mbp:pagebreak>", "\n")
        .replace("</mbp:pagebreak>", "\n");
    let md = html_str_to_md(&html);
    let md = md.trim().to_string();
    if md.is_empty() {
        return Err("HTML 正文为空（非 MOBI 或无正文）".into());
    }
    Ok(md)
}
