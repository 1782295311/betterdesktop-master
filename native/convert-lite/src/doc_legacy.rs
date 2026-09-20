//! .doc（Word 97-2003 二进制格式）-> Markdown。
//!
//! 纯 Rust：OLE2 复合文档读 WordDocument 流 -> FIB（文件信息块）定位文本区
//!   （fcMin..fcMac）-> fExtChar 决定 UTF-16LE / ANSI 解码 -> 段落化输出 md。
//!
//! 诚实边界：只提取正文文本与段落结构，不解析 CHPX/样式（粗体/字号/表格
//!   边框等格式降级为纯文本）；目录/页眉页脚/批注忽略；加密文档（fEncrypted）
//!   报错；ANSI 文本按 GBK 解码（encoding_rs）。

use std::path::Path;

use crate::ole2::Compound;

fn u16_at(b: &[u8], i: usize) -> u16 {
    u16::from_le_bytes([b[i], b[i + 1]])
}
fn u32_at(b: &[u8], i: usize) -> u32 {
    u32::from_le_bytes([b[i], b[i + 1], b[i + 2], b[i + 3]])
}

fn utf16le_to_string(b: &[u8]) -> String {
    let units: Vec<u16> = b
        .chunks(2)
        .map(|c| u16::from_le_bytes([c[0], c[1]]))
        .collect();
    String::from_utf16_lossy(&units)
}

/// .doc -> Markdown 纯文本（段落结构）。
pub fn doc_to_md(path: &Path) -> Result<String, String> {
    let c = Compound::open(path)?;
    if c.stream_size("WordDocument").unwrap_or(0) < 0x40 {
        return Err(format!(
            "WordDocument 流缺失或过短（可用流：{:?}），不是有效的 .doc",
            c.stream_names()
        ));
    }
    let wd = c.read_stream("WordDocument")?;
    if u16_at(&wd, 0) != 0xA5EC {
        return Err("WordDocument 流签名错误（非 Word 97-2003 二进制）".into());
    }
    let flags = u16_at(&wd, 0x0A);
    if flags & 0x0100 != 0 {
        return Err("文档已加密（fEncrypted），无法读取".into());
    }
    // fExtChar 在 flags 的 bit12（0x1000）：1 = UTF-16LE 文本，0 = ANSI(GBK)
    let f_ext_char = flags & 0x1000 != 0;
    // fcMin/fcMac 位于 FIBBase 0x18/0x1C（Word 97-2003 二进制实测布局）
    let fc_min = u32_at(&wd, 0x18) as usize;
    let fc_mac = u32_at(&wd, 0x1C) as usize;
    if fc_min >= fc_mac || fc_min >= wd.len() {
        return Err(format!("文本区无效（fcMin={fc_min} fcMac={fc_mac}）"));
    }
    let end = fc_mac.min(wd.len());
    let raw = &wd[fc_min..end];
    let text = if f_ext_char {
        utf16le_to_string(raw)
    } else {
        match std::str::from_utf8(raw) {
            Ok(s) => s.to_string(),
            Err(_) => {
                let (s, _, _) = encoding_rs::GBK.decode(raw);
                s.into_owned()
            }
        }
    };

    // 控制字符清理：\x07 表格单元格分隔、\x0b 垂直制表、\x0c 分页
    let mut cleaned = text
        .replace('\u{0007}', "\t")
        .replace('\u{000b}', "\n")
        .replace('\u{000c}', "\n")
        .replace('\u{000d}', "\n")
        .replace('\u{0001}', "\t");
    // Word 段落符是 \r（0x0D），已换行。合并连续换行
    while cleaned.contains("\n\n\n") {
        cleaned = cleaned.replace("\n\n\n", "\n\n");
    }
    let mut out = String::new();
    for para in cleaned.split('\n') {
        let p = para.trim();
        if p.is_empty() {
            continue;
        }
        out.push_str(p);
        out.push_str("\n\n");
    }
    let out = out.trim_end().to_string();
    if out.is_empty() {
        return Err("未提取到正文文本（可能是纯图片/空文档）".into());
    }
    Ok(out)
}

/// .doc -> 文本（供 csv/jsonl 之外的目标复用）。
pub fn doc_to_text(path: &Path) -> Result<String, String> {
    doc_to_md(path)
}
