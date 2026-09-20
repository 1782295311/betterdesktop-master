//! 剪贴板快照读取（原生 Win32，替代 WPF System.Windows.Clipboard 的托管封装层开销）。
//! 捕获顺序（与 C# ReadClipboardSnapshot 一致）：HTML > Text > Image > Files。
//! - HTML 条目三格式全取（Html + Rtf + Text；有 RTF → RichText 类型）
//! - 图片：CF_DIB → RGBA → NormalizeVacuousAlpha（1502 红线）→ PNG 编码（内存）
//! - OpenClipboard 竞争重试 3×50ms（A5），仍失败跳过本次
//! S4 接入：data URI 提取、落盘、paths-only 分支、来源进程/标题（隐私黑名单判定在捕获前）

use std::thread;
use std::time::Duration;

use windows::core::{w, PWSTR};
use windows::Win32::Foundation::{CloseHandle, GlobalFree, HANDLE, HGLOBAL, HWND};
use windows::Win32::System::DataExchange::{
    CloseClipboard, EmptyClipboard, GetClipboardData, IsClipboardFormatAvailable, OpenClipboard,
    RegisterClipboardFormatW, SetClipboardData,
};
use windows::Win32::System::Memory::{GlobalAlloc, GlobalLock, GlobalSize, GlobalUnlock};
use windows::Win32::System::Threading::{
    OpenProcess, QueryFullProcessImageNameW, PROCESS_NAME_FORMAT, PROCESS_QUERY_LIMITED_INFORMATION,
};
use windows::Win32::UI::WindowsAndMessaging::{
    GetForegroundWindow, GetWindowTextLengthW, GetWindowTextW, GetWindowThreadProcessId,
};

use crate::model::{ClipboardEntry, ItemKind, NamedFormat};
use crate::store::Store;

pub const CF_DIB: u32 = 8;
pub const CF_UNICODETEXT: u32 = 13;
pub const CF_HDROP: u32 = 15;

const GMEM_MOVEABLE: u32 = 0x0002;

/// 一次捕获的快照（与 C# ClipboardSnapshot 对齐；图片为内存 PNG，S4 落盘）。
#[derive(Debug, Default)]
pub struct Snapshot {
    pub kind: ItemKind,
    pub html: String,
    pub rtf: String,
    pub text: String,
    pub image_png: Option<Vec<u8>>,
    pub image_width: i32,
    pub image_height: i32,
    pub files: Vec<String>,
    /// 【P1-4 命名格式透传】自定义（命名）格式的原始字节（仅文本类条目采集）。
    pub named_formats: Vec<NamedFormat>,
}

/// 读取剪贴板快照。None = 无内容/剪贴板被占用（重试后仍失败）。
///
/// `named_format_limits`：命名格式采集上限（`None` = 不透传，见 `crate::formats::Limits`）。
pub fn read_snapshot(named_format_limits: Option<crate::formats::Limits>) -> Option<Snapshot> {
    if !open_clipboard_with_retry() {
        crate::log::info("OpenClipboard failed after retries; skipping this capture");
        return None;
    }
    let result = read_snapshot_locked().map(|mut snap| {
        attach_named_formats(&mut snap, named_format_limits);
        snap
    });
    unsafe { let _ = CloseClipboard(); }
    result
}

/// 采集命名格式（须在剪贴板打开期间调用）。仅文本类条目需要 —— 图片/文件条目已有自己的
/// 多格式写回（PNG/DIB/CF_HDROP），不需要额外搬运自定义格式。
fn attach_named_formats(snap: &mut Snapshot, limits: Option<crate::formats::Limits>) {
    let Some(limits) = limits else {
        return;
    };
    if !matches!(snap.kind, ItemKind::Text | ItemKind::Html | ItemKind::RichText) {
        return;
    }
    snap.named_formats = crate::formats::collect(limits);
}

// ---------------- 【P2-1 渐进式富文本探测 · 2026-09-13】 ----------------

/// 会"分步写剪贴板格式"的富文本应用（先给 `CF_UNICODETEXT`，稍后才补 HTML/RTF）。
pub const RICH_TEXT_APPS: &[&str] = &[
    "excel", "winword", "powerpnt", "outlook", "et", "wps", "wpp", "wpspdf",
];

/// 渐进探测调度（毫秒，**相对首次读取的累计时间点**）。
///
/// 取自对标 TieZ 的经验表 —— 覆盖面广的"分步写格式"延时分布；一旦拿到富文本立即返回，
/// 典型命中在 40~160ms。调度耗尽（560ms）仍未拿到才放弃。
pub const PROBE_SCHEDULE_MS: &[u64] = &[0, 40, 80, 140, 220, 360, 560];

/// 来源进程是否属于"分步写富文本格式"的应用白名单（大小写不敏感子串）。
pub fn is_rich_text_app(process: &str) -> bool {
    if process.is_empty() {
        return false;
    }
    let p = process.to_lowercase();
    RICH_TEXT_APPS.iter().any(|a| p.contains(a))
}

/// 渐进式读快照：来源是 Office/WPS 且**首读为纯文本**时，按调度重读直到拿到富文本或超时。
///
/// 【为什么需要】固定的一次性读取会偶发拿到"只有纯文本"的快照 —— 用户感知即"从 Word/WPS
/// 复制的富文本偶尔丢格式"。**只在白名单应用触发**（普通应用零延迟）；拿到富文本立刻返回；
/// 调度耗尽则返回**最后一次有效快照**（绝不因探测丢内容）。
pub fn read_snapshot_progressive(
    process: &str,
    named_format_limits: Option<crate::formats::Limits>,
) -> Option<Snapshot> {
    let mut latest = read_snapshot(named_format_limits);
    // 只有"首读为纯文本"才值得探测：已是富文本/图片/文件 → 直接返回
    match &latest {
        Some(s) if s.kind == ItemKind::Text => {}
        _ => return latest,
    }

    let mut prev = 0u64;
    for (idx, target) in PROBE_SCHEDULE_MS.iter().enumerate().skip(1) {
        let delta = target.saturating_sub(prev);
        prev = *target;
        thread::sleep(Duration::from_millis(delta));
        let Some(snap) = read_snapshot(named_format_limits) else {
            continue;
        };
        if matches!(snap.kind, ItemKind::Html | ItemKind::RichText) {
            crate::log::info(format!(
                "rich-text probe: {process} 第 {idx} 次（+{target}ms）拿到富文本"
            ));
            return Some(snap);
        }
        latest = Some(snap);
    }
    crate::log::info(format!(
        "rich-text probe: {process} 调度耗尽（{}ms）仍未拿到富文本，使用最后一次快照",
        PROBE_SCHEDULE_MS.last().copied().unwrap_or(0)
    ));
    latest
}

