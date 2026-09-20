//! 加密/解密域（纯 Rust，AES-256-GCM 认证加密）。
//!
//! 文件格式（自包含头 + 密文）：
//!   magic "CVLT" (4B) | version u8 = 1 | salt 16B | nonce 12B | AES-256-GCM 密文(含 16B tag)
//! 密钥派生：PBKDF2-HMAC-SHA256(口令, salt, 100_000 轮, 32B)。
//! 认证：GCM 自带 AEAD，密文被篡改/口令错误 -> 解密报错，不会静默产出错误数据。
//! 诚实边界：PBKDF2 迭代固定 100k（可调参数未暴露）；不支持口令文件/多密钥。

use aes_gcm::aead::{Aead, KeyInit};
use aes_gcm::{Aes256Gcm, Nonce};
use rand::RngCore;
use std::path::Path;

const MAGIC: &[u8; 4] = b"CVLT";
const VERSION: u8 = 1;
const SALT_LEN: usize = 16;
const NONCE_LEN: usize = 12;
const KEY_LEN: usize = 32;
const ITER: u32 = 100_000;

fn derive_key(pass: &str, salt: &[u8]) -> [u8; KEY_LEN] {
    let mut key = [0u8; KEY_LEN];
    pbkdf2::pbkdf2_hmac::<sha2::Sha256>(pass.as_bytes(), salt, ITER, &mut key);
    key
}

/// 文件 -> AES-256-GCM 加密字节（随机盐 + 随机 nonce，自包含头）。
pub fn encrypt_bytes(data: &[u8], pass: &str) -> Result<Vec<u8>, String> {
    let mut salt = [0u8; SALT_LEN];
    let mut nonce_bytes = [0u8; NONCE_LEN];
    rand::rngs::OsRng.fill_bytes(&mut salt);
    rand::rngs::OsRng.fill_bytes(&mut nonce_bytes);

    let key = derive_key(pass, &salt);
    let cipher = Aes256Gcm::new_from_slice(&key).map_err(|e| e.to_string())?;
    let nonce = Nonce::from_slice(&nonce_bytes);
    let ct = cipher
        .encrypt(nonce, data)
        .map_err(|e| format!("加密失败：{e}"))?;

    let mut out = Vec::with_capacity(MAGIC.len() + 1 + SALT_LEN + NONCE_LEN + ct.len());
    out.extend_from_slice(MAGIC);
    out.push(VERSION);
    out.extend_from_slice(&salt);
    out.extend_from_slice(&nonce_bytes);
    out.extend_from_slice(&ct);
    Ok(out)
}

/// 加密字节 -> 明文（校验头、派生密钥、GCM 认证解密）。
pub fn decrypt_bytes(data: &[u8], pass: &str) -> Result<Vec<u8>, String> {
    if data.len() < MAGIC.len() + 1 + SALT_LEN + NONCE_LEN + 16 {
        return Err("文件不是有效的 CVLT 加密格式（长度不足）".to_string());
    }
    if &data[0..4] != MAGIC {
        return Err("文件不是有效的 CVLT 加密格式（魔数不符）".to_string());
    }
    if data[4] != VERSION {
        return Err(format!("不支持的加密版本：{}", data[4]));
    }
    let salt = &data[5..5 + SALT_LEN];
    let nonce_bytes = &data[5 + SALT_LEN..5 + SALT_LEN + NONCE_LEN];
    let ct = &data[5 + SALT_LEN + NONCE_LEN..];

    let key = derive_key(pass, salt);
    let cipher = Aes256Gcm::new_from_slice(&key).map_err(|e| e.to_string())?;
    let nonce = Nonce::from_slice(nonce_bytes);
    cipher
        .decrypt(nonce, ct)
        .map_err(|_| "解密失败：口令错误或数据被篡改".to_string())
}

/// 文件 -> .cvlt 加密文件。
pub fn encrypt_file(input: &Path, output: &Path, pass: &str) -> Result<(), String> {
    let data = std::fs::read(input).map_err(|e| format!("读输入失败：{e}"))?;
    let enc = encrypt_bytes(&data, pass)?;
    std::fs::write(output, &enc).map_err(|e| e.to_string())
}

/// .cvlt 加密文件 -> 原文件。
pub fn decrypt_file(input: &Path, output: &Path, pass: &str) -> Result<(), String> {
    let data = std::fs::read(input).map_err(|e| format!("读输入失败：{e}"))?;
    let plain = decrypt_bytes(&data, pass)?;
    std::fs::write(output, &plain).map_err(|e| e.to_string())
}
