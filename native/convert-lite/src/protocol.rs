//! 诚实进度协议（NDJSON 事件流）。
//!
//! 与原 native/convert-engine 的契约对齐：一行一个 JSON。
//! 铁律：percent 只在能被真实测量时给 Some(_)；无法测量就给 None，
//! 只推进 phase，绝不编造一个看起来在动的假百分比。

use serde::Serialize;
use std::time::{Duration, Instant};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum Phase {
    /// 输入探测（大小/格式/尺寸），尚未开始干活。
    Probing,
    /// 正在转换主体。
    Running,
    /// 写出产物。
    Publishing,
    /// 收尾（校验、原子改名）。
    Finalizing,
    /// 成功结束。
    Done,
    /// 失败结束。
    Failed,
}

#[derive(Debug, Serialize)]
pub struct ProgressEvent<'a> {
    pub kind: &'a str,
    pub input: &'a str,
    pub target: &'a str,
    pub engine: &'a str,
    pub phase: Phase,
    /// 0..=100；None = 阶段推进（诚实，不编造）。
    pub percent: Option<u8>,
    pub elapsed_ms: u128,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub message: Option<&'a str>,
}

#[derive(Debug, Serialize)]
pub struct ResultEvent<'a> {
    pub kind: &'a str,
    pub ok: bool,
    pub input: &'a str,
    pub output: Option<&'a str>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub error: Option<&'a str>,
    pub elapsed_ms: u128,
}

/// 进度发射器：持有起始时刻，保证 elapsed_ms 单调。
pub struct Emitter {
    started: Instant,
    /// 静默模式（库接口）：不打印任何行，避免污染宿主协议输出。
    silent: bool,
}

impl Emitter {
    pub fn new() -> Self {
        Self {
            started: Instant::now(),
            silent: false,
        }
    }

    /// 静默发射器：库接口用（无 stdout 副作用）。
    pub fn silent() -> Self {
        Self {
            started: Instant::now(),
            silent: true,
        }
    }

    #[inline]
    fn elapsed(&self) -> u128 {
        self.started.elapsed().as_millis()
    }

    /// 发射一行 progress。percent 为 None 时即"阶段推进、不编数字"。
    pub fn progress(
        &self,
        input: &str,
        target: &str,
        engine: &str,
        phase: Phase,
        percent: Option<u8>,
        message: Option<&str>,
    ) {
        if self.silent {
            return;
        }
        let ev = ProgressEvent {
            kind: "progress",
            input,
            target,
            engine,
            phase,
            percent,
            elapsed_ms: self.elapsed(),
            message,
        };
        // 用 writeln! 到 stdout；NDJSON 每行独立，失败即 panic（与原契约一致）。
        let line = serde_json::to_string(&ev).expect("progress 序列化不应失败");
        println!("{line}");
    }

    pub fn result_ok(&self, input: &str, output: &str) {
        if self.silent {
            return;
        }
        let ev = ResultEvent {
            kind: "result",
            ok: true,
            input,
            output: Some(output),
            error: None,
            elapsed_ms: self.elapsed(),
        };
        let line = serde_json::to_string(&ev).expect("result 序列化不应失败");
        println!("{line}");
    }

    pub fn result_err(&self, input: &str, error: &str) {
        if self.silent {
            return;
        }
        let ev = ResultEvent {
            kind: "result",
            ok: false,
            input,
            output: None,
            error: Some(error),
            elapsed_ms: self.elapsed(),
        };
        let line = serde_json::to_string(&ev).expect("result 序列化不应失败");
        println!("{line}");
    }
}

/// 把 0.0..=1.0 安全夹到 0..=100。仅在分母真实存在时调用。
pub fn clamp_ratio(r: f64) -> u8 {
    (r.clamp(0.0, 1.0) * 100.0).round() as u8
}

#[allow(dead_code)]
pub fn elapsed(d: Duration) -> u128 {
    d.as_millis()
}