/// OpenClipboard 竞争重试 3×50ms（A5：其他进程持锁时跳过本次，不崩）。
fn open_clipboard_with_retry() -> bool {
    for attempt in 0..3 {
        match unsafe { OpenClipboard(HWND::default()) } {
            Ok(()) => return true,
            Err(e) => {
                crate::log::info(format!(
                    "OpenClipboard attempt {} failed: {e}",
                    attempt + 1
                ));
                thread::sleep(Duration::from_millis(50));
            }
        }
    }
    false
}

/// 剪贴板已打开状态下读取（调用方负责 CloseClipboard）。
fn read_snapshot_locked() -> Option<Snapshot> {
    let html_format = register_format(w!("HTML Format"));
    let rtf_format = register_format(w!("Rich Text Format"));
    let rtf_noobjs = register_format(w!("Rich Text Format Without Objects"));

    // 1. HTML > RichText（有 RTF 三格式并存）
    if html_format != 0 && is_available(html_format) {
        if let Some(html) = read_text_format(html_format) {
            if !html.is_empty() {
                // 【2026-09-12 修复】剪贴板 "HTML Format" 是带偏移量头的 CF_HTML 串，
                // 必须剥掉头只存 HTML 本体 —— 否则写回时会被 wrap_html_for_clipboard 再包一层，
                // 形成双重 CF_HTML 头，应用解析失败后回退显示整串元数据
                //（用户实测："粘贴出来一堆 Version:0.9 / StartHTML:..."）。
                let body = crate::html::strip_cf_html_header(&html);
                let rtf = read_text_format(rtf_format)
                    .or_else(|| read_text_format(rtf_noobjs))
                    .unwrap_or_default();
                let mut text = read_unicode_text().unwrap_or_default();
                // 剪贴板未提供 CF_UNICODETEXT 时（部分应用的富文本复制）从 HTML 提取纯文本：
                // 否则该条目既不可被搜索，也没有粘贴兜底格式（用户实测：HTML 条目 content 为空）。
                // 用 is_blank_text 而非 trim()：零宽字符（\u{200B} 等）不算 Unicode 空白，
    // 会让"看起来是空"的快照绕过红线入库（2026-09-12 真机：哨兵条目入库 7 条）。
    if crate::store::Store::is_blank_text(&text) {
                    text = crate::html::html_to_text(&body);
                }
                return Some(Snapshot {
                    kind: if rtf.is_empty() {
                        ItemKind::Html
                    } else {
                        ItemKind::RichText
                    },
                    html: body,
                    rtf,
                    text,
                    ..Default::default()
                });
            }
        }
    }

    // 2. Text
    if is_available(CF_UNICODETEXT) {
        if let Some(text) = read_unicode_text() {
            if !text.is_empty() {
                return Some(Snapshot {
                    kind: ItemKind::Text,
                    text,
                    ..Default::default()
                });
            }
        }
    }

    // 3. Image（CF_DIB → PNG）
    if is_available(CF_DIB) {
        if let Some(dib) = read_format_bytes(CF_DIB) {
            if let Some(processed) = process_dib_to_png(&dib) {
                return Some(Snapshot {
                    kind: ItemKind::Image,
                    image_png: Some(processed.png),
                    image_width: processed.width,
                    image_height: processed.height,
                    ..Default::default()
                });
            }
        }
    }

    // 4. Files（CF_HDROP）
    if is_available(CF_HDROP) {
        if let Some(drop) = read_format_bytes(CF_HDROP) {
            let files = parse_hdrop(&drop);
            if !files.is_empty() {
                return Some(Snapshot {
                    kind: ItemKind::Files,
                    files,
                    ..Default::default()
                });
            }
        }
    }

    None
}

fn register_format(name: windows::core::PCWSTR) -> u32 {
    unsafe { RegisterClipboardFormatW(name) }
}

fn is_available(format: u32) -> bool {
    // 0.58：IsClipboardFormatAvailable 返回 Result<()>（Err = 格式不可用）
    unsafe { IsClipboardFormatAvailable(format).is_ok() }
}

/// 读取指定格式的原始字节（GlobalLock + GlobalSize + GlobalUnlock）。
/// `pub(crate)`：命名格式采集（`crate::formats`）复用本函数。
pub(crate) fn read_format_bytes(format: u32) -> Option<Vec<u8>> {
    unsafe {
        let h = GetClipboardData(format).ok()?;
        let hmem = HGLOBAL(h.0);
        let ptr = GlobalLock(hmem);
        if ptr.is_null() {
            return None;
        }
        let size = GlobalSize(hmem);
        let bytes = std::slice::from_raw_parts(ptr as *const u8, size).to_vec();
        let _ = GlobalUnlock(hmem);
        Some(bytes)
    }
}

/// 读取 Unicode 文本格式（CF_UNICODETEXT=13）：Windows 剪贴板 UTF-16LE **无 BOM**，必须按 UTF-16LE 解码。
fn read_unicode_text() -> Option<String> {
    let bytes = read_format_bytes(CF_UNICODETEXT)?;
    let s = decode_utf16_le(&bytes);
    Some(s)
}

/// 读取文本格式：HTML/RTF=字节流（UTF-8 优先，带 BOM 的 UTF-16 其次，最后 lossy）。
fn read_text_format(format: u32) -> Option<String> {
    let bytes = read_format_bytes(format)?;
    Some(decode_text_bytes(&bytes))
}

fn decode_text_bytes(bytes: &[u8]) -> String {
    // UTF-16LE BOM
    if bytes.len() >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE {
        return decode_utf16_le(&bytes[2..]);
    }
    // UTF-8 严格解码优先（现代应用）
    match std::str::from_utf8(bytes) {
        Ok(s) => s.trim_end_matches('\0').to_string(),
        Err(_) => String::from_utf8_lossy(bytes).trim_end_matches('\0').to_string(),
    }
}

fn decode_utf16_le(bytes: &[u8]) -> String {
    let units: Vec<u16> = bytes
        .chunks_exact(2)
        .map(|c| u16::from_le_bytes([c[0], c[1]]))
        .take_while(|&u| u != 0)
        .collect();
    String::from_utf16_lossy(&units)
}

// ---------------- DIB → PNG（含 1502 红线：vacuous alpha 归一化） ----------------

/// 处理结果：PNG 字节 + 尺寸。
pub struct ProcessedImage {
    pub png: Vec<u8>,
    pub width: i32,
    pub height: i32,
}

