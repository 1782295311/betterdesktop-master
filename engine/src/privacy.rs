//! 隐私黑名单（对齐 C# ClipboardManager 66-81 行语义）：
//! 进程名命中（大小写不敏感）或窗口标题包含任一关键词（OrdinalIgnoreCase）→ 不捕获。
//!
//! 【P2-2 敏感信息 · 2026-09-13】本模块另含 `detect_sensitive`：识别**内容里**的敏感信息
//!（手机号/身份证/邮箱/银行卡/密钥），用于给条目打标记 + 面板预览遮罩。
//! 与黑名单的区别：黑名单是"整条不捕获"，敏感识别是"照常捕获、只遮展示"。

use std::sync::OnceLock;

use regex::Regex;

pub fn is_privacy_sensitive(process_name: &str, window_title: &str) -> bool {
    // C# 语义：进程名 OrdinalIgnoreCase 精确匹配黑名单集合
    if !process_name.is_empty() {
        let lower = process_name.to_lowercase();
        if PRIVACY_BLACKLIST_PROCESSES.contains(&lower.as_str()) {
            return true;
        }
    }
    if !window_title.is_empty() {
        let lower = window_title.to_lowercase();
        for kw in PRIVACY_BLACKLIST_TITLE_KEYWORDS {
            if lower.contains(kw) {
                return true;
            }
        }
    }
    false
}

// ---------------- 【P2-2 敏感信息识别】 ----------------

/// 识别文本里的敏感信息，返回**类别名**（供打标记 + 面板遮罩）；无命中返回 `None`。
///
/// **只识别、不改内容** —— 用户粘贴时仍是全文（面板仅在预览里遮罩）。
/// 命中优先级：密钥 > 手机号 > 身份证 > 银行卡 > 邮箱（越靠前特征越强、误报越低）。
///
/// 注意：**不用 `\b`** —— Rust 的 `\b` 是 Unicode 词边界，中文字符属 word char，
/// 于是"手机号13812345678"里的 `\b` 不成立、永远匹配不到（中文语境下的经典陷阱）。
/// 这里改用"前后不是数字"的显式断言。
pub fn detect_sensitive(text: &str) -> Option<&'static str> {
    if text.len() < 6 {
        return None;
    }

    fn cached<'a>(slot: &'a OnceLock<Regex>, pattern: &str) -> &'a Regex {
        slot.get_or_init(|| Regex::new(pattern).expect("内置敏感信息正则必须可编译"))
    }

    static KEY: OnceLock<Regex> = OnceLock::new();
    static PHONE: OnceLock<Regex> = OnceLock::new();
    static ID: OnceLock<Regex> = OnceLock::new();
    static BANK: OnceLock<Regex> = OnceLock::new();
    static EMAIL: OnceLock<Regex> = OnceLock::new();

    let key = cached(
        &KEY,
        r"(?:sk-[A-Za-z0-9]{20,}|AKIA[0-9A-Z]{16}|gh[pousr]_[A-Za-z0-9]{20,}|xox[baprs]-[A-Za-z0-9-]{10,})",
    );
    if key.is_match(text) {
        return Some("密钥");
    }
    let phone = cached(&PHONE, r"(?:^|[^0-9])1[3-9][0-9]{9}(?:[^0-9]|$)");
    if phone.is_match(text) {
        return Some("手机号");
    }
    let id = cached(&ID, r"(?:^|[^0-9Xx])[0-9]{17}[0-9Xx](?:[^0-9Xx]|$)");
    if id.is_match(text) {
        return Some("身份证");
    }
    let bank = cached(&BANK, r"(?:^|[^0-9])[0-9]{16,19}(?:[^0-9]|$)");
    if bank.is_match(text) {
        return Some("银行卡");
    }
    let email = cached(&EMAIL, r"[\w.+-]+@[\w-]+\.[A-Za-z]{2,}");
    if email.is_match(text) {
        return Some("邮箱");
    }
    None
}

/// 进程黑名单（C# 66-74 行原文；进程名可能带或不带 .exe，均列出）。
const PRIVACY_BLACKLIST_PROCESSES: &[&str] = &[
    "1password",
    "1password.exe",
    "bitwarden",
    "bitwarden.exe",
    "keepass",
    "keepass.exe",
    "keepassxc",
    "keepassxc.exe",
    "lastpass",
    "lastpass.exe",
    "dashlane",
    "dashlane.exe",
    "enpass",
    "enpass.exe",
    "keychain",
    "passwordmanager",
    "authy",
    "authy.exe",
    "microsoft.aad.brokerplugin",
    "credentialuibroker",
    "vcred",
    "alipaysecurity",
    "wcps",
];

/// 标题关键词黑名单（C# 76-81 行原文；统一小写比较）。
const PRIVACY_BLACKLIST_TITLE_KEYWORDS: &[&str] = &[
    "密码", "password", "网银", "bank", "二步验证", "验证码", "verification", "1password",
    "bitwarden", "keepass", "lastpass", "dashlane",
];

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn process_blacklist_hits() {
        assert!(is_privacy_sensitive("1Password.exe", ""));
        assert!(is_privacy_sensitive("keepassxc", ""));
        assert!(is_privacy_sensitive("BITWARDEN", ""));
        assert!(is_privacy_sensitive("authy.exe", ""));
    }

    #[test]
    fn title_keyword_hits() {
        assert!(is_privacy_sensitive("notepad", "密码本 - 记事本"));
        assert!(is_privacy_sensitive("chrome", "Password Manager"));
        assert!(is_privacy_sensitive("wechat", "验证码"));
        assert!(is_privacy_sensitive("browser", "网银登录"));
    }

    #[test]
    fn normal_sources_not_blocked() {
        assert!(!is_privacy_sensitive("", ""));
        assert!(!is_privacy_sensitive("notepad", "README.md - 记事本"));
        assert!(!is_privacy_sensitive("code", "clipboard-engine - main.rs"));
        assert!(!is_privacy_sensitive("word", "年度总结.docx"));
    }

    #[test]
    fn detects_sensitive_kinds() {
        // 中文语境（前后都是中文字符）：验证不用 \b 的写法确实有效
        assert_eq!(detect_sensitive("我的手机号是 13812345678 请联系"), Some("手机号"));
        assert_eq!(detect_sensitive("手机号13812345678"), Some("手机号"));
        assert_eq!(detect_sensitive("身份证 11010119900307123X 请勿外传"), Some("身份证"));
        assert_eq!(detect_sensitive("邮箱 test.user@example.com"), Some("邮箱"));
        assert_eq!(detect_sensitive("key: sk-abcdefghijklmnopqrstuvwxyz0123"), Some("密钥"));
    }

    #[test]
    fn plain_text_is_not_sensitive() {
        assert_eq!(detect_sensitive("这是一段普通文本，没有任何敏感信息"), None);
        assert_eq!(detect_sensitive(""), None);
        assert_eq!(detect_sensitive("sh"), None);
        assert_eq!(detect_sensitive("版本 1.2.3"), None);
    }
}
