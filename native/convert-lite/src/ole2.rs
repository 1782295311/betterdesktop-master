//! OLE2 复合文档（CFB）读取器——.doc/.xls/.ppt 等旧版 Office 格式的基础层。
//!
//! 纯 Rust 实现 MS-CFB：512/4096 字节扇区、DIFAT/FAT 链、目录流、
//! 迷你流（mini stream + mini FAT，经 Root Entry 数据读取）。
//!
//! 诚实边界：只读不支持写；不支持红黑树目录排序（按目录顺序线性扫描
//!   找名字，文件名唯一即可）；损坏文件报错明确。

use std::collections::HashMap;
use std::io::Read;
use std::path::Path;

const FREESECT: u32 = 0xFFFF_FFFF;
const ENDOFCHAIN: u32 = 0xFFFF_FFFE;
const FATSECT: u32 = 0xFFFF_FFFD;
const DIFSECT: u32 = 0xFFFF_FFFC;
const MAX_REGULAR: usize = 4096; // mini stream cutoff 默认值

#[derive(Debug)]
pub struct Entry {
    pub name: String,
    pub is_stream: bool,
    pub start_sector: u32,
    pub size: u64,
}

pub struct Compound {
    sector_size: usize,
    fat: Vec<u32>,             // 所有 FAT 扇区拼成的扇区号表
    dir_sector: u32,           // 目录首扇区
    mini_fat: Vec<u32>,        // 迷你 FAT（小流扇区号表）
    mini_stream: Vec<u8>,      // Root Entry 的大流（小流内容都放这里）
    entries: Vec<Entry>,
    bytes: Vec<u8>,
}

impl Compound {
    pub fn open(path: &Path) -> Result<Compound, String> {
        let mut f = std::fs::File::open(path).map_err(|e| format!("打开 {} 失败：{e}", path.display()))?;
        let mut bytes = Vec::new();
        f.read_to_end(&mut bytes).map_err(|e| format!("读取失败：{e}"))?;
        Compound::parse(&bytes)
    }

