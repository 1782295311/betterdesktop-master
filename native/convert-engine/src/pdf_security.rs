// S7 PDF 加密（Standard Security，RC4-128 / R=3 / V=2；**PDF 2.0 修订算法**——对齐 lopdf 0.34 解密实现
// 与 Adobe/pdf.js 事实标准：file key 51 轮 MD5、U 值 = MD5(PAD+ID[0]) + RC4 链，O 值 owner key 51 轮 + 20 轮 RC4）。
// O1 spike 实证：lopdf 0.34 仅有 decrypt，无 encrypt 写入 API → 手写标准层（ISO 32000-2 第 7.6 节）。
// 能力诚实：128 位 RC4（PDFsharp 6.2.4 为 AES-256，强度差异不在菜单宣称算法——C# 侧同为"库内标准处理"）。
// 验证闭环：加密后自身 decrypt 可还原（测试覆盖，与 lopdf get_encryption_key 逐字节对齐）。
use std::path::Path;

use lopdf::{dictionary, Document, Object, StringFormat};

use crate::error::Error;

/// 标准 32 字节 padding（ISO 32000-2 表 21）。
const PADDING: [u8; 32] = [
    0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
    0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
];

/// 权限位（P）：bit1/2 保留=1，bit3-6（打印/修改/复制/标注）置 1 = 允许。
const PERMISSIONS: u32 = 0xFFFF_FFFC;

/// 密码 → 32 字节填充（截断/补齐）。
fn pad_password(pw: &str) -> Vec<u8> {
    let bytes = pw.as_bytes();
    let mut out = Vec::with_capacity(32);
    out.extend_from_slice(&bytes[..bytes.len().min(32)]);
    out.extend_from_slice(&PADDING[..32 - out.len()]);
    out
}

/// RC4 流加密（手写；避免 rc4 crate 的 cipher 0.4 新接口纠缠）。
fn rc4_encrypt(data: &mut [u8], key: &[u8]) {
    let mut s: [u8; 256] = [0; 256];
    for (i, v) in s.iter_mut().enumerate() {
        *v = i as u8;
    }
    let mut j = 0u8;
    for i in 0..256usize {
        j = j.wrapping_add(s[i]).wrapping_add(key[i % key.len()]);
        s.swap(i, j as usize);
    }
    let (mut i, mut j) = (0u8, 0u8);
    for b in data.iter_mut() {
        i = i.wrapping_add(1);
        j = j.wrapping_add(s[i as usize]);
        s.swap(i as usize, j as usize);
        *b ^= s[(s[i as usize].wrapping_add(s[j as usize])) as usize];
    }
}

fn rc4_bytes(data: &[u8], key: &[u8]) -> Vec<u8> {
    let mut v = data.to_vec();
    rc4_encrypt(&mut v, key);
    v
}

fn md5(bytes: &[u8]) -> Vec<u8> {
    use md5::Digest;
    md5::Md5::digest(bytes).to_vec()
}

/// 修订版哈希（ISO 32000-2 Algorithm 2：n_hashes 轮；R=3 → 51 轮，对齐 lopdf get_encryption_key）。
fn hash_key(input: &[u8], keylen: usize, rounds: usize) -> Vec<u8> {
    let mut key = input.to_vec();
    for _ in 0..rounds {
        let digest = md5(&key);
        key = digest[..keylen].to_vec();
    }
    key
}

/// 算法 3（修订版）：计算 O 值——owner key = 51 轮 MD5(owner_pw)，RC4(padding)，20 轮 XOR+RC4。
fn owner_entry(owner_pw: &str, keylen: usize) -> [u8; 32] {
    let mut okey = hash_key(&pad_password(owner_pw), keylen, 51);
    let mut data = PADDING.to_vec();
    rc4_encrypt(&mut data, &okey);
    for i in (0..20u8).rev() {
        for b in okey.iter_mut() {
            *b ^= i;
        }
        rc4_encrypt(&mut data, &okey);
    }
    data.try_into().expect("32 bytes")
}