/// CF_DIB（BITMAPINFOHEADER + 像素）→ PNG。
/// 支持 32/24bpp（BI_RGB/BI_BITFIELDS）与 8bpp 灰度；其余位深返回 None（跳过捕获，不崩）。
pub fn process_dib_to_png(dib: &[u8]) -> Option<ProcessedImage> {
    let raw = dib_to_rgba(dib)?;
    // 1502 红线：真空 alpha 归一化（全 alpha=0 且 RGB 有内容 → 强制 alpha=255，否则 PNG 整图不可见）
    let mut rgba = raw.rgba;
    let mut has_alpha = raw.has_alpha;
    if has_alpha {
        let all_zero = rgba.chunks_exact(4).all(|p| p[3] == 0);
        let has_rgb = rgba.chunks_exact(4).any(|p| p[0] != 0 || p[1] != 0 || p[2] != 0);
        if all_zero && has_rgb {
            for p in rgba.chunks_exact_mut(4) {
                p[3] = 255;
            }
        }
        // alpha 全 255（归一化后）→ 去 alpha 通道（PNG RGB，省空间且无损）
        if rgba.chunks_exact(4).all(|p| p[3] == 255) {
            has_alpha = false;
        }
    }

    let png = encode_png(&rgba, raw.width, raw.height, has_alpha)?;
    Some(ProcessedImage {
        png,
        width: raw.width as i32,
        height: raw.height as i32,
    })
}

struct DibRaw {
    width: u32,
    height: u32,
    rgba: Vec<u8>, // RGBA8
    has_alpha: bool,
}

/// 解析 BITMAPINFOHEADER（biSize 40/108/124 兼容）+ 像素数据 → RGBA8。
fn dib_to_rgba(dib: &[u8]) -> Option<DibRaw> {
    if dib.len() < 40 {
        return None;
    }
    let read_u32 = |o: usize| u32::from_le_bytes([dib[o], dib[o + 1], dib[o + 2], dib[o + 3]]);
    let read_i32 = |o: usize| i32::from_le_bytes([dib[o], dib[o + 1], dib[o + 2], dib[o + 3]]);
    let read_u16 = |o: usize| u16::from_le_bytes([dib[o], dib[o + 1]]);

    let bi_size = read_u32(0) as usize;
    let bi_width = read_i32(4);
    let bi_height = read_i32(8);
    let bi_bit_count = read_u16(14);
    let bi_compression = read_u32(16);
    let bi_clr_used = read_u32(32);

    if bi_width <= 0 || bi_height == 0 || bi_size < 40 || bi_size > dib.len() {
        return None;
    }

    let width = bi_width as u32;
    let height = bi_height.unsigned_abs();
    let top_down = bi_height < 0;
    // 【2026-09-12 修复】stride 用 u64 计算：width 可达 u32::MAX，`width * bi_bit_count` 在
    // release 下会静默回绕成错误 stride（debug 直接 panic）。
    let stride = ((width as u64 * bi_bit_count as u64 + 31) / 32 * 4) as usize;

    // 颜色表起始（biSize 后；BI_BITFIELDS 32/16 位时先有 3 个掩码 DWORD）
    let mut color_table_offset = bi_size;
    let mut masks = [0u32; 3];
    if bi_compression == 3 {
        // BI_BITFIELDS：header 后 12 字节 R/G/B 掩码
        if bi_size + 12 > dib.len() {
            return None;
        }
        for i in 0..3 {
            masks[i] = read_u32(bi_size + i * 4);
        }
        color_table_offset = bi_size + 12;
    }

    // 像素数据起始（颜色表之后）
    let palette_entries = if bi_bit_count <= 8 {
        if bi_clr_used > 0 {
            bi_clr_used as usize
        } else {
            1usize << bi_bit_count
        }
    } else {
        0
    };
    let pixel_offset = color_table_offset + palette_entries * 4;
    if pixel_offset > dib.len() {
        return None;
    }

    // 调色板：BGRA 每项 4 字节（Windows 位图 QUAD）
    let mut palette: Vec<[u8; 4]> = Vec::with_capacity(palette_entries);
    for i in 0..palette_entries {
        let o = color_table_offset + i * 4;
        if o + 4 <= dib.len() {
            palette.push([dib[o], dib[o + 1], dib[o + 2], dib[o + 3]]);
        } else {
            palette.push([0, 0, 0, 255]);
        }
    }

    // 【2026-09-12 修复】先校验"header 声明的尺寸"与"实际像素数据"是否自洽，**再**分配。
    // 此前直接按声明尺寸 `width×height×4` 预分配 —— 损坏或恶意构造的 CF_DIB（如声明 100000×100000）
    // 会让引擎试图分配几十 GB：分配失败 = abort（**不可捕获**），尺寸溢出 = capacity overflow panic，
    // 两者都会让整个引擎进程崩溃（剪贴板监听 + IPC 服务全挂）。现在数据不足即拒绝，
    // 分配量被 dib.len() 约束。
    let pixel_bytes = match (height as usize).checked_mul(stride) {
        Some(n) => n,
        None => return None,
    };
    let data_end = match pixel_offset.checked_add(pixel_bytes) {
        Some(n) => n,
        None => return None,
    };
    if data_end > dib.len() {
        return None;
    }

    // 行像素读取
    let mut rgba = Vec::with_capacity(width as usize * height as usize * 4);
    let mut has_alpha = false;
    for row in 0..height {
        // 底向上（biHeight>0）最后一行在最前
        let src_row = if top_down { row } else { height - 1 - row };
        let row_start = pixel_offset + src_row as usize * stride as usize;
        if row_start + stride as usize > dib.len() {
            return None;
        }
        let row_bytes = &dib[row_start..row_start + stride as usize];
        for x in 0..width {
            let o = x as usize * (bi_bit_count as usize / 8);
            match bi_bit_count {
                32 => {
                    let (b, g, r, a) = if bi_compression == 3 && masks[0] != 0 {
                        let px = read_u32(row_start + x as usize * 4);
                        let r = mask_shift(px, masks[0]);
                        let g = mask_shift(px, masks[1]);
                        let b = mask_shift(px, masks[2]);
                        (b, g, r, 255u8)
                    } else {
                        (row_bytes[o], row_bytes[o + 1], row_bytes[o + 2], row_bytes[o + 3])
                    };
                    has_alpha |= a != 255;
                    rgba.extend_from_slice(&[r, g, b, a]);
                }
                24 => {
                    rgba.extend_from_slice(&[row_bytes[o + 2], row_bytes[o + 1], row_bytes[o], 255]);
                }
                16 => {
                    let px = u16::from_le_bytes([row_bytes[o], row_bytes[o + 1]]);
                    let (r, g, b) = if bi_compression == 3 && masks[0] != 0 {
                        (
                            mask_shift(px as u32, masks[0]),
                            mask_shift(px as u32, masks[1]),
                            mask_shift(px as u32, masks[2]),
                        )
                    } else {
                        // BI_RGB 16bpp：5-5-5
                        (
                            ((px >> 10) & 0x1F) as u8 * 255 / 31,
                            ((px >> 5) & 0x1F) as u8 * 255 / 31,
                            (px & 0x1F) as u8 * 255 / 31,
                        )
                    };
                    rgba.extend_from_slice(&[r, g, b, 255]);
                }
                8 => {
                    let idx = row_bytes[o] as usize;
                    let p = palette.get(idx).copied().unwrap_or([0, 0, 0, 255]);
                    has_alpha |= p[3] != 255;
                    rgba.extend_from_slice(&[p[0], p[1], p[2], p[3]]);
                }
                _ => return None, // 4/1bpp/RLE 等 v1 不支持：跳过捕获
            }
        }
    }

    Some(DibRaw {
        width,
        height,
        rgba,
        has_alpha,
    })
}

