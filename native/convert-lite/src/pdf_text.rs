//! PDF 文本层提取 -> txt/md（零外部二进制）。
//!
//! 解析：对象表扫描（N 0 obj ... endobj）→ trailer /Root → /Pages 树递归收集
//!   /Page → /Contents（引用或数组）→ FlateDecode 解压 → 内容流文本运算符
//!   （BT/ET 内 Tj/TJ/'/"、Td/TD/T* 产生换行）→ WinAnsi(Latin-1) 解码。
//!
//! 诚实边界：
//!   - 仅文本层（Tj/TJ 字符串）；扫描件/纯图片 PDF 返回空并报"无文本层"。
//!   - 嵌入 CID 字体（中文等 Identity-H 编码）无 ToUnicode CMap 时按字节解码，
//!     结果为 GID 原始字节（不可读）——与第八轮 doc_pdf 生成物一致；标准
//!     WinAnsi 字体（英文 PDF）可读。
//!   - 不解析 ToUnicode CMap / 字体宽度 / 版面坐标（不重建版式，按 Td 换行）。

use std::path::Path;

fn find_all(hay: &[u8], pat: &[u8]) -> Vec<usize> {
    let mut v = Vec::new();
    if pat.is_empty() || hay.len() < pat.len() {
        return v;
    }
    let mut i = 0usize;
    while i + pat.len() <= hay.len() {
        if &hay[i..i + pat.len()] == pat {
            v.push(i);
            i += pat.len();
        } else {
            i += 1;
        }
    }
    v
}

fn obj_scan(bytes: &[u8]) -> Vec<(u32, Vec<u8>)> {
    let mut objs = Vec::new();
    let mut i = 0usize;
    while i < bytes.len() {
        // 找 "N 0 obj"
        if let Some(rel) = find_byte(bytes, i, b"0 obj") {
            // 往前解析 "N 0 obj"：rel 指向 generation '0'，跳其前空格取对象编号 N
            let mut num_end = rel;
            while num_end > 0 && bytes[num_end - 1] == b' ' {
                num_end -= 1;
            }
            let mut num_start = num_end;
            while num_start > 0 && bytes[num_start - 1].is_ascii_digit() {
                num_start -= 1;
            }
            let num: u32 = std::str::from_utf8(&bytes[num_start..num_end])
                .ok()
                .and_then(|s| s.trim().parse().ok())
                .unwrap_or(0);
            let body_start = rel + b"0 obj".len();
            let body_end = find_sub(bytes, body_start, b"endobj").unwrap_or(bytes.len());
            objs.push((num, bytes[body_start..body_end].to_vec()));
            i = body_end + 6;
        } else {
            break;
        }
    }
    objs
}

fn find_byte(hay: &[u8], from: usize, pat: &[u8]) -> Option<usize> {
    if from >= hay.len() {
        return None;
    }
    hay[from..]
        .windows(pat.len())
        .position(|w| w == pat)
        .map(|p| from + p)
}

fn find_sub(hay: &[u8], from: usize, pat: &[u8]) -> Option<usize> {
    find_byte(hay, from, pat)
}

/// 取字典中 /Key 的值原文（从 key 后到下一个 / 或 >>）。
fn dict_get(dict: &[u8], key: &[u8]) -> Option<Vec<u8>> {
    let mut search = 0usize;
    loop {
        let pos = find_sub(dict, search, key)?;
        // 确认是 /Key 边界（前面是 / 或空格，后面不是字母）
        if pos == 0 || dict[pos - 1] != b'/' {
            search = pos + key.len();
            continue;
        }
        // key 后应是非字母数字
        let after = pos + key.len();
        if after < dict.len() && dict[after].is_ascii_alphanumeric() {
            search = after;
            continue;
        }
        // 值从 key 后（跳过空白）到下一个 / 或 >> 或数组结束
        let mut v = after;
        while v < dict.len() && (dict[v] == b' ' || dict[v] == b'\n' || dict[v] == b'\r' || dict[v] == b'\t') {
            v += 1;
        }
        let start = v;
        while v < dict.len() && dict[v] != b'/' && !(v + 1 < dict.len() && dict[v] == b'>' && dict[v + 1] == b'>') {
            v += 1;
        }
        return Some(dict[start..v].to_vec());
    }
}