/// 算法 2（修订版）：文件加密 key = 51 轮 MD5(padded_pw + O + P(LE) + ID[0]) 截断 keylen。
fn file_key(user_pw: &str, o: &[u8; 32], p: u32, id0: &[u8], keylen: usize) -> Vec<u8> {
    let mut buf = pad_password(user_pw);
    buf.extend_from_slice(o);
    buf.extend_from_slice(&p.to_le_bytes());
    buf.extend_from_slice(id0);
    hash_key(&buf, keylen, 51)
}

/// 算法 3.5（修订版，对齐 lopdf compute_user_password）：U = RC4 链(MD5(PAD+ID[0])) + PAD[0..16]。
fn user_entry(file_key: &[u8], id0: &[u8], keylen: usize) -> [u8; 32] {
    let mut ctx = PADDING.to_vec();
    ctx.extend_from_slice(id0);
    let hash = md5(&ctx);
    let mut encrypted = rc4_bytes(&hash, file_key);
    for i in 1..=19u8 {
        let temp_key: Vec<u8> = file_key[..keylen].iter().map(|b| b ^ i).collect();
        encrypted = rc4_bytes(&encrypted, &temp_key);
    }
    encrypted.extend_from_slice(&PADDING[..16]);
    encrypted.try_into().expect("32 bytes")
}

/// 算法 1：对象加密 key（file_key + obj 3LE + gen 2LE → MD5 截断）。
fn object_key(file_key: &[u8], obj_id: lopdf::ObjectId, keylen: usize) -> Vec<u8> {
    let mut key = file_key.to_vec();
    key.extend_from_slice(&obj_id.0.to_le_bytes()[..3]);
    key.extend_from_slice(&obj_id.1.to_le_bytes());
    let hash = md5(&key);
    hash[..keylen].to_vec()
}

/// 加密单个对象内的 string/stream（跳过加密字典本身——由调用方保证其不在 objects 中）。
fn encrypt_object(obj: &mut Object, file_key: &[u8], obj_id: lopdf::ObjectId, keylen: usize) {
    let k = object_key(file_key, obj_id, keylen);
    match obj {
        Object::String(s, _) => rc4_encrypt(s, &k),
        Object::Stream(s) => rc4_encrypt(&mut s.content, &k),
        _ => {}
    }
}

/// PDF 加密 → stem-encrypted.pdf（C# PdfSecurityEngine.Encrypt 语义：用户/所有者同密码）。
pub fn encrypt_pdf(input: &Path, password: &str, product: &Path) -> Result<(), Error> {
    if password.is_empty() {
        return Err(Error::input_invalid("密码为空（已取消或未输入）".to_string()));
    }
    let mut doc = Document::load(input)
        .map_err(|e| Error::conversion_failed(format!("PDF 打开失败: {e}")))?;
    if doc.is_encrypted() {
        return Err(Error::conversion_failed("PDF 已加密，请先解密再加密".to_string()));
    }
    const KEYLEN: usize = 16; // R=3：128 位

    // ID[0]（算法 2 输入；缺失则生成确定性伪随机 16 字节）
    let id0: Vec<u8> = match doc.trailer.get(b"ID").ok().and_then(|o| o.as_array().ok()) {
        Some(arr) if !arr.is_empty() => match &arr[0] {
            Object::String(s, _) => s.clone(),
            _ => gen_id0(),
        },
        _ => gen_id0(),
    };

    let o = owner_entry(password, KEYLEN);
    let fk = file_key(password, &o, PERMISSIONS, &id0, KEYLEN);
    let u = user_entry(&fk, &id0, KEYLEN);

    // 加密全部对象（此时文档无 Encrypt 字典，无跳过项）
    for (&id, obj) in doc.objects.iter_mut() {
        encrypt_object(obj, &fk, id, KEYLEN);
    }

    // 加密字典（O/U 十六进制字符串）
    let enc_id = doc.add_object(dictionary! {
        "Filter" => "Standard",
        "V" => 2,
        "R" => 3,
        "Length" => 128,
        "O" => Object::String(o.to_vec(), StringFormat::Hexadecimal),
        "U" => Object::String(u.to_vec(), StringFormat::Hexadecimal),
        "P" => PERMISSIONS as i64,
    });
    doc.trailer.set("Encrypt", enc_id);
    if doc.trailer.get(b"ID").is_err() {
        doc.trailer.set(
            "ID",
            Object::Array(vec![
                Object::String(id0.clone(), StringFormat::Hexadecimal),
                Object::String(id0, StringFormat::Hexadecimal),
            ]),
        );
    }
    // 注意：加密后不 compress——compress 会对已加密流再套 Flate，破坏与解密器的算法一致
    doc.save(product)
        .map_err(|e| Error::conversion_failed(format!("PDF 保存失败: {e}")))?;
    Ok(())
}