/// 从位掩码提取通道值（取掩码最高位对齐到 8 位）。
fn mask_shift(px: u32, mask: u32) -> u8 {
    if mask == 0 {
        return 0;
    }
    let mut v = px & mask;
    // 右移去掉低位零
    let low = mask.trailing_zeros();
    v >>= low;
    // 对齐到 8 位：掩码位宽 m → 左移 (8 - m)
    let bits = (mask >> low).count_ones();
    if bits >= 8 {
        v as u8
    } else {
        // 缩放到 0-255
        let scale = 255u32 / ((1u32 << bits) - 1);
        ((v * scale) & 0xFF) as u8
    }
}

/// 编码 PNG（has_alpha=false 时输出 RGB 8bit，无损去 alpha）。
fn encode_png(rgba: &[u8], width: u32, height: u32, has_alpha: bool) -> Option<Vec<u8>> {
    let mut out = Vec::new();
    let result = if has_alpha {
        let img = image::RgbaImage::from_raw(width, height, rgba.to_vec())?;
        image::DynamicImage::ImageRgba8(img).write_to(&mut std::io::Cursor::new(&mut out), image::ImageFormat::Png)
    } else {
        let rgb: Vec<u8> = rgba
            .chunks_exact(4)
            .flat_map(|p| [p[0], p[1], p[2]])
            .collect();
        let img = image::RgbImage::from_raw(width, height, rgb)?;
        image::DynamicImage::ImageRgb8(img).write_to(&mut std::io::Cursor::new(&mut out), image::ImageFormat::Png)
    };
    match result {
        Ok(()) => Some(out),
        Err(e) => {
            crate::log::error(format!("PNG encode failed: {e}"));
            None
        }
    }
}

/// 解析 CF_HDROP（DROPFILES 头 + null 分隔路径）。
fn parse_hdrop(bytes: &[u8]) -> Vec<String> {
    if bytes.len() < 20 {
        return Vec::new();
    }
    let offset = u32::from_le_bytes([bytes[0], bytes[1], bytes[2], bytes[3]]) as usize;
    // 【2026-09-14 修复】DROPFILES 布局 = pFiles(0..4) + POINT(4..12) + fNC(12..16) + fWide(16..20)，
    // 故 fWide 在**偏移 16**。此前读偏移 20 —— 那是路径表的首字节：
    // 宽字符路径下它恰好非零（"C" 的低字节 0x43）所以"蒙对"，而 ANSI 投递会被误判成宽字符。
    let wide = bytes.get(16).copied().unwrap_or(0) != 0;
    if offset == 0 || offset >= bytes.len() {
        return Vec::new();
    }
    let mut files = Vec::new();
    if wide {
        let mut pos = offset;
        while pos + 2 <= bytes.len() {
            let u = u16::from_le_bytes([bytes[pos], bytes[pos + 1]]);
            if u == 0 {
                if pos > offset {
                    break;
                }
                pos += 2;
                continue;
            }
            let mut unit = Vec::new();
            let mut cur = pos;
            while cur + 2 <= bytes.len() {
                let ch = u16::from_le_bytes([bytes[cur], bytes[cur + 1]]);
                if ch == 0 {
                    break;
                }
                unit.push(ch);
                cur += 2;
            }
            files.push(String::from_utf16_lossy(&unit));
            if cur + 2 > bytes.len() {
                break;
            }
            pos = cur + 2;
            // 双 null 结束
            if pos + 2 <= bytes.len() && u16::from_le_bytes([bytes[pos], bytes[pos + 1]]) == 0 {
                break;
            }
        }
    } else {
        let mut pos = offset;
        while pos < bytes.len() {
            let end = bytes[pos..]
                .iter()
                .position(|&b| b == 0)
                .map(|i| pos + i)
                .unwrap_or(bytes.len());
            let s = String::from_utf8_lossy(&bytes[pos..end]).to_string();
            if !s.is_empty() {
                files.push(s);
            }
            if end >= bytes.len() {
                break;
            }
            pos = end + 1;
        }
    }
    files
}

// ---------------- 来源信息（隐私黑名单输入） ----------------

/// 前台窗口来源：进程名（去 .exe）+ 窗口标题。
pub fn read_source_info() -> (String, String) {
    unsafe {
        let hwnd = GetForegroundWindow();
        if hwnd.0.is_null() {
            return (String::new(), String::new());
        }
        let mut pid = 0u32;
        let _ = GetWindowThreadProcessId(hwnd, Some(&mut pid));
        let process = process_name(pid);
        let title = window_title(hwnd);
        (process, title)
    }
}

fn process_name(pid: u32) -> String {
    if pid == 0 {
        return String::new();
    }
    unsafe {
        // 0.58：OpenProcess 返回 Result<HANDLE>
        let Ok(h) = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid) else {
            return String::new();
        };
        if h.is_invalid() {
            return String::new();
        }
        let mut buf = [0u16; 1024];
        let mut size = buf.len() as u32;
        let name = QueryFullProcessImageNameW(
            h,
            PROCESS_NAME_FORMAT(0),
            PWSTR(buf.as_mut_ptr()),
            &mut size,
        )
        .map(|_| String::from_utf16_lossy(&buf[..size as usize]))
        .unwrap_or_default();
        let _ = CloseHandle(h);
        // 去目录 + 去 .exe（大小写不敏感）
        let file = std::path::Path::new(&name)
            .file_name()
            .map(|f| f.to_string_lossy().to_string())
            .unwrap_or(name);
        strip_exe(&file)
    }
}

fn strip_exe(name: &str) -> String {
    let lower = name.to_lowercase();
    if lower.ends_with(".exe") {
        name[..name.len() - 4].to_string()
    } else {
        name.to_string()
    }
}

