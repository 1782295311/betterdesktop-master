//! 【P1-4 命名格式透传 · 2026-09-13】采集/还原自定义（命名）剪贴板格式。
//!
//! **背景**：Excel / WPS 表格的"可编辑表格"数据放在应用自己注册的**命名格式**里
//!（如 `Microsoft Excel Worksheet`），不在 `CF_UNICODETEXT` / `HTML Format` / `Rich Text Format` 中。
//! 我们此前只读写标准格式 → 用户把历史条目粘回表格时，Excel 拿不到自家格式 → **降级成纯文本**。
//!
//! **策略**：不做格式名白名单猜测（Excel / WPS / 各版本格式名不一，猜错 = 功能不生效），
//! 而是「无名 CF_ 标准格式 + 已知已处理格式」排除 + **三重体积上限**：
//!   ① 条数 `count` ② 单项字节 `each_bytes` ③ 单条目总字节 `total_bytes`。
//! 超限**跳过而非截断**（截断后的二进制是坏数据）。只做字节搬运 —— **不解释、不执行**；
//! 持久化走全库 DPAPI 加密，base64 仅为 JSON 安全。
//!
//! **调用约定**：`collect` 要求剪贴板**已打开**（由 `capture::read_snapshot` 持锁调用）；
//! `write_back` 要求剪贴板**已打开且已 EmptyClipboard**（由 `capture::write_back` 调用）。

use base64::Engine as _;
use windows::core::{w, PCWSTR};
use windows::Win32::System::DataExchange::{
    EnumClipboardFormats, GetClipboardFormatNameW, RegisterClipboardFormatW,
};

use crate::capture::{read_format_bytes, set_clipboard_bytes, CF_DIB, CF_HDROP, CF_UNICODETEXT};
use crate::model::NamedFormat;

/// 采集上限（字节单位；`0` 任一维度 = 关闭采集）。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Limits {
    /// 单条目最多保留的格式数。
    pub count: usize,
    /// 单个格式字节上限（超限跳过该格式）。
    pub each_bytes: usize,
    /// 单条目格式总字节上限（超限停止采集）。
    pub total_bytes: usize,
}

/// 已知格式（标准 CF_ 或我们已单独读写的注册格式）—— 这些**不进命名格式通道**，避免重复占用体积。
fn is_known(format: u32) -> bool {
    if matches!(format, CF_DIB | CF_UNICODETEXT | CF_HDROP) {
        return true;
    }
    // 我们已单独处理的注册格式（名字固定、id 在本会话内全局一致）
    for name in [
        w!("HTML Format"),
        w!("Rich Text Format"),
        w!("Rich Text Format Without Objects"),
        w!("PNG"),
    ] {
        let id = unsafe { RegisterClipboardFormatW(name) };
        if id != 0 && id == format {
            return true;
        }
    }
    false
}

/// 反查格式名；返回 `None` = 无名（即 CF_ 标准格式，不属于我们的透传范围）。
fn format_name(format: u32) -> Option<String> {
    let mut buf = [0u16; 256];
    let len = unsafe { GetClipboardFormatNameW(format, &mut buf) };
    if len <= 0 {
        return None;
    }
    Some(String::from_utf16_lossy(&buf[..len as usize]))
}