/// 解析引用 "N 0 R"。
fn parse_ref(v: &[u8]) -> Option<u32> {
    let s = std::str::from_utf8(v).ok()?.trim();
    let mut parts = s.split_whitespace();
    let n: u32 = parts.next()?.parse().ok()?;
    parts.next()?; // 0
    if parts.next()? != "R" {
        return None;
    }
    Some(n)
}

/// 从流对象 dict 中取 /Length 与 stream 数据。
fn stream_data(obj: &[u8]) -> Option<(Vec<u8>, bool)> {
    let s = find_sub(obj, 0, b"stream")?;
    let mut data_start = s + 6;
    if data_start < obj.len() && obj[data_start] == b'\r' {
        data_start += 1;
    }
    if data_start < obj.len() && obj[data_start] == b'\n' {
        data_start += 1;
    }
    let e = find_sub(obj, data_start, b"endstream")?;
    Some((obj[data_start..e].to_vec(), true))
}

fn inflate(data: &[u8]) -> Result<Vec<u8>, String> {
    use flate2::read::ZlibDecoder;
    use std::io::Read;
    let mut out = Vec::new();
    ZlibDecoder::new(data)
        .read_to_end(&mut out)
        .map_err(|e| format!("FlateDecode 解压失败：{e}"))?;
    Ok(out)
}

/// 解析 PDF 字符串字面量（含转义）。
fn parse_pdf_string(bytes: &[u8], start: usize) -> Option<(Vec<u8>, usize)> {
    let b = bytes[start];
    if b == b'(' {
        let mut out = Vec::new();
        let mut i = start + 1;
        let mut depth = 1usize;
        while i < bytes.len() {
            match bytes[i] {
                b'\\' if i + 1 < bytes.len() => {
                    let c = bytes[i + 1];
                    match c {
                        b'n' => out.push(b'\n'),
                        b'r' => out.push(b'\r'),
                        b't' => out.push(b'\t'),
                        b'b' => out.push(0x08),
                        b'f' => out.push(0x0C),
                        b'(' => out.push(b'('),
                        b')' => out.push(b')'),
                        b'\\' => out.push(b'\\'),
                        d if d.is_ascii_digit() => {
                            // \ddd 八进制
                            let mut v = 0u8;
                            let mut k = 0;
                            while k < 3 && i + 1 + k < bytes.len() && bytes[i + 1 + k].is_ascii_digit() {
                                v = v * 8 + (bytes[i + 1 + k] - b'0');
                                k += 1;
                            }
                            out.push(v);
                            i += k - 1;
                        }
                        _ => out.push(c),
                    }
                    i += 2;
                }
                b'(' => {
                    depth += 1;
                    out.push(b'(');
                    i += 1;
                }
                b')' => {
                    depth -= 1;
                    if depth == 0 {
                        return Some((out, i + 1));
                    }
                    out.push(b')');
                    i += 1;
                }
                c => {
                    out.push(c);
                    i += 1;
                }
            }
        }
        None
    } else if b == b'<' {
        // 十六进制字符串
        let mut out = Vec::new();
        let mut i = start + 1;
        while i < bytes.len() && bytes[i] != b'>' {
            if bytes[i].is_ascii_hexdigit() && i + 1 < bytes.len() && bytes[i + 1].is_ascii_hexdigit() {
                let hi = (bytes[i] as char).to_digit(16).unwrap();
                let lo = (bytes[i + 1] as char).to_digit(16).unwrap();
                out.push((hi * 16 + lo) as u8);
                i += 2;
            } else {
                i += 1;
            }
        }
        Some((out, (i + 1).min(bytes.len())))
    } else {
        None
    }
}

/// WinAnsi/Latin-1 字节 -> 字符串（可打印）。
fn winansi_decode(bytes: &[u8]) -> String {
    let mut out = String::new();
    for &b in bytes {
        match b {
            0x20..=0x7E => out.push(b as char),
            0xA0..=0xFF => out.push((b as char)), // Latin-1 区
            _ => out.push(' '),                    // 控制/扩展字节 -> 空格
        }
    }
    out
}