fn window_title(hwnd: HWND) -> String {
    unsafe {
        let len = GetWindowTextLengthW(hwnd);
        if len <= 0 {
            return String::new();
        }
        // 0.58：GetWindowTextW(hwnd, &mut [u16]) slice 形式
        let mut buf = vec![0u16; (len + 1) as usize];
        let n = GetWindowTextW(hwnd, &mut buf);
        if n <= 0 {
            return String::new();
        }
        String::from_utf16_lossy(&buf[..n as usize])
    }
}

// ---------------- 写回剪贴板（copy_to_clipboard） ----------------

/// 把历史条目写回剪贴板（多格式写：Text/HTML+RTF/图片 CF_PNG+CF_DIB/文件 CF_HDROP）。
/// 调用方负责抑制令牌（防回环）。
/// 写回剪贴板（S6 增 plain 参数：强制纯文本写回——客户端 CopyEntryAsPlainText/代码条目）。
/// 引擎侧在调用方抑制回环（listener.suppress）。
pub fn write_back(entry: &ClipboardEntry, store: &Store, plain: bool) -> bool {
    if !open_clipboard_with_retry() {
        return false;
    }
    unsafe {
        let _ = EmptyClipboard();
    }
    let ok = if plain {
        set_plain_text(entry)
    } else {
        match entry.content_type {
            ItemKind::Text => set_unicode_text(&entry.content),
            ItemKind::Html | ItemKind::RichText => set_html_entry(entry, store),
            ItemKind::Image => set_image_entry(entry, store),
            // 【表情包 · 2026-09-12】表情包也是 Files（文件路径 + CF_HDROP），但额外补静帧位图格式。
            // 【2026-09-13 修复 · 模型改造遗漏】判据必须是**标记** `is_sticker`：表情包已由"分类"改为标记
            //（导入后 `category` 是 Image/File）。旧判据 `category == Sticker` **永不成立** →
            // 表情包写回时只走下面的 `set_files_entry`（只有 CF_HDROP），丢掉 PNG 原字节与 CF_DIB 首帧兜底
            // → **只认位图的应用（不少聊天输入框）粘不出东西**，用户感知为"点了复制却粘不上"。
            ItemKind::Files if entry.is_sticker => set_sticker_entry(entry, store),
            ItemKind::Files => set_files_entry(entry, store),
        }
    };
    // 【P1-4 命名格式透传 · 2026-09-13】标准格式写完后**追加**自定义命名格式（Excel/WPS 表格等）。
    // 只在非纯文本模式写（纯文本语义下用户要的就是"只有文字"）；单个失败不阻断其余标准格式。
    // 注意必须在 `EmptyClipboard` 之后、`CloseClipboard` 之前 —— 多格式并存的前提（1302 红线）。
    if !plain && !entry.named_formats.is_empty() {
        crate::formats::write_back(&entry.named_formats);
    }
    unsafe {
        let _ = CloseClipboard();
    }
    ok
}

/// 【P2-3 临时粘贴 · 2026-09-13】把**快照**直接写回剪贴板（不依赖 store）。
///
/// 用于「临时粘贴」的剪贴板还原：快照里已有全部原始数据（文本/HTML/RTF、内存 PNG、文件路径、
/// 命名格式），因此无需落盘即可完整还原。调用方负责回环抑制（`mark_echo` + `snapshot_fingerprint`）。
pub fn write_back_snapshot(snap: &Snapshot) -> bool {
    if !open_clipboard_with_retry() {
        return false;
    }
    unsafe {
        let _ = EmptyClipboard();
    }

    let ok = match snap.kind {
        ItemKind::Text => set_unicode_text(&snap.text),
        ItemKind::Html | ItemKind::RichText => {
            let mut ok = true;
            if !snap.html.is_empty() {
                let wrapped = wrap_html_for_clipboard(&snap.html);
                let html_format = unsafe { RegisterClipboardFormatW(w!("HTML Format")) };
                if html_format != 0 {
                    ok = set_clipboard_bytes(html_format, wrapped.as_bytes());
                }
            }
            if !snap.text.is_empty() {
                ok = set_unicode_text(&snap.text) && ok;
            }
            if !snap.rtf.is_empty() {
                let rtf_format = unsafe { RegisterClipboardFormatW(w!("Rich Text Format")) };
                if rtf_format != 0 {
                    let mut bytes = snap.rtf.as_bytes().to_vec();
                    bytes.push(0);
                    ok = set_clipboard_bytes(rtf_format, &bytes) && ok;
                }
            }
            ok
        }
        ItemKind::Image => {
            let Some(png) = &snap.image_png else {
                unsafe { let _ = CloseClipboard(); }
                return false;
            };
            let png_format = unsafe { RegisterClipboardFormatW(w!("PNG")) };
            let mut ok = png_format != 0 && set_clipboard_bytes(png_format, png);
            if let Some(dib) = png_to_dib(png) {
                ok = set_clipboard_bytes(CF_DIB, &dib) && ok;
            }
            ok
        }
        ItemKind::Files => {
            if snap.files.is_empty() {
                false
            } else {
                let drop = build_hdrop(&snap.files);
                let ok = set_clipboard_bytes(CF_HDROP, &drop);
                let _ = set_unicode_text(&snap.files.join("\r\n"));
                ok
            }
        }
    };

    // 命名格式同样还原（多格式并存：EmptyClipboard 只调一次）
    if !snap.named_formats.is_empty() {
        crate::formats::write_back(&snap.named_formats);
    }

    unsafe {
        let _ = CloseClipboard();
    }
    ok
}

/// 【P2-3】快照的**回环指纹**（与 `ClipboardEntry::fingerprint` 同键空间）。
///
/// 临时粘贴还原后，这次写回也会触发捕获 —— 必须用同一指纹把它吞掉（否则用户剪贴板里凭空多一条）。
pub fn snapshot_fingerprint(snap: &Snapshot) -> String {
    let mut e = ClipboardEntry::new("tmp", snap.kind, snap.text.clone());
    e.html_content = snap.html.clone();
    e.rtf_content = snap.rtf.clone();
    e.file_paths = snap.files.clone();
    if let Some(png) = &snap.image_png {
        e.content_hash = crate::fingerprint::image_content_hash(png);
    }
    e.fingerprint()
}