/// 枚举并采集当前剪贴板里的**命名格式**（调用方须已打开剪贴板）。
///
/// 逐条按上限校验：单项超限跳过该格式；总字节超限停止采集；条数超限停止采集。
/// 全部跳过时不产生任何输出（普通文本复制零开销）。
pub fn collect(limits: Limits) -> Vec<NamedFormat> {
    if limits.count == 0 || limits.each_bytes == 0 || limits.total_bytes == 0 {
        return Vec::new();
    }

    let mut out: Vec<NamedFormat> = Vec::new();
    let mut total = 0usize;
    let mut format = 0u32;

    loop {
        format = unsafe { EnumClipboardFormats(format) };
        if format == 0 {
            break;
        }
        if is_known(format) {
            continue;
        }
        let Some(name) = format_name(format) else {
            continue; // 无名 = CF_ 标准格式
        };
        if out.len() >= limits.count {
            crate::log::info(format!(
                "named-format: 已达条数上限 {}，停止采集（{name} 起跳过）",
                limits.count
            ));
            break;
        }
        let Some(bytes) = read_format_bytes(format) else {
            continue;
        };
        if bytes.is_empty() {
            continue;
        }
        if bytes.len() > limits.each_bytes {
            crate::log::info(format!(
                "named-format: '{name}' {}B 超过单项上限 {}B，跳过",
                bytes.len(),
                limits.each_bytes
            ));
            continue;
        }
        if total + bytes.len() > limits.total_bytes {
            crate::log::info(format!(
                "named-format: 已达总字节上限 {}B，停止采集（{name} 起跳过）",
                limits.total_bytes
            ));
            break;
        }
        total += bytes.len();
        out.push(NamedFormat {
            name,
            data_base64: base64::engine::general_purpose::STANDARD.encode(&bytes),
        });
    }

    if !out.is_empty() {
        crate::log::info(format!(
            "named-format: 采集 {} 项、共 {}B（写回时将按同名还原）",
            out.len(),
            total
        ));
    }
    out
}

/// 把命名格式写回剪贴板（调用方须已打开剪贴板且已 `EmptyClipboard`）。
///
/// 单个格式失败不阻断其余（返回成功写入的条数）；base64 解码失败 / 名字非法均只记日志。
pub fn write_back(formats: &[NamedFormat]) -> usize {
    let mut written = 0usize;
    for f in formats {
        if f.name.is_empty() || f.data_base64.is_empty() {
            continue;
        }
        let Ok(bytes) = base64::engine::general_purpose::STANDARD.decode(&f.data_base64) else {
            crate::log::warn(format!("named-format: '{}' base64 解码失败，跳过", f.name));
            continue;
        };
        if bytes.is_empty() {
            continue;
        }
        let wide: Vec<u16> = f.name.encode_utf16().chain(std::iter::once(0)).collect();
        let id = unsafe { RegisterClipboardFormatW(PCWSTR(wide.as_ptr())) };
        if id == 0 {
            crate::log::warn(format!(
                "named-format: RegisterClipboardFormatW('{}') 返回 0，跳过",
                f.name
            ));
            continue;
        }
        if set_clipboard_bytes(id, &bytes) {
            written += 1;
        }
    }
    if written > 0 {
        crate::log::info(format!("named-format: 写回 {written}/{} 项", formats.len()));
    }
    written
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn zero_limit_disables_collection() {
        assert!(collect(Limits { count: 0, each_bytes: 1024, total_bytes: 4096 }).is_empty());
        assert!(collect(Limits { count: 8, each_bytes: 0, total_bytes: 4096 }).is_empty());
        assert!(collect(Limits { count: 8, each_bytes: 1024, total_bytes: 0 }).is_empty());
    }

    /// 标准 CF_ 与已处理的注册格式必须被排除 —— 否则它们会被重复搬进 base64 字段。
    #[test]
    fn known_formats_are_excluded() {
        assert!(is_known(CF_DIB));
        assert!(is_known(CF_UNICODETEXT));
        assert!(is_known(CF_HDROP));
        // 任意未注册 id 不属于已知格式（RegisterClipboardFormat 的 id 从 0xC000 起）
        assert!(!is_known(0xC123));
    }

    #[test]
    fn empty_input_writes_nothing() {
        assert_eq!(write_back(&[]), 0);
        let bad = vec![NamedFormat { name: String::new(), data_base64: "AAAA".into() }];
        assert_eq!(write_back(&bad), 0);
        let bad_b64 = vec![NamedFormat { name: "X".into(), data_base64: "!!!not-base64".into() }];
        assert_eq!(write_back(&bad_b64), 0);
    }
}
