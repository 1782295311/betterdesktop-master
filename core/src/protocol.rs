//! BDMC1 协议（控制管道）—— 解析与编码。
//!
//! # 契约来源
//!
//! **权威是 `protocols/bdmc1-test-vectors.json`**（仓库根），不是本文件。本模块只实现它。
//! C# 侧 `packages/kernel/kernel/MenuCommandPipeCodec.cs` 是同一契约的第二实现，两侧跑同一批用例。
//! 这样做的目的：消灭"两侧各自猜边界、各自单测都绿、真机对不上"。
//!
//! # 两种形态
//!
//! - **legacy**：`BDMC1|<action>|<arg...>` —— 写后即忘，不回包（兼容既有 CLI 与系统右键扩展）。
//! - **control**：`BDMC1|@ctl|<verb>|<arg...>` —— 请求/应答，客户端读一行 JSON 响应。
//!
//! 切分规则：首个 `|` 之后按 `|` 切；`head` 取第 0 段，`arg` = **其余段用 `|` 重新拼接**，
//! 因此 `arg` 内可安全包含 `|`（Windows 文件名与设置值里都可能出现）。
//!
//! # 保真约定
//!
//! 只剥行终止符（CRLF / LF），**其余字节一律保留**（含首尾空格）——
//! "顺手 trim 一下"会让含尾空格的路径静默变短，是最难查的那类 bug。

use std::fmt::Write as _;

/// 协议魔数前缀（大小写敏感）。
pub const MAGIC: &str = "BDMC1|";

/// 控制形态的哨兵头。
pub const CTL: &str = "@ctl";

/// 单条消息字节上限（与 `protocols/bdmc1-test-vectors.json` 的 `maxMessageBytes` 一致）。
pub const MAX_MESSAGE_BYTES: usize = 1_048_576;

/// 解析失败原因。取值与共享向量的 `invalidReasons` 逐字对应（跨语言按字符串比对）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum InvalidReason {
    /// 去掉行终止符后为空。
    Empty,
    /// 字节数超过 [`MAX_MESSAGE_BYTES`]。
    TooLarge,
    /// 不是合法 UTF-8。
    InvalidUtf8,
    /// 不以 `BDMC1|` 开头。
    BadMagic,
    /// action 或 verb 为空。
    EmptyHead,
}

impl InvalidReason {
    /// 共享向量里用的字面量（跨语言契约的一部分，改名即破约）。
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Empty => "empty",
            Self::TooLarge => "too-large",
            Self::InvalidUtf8 => "invalid-utf8",
            Self::BadMagic => "bad-magic",
            Self::EmptyHead => "empty-head",
        }
    }
}

/// 解析结果。
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Message {
    /// `BDMC1|<action>|<arg>`：写后即忘。
    Legacy { action: String, arg: String },
    /// `BDMC1|@ctl|<verb>|<arg>`：请求/应答。
    Control { verb: String, arg: String },
}

impl Message {
    /// `head`（legacy 的 action / control 的 verb），供统一断言与日志使用。
    pub fn head(&self) -> &str {
        match self {
            Self::Legacy { action, .. } => action,
            Self::Control { verb, .. } => verb,
        }
    }

    /// `arg`（已按 `|` 还原）。
    pub fn arg(&self) -> &str {
        match self {
            Self::Legacy { arg, .. } | Self::Control { arg, .. } => arg,
        }
    }

    /// 共享向量里的 `kind` 字面量。
    pub fn kind(&self) -> &'static str {
        match self {
            Self::Legacy { .. } => "legacy",
            Self::Control { .. } => "control",
        }
    }
}

