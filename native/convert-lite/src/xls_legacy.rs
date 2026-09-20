//! .xls（Excel 97-2003 二进制格式，BIFF8）-> CSV。
//!
//! 纯 Rust：OLE2 读 Workbook 流 -> BIFF8 记录流（BOF/EOF）-> SST 共享字符串表
//!   + LABELSST/NUMBER/RK 单元格 -> 按行输出 CSV。
//!
//! 诚实边界：只取第一个工作表（忽略 BOUNDSHEET 多表）；SST 跨 CONTINUE 记录
//!   不拼接（超长共享字符串表会截断，小文件无碍）；公式不计算（缓存值优先，
//!   FORMULA 记录未实现）；MULRK 多单元格记录未实现。

use std::collections::BTreeMap;
use std::path::Path;

use crate::ole2::Compound;

fn u16_at(b: &[u8], i: usize) -> u16 {
    u16::from_le_bytes([b[i], b[i + 1]])
}
fn u32_at(b: &[u8], i: usize) -> u32 {
    u32::from_le_bytes([b[i], b[i + 1], b[i + 2], b[i + 3]])
}
fn f64_at(b: &[u8], i: usize) -> f64 {
    f64::from_le_bytes([
        b[i], b[i + 1], b[i + 2], b[i + 3], b[i + 4], b[i + 5], b[i + 6], b[i + 7],
    ])
}

fn utf16le_to_string(b: &[u8]) -> String {
    let units: Vec<u16> = b
        .chunks(2)
        .map(|c| u16::from_le_bytes([c[0], c[1]]))
        .collect();
    String::from_utf16_lossy(&units)
}

fn csv_escape(s: &str) -> String {
    if s.contains([',', '"', '\n', '\r']) {
        format!("\"{}\"", s.replace('"', "\"\""))
    } else {
        s.to_string()
    }
}

fn format_num(v: f64) -> String {
    if v.fract() == 0.0 && v.abs() < 1e15 {
        format!("{}", v as i64)
    } else {
        format!("{v}")
    }
}

/// 解析 SST 字符串表（不跨 CONTINUE）。
fn parse_sst(data: &[u8]) -> Vec<String> {
    let mut sst = Vec::new();
    if data.len() < 8 {
        return sst;
    }
    let unique = u32_at(data, 4) as usize;
    let mut p = 8usize;
    let mut guard = 0usize;
    while sst.len() < unique && p + 3 <= data.len() && guard < 1_000_000 {
        guard += 1;
        let cch = u16_at(data, p) as usize;
        p += 2;
        if p >= data.len() {
            break;
        }
        let flags = data[p];
        p += 1;
        let wide = flags & 0x01 != 0;
        let nbytes = cch * if wide { 2 } else { 1 };
        if p + nbytes > data.len() {
            break;
        }
        let s = if wide {
            utf16le_to_string(&data[p..p + nbytes])
        } else {
            String::from_utf8_lossy(&data[p..p + nbytes]).into_owned()
        };
        p += nbytes;
        // 富文本（flags&0x08）：cRun(2) + 4*cRun；音标（flags&0x04）：cb(4)+cb
        if flags & 0x08 != 0 && p + 2 <= data.len() {
            let c_run = u16_at(data, p) as usize;
            p += 2 + c_run * 4;
        }
        if flags & 0x04 != 0 && p + 4 <= data.len() {
            let cb = u32_at(data, p) as usize;
            p += 4 + cb;
        }
        sst.push(s);
    }
    sst
}

fn decode_rk(v: u32) -> f64 {
    let is_int = v & 2 != 0;
    let scale100 = v & 1 != 0;
    let sign = if v & 4 != 0 { -1.0 } else { 1.0 };
    if is_int {
        let i = ((v >> 2) as i32) as f64;
        sign * i / if scale100 { 100.0 } else { 1.0 }
    } else {
        let bits = ((v & 0xFFFF_FFFC) as u64) << 32;
        let d = f64::from_bits(bits);
        sign * d / if scale100 { 100.0 } else { 1.0 }
    }
}

/// .xls -> CSV（第一个工作表）。
pub fn xls_to_csv(path: &Path) -> Result<String, String> {
    let c = Compound::open(path)?;
    let wb = c.read_stream("Workbook")?;
    if wb.len() < 8 {
        return Err("Workbook 流过短".into());
    }
    let mut sst: Vec<String> = Vec::new();
    let mut rows: BTreeMap<u32, BTreeMap<u32, String>> = BTreeMap::new();
    let mut max_col: u32 = 0;

    let mut pos = 0usize;
    let mut guard = 0usize;
    while pos + 4 <= wb.len() && guard < 10_000_000 {
        guard += 1;
        let rtype = u16_at(&wb, pos);
        let rlen = u16_at(&wb, pos + 2) as usize;
        if pos + 4 + rlen > wb.len() {
            break;
        }
        let data = &wb[pos + 4..pos + 4 + rlen];
        match rtype {
            0x00FC => sst = parse_sst(data), // SST
            0x00FD => {
                // LABELSST
                if data.len() >= 10 {
                    let r = u16_at(data, 0) as u32;
                    let col = u16_at(data, 2) as u32;
                    let idx = u32_at(data, 6) as usize;
                    let v = sst.get(idx).cloned().unwrap_or_default();
                    rows.entry(r).or_default().insert(col, v);
                    max_col = max_col.max(col);
                }
            }
            0x0203 => {
                // NUMBER
                if data.len() >= 14 {
                    let r = u16_at(data, 0) as u32;
                    let col = u16_at(data, 2) as u32;
                    let v = f64_at(data, 6);
                    rows.entry(r).or_default().insert(col, format_num(v));
                    max_col = max_col.max(col);
                }
            }
            0x027E => {
                // RK
                if data.len() >= 10 {
                    let r = u16_at(data, 0) as u32;
                    let col = u16_at(data, 2) as u32;
                    let v = decode_rk(u32_at(data, 6));
                    rows.entry(r).or_default().insert(col, format_num(v));
                    max_col = max_col.max(col);
                }
            }
            0x00BD => {
                // MULRK：多单元格 RK（简化逐格解码）
                if data.len() >= 12 {
                    let r = u16_at(data, 0) as u32;
                    let c_first = u16_at(data, 2) as u32;
                    let n = (data.len() - 6) / 6;
                    for i in 0..n {
                        let col = c_first + i as u32;
                        let v = decode_rk(u32_at(data, 4 + i * 6));
                        rows.entry(r).or_default().insert(col, format_num(v));
                        max_col = max_col.max(col);
                    }
                }
            }
            _ => {}
        }
        pos += 4 + rlen;
    }

    if rows.is_empty() {
        return Err("未提取到单元格数据".into());
    }
    let mut out = String::new();
    for (_, row) in &rows {
        let mut cells: Vec<String> = Vec::new();
        for col in 0..=max_col {
            cells.push(csv_escape(row.get(&col).map(|s| s.as_str()).unwrap_or("")));
        }
        out.push_str(&cells.join(","));
        out.push('\n');
    }
    Ok(out.trim_end().to_string())
}
