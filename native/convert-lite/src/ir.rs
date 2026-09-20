//! 轻量统一中间表示（IR）——验证"解析 → IR → 生成"管线。
//!
//! 设计克制：spike 阶段只表达"文档由若干页组成，每页可放一张整页图片"。
//! 不追求富文本/表格/浮动排版——那是 docling.rs / CasualOffice/core 级别的工程，
//! 这里只验证架构方向：新增格式只需写一个 parser（产出 DocIR）和一个 renderer（消费 DocIR）。

/// 一页。当前只承载一张整页图片（PDF 中以 DCTDecode 直接嵌入 JPEG，无需重编码）。
#[derive(Debug, Clone)]
pub struct Page {
    /// 图像像素宽高（来自源格式；JPEG 由 SOF 解析得到）。
    pub width_px: u32,
    pub height_px: u32,
    /// 原始 JPEG 字节。
    pub jpeg: Vec<u8>,
}

/// 文档 IR：与具体输入/输出格式无关。
#[derive(Debug, Clone)]
pub struct DocIR {
    pub pages: Vec<Page>,
}

impl DocIR {
    pub fn new() -> Self {
        Self { pages: Vec::new() }
    }
    pub fn push(&mut self, page: Page) {
        self.pages.push(page);
    }
    pub fn len(&self) -> usize {
        self.pages.len()
    }
    pub fn is_empty(&self) -> bool {
        self.pages.is_empty()
    }
}

/// 从 JPEG 字节解析像素宽高。
/// 原理：JPEG 段以 0xFF 开头；扫到 SOF0(0xFFC0)/SOF2(0xFFC2) 等帧头，
/// 其后 2 字节为高度、2 字节为宽度。其他段按其声明长度跳过。
/// 不依赖任何图像库——PDF 直接嵌入 JPEG (DCTDecode) 时只需要知道宽高。
pub fn jpeg_size(data: &[u8]) -> Result<(u32, u32), String> {
    if data.len() < 4 || &data[0..2] != &[0xFF, 0xD8] {
        return Err("不是 JPEG（缺 SOI 标记）".into());
    }
    let mut i = 2;
    while i + 1 < data.len() {
        if data[i] != 0xFF {
            return Err(format!("JPEG 段同步失败 @ {i}"));
        }
        let marker = data[i + 1];
        match marker {
            0xFF => {
                i += 1;
                continue;
            }
            0xD8 | 0x01 | 0xD9 => {
                i += 2;
                continue;
            }
            m if (0xD0..=0xD7).contains(&m) => {
                i += 2;
                continue;
            }
            _ => {}
        }
        if (0xC0..=0xCF).contains(&marker)
            && marker != 0xC4
            && marker != 0xC8
            && marker != 0xCC
        {
            if i + 9 >= data.len() {
                return Err("SOF 段不完整".into());
            }
            let h = u16::from_be_bytes([data[i + 5], data[i + 6]]) as u32;
            let w = u16::from_be_bytes([data[i + 7], data[i + 8]]) as u32;
            if w == 0 || h == 0 {
                return Err("SOF 宽高为 0".into());
            }
            return Ok((w, h));
        }
        if i + 3 >= data.len() {
            return Err("JPEG 段不完整".into());
        }
        let seg_len = u16::from_be_bytes([data[i + 2], data[i + 3]]) as usize;
        if seg_len < 2 {
            return Err("JPEG 段长非法".into());
        }
        i += 2 + seg_len;
    }
    Err("未找到 JPEG SOF 帧头".into())
}