/// 解析一行消息（**输入含行终止符**，本函数负责剥离）。
///
/// 顺序有意如此：**先判大小、再判 UTF-8、最后判魔数** ——
///   · 大小判定必须在解码前（否则超长输入会在解码阶段先吃内存）；
///   · UTF-8 判定必须在字符串操作前（Rust 的 `&str` 保证跳过它是不可能的，但显式报错比 lossy 转换好：
///     lossy 会把坏字节变成 U+FFFD，于是"畸形输入"被静默当成合法路径，正是我们要避免的）。
pub fn parse_line(raw: &[u8]) -> Result<Message, InvalidReason> {
    if raw.len() > MAX_MESSAGE_BYTES {
        return Err(InvalidReason::TooLarge);
    }
    let text = std::str::from_utf8(raw).map_err(|_| InvalidReason::InvalidUtf8)?;
    let line = strip_terminator(text);
    if line.is_empty() {
        return Err(InvalidReason::Empty);
    }
    let Some(rest) = line.strip_prefix(MAGIC) else {
        return Err(InvalidReason::BadMagic);
    };

    // 按 '|' 切；head = 第 0 段，arg = 其余段重新拼接（保留 arg 内的 '|'）
    let mut parts = rest.split('|');
    let first = parts.next().unwrap_or("");
    let tail: Vec<&str> = parts.collect();

    if first == CTL {
        // 控制形态：`@ctl` 之后第 0 段是 verb，其余是 arg
        let verb = tail.first().copied().unwrap_or("");
        if verb.is_empty() {
            return Err(InvalidReason::EmptyHead);
        }
        let arg = if tail.len() > 1 {
            tail[1..].join("|")
        } else {
            String::new()
        };
        return Ok(Message::Control {
            verb: verb.to_string(),
            arg,
        });
    }

    if first.is_empty() {
        return Err(InvalidReason::EmptyHead);
    }
    Ok(Message::Legacy {
        action: first.to_string(),
        arg: tail.join("|"),
    })
}

/// 只剥行终止符（CRLF / LF）；**不 trim 其它字符**。
fn strip_terminator(s: &str) -> &str {
    s.strip_suffix("\r\n")
        .or_else(|| s.strip_suffix('\n'))
        .or_else(|| s.strip_suffix('\r'))
        .unwrap_or(s)
}

/// 结构化错误码（跨语言按字符串比对；**改名即破约**）。
///
/// 取值风格与协议其余部分一致：**kebab-case**（如 `probe-and-reinit` / `on-demand`）。
/// 完整清单与语义见 `docs/architecture/error-codes.md`。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ErrorCode {
    /// 消息超过 1 MiB。
    PayloadTooLarge,
    /// 未知 verb。
    UnknownVerb,
    /// 未知组件名。
    UnknownComponent,
    /// 开关关闭，拒绝拉起（用户主动关掉的东西不许被复活）。
    GateClosed,
    /// 调用方校验失败（SID / 会话不匹配）。
    Forbidden,
    /// 内部错误（异常、I/O 失败等）。
    InternalError,
}

impl ErrorCode {
    /// 线上字面量。
    pub fn as_str(self) -> &'static str {
        match self {
            Self::PayloadTooLarge => "payload-too-large",
            Self::UnknownVerb => "unknown-verb",
            Self::UnknownComponent => "unknown-component",
            Self::GateClosed => "gate-closed",
            Self::Forbidden => "forbidden",
            Self::InternalError => "internal-error",
        }
    }
}

/// 编解码控制响应（供两侧一致）。
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Response {
    /// `{"ok":true,"verb":"<verb>","data":{...}}`
    Ok { verb: String, data: serde_json::Value },
    /// `{"ok":false,"verb":"<verb>","error":"<code>","message":"..."}`
    Err {
        verb: String,
        error: String,
        message: String,
    },
}

impl Response {
    /// 编码为**单行紧凑 JSON**（不得含换行；管道按行分帧）。
    pub fn encode(&self) -> String {
        let value = match self {
            Self::Ok { verb, data } => serde_json::json!({
                "ok": true,
                "verb": verb,
                "data": data,
            }),
            Self::Err {
                verb,
                error,
                message,
            } => serde_json::json!({
                "ok": false,
                "verb": verb,
                "error": error,
                "message": message,
            }),
        };
        // serde_json 的 Display 即紧凑单行；换行由 ensure_single_line 兜底
        ensure_single_line(&value.to_string())
    }

    /// 便捷构造：错误响应（用强类型错误码，避免各处手写字符串漂移）。
    pub fn err(verb: &str, code: ErrorCode, message: impl Into<String>) -> Self {
        Self::Err {
            verb: verb.to_string(),
            error: code.as_str().to_string(),
            message: message.into(),
        }
    }
}

