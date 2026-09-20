// 稳定错误六分类（C# ConvertError/ConvertException 等价物）。
// 契约红线：错误码不得变（C# MapConvertError 按此映射）；引擎缺失禁止伪装成转换失败。
use std::fmt;

/// 转换错误码（C# ConversionResult.ConvertError 等价物）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ConvertError {
    /// 成功（无错误）。
    None,
    /// 引擎缺失（不是用户文件错误——提示语必须区分）。
    EngineMissing,
    /// 引擎崩溃（进程非零退出且无产物）。
    EngineCrashed,
    /// 超时（超时控制必须存在）。
    Timeout,
    /// 转换失败（引擎正常退出但无输出/业务失败）。
    ConversionFailed,
    /// 输入非法（空路径/\0/不存在/不支持类型）。
    InputInvalid,
    /// 输出发布失败（写/移动失败/输出与输入冲突）。
    OutputFailed,
}

impl ConvertError {
    /// C# 枚举名原文（capabilities/run 输出用）。
    pub fn as_str(&self) -> &'static str {
        match self {
            ConvertError::None => "None",
            ConvertError::EngineMissing => "EngineMissing",
            ConvertError::EngineCrashed => "EngineCrashed",
            ConvertError::Timeout => "Timeout",
            ConvertError::ConversionFailed => "ConversionFailed",
            ConvertError::InputInvalid => "InputInvalid",
            ConvertError::OutputFailed => "OutputFailed",
        }
    }
}

/// 转换错误（携带六分类 + 面向用户的消息）。
#[derive(Debug, Clone)]
pub struct Error {
    pub code: ConvertError,
    pub message: String,
}

impl Error {
    pub fn new(code: ConvertError, message: impl Into<String>) -> Self {
        Error { code, message: message.into() }
    }

    pub fn input_invalid(message: impl Into<String>) -> Self {
        Error::new(ConvertError::InputInvalid, message)
    }

    pub fn engine_missing(message: impl Into<String>) -> Self {
        Error::new(ConvertError::EngineMissing, message)
    }

    pub fn engine_crashed(message: impl Into<String>) -> Self {
        Error::new(ConvertError::EngineCrashed, message)
    }

    pub fn timeout(message: impl Into<String>) -> Self {
        Error::new(ConvertError::Timeout, message)
    }

    pub fn conversion_failed(message: impl Into<String>) -> Self {
        Error::new(ConvertError::ConversionFailed, message)
    }

    pub fn output_failed(message: impl Into<String>) -> Self {
        Error::new(ConvertError::OutputFailed, message)
    }
}

impl fmt::Display for Error {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "[{}] {}", self.code.as_str(), self.message)
    }
}

impl std::error::Error for Error {}

/// lopdf 错误统一映射为 ConversionFailed（PDF 结构错误不属于输入校验层）。
impl From<lopdf::Error> for Error {
    fn from(e: lopdf::Error) -> Self {
        Error::conversion_failed(format!("PDF 操作失败: {e}"))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn error_codes_match_csharp_names() {
        assert_eq!(ConvertError::EngineMissing.as_str(), "EngineMissing");
        assert_eq!(ConvertError::EngineCrashed.as_str(), "EngineCrashed");
        assert_eq!(ConvertError::Timeout.as_str(), "Timeout");
        assert_eq!(ConvertError::ConversionFailed.as_str(), "ConversionFailed");
        assert_eq!(ConvertError::InputInvalid.as_str(), "InputInvalid");
        assert_eq!(ConvertError::OutputFailed.as_str(), "OutputFailed");
    }

    #[test]
    fn constructors_carry_codes() {
        assert_eq!(Error::input_invalid("x").code, ConvertError::InputInvalid);
        assert_eq!(Error::engine_missing("x").code, ConvertError::EngineMissing);
        assert_eq!(Error::engine_crashed("x").code, ConvertError::EngineCrashed);
        assert_eq!(Error::timeout("x").code, ConvertError::Timeout);
        assert_eq!(Error::conversion_failed("x").code, ConvertError::ConversionFailed);
        assert_eq!(Error::output_failed("x").code, ConvertError::OutputFailed);
    }

    #[test]
    fn display_contains_code_and_message() {
        let e = Error::timeout("soffice 超时（120000ms，已强杀进程树）");
        let s = e.to_string();
        assert!(s.contains("Timeout"));
        assert!(s.contains("120000ms"));
    }
}