/// 从内容流提取文本（Tj/TJ，Td/TD/T* 换行）。
fn extract_stream_text(stream: &[u8]) -> String {
    let mut out = String::new();
    let mut i = 0usize;
    let n = stream.len();
    while i < n {
        // 跳过空白
        while i < n && (stream[i] == b' ' || stream[i] == b'\n' || stream[i] == b'\r' || stream[i] == b'\t') {
            i += 1;
        }
        if i >= n {
            break;
        }
        let c = stream[i];
        if c == b'(' || c == b'<' {
            if let Some((text, end)) = parse_pdf_string(stream, i) {
                // 后续运算符
                let mut j = end;
                while j < n && (stream[j] == b' ' || stream[j] == b'\n' || stream[j] == b'\r' || stream[j] == b'\t') {
                    j += 1;
                }
                // 跳过数值（TJ 数组中的数字偏移）
                if j < n && (stream[j] == b'-' || stream[j].is_ascii_digit()) {
                    while j < n && (stream[j] == b'-' || stream[j].is_ascii_digit() || stream[j] == b'.') {
                        j += 1;
                    }
                    while j < n && (stream[j] == b' ' || stream[j] == b'\n' || stream[j] == b'\r' || stream[j] == b'\t') {
                        j += 1;
                    }
                }
                if j + 1 < n && &stream[j..j + 2] == b"TJ" {
                    out.push_str(&winansi_decode(&text));
                    i = j + 2;
                } else if j < n && stream[j] == b'T' && (j + 1 >= n || stream[j + 1] == b'j') {
                    out.push_str(&winansi_decode(&text));
                    i = j + 1;
                } else {
                    // 非文本运算符的字符串：跳过
                    i = end;
                }
                continue;
            }
            i += 1;
            continue;
        }
        // 定位运算符
        if c == b'T' {
            // Td TD T* TJ Tj
            let op = if i + 1 < n {
                if stream[i + 1] == b'd' {
                    Some(b"Td")
                } else if stream[i + 1] == b'D' {
                    Some(b"TD")
                } else if stream[i + 1] == b'*' {
                    Some(b"T*")
                } else if stream[i + 1] == b'j' {
                    Some(b"Tj")
                } else if stream[i + 1] == b'J' {
                    Some(b"TJ")
                } else {
                    None
                }
            } else {
                None
            };
            if let Some(op) = op {
                if op == b"Td" || op == b"TD" || op == b"T*" {
                    if !out.is_empty() && !out.ends_with('\n') {
                        out.push('\n');
                    }
                }
                i += 2;
                continue;
            }
        }
        i += 1;
    }
    out
}

/// 递归收集页对象引用。
fn collect_pages(objs: &[(u32, Vec<u8>)], node: u32, pages: &mut Vec<u32>, depth: usize) {
    if depth > 20 {
        return;
    }
    if let Some((_, obj)) = objs.iter().find(|(num, _)| *num == node) {
        if let Some(kids) = dict_get(obj, b"Kids") {
            // 解析 [N 0 R N 0 R ...]（字节扫描 "0 R" 模式）
            let kb = kids.as_slice();
            let mut i = 0usize;
            while i + 2 < kb.len() {
                if kb[i] == b'R' && i >= 3 && kb[i - 1] == b' ' && kb[i - 2] == b'0' && kb[i - 3] == b' ' {
                    let mut j = i - 3;
                    while j > 0 && kb[j - 1].is_ascii_digit() {
                        j -= 1;
                    }
                    if let Ok(n) = std::str::from_utf8(&kb[j..i - 3]).unwrap_or("").trim().parse::<u32>() {
                        if let Some((_, o2)) = objs.iter().find(|(num, _)| *num == n) {
                            if dict_get(o2, b"Kids").is_some() {
                                collect_pages(objs, n, pages, depth + 1);
                            } else {
                                pages.push(n);
                            }
                        }
                    }
                    i += 3;
                } else {
                    i += 1;
                }
            }
        }
    }
}