/// 纯文本写回（Ctrl+点击「复制为纯文本」/ 代码条目）：**必须从正确的字段取内容**。
///
/// 【为什么不能直接写 `entry.content` · 2026-09-13 用户场景】文件/目录条目的内容在 `file_paths` 里，
/// `content` 是空的 —— 直接写会粘出**一个空字符串**（用户点"复制为纯文本"却什么都没粘上）。
/// 图片条目同理：内容在磁盘，`image_path` 才是它的位置。
///
/// 这条通道同时是"文件夹适配"的答案：默认复制带 `CF_HDROP`（文件管理器能粘出文件夹），
/// 而网页聊天框会把目录**展开成一堆子项**；此时改用纯文本复制 —— 剪贴板里**只有文本**，
/// 任何输入框都只能得到路径文字，两者各取所需、互不牺牲。
fn set_plain_text(entry: &ClipboardEntry) -> bool {
    if !entry.file_paths.is_empty() {
        return set_unicode_text(&entry.file_paths.join("\r\n"));
    }
    if entry.content_type == ItemKind::Image && !entry.image_path.is_empty() {
        return set_unicode_text(&entry.image_path);
    }
    set_unicode_text(&entry.content)
}

fn set_unicode_text(text: &str) -> bool {
    let mut units: Vec<u16> = text.encode_utf16().collect();
    units.push(0);
    let bytes = to_le_bytes_u16(&units);
    set_clipboard_bytes(CF_UNICODETEXT, &bytes)
}

/// HTML 条目：还原 data URI → CF_HTML 包装（对齐 C# WrapHtmlForClipboard）+ CF_RTF + CF_UNICODETEXT。
fn set_html_entry(entry: &ClipboardEntry, store: &Store) -> bool {
    // 还原占位符（html-images\{id}\{n}.{ext}）
    let mut html = entry.html_content.clone();
    if html.contains(crate::html::PLACEHOLDER_PREFIX) {
        let dir = store.html_images_dir().join(&entry.id);
        let mut images = Vec::new();
        // 【2026-09-12 修复】上限 64 → 512：此前硬编码 64，index ≥ 64 的占位符（`{{BD_IMG:n}}`）
        // 不会被还原，而原 data URI 在入库时已丢弃 → 粘贴出字面量占位符 = **内容不可逆损坏**。
        // 小图标（几十字节）很容易超过 64 张。`!found` 即 break，正常内容不会扫满。
        for i in 0..512 {
            let mut found = false;
            for ext in ["png", "jpg", "gif", "webp", "bmp", "svg", "ico", "avif"] {
                let p = dir.join(format!("{i}.{ext}"));
                if let Ok(bytes) = std::fs::read(&p) {
                    images.push(crate::html::ExtractedImage {
                        filename: format!("{i}.{ext}"),
                        bytes,
                        ext: ext.to_string(),
                    });
                    found = true;
                    break;
                }
            }
            if !found {
                break;
            }
        }
        html = crate::html::restore_data_uris(&html, &images);
    }

    let wrapped = wrap_html_for_clipboard(&html);
    let html_format = unsafe { RegisterClipboardFormatW(w!("HTML Format")) };
    let mut ok = html_format != 0 && set_clipboard_bytes(html_format, wrapped.as_bytes());
    // 纯文本兜底
    if !entry.content.is_empty() {
        ok = set_unicode_text(&entry.content) && ok;
    }
    // RTF（CF_RTF 按 UTF-8 字节写入）
    if !entry.rtf_content.is_empty() {
        let rtf_format = unsafe { RegisterClipboardFormatW(w!("Rich Text Format")) };
        if rtf_format != 0 {
            let mut bytes = entry.rtf_content.as_bytes().to_vec();
            bytes.push(0);
            ok = set_clipboard_bytes(rtf_format, &bytes) && ok;
        }
    }
    ok
}

/// CF_HTML 标准包装（对齐 C# ClipboardNative.WrapHtmlForClipboard）。
fn wrap_html_for_clipboard(html_content: &str) -> String {
    if html_content.is_empty() {
        return String::new();
    }
    const FRAGMENT_PREFIX: &str = "<html><body><!--StartFragment-->";
    const FRAGMENT_SUFFIX: &str = "<!--EndFragment--></body></html>";

    let placeholder = format!(
        "Version:0.9\r\nStartHTML:{:08}\r\nEndHTML:{:08}\r\nStartFragment:{:08}\r\nEndFragment:{:08}\r\n",
        99999999, 99999999, 99999999, 99999999
    );
    let start_html = placeholder.len();
    let end_html = start_html + FRAGMENT_PREFIX.len() + html_content.len() + FRAGMENT_SUFFIX.len();
    let start_fragment = start_html + FRAGMENT_PREFIX.len();
    let end_fragment = start_html + FRAGMENT_PREFIX.len() + html_content.len();

    format!(
        "Version:0.9\r\nStartHTML:{start_html:08}\r\nEndHTML:{end_html:08}\r\nStartFragment:{start_fragment:08}\r\nEndFragment:{end_fragment:08}\r\n{FRAGMENT_PREFIX}{html_content}{FRAGMENT_SUFFIX}"
    )
}

/// 图片写回：CF_PNG（原图字节）+ CF_DIB（从 PNG 解码构造，兼容老应用）。
fn set_image_entry(entry: &ClipboardEntry, store: &Store) -> bool {
    let path = store.root().join(&entry.image_path);
    let Ok(png) = std::fs::read(&path) else {
        return false;
    };
    let png_format = unsafe { RegisterClipboardFormatW(w!("PNG")) };
    let mut ok = png_format != 0 && set_clipboard_bytes(png_format, &png);
    // CF_DIB：PNG 解码 → RGBA → BITMAPINFOHEADER + BGRA 底向上
    if let Some(dib) = png_to_dib(&png) {
        ok = set_clipboard_bytes(CF_DIB, &dib) && ok;
    }
    ok
}

/// PNG → DIB（BITMAPINFOHEADER 40 + BGRA 像素，底向上）。
fn png_to_dib(png: &[u8]) -> Option<Vec<u8>> {
    let img = image::load_from_memory(png).ok()?;
    Some(rgba_to_dib(&img.to_rgba8()))
}

/// 任意可解码图片（**含 GIF/WebP 首帧**）→ DIB，供"只认位图"的目标应用粘贴。
/// 解不出返回 None —— 调用方视作"静帧降级不可用"，不影响主路径（CF_HDROP 原文件）。
fn decode_first_frame_dib(bytes: &[u8]) -> Option<Vec<u8>> {
    let img = image::load_from_memory(bytes).ok()?;
    Some(rgba_to_dib(&img.to_rgba8()))
}

