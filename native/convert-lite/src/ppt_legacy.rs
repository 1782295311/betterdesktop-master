//! .ppt（PowerPoint 97-2003 二进制格式）-> Markdown/纯文本。
//!
//! 纯 Rust：OLE2 读 "PowerPoint Document" 流 -> 递归扫描记录（容错：
//!   记录头首字节低 4 位 recVer=0xF 或 recType 为已知容器类型则递归；
//!   recLen 越界视为 persist 空隙，跳过 8 字节继续）-> 提取
//!   TextCharsAtom（UTF-16LE）与 TextBytesAtom（压缩字节）文本，
//!   过滤母版模板占位（"单击此处编辑…"等）后按行输出。
//!
//! 诚实边界：只提取文本（不解析占位符/版式/动画/图表对象）；
//!   颜色/字体/位置降级；模板占位文本（母版"单击此处编辑…"）被过滤；
//!   大纲与幻灯片文本按出现顺序输出，幻灯片之间用空行分隔。

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

/// 容器记录类型（recVer 可能非 0xF，如 SlideListWithTextContainer=0x03E9 的 recVer=0x1）。
const CONTAINERS: &[u16] = &[
    0x03E8, // DocumentContainer
    0x03E9, // SlideListWithTextContainer（recVer=0x1）
    0x03EA, 0x03EB, 0x03EC, // Handout 等
    0x03EE, // SlideContainer
    0x03EF, // NotesContainer
    0x03F2, 0x03F8, // MasterListContainer
    0x03F9, // MasterList
    0x07D5, // 老版 Shape 容器
    0x0F00, // ShapeContainer
    0x0FBA, // MainMasterContainer
    0x0FBC, // SlideMasterContainer
    0x0FBF, // SlideLayoutContainer
    0x0FF0, // SlideListWithText
    0x0FF1, // SlideListWithText（扩展）
    0x0FF2, // SlideListWithText（旧）
];

/// 模板占位文本（母版默认提示）过滤。
fn is_placeholder(s: &str) -> bool {
    let s = s.trim();
    s.is_empty()
        || s == "*"
        || s.starts_with("单击此处编辑")
        || s.starts_with("单击此处添加")
        || s == "标题"
        || s == "文本"
}

fn walk(b: &[u8], pos: usize, out: &mut String) {
    let mut pos = pos;
    while pos + 8 <= b.len() {
        let b0 = b[pos];
        let rec_ver = b0 & 0x0F;
        let rec_type = u16_at(b, pos + 2);
        let rec_len = u32_at(b, pos + 4) as usize;
        if pos + 8 + rec_len > b.len() {
            // persist 空隙/未知记录：跳过 8 字节继续
            pos += 8;
            continue;
        }
        let data = &b[pos + 8..pos + 8 + rec_len];
        match rec_type {
            0x0FA0 => {
                // TextCharsAtom（UTF-16LE）
                if rec_len >= 2 && rec_len % 2 == 0 {
                    let s = utf16le_to_string(data);
                    let s = s.trim_end_matches('\0').trim();
                    if !s.is_empty() {
                        out.push_str(s);
                        out.push('\n');
                    }
                }
            }
            0x0FA8 => {
                // TextBytesAtom（压缩字节，latin-1 近似）
                let s = String::from_utf8_lossy(data);
                let s = s.trim_end_matches('\0').trim();
                if !s.is_empty() {
                    out.push_str(s);
                    out.push('\n');
                }
            }
            _ => {
                if rec_ver == 0x0F || CONTAINERS.contains(&rec_type) {
                    walk(data, 0, out);
                }
            }
        }
        pos += 8 + rec_len;
    }
}

/// .ppt -> Markdown/纯文本（每行一个文本框，过滤母版模板占位）。
pub fn ppt_to_md(path: &Path) -> Result<String, String> {
    let c = Compound::open(path)?;
    let doc = c.read_stream("PowerPoint Document")?;
    if doc.len() < 8 {
        return Err("PowerPoint Document 流过短".into());
    }
    let mut raw = String::new();
    walk(&doc, 0, &mut raw);
    let mut out = String::new();
    for line in raw.lines() {
        let l = line.trim();
        // 母版模板占位整块过滤（含 \r 分隔的层级，如"单击此处编辑母版文本样式\r二级\r三级…"）
        if l.is_empty() || l.starts_with("单击此处编辑") || l.starts_with("单击此处添加") {
            continue;
        }
        for piece in l.split('\n') {
            let piece = piece.trim();
            if !piece.is_empty() && !is_placeholder(piece) {
                out.push_str(piece);
                out.push('\n');
            }
        }
    }
    let mut out = out.trim_end().to_string();
    if out.is_empty() {
        return Err("未提取到文本（可能全是图片/图表）".into());
    }
    while out.contains("\n\n\n") {
        out = out.replace("\n\n\n", "\n\n");
    }
    Ok(out)
}