/// 确定性伪随机 ID（无 rand 依赖：时间 + 进程号 → MD5）。
fn gen_id0() -> Vec<u8> {
    let seed = format!(
        "{}-{}-{}",
        std::process::id(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|d| d.as_nanos())
            .unwrap_or(0),
        std::env::temp_dir().to_string_lossy()
    );
    md5(seed.as_bytes())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn make_pdf(dir: &Path, name: &str) -> std::path::PathBuf {
        let mut doc = Document::with_version("1.4");
        let pages_id = doc.new_object_id();
        let font_id = doc.add_object(dictionary! {
            "Type" => "Font", "Subtype" => "Type1", "BaseFont" => "Courier",
        });
        let resources_id = doc.add_object(dictionary! {
            "Font" => dictionary! { "F1" => font_id },
        });
        let content_id = doc.add_object(lopdf::Stream::new(
            dictionary! {},
            format!("BT /F1 24 Tf 100 700 Td (机密内容) Tj ET").into_bytes(),
        ));
        let page_id = doc.add_object(dictionary! {
            "Type" => "Page", "Parent" => pages_id, "Contents" => content_id,
            "Resources" => resources_id,
            "MediaBox" => vec![0.into(), 0.into(), 595.into(), 842.into()],
        });
        let pages = dictionary! {
            "Type" => "Pages", "Kids" => vec![Object::Reference(page_id)], "Count" => 1,
        };
        doc.objects.insert(pages_id, Object::Dictionary(pages));
        let catalog_id = doc.add_object(dictionary! { "Type" => "Catalog", "Pages" => pages_id });
        doc.trailer.set("Root", catalog_id);
        let p = dir.join(name);
        doc.save(&p).unwrap();
        p
    }

    #[test]
    fn encrypt_then_decrypt_roundtrip() {
        let dir = std::env::temp_dir().join(format!("bdt-pdf-sec-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let src = make_pdf(&dir, "src.pdf");
        let enc = dir.join("enc.pdf");
        encrypt_pdf(&src, "秘密密码123", &enc).unwrap();

        // 未解密打开应识别为加密
        let locked = Document::load(&enc).unwrap();
        assert!(locked.is_encrypted());

        // 用本模块 decrypt 链路还原（lopdf Document::decrypt）
        let mut doc = Document::load(&enc).unwrap();
        doc.decrypt("秘密密码123".as_bytes()).unwrap();
        // 内容流可读且含明文
        let page_id = *doc.get_pages().iter().next().unwrap().1;
        let content = doc.get_page_content(page_id).unwrap();
        assert!(String::from_utf8_lossy(&content).contains("机密内容"));
        std::fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn wrong_password_fails() {
        let dir = std::env::temp_dir().join(format!("bdt-pdf-secw-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let src = make_pdf(&dir, "src.pdf");
        let enc = dir.join("enc.pdf");
        encrypt_pdf(&src, "pw", &enc).unwrap();
        let mut doc = Document::load(&enc).unwrap();
        assert!(doc.decrypt("wrong".as_bytes()).is_err());
        std::fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn empty_password_rejected() {
        let dir = std::env::temp_dir().join(format!("bdt-pdf-sece-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let src = make_pdf(&dir, "src.pdf");
        let err = encrypt_pdf(&src, "", &dir.join("e.pdf")).unwrap_err();
        assert_eq!(err.code.as_str(), "InputInvalid");
        std::fs::remove_dir_all(&dir).ok();
    }
}