/// RGBA 像素 → DIB 字节（BITMAPINFOHEADER 40 + BGRA，底向上）。
fn rgba_to_dib(rgba: &image::RgbaImage) -> Vec<u8> {
    let w = rgba.width();
    let h = rgba.height();
    let stride = w * 4;
    let mut dib = Vec::with_capacity(40 + stride as usize * h as usize);
    dib.extend_from_slice(&40u32.to_le_bytes()); // biSize
    dib.extend_from_slice(&(w as i32).to_le_bytes());
    dib.extend_from_slice(&(h as i32).to_le_bytes()); // 正高 = 底向上
    dib.extend_from_slice(&1u16.to_le_bytes()); // planes
    dib.extend_from_slice(&32u16.to_le_bytes()); // bitcount
    dib.extend_from_slice(&0u32.to_le_bytes()); // BI_RGB
    dib.extend_from_slice(&(stride * h).to_le_bytes()); // sizeimage
    dib.extend_from_slice(&0i32.to_le_bytes());
    dib.extend_from_slice(&0i32.to_le_bytes());
    dib.extend_from_slice(&0u32.to_le_bytes()); // clrused
    dib.extend_from_slice(&0u32.to_le_bytes()); // clrimportant
    for row in (0..h).rev() {
        for x in 0..w {
            let p = rgba.get_pixel(x, row);
            dib.extend_from_slice(&[p.0[2], p.0[1], p.0[0], p.0[3]]); // BGRA
        }
    }
    dib
}

/// 表情包写回：**多格式齐发**，让目标应用自选（计划 §3 D2）。
///
/// ① `CF_HDROP`（stickers 副本路径）—— 聊天软件/浏览器取文件即发**原动图**（唯一保动画的路径）；
/// ② `PNG`（原文件本身是 PNG 家族时**字节级透传**，不重编码）；
/// ③ `CF_DIB`（GIF/WebP 首帧解码 → 位图）—— 仅支持位图的应用/输入框拿到静帧，不至于"粘不出东西"。
fn set_sticker_entry(entry: &ClipboardEntry, store: &Store) -> bool {
    let mut ok = set_files_entry(entry, store); // ① 主路径

    let Some(path) = entry.file_paths.first() else {
        return ok;
    };
    let Ok(bytes) = std::fs::read(path) else {
        return ok;
    };

    let ext = std::path::Path::new(path)
        .extension()
        .and_then(|e| e.to_str())
        .unwrap_or_default()
        .to_ascii_lowercase();
    if matches!(ext.as_str(), "png" | "apng") {
        let png_format = unsafe { RegisterClipboardFormatW(w!("PNG")) };
        if png_format != 0 {
            ok = set_clipboard_bytes(png_format, &bytes) && ok;
        }
    }
    if let Some(dib) = decode_first_frame_dib(&bytes) {
        ok = set_clipboard_bytes(CF_DIB, &dib) && ok;
    }
    ok
}

/// 文件写回：`CF_HDROP`（storage-mode 决定原路径或副本路径）+ **`CF_UNICODETEXT` 兜底**。
///
/// 【为什么必须补文本格式 · 2026-09-13 用户实测】此前只写 CF_HDROP。把它粘到"当文件上传"的目标
///（网页聊天框、富文本编辑器）时，对方拿到 CF_HDROP 后会去**展开路径** ——
/// 目录被枚举成一堆子项图标、或在输入框里被拆成若干 mention/字符标记，
/// 用户看到的就是"粘贴一个文件夹，出来一堆图标或乱码"。
///
/// **这是我们的实现不完整，不是目标应用的问题**：Windows 资源管理器自己复制文件时，剪贴板里是
/// `CF_HDROP` + `CF_UNICODETEXT`（路径文本）+ `Preferred DropEffect` **并存** ——
/// 目标是文件管理器就取 CF_HDROP（得到可粘贴的文件夹），目标是文本框就取文本，各取所需。
/// 我们只给了一半，于是**所有**目标都被迫走"文件/上传"语义。
///
/// 多格式不会互相覆盖：`write_back` 只在开头 `EmptyClipboard()` 一次，之后逐个 `SetClipboardData`。
fn set_files_entry(entry: &ClipboardEntry, store: &Store) -> bool {
    let mut paths: Vec<String> = Vec::new();
    for path in &entry.file_paths {
        // full 模式副本存在则用副本（离线可用），否则回退原路径
        let copy = store.files_dir().join(&entry.id);
        if let Some(name) = std::path::Path::new(path).file_name() {
            let candidate = copy.join(name);
            if candidate.exists() {
                paths.push(candidate.to_string_lossy().to_string());
                continue;
            }
        }
        paths.push(path.clone());
    }
    if paths.is_empty() {
        return false;
    }

    // ① 主路径：CF_HDROP —— 文件管理器粘贴得到文件/文件夹
    let drop = build_hdrop(&paths);
    let ok = set_clipboard_bytes(CF_HDROP, &drop);

    // ② 兜底：纯文本路径（多路径按行分隔）—— 文本框/网页输入框据此粘"路径文字"，
    //    而不是去展开目录（这正是"文件夹粘成一堆图标"的根因）。
    let text_ok = set_unicode_text(&paths.join("\r\n"));
    ok && text_ok
}

/// 构造 CF_HDROP（DROPFILES 头 + UTF-16 路径 + 双 null）。
fn build_hdrop(paths: &[String]) -> Vec<u8> {
    // 【2026-09-14 修复 · 已复现】DROPFILES 实际大小 = 20 字节（pFiles 4 + POINT 8 + fNC 4 + fWide 4），
    // 20 本身已 4 字节对齐 —— **不存在"再对齐 4 → 24"**。此前把 pFiles 声明为 24 却只写了 20 字节头，
    // 于是路径表偏移与声明不符：消费方（资源管理器 / DragQueryFile）从 24 处开始读，
    // 把路径的前两个字符吃掉（实测 "C:\a.txt" → "\a.txt"）→ 粘贴文件/文件夹/表情包全部拿到坏路径。
    // 对齐 C# `ClipboardIpcClient.BuildDropFiles` 的 `HeaderSize = 20`（两处必须同值）。
    let header_len = 20usize; // sizeof(DROPFILES)
    let mut out = Vec::new();
    out.extend_from_slice(&(header_len as u32).to_le_bytes()); // pFiles
    out.extend_from_slice(&[0u8; 8]); // pt (POINT)
    out.extend_from_slice(&0u32.to_le_bytes()); // fNC
    out.extend_from_slice(&1u32.to_le_bytes()); // fWide
    for p in paths {
        for u in p.encode_utf16() {
            out.extend_from_slice(&u.to_le_bytes());
        }
        out.extend_from_slice(&0u16.to_le_bytes());
    }
    out.extend_from_slice(&0u16.to_le_bytes()); // 双 null 结束
    out
}