/// PDF -> 文本（纯文本层，无版式重建）。
pub fn pdf_to_text(path: &Path) -> Result<String, String> {
    let bytes = std::fs::read(path).map_err(|e| format!("读取失败：{e}"))?;
    if bytes.len() < 8 || &bytes[0..5] != b"%PDF-" {
        return Err("非 PDF 文件（缺少 %PDF- 头）".into());
    }
    let objs = obj_scan(&bytes);
    if objs.is_empty() {
        return Err("PDF 对象表解析失败".into());
    }
    // 根：trailer /Root 或第一个 /Type /Catalog
    let mut root: Option<u32> = None;
    if let Some(tpos) = find_sub(&bytes, 0, b"trailer") {
        let tseg = &bytes[tpos..(tpos + 8192).min(bytes.len())];
        if let Some(rv) = dict_get(tseg, b"Root") {
            root = parse_ref(&rv);
        }
    }
    if root.is_none() {
        // 找 Catalog
        for (num, obj) in &objs {
            if dict_get(obj, b"Type") == Some(b"/Catalog".to_vec()) {
                root = Some(*num);
                break;
            }
        }
    }
    let root = match root {
        Some(r) => r,
        None => return Err("未找到 PDF 根对象（/Catalog）".into()),
    };
    let mut pages: Vec<u32> = Vec::new();
    if let Some((_, robj)) = objs.iter().find(|(num, _)| *num == root) {
        if let Some(pages_ref) = dict_get(robj, b"Pages") {
            if let Some(pn) = parse_ref(&pages_ref) {
                collect_pages(&objs, pn, &mut pages, 0);
            }
        }
    }
    if pages.is_empty() {
        return Err("未找到页对象（可能为非标准 PDF）".into());
    }
    let mut out = String::new();
    let mut page_no = 0usize;
    for p in &pages {
        if let Some((_, pobj)) = objs.iter().find(|(num, _)| *num == *p) {
            let mut streams: Vec<Vec<u8>> = Vec::new();
            if let Some(cont) = dict_get(pobj, b"Contents") {
                if let Some(refn) = parse_ref(&cont) {
                    if let Some((_, cobj)) = objs.iter().find(|(num, _)| *num == refn) {
                        if let Some((data, _)) = stream_data(cobj) {
                            streams.push(data);
                        }
                    }
                } else {
                    // 数组 [N 0 R N 0 R]（字节扫描 "0 R"）
                    let cb = cont.as_slice();
                    let mut k = 0usize;
                    while k + 2 < cb.len() {
                        if cb[k] == b'R' && k >= 3 && cb[k - 1] == b' ' && cb[k - 2] == b'0' && cb[k - 3] == b' ' {
                            let mut j = k - 3;
                            while j > 0 && cb[j - 1].is_ascii_digit() {
                                j -= 1;
                            }
                            if let Ok(n) = std::str::from_utf8(&cb[j..k - 3]).unwrap_or("").trim().parse::<u32>() {
                                if let Some((_, cobj)) = objs.iter().find(|(num, _)| *num == n) {
                                    if let Some((data, _)) = stream_data(cobj) {
                                        streams.push(data);
                                    }
                                }
                            }
                            k += 3;
                        } else {
                            k += 1;
                        }
                    }
                }
            }
            page_no += 1;
            if page_no > 1 {
                out.push_str(&format!("\n\n--- 第 {page_no} 页 ---\n\n"));
            }
            for data in &streams {
                let inflated = if data.len() >= 2 && data[0] == 0x78 {
                    inflate(data).unwrap_or_else(|_| data.clone())
                } else {
                    data.clone()
                };
                let t = extract_stream_text(&inflated);
                if !t.trim().is_empty() {
                    out.push_str(&t);
                    out.push('\n');
                }
            }
        }
    }
    let out = out.trim().to_string();
    if out.is_empty() {
        return Err("无文本层（扫描件/图片 PDF，或嵌入 CID 字体无 ToUnicode）".into());
    }
    Ok(out)
}