/// 响应绝不允许多行（否则客户端按行读会把半条消息当完整消息）。
fn ensure_single_line(json: &str) -> String {
    if !json.contains(['\n', '\r']) {
        return json.to_string();
    }
    let mut out = String::with_capacity(json.len());
    for ch in json.chars() {
        match ch {
            '\n' => {
                let _ = write!(out, "\\n");
            }
            '\r' => {
                let _ = write!(out, "\\r");
            }
            other => out.push(other),
        }
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde::Deserialize;

    // ── 共享向量（契约的唯一来源；编译期绑定，改动向量即触发重编） ──
    #[derive(Deserialize)]
    struct Vectors {
        #[serde(rename = "maxMessageBytes")]
        max_message_bytes: usize,
        cases: Vec<Case>,
    }

    #[derive(Deserialize)]
    struct Case {
        name: String,
        input: Option<String>,
        generate: Option<String>,
        expect: Expect,
    }

    #[derive(Deserialize)]
    struct Expect {
        kind: String,
        head: Option<String>,
        arg: Option<String>,
        reason: Option<String>,
    }

    fn vectors() -> Vectors {
        serde_json::from_str(include_str!("../../protocols/bdmc1-test-vectors.json"))
            .expect("shared vectors must parse")
    }

    fn case_bytes(c: &Case, max: usize) -> Vec<u8> {
        match (c.input.as_ref(), c.generate.as_deref()) {
            (Some(s), _) => s.as_bytes().to_vec(),
            (None, Some("oversize")) => vec![b'a'; max + 1],
            (None, Some("invalid-utf8")) => vec![0xFF, 0xFE, b'A'],
            (None, other) => panic!("case '{}' has unsupported generate: {:?}", c.name, other),
        }
    }

    /// **契约测试**：共享向量里的每一条，本实现都必须给出 `expect` 的结果。
    #[test]
    fn matches_shared_vectors() {
        let v = vectors();
        assert_eq!(
            v.max_message_bytes, MAX_MESSAGE_BYTES,
            "MAX_MESSAGE_BYTES 与共享向量不一致（改协议必须同时改两侧常量与向量）"
        );
        assert!(
            v.cases.len() >= 10,
            "共享向量应覆盖足够边界（当前 {} 条）",
            v.cases.len()
        );

        let mut failed = Vec::new();
        for c in &v.cases {
            let bytes = case_bytes(c, v.max_message_bytes);
            match parse_line(&bytes) {
                Ok(msg) => {
                    let head_ok = c.expect.head.as_deref() == Some(msg.head());
                    let arg_ok = c.expect.arg.as_deref() == Some(msg.arg());
                    let kind_ok = c.expect.kind == msg.kind();
                    if !(head_ok && arg_ok && kind_ok) {
                        failed.push(format!(
                            "{}: expect kind={} head={:?} arg={:?}, got kind={} head={:?} arg={:?}",
                            c.name,
                            c.expect.kind,
                            c.expect.head,
                            c.expect.arg,
                            msg.kind(),
                            msg.head(),
                            msg.arg()
                        ));
                    }
                }
                Err(reason) => {
                    let want = c.expect.reason.as_deref().unwrap_or("<none>");
                    if c.expect.kind != "invalid" || want != reason.as_str() {
                        failed.push(format!(
                            "{}: expect kind={} reason={}, got Err({})",
                            c.name,
                            c.expect.kind,
                            want,
                            reason.as_str()
                        ));
                    }
                }
            }
        }
        assert!(
            failed.is_empty(),
            "共享向量未通过（{} 条）：\n{}",
            failed.len(),
            failed.join("\n")
        );
    }

    #[test]
    fn every_invalid_reason_literal_is_locked() {
        // 跨语言按字符串比对，改名即破约 —— 这里把字面量钉死
        assert_eq!(InvalidReason::Empty.as_str(), "empty");
        assert_eq!(InvalidReason::TooLarge.as_str(), "too-large");
        assert_eq!(InvalidReason::InvalidUtf8.as_str(), "invalid-utf8");
        assert_eq!(InvalidReason::BadMagic.as_str(), "bad-magic");
        assert_eq!(InvalidReason::EmptyHead.as_str(), "empty-head");
    }

    #[test]
    fn tail_spaces_are_preserved() {
        let m = parse_line(b"BDMC1|convert|x ").unwrap();
        assert_eq!(m.arg(), "x ", "不得顺手 trim：尾空格可能是真实路径的一部分");
    }

    #[test]
    fn oversize_is_rejected_before_decode() {
        let huge = vec![b'a'; MAX_MESSAGE_BYTES + 1];
        assert_eq!(parse_line(&huge), Err(InvalidReason::TooLarge));
        // 恰好等于上限 → 不因大小被拒（后续按内容判定）
        let at_limit = vec![b'a'; MAX_MESSAGE_BYTES];
        assert_eq!(parse_line(&at_limit), Err(InvalidReason::BadMagic));
    }

    #[test]
    fn response_is_single_line_compact_json() {
        let ok = Response::Ok {
            verb: "status".into(),
            data: serde_json::json!({ "desired": "running" }),
        };
        let s = ok.encode();
        assert!(!s.contains('\n') && !s.contains('\r'), "响应不得含换行");
        let parsed: serde_json::Value = serde_json::from_str(&s).unwrap();
        assert_eq!(parsed["ok"], serde_json::json!(true));
        assert_eq!(parsed["verb"], serde_json::json!("status"));
        assert_eq!(parsed["data"]["desired"], serde_json::json!("running"));
    }

    #[test]
    fn error_response_shape() {
        let s = Response::err("start", ErrorCode::UnknownComponent, "no such component").encode();
        let parsed: serde_json::Value = serde_json::from_str(&s).unwrap();
        assert_eq!(parsed["ok"], serde_json::json!(false));
        assert_eq!(parsed["error"], serde_json::json!("unknown-component"));
        assert_eq!(parsed["message"], serde_json::json!("no such component"));
    }

    /// 错误码字面量是**跨语言契约**（C# 与文档都按字符串比对）—— 改名即破约。
    #[test]
    fn error_code_literals_are_locked() {
        let pairs = [
            (ErrorCode::PayloadTooLarge, "payload-too-large"),
            (ErrorCode::UnknownVerb, "unknown-verb"),
            (ErrorCode::UnknownComponent, "unknown-component"),
            (ErrorCode::GateClosed, "gate-closed"),
            (ErrorCode::Forbidden, "forbidden"),
            (ErrorCode::InternalError, "internal-error"),
        ];
        for (code, wire) in pairs {
            assert_eq!(code.as_str(), wire);
        }
    }

    /// 响应里若混入换行字符，编码结果**仍不得含裸换行**（否则按行分帧会把半条消息当完整消息）。
    /// serde_json 自身就会转义控制字符，本用例把它钉死；`ensure_single_line` 是第二道保险。
    #[test]
    fn response_never_emits_raw_newlines() {
        let s = Response::err("x", ErrorCode::InternalError, "line1\nline2\r\nline3").encode();
        assert!(!s.contains('\n') && !s.contains('\r'));
        let parsed: serde_json::Value = serde_json::from_str(&s).unwrap();
        assert_eq!(
            parsed["message"],
            serde_json::json!("line1\nline2\r\nline3"),
            "转义必须可无损还原"
        );
    }

    /// `ensure_single_line` 的独立单测：它是分帧安全的最后一道防线，
    /// 逻辑虽短但一旦失效就是"客户读到半条消息"这类难查故障。
    #[test]
    fn ensure_single_line_escapes_raw_breaks() {
        assert_eq!(ensure_single_line("{\"a\":1}"), "{\"a\":1}");
        // LF → 字面 `\n`；CR → 字面 `\r`（两者区分保留，不做有损归一）
        let fixed = ensure_single_line("a\nb\r\nc");
        assert!(!fixed.contains('\n') && !fixed.contains('\r'));
        assert_eq!(fixed, "a\\nb\\r\\nc");
    }
}