/// 通过 GlobalAlloc+SetClipboardData 写入格式字节。
/// `pub(crate)`：命名格式写回（`crate::formats`）复用本函数。
pub(crate) fn set_clipboard_bytes(format: u32, bytes: &[u8]) -> bool {
    unsafe {
        let hmem = match GlobalAlloc(windows::Win32::System::Memory::GLOBAL_ALLOC_FLAGS(GMEM_MOVEABLE), bytes.len()) {
            Ok(h) => h,
            Err(e) => {
                crate::log::error(format!("GlobalAlloc failed: {e}"));
                return false;
            }
        };
        let ptr = GlobalLock(hmem);
        if ptr.is_null() {
            let _ = GlobalFree(hmem);
            return false;
        }
        std::ptr::copy_nonoverlapping(bytes.as_ptr(), ptr as *mut u8, bytes.len());
        let _ = GlobalUnlock(hmem);
        // SetClipboardData 收 HANDLE（HGLOBAL.0 转换）
        match SetClipboardData(format, HANDLE(hmem.0)) {
            Ok(_) => true,
            Err(e) => {
                crate::log::error(format!("SetClipboardData failed: {e}"));
                let _ = GlobalFree(hmem);
                false
            }
        }
    }
}

fn to_le_bytes_u16(units: &[u16]) -> Vec<u8> {
    let mut out = Vec::with_capacity(units.len() * 2);
    for u in units {
        out.extend_from_slice(&u.to_le_bytes());
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    fn dib_32bpp(width: u32, height: u32, pixels: &[(u8, u8, u8, u8)]) -> Vec<u8> {
        let stride = width * 4;
        let mut dib = Vec::new();
        let mut hdr = Vec::new();
        hdr.extend_from_slice(&40u32.to_le_bytes()); // biSize
        hdr.extend_from_slice(&(width as i32).to_le_bytes());
        hdr.extend_from_slice(&(-(height as i32)).to_le_bytes());
        hdr.extend_from_slice(&1u16.to_le_bytes()); // planes
        hdr.extend_from_slice(&32u16.to_le_bytes()); // bitcount
        hdr.extend_from_slice(&0u32.to_le_bytes()); // BI_RGB
        hdr.extend_from_slice(&(stride * height).to_le_bytes()); // sizeimage
        hdr.extend_from_slice(&0i32.to_le_bytes()); // xppm
        hdr.extend_from_slice(&0i32.to_le_bytes()); // yppm
        hdr.extend_from_slice(&0u32.to_le_bytes()); // clrused
        hdr.extend_from_slice(&0u32.to_le_bytes()); // clrimportant
        dib.extend_from_slice(&hdr);
        for &(r, g, b, a) in pixels {
            dib.extend_from_slice(&[b, g, r, a]);
        }
        dib
    }

    #[test]
    fn dib_32bpp_opaque_encodes() {
        let w = 2;
        let h = 1;
        let dib = dib_32bpp(
            w,
            h,
            &[(255, 0, 0, 255), (0, 255, 0, 255)],
        );
        let out = process_dib_to_png(&dib).expect("process");
        assert_eq!(out.width as u32, w);
        assert_eq!(out.height as u32, h);
        // PNG 魔数
        assert_eq!(&out.png[..8], &[0x89, b'P', b'N', b'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        // 不透明 → 无 alpha 通道（PNG IHDR color type 2 = truecolor RGB）
        assert_eq!(out.png[25], 2, "opaque image should encode as RGB PNG");
        let decoded = image::load_from_memory(&out.png).expect("decode png");
        assert_eq!(decoded.width(), 2);
        assert_eq!(decoded.height(), 1);
    }

    #[test]
    fn vacuous_alpha_normalized() {
        // 1502 红线：全 alpha=0 但 RGB 有内容 → 强制 255，PNG 解码后可见（非全黑）
        let w = 2;
        let h = 1;
        let dib = dib_32bpp(w, h, &[(255, 128, 0, 0), (0, 0, 255, 0)]);
        let out = process_dib_to_png(&dib).expect("process");
        let decoded = image::load_from_memory(&out.png).expect("decode");
        let rgba = decoded.to_rgba8();
        assert_eq!(rgba.get_pixel(0, 0).0, [255, 128, 0, 255]);
        assert_eq!(rgba.get_pixel(1, 0).0, [0, 0, 255, 255]);
    }

    #[test]
    fn real_alpha_preserved() {
        let w = 1;
        let h = 1;
        let dib = dib_32bpp(w, h, &[(10, 20, 30, 128)]);
        let out = process_dib_to_png(&dib).expect("process");
        let decoded = image::load_from_memory(&out.png).expect("decode");
        assert_eq!(decoded.to_rgba8().get_pixel(0, 0).0, [10, 20, 30, 128]);
    }

    #[test]
    fn hdrop_wide_parse() {
        // DROPFILES：20 字节结构（pFiles@0=20, fNC@12, fWide@16）+ 20 起 UTF-16 路径，双 null 结束
        let mut bytes = vec![0u8; 20];
        bytes[0..4].copy_from_slice(&20u32.to_le_bytes()); // pFiles 指向结构之后
        bytes[16] = 1; // fWide
        let mut paths = Vec::new();
        for p in ["C:\\a.txt", "D:\\b c.png"] {
            for u in p.encode_utf16() {
                paths.extend_from_slice(&u.to_le_bytes());
            }
            paths.extend_from_slice(&0u16.to_le_bytes());
        }
        bytes.extend_from_slice(&paths);
        bytes.extend_from_slice(&0u16.to_le_bytes());
        let files = parse_hdrop(&bytes);
        assert_eq!(files, vec!["C:\\a.txt", "D:\\b c.png"]);
    }

    /// 【2026-09-14 回归 · 锁死 DROPFILES 契约】构件输出必须能被自身解析器**原样**还原。
    ///
    /// 此前构造侧声明 `pFiles = 24` 而头部只有 20 字节，两边各自"自洽"于错误的偏移：
    /// 单测手工构造了 24 字节头所以全绿，真实消费方（资源管理器）却从 24 处读路径 →
    /// `C:\a.txt` 被读成 `\a.txt`（已实测复现）。本用例同时锁死 `pFiles == 20` 与 `fWide@16`。
    #[test]
    fn hdrop_build_parse_roundtrip() {
        let paths = vec!["C:\\a.txt".to_string(), "D:\\b c.png".to_string()];
        let out = build_hdrop(&paths);
        let p_files = u32::from_le_bytes([out[0], out[1], out[2], out[3]]) as usize;
        assert_eq!(p_files, 20, "pFiles 必须 = sizeof(DROPFILES) = 20");
        assert_eq!(out[16], 1, "fWide 必须写在偏移 16");
        assert_eq!(parse_hdrop(&out), paths, "构件输出必须能被自身解析器原样还原");
    }
}
