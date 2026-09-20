//! DPAPI 加密/解密（对齐 C# ClipboardNative.Protect/Unprotect：CryptProtectData dwFlags=0，当前用户、无熵）。
//! 用于 clipboard_history.json 与 >100KB 压缩文本 content\*.bin 的加密（图片/缩略图维持明文，与现有 C# 一致）。

use windows::Win32::Foundation::{LocalFree, HLOCAL};
use windows::Win32::Security::Cryptography::{CryptProtectData, CryptUnprotectData, CRYPT_INTEGER_BLOB};

/// DPAPI 加密（当前用户；空输入返回空，与 C# 行为一致）。
pub fn protect(plain: &[u8]) -> windows::core::Result<Vec<u8>> {
    if plain.is_empty() {
        return Ok(Vec::new());
    }
    unsafe {
        let input = CRYPT_INTEGER_BLOB {
            cbData: plain.len() as u32,
            pbData: plain.as_ptr() as *mut u8,
        };
        let mut output = CRYPT_INTEGER_BLOB::default();
        CryptProtectData(&input, None, None, None, None, 0, &mut output)?;
        let result = std::slice::from_raw_parts(output.pbData, output.cbData as usize).to_vec();
        let _ = LocalFree(HLOCAL(output.pbData as *mut core::ffi::c_void));
        Ok(result)
    }
}

/// DPAPI 解密（当前用户；空输入返回空）。
pub fn unprotect(cipher: &[u8]) -> windows::core::Result<Vec<u8>> {
    if cipher.is_empty() {
        return Ok(Vec::new());
    }
    unsafe {
        let input = CRYPT_INTEGER_BLOB {
            cbData: cipher.len() as u32,
            pbData: cipher.as_ptr() as *mut u8,
        };
        let mut output = CRYPT_INTEGER_BLOB::default();
        CryptUnprotectData(&input, None, None, None, None, 0, &mut output)?;
        let result = std::slice::from_raw_parts(output.pbData, output.cbData as usize).to_vec();
        let _ = LocalFree(HLOCAL(output.pbData as *mut core::ffi::c_void));
        Ok(result)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn protect_unprotect_roundtrip() {
        let plain = b"hello clipboard history \xe4\xb8\xad\xe6\x96\x87";
        let cipher = protect(plain).expect("protect");
        assert_ne!(cipher, plain, "cipher must differ from plaintext");
        let back = unprotect(&cipher).expect("unprotect");
        assert_eq!(back, plain);
    }

    #[test]
    fn empty_roundtrip() {
        assert!(protect(b"").unwrap().is_empty());
        assert!(unprotect(b"").unwrap().is_empty());
    }

    #[test]
    fn unprotect_garbage_fails() {
        // 非 DPAPI 数据必须报错（旧文件损坏自愈路径依赖此行为）
        assert!(unprotect(b"not a dpapi blob").is_err());
    }
}