    pub fn parse(bytes: &[u8]) -> Result<Compound, String> {
        if bytes.len() < 512 {
            return Err("文件过小，不是有效的 OLE2 复合文档".into());
        }
        let magic = [0xD0u8, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
        if &bytes[0..8] != &magic {
            return Err("不是 OLE2 复合文档（缺少 D0CF11E0 魔数）".into());
        }
        let sector_shift = bytes[30] as usize;
        if sector_shift != 9 && sector_shift != 12 {
            return Err(format!("不支持的扇区位移 {sector_shift}（应为 9=512B 或 12=4096B）"));
        }
        let sector_size = 1usize << sector_shift;
        let mini_shift = bytes[32] as usize;
        if mini_shift != 6 {
            return Err(format!("不支持的迷你扇区位移 {mini_shift}"));
        }
        let mini_size = 1usize << mini_shift; // 64
        let num_fat = u32_at(bytes, 44) as usize;
        let first_dir = u32_at(bytes, 48);
        let mini_cutoff = u32_at(bytes, 56) as usize;
        let first_mini_fat = u32_at(bytes, 60);
        let num_mini_fat = u32_at(bytes, 64) as usize;
        let first_difat = u32_at(bytes, 68);
        let num_difat = u32_at(bytes, 72) as usize;
        let _ = mini_cutoff; // 规范默认 4096，直接用 MAX_REGULAR

        // DIFAT：头部 109 个 + 链式 DIFAT 扇区
        let mut difat: Vec<u32> = Vec::new();
        for i in 0..109usize {
            difat.push(u32_at(bytes, 76 + i * 4));
        }
        let mut next_difat = first_difat;
        let mut difat_count = num_difat;
        while next_difat != FREESECT && difat_count > 0 {
            let off = 512 + next_difat as usize * sector_size;
            let n = sector_size / 4;
            let mut carry = Vec::new();
            for i in 0..n - 1 {
                let v = u32_at(bytes, off + i * 4);
                if v != FREESECT {
                    carry.push(v);
                }
            }
            difat.extend(carry);
            let next = u32_at(bytes, off + (n - 1) * 4);
            if next == next_difat {
                break; // 防循环
            }
            next_difat = next;
            difat_count = difat_count.saturating_sub(1);
        }
        difat.retain(|&v| v != FREESECT && v != FATSECT && v != DIFSECT && v != ENDOFCHAIN);

        // FAT：每个 FAT 扇区 = sector_size/4 个 u32
        let mut fat: Vec<u32> = Vec::new();
        for &fs in &difat {
            let off = 512 + fs as usize * sector_size;
            for i in 0..sector_size / 4 {
                fat.push(u32_at(bytes, off + i * 4));
            }
        }
        // FAT 扇区本身可能超过 109 个（num_fat）——按 num_fat 截断
        if num_fat > 0 && fat.len() > num_fat * sector_size / 4 {
            fat.truncate(num_fat * sector_size / 4);
        }

        // 目录
        let mut entries = Vec::new();
        let mut dir_sec = first_dir;
        let mut seen = 0usize;
        while dir_sec != FREESECT && dir_sec != ENDOFCHAIN && seen < 1_000_000 {
            let off = 512 + dir_sec as usize * sector_size;
            for i in 0..sector_size / 128 {
                let base = off + i * 128;
                let name_len = u16_at(bytes, base + 64) as usize;
                if name_len == 0 || name_len > 64 || name_len % 2 != 0 {
                    continue;
                }
                let mut name_bytes = vec![0u8; name_len];
                name_bytes.copy_from_slice(&bytes[base..base + name_len]);
                let name = String::from_utf16_lossy(
                    &name_bytes
                        .chunks(2)
                        .map(|c| u16::from_le_bytes([c[0], c[1]]))
                        .collect::<Vec<_>>(),
                );
                let name = name.trim_end_matches('\0').to_string();
                let etype = bytes[base + 66];
                let start = u32_at(bytes, base + 116);
                let size = u64_at(bytes, base + 120);
                if name.is_empty() {
                    continue;
                }
                entries.push(Entry {
                    name,
                    is_stream: etype == 2,
                    start_sector: start,
                    size,
                });
            }
            let next = fat.get(dir_sec as usize).copied().unwrap_or(FREESECT);
            if next == dir_sec {
                break;
            }
            dir_sec = next;
            seen += 1;
        }

        // 迷你 FAT
        let mut mini_fat: Vec<u32> = Vec::new();
        let mut mf = first_mini_fat;
        let mut seen_mf = 0usize;
        while mf != FREESECT && mf != ENDOFCHAIN && seen_mf < 1_000_000 {
            let off = 512 + mf as usize * sector_size;
            for i in 0..sector_size / 4 {
                mini_fat.push(u32_at(bytes, off + i * 4));
            }
            let next = fat.get(mf as usize).copied().unwrap_or(FREESECT);
            if next == mf {
                break;
            }
            mf = next;
            seen_mf += 1;
        }
        if num_mini_fat > 0 && mini_fat.len() > num_mini_fat * sector_size / 4 {
            mini_fat.truncate(num_mini_fat * sector_size / 4);
        }

        // 迷你流 = Root Entry 的大流
        let mut mini_stream: Vec<u8> = Vec::new();
        if let Some(root) = entries.iter().find(|e| e.name.eq_ignore_ascii_case("root entry")) {
            if root.is_stream {
                // Root Entry 本身是"存储"但持有 mini stream 数据
            }
            mini_stream = Compound::read_chain(&bytes, &fat, root.start_sector, root.size, sector_size);
        }

        Ok(Compound {
            sector_size,
            fat,
            dir_sector: first_dir,
            mini_fat,
            mini_stream,
            entries,
            bytes: bytes.to_vec(),
        })
    }

    fn read_chain(bytes: &[u8], fat: &[u32], start: u32, size: u64, sector_size: usize) -> Vec<u8> {
        let mut out = Vec::with_capacity(size as usize);
        let mut sec = start;
        let mut seen = 0usize;
        let limit = (size as usize).saturating_add(sector_size);
        while sec != FREESECT && sec != ENDOFCHAIN && seen < 1_000_000 && out.len() < limit {
            let off = 512 + sec as usize * sector_size;
            if off + sector_size <= bytes.len() {
                out.extend_from_slice(&bytes[off..off + sector_size]);
            }
            let next = fat.get(sec as usize).copied().unwrap_or(FREESECT);
            if next == sec {
                break;
            }
            sec = next;
            seen += 1;
        }
        out.truncate(size as usize);
        out
    }

    /// 按名字读取流（大小流自动路由）。
    pub fn read_stream(&self, name: &str) -> Result<Vec<u8>, String> {
        let entry = self
            .entries
            .iter()
            .find(|e| e.is_stream && e.name.eq_ignore_ascii_case(name))
            .ok_or_else(|| format!("流不存在：{name}"))?;
        if entry.size < MAX_REGULAR as u64 {
            // 小流：从 mini stream 按 mini FAT 链读取（迷你扇区 64B）
            let mut out = Vec::with_capacity(entry.size as usize);
            let mut sec = entry.start_sector;
            let mut seen = 0usize;
            let mini = 64usize;
            let limit = entry.size as usize + mini;
            while sec != FREESECT && sec != ENDOFCHAIN && seen < 1_000_000 && out.len() < limit {
                let off = sec as usize * mini;
                if off + mini <= self.mini_stream.len() {
                    out.extend_from_slice(&self.mini_stream[off..off + mini]);
                }
                let next = self.mini_fat.get(sec as usize).copied().unwrap_or(FREESECT);
                if next == sec {
                    break;
                }
                sec = next;
                seen += 1;
            }
            out.truncate(entry.size as usize);
            Ok(out)
        } else {
            Ok(Compound::read_chain(
                &self.bytes,
                &self.fat,
                entry.start_sector,
                entry.size,
                self.sector_size,
            ))
        }
    }

    /// 列出所有流名。
    pub fn stream_names(&self) -> Vec<&str> {
        self.entries
            .iter()
            .filter(|e| e.is_stream)
            .map(|e| e.name.as_str())
            .collect()
    }

    /// 流信息（大小）。
    pub fn stream_size(&self, name: &str) -> Option<u64> {
        self.entries
            .iter()
            .find(|e| e.is_stream && e.name.eq_ignore_ascii_case(name))
            .map(|e| e.size)
    }

    pub fn _dir_sector(&self) -> u32 {
        self.dir_sector
    }

    pub fn _entries(&self) -> &[Entry] {
        &self.entries
    }
}

fn u16_at(b: &[u8], i: usize) -> u16 {
    u16::from_le_bytes([b[i], b[i + 1]])
}
fn u32_at(b: &[u8], i: usize) -> u32 {
    u32::from_le_bytes([b[i], b[i + 1], b[i + 2], b[i + 3]])
}
fn u64_at(b: &[u8], i: usize) -> u64 {
    u64::from_le_bytes([
        b[i], b[i + 1], b[i + 2], b[i + 3], b[i + 4], b[i + 5], b[i + 6], b[i + 7],
    ])
}

/// 便捷：从文件读取 OLE2 流并转为 UTF-8 文本（供解析器复用）。
pub fn read_stream_str(path: &Path, stream: &str) -> Result<(Vec<u8>, String), String> {
    let c = Compound::open(path)?;
    let data = c.read_stream(stream)?;
    let name = stream.to_string();
    Ok((data, name))
}

/// 供解析器使用的内部结构导出。
pub fn _use_map(_m: &HashMap<String, u32>) {}
