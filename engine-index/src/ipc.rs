//! IPC server：NamedPipe 多客户端（4 连接槽），JSON-RPC 2.0 帧协议。
//!
//! 管道：`\\.\pipe\BetterDesktop.Index.Engine`，magic 前缀 `BDIX1|`
//! （安全模型与剪贴板引擎一致：magic 校验通过后才派发，丢弃误连/扫描器）。
//!
//! 【照抄 engine/src/ipc.rs 的真机结论，勿回退】
//! 1. 每连接**单线程统一读写**（响应串行；实测并发 Read/Write 同一句柄会让 WriteFile 挂起）；
//! 2. 主循环用 `PeekNamedPipe` 非阻塞轮询（PIPE_WAIT 下 ReadFile 无数据会永久阻塞，读超时不生效）；
//! 3. 首帧必须有超时（剪贴板引擎曾因无限等待首帧，被 4 个误连耗尽连接槽 → 引擎"看着活着"实际不可用）；
//! 4. 入站帧硬上限，防「超大帧吃光内存」OOM；
//! 5. 写出失败按「对端正常关闭」与「真异常」分级——否则断线重连会刷爆日志淹没真错误。
//!
//! 【M1 范围】仅请求/响应，无事件推送（订阅与增量通知属 M2；届时按需引入 Subscriber 机制）。

use std::thread;
use std::time::{Duration, Instant};

use serde_json::Value;
use windows::core::{w, Result};
use windows::Win32::Foundation::{
    CloseHandle, ERROR_BROKEN_PIPE, ERROR_MORE_DATA, ERROR_PIPE_CONNECTED, ERROR_PIPE_NOT_CONNECTED,
    HANDLE,
};
use windows::Win32::Storage::FileSystem::{
    FlushFileBuffers, ReadFile, WriteFile, PIPE_ACCESS_DUPLEX,
};
use windows::Win32::System::Pipes::{
    ConnectNamedPipe, CreateNamedPipeW, DisconnectNamedPipe, PeekNamedPipe, NAMED_PIPE_MODE,
    PIPE_READMODE_MESSAGE, PIPE_TYPE_MESSAGE, PIPE_WAIT,
};

pub const PIPE_NAME: &str = r"\\.\pipe\BetterDesktop.Index.Engine";
pub const MAGIC: &str = "BDIX1|";
pub const MAX_INSTANCES: u32 = 4;

const READ_BUF: usize = 64 * 1024;
/// 首帧超时（见模块头第 3 条）。
const FIRST_FRAME_TIMEOUT: Duration = Duration::from_secs(5);
/// 单条入站消息硬上限 32MB（见模块头第 4 条）。
const MAX_INBOUND_FRAME: usize = 32 * 1024 * 1024;

pub const ERR_PARSE: i64 = -32700;
pub const ERR_INVALID_REQUEST: i64 = -32600;

/// 解析成功后的请求。
#[derive(Debug, PartialEq)]
pub struct Request {
    pub id: Option<Value>,
    pub method: String,
    pub params: Option<Value>,
}

/// JSON-RPC 错误（可直接转错误响应）。
#[derive(Debug, PartialEq)]
pub struct RpcFailure {
    pub id: Option<Value>,
    pub code: i64,
    pub message: String,
}

/// 启动 IPC server：MAX_INSTANCES 个连接线程，每个循环「等待连接 → 处理 → 断开 → 再等待」。
pub fn start() {
    for _ in 0..MAX_INSTANCES {
        thread::spawn(instance_loop);
    }
    crate::log::info(format!(
        "ipc server started: {PIPE_NAME} ({MAX_INSTANCES} slots)"
    ));
}

fn instance_loop() {
    unsafe {
        loop {
            // dwpipemode：消息模式（一条消息 = 一帧协议）
            let pipe_mode =
                NAMED_PIPE_MODE(PIPE_TYPE_MESSAGE.0 | PIPE_READMODE_MESSAGE.0 | PIPE_WAIT.0);
            // 0.58：CreateNamedPipeW 裸返回 HANDLE（非 Result），用 is_invalid 判断失败
            let handle = CreateNamedPipeW(
                w!(r"\\.\pipe\BetterDesktop.Index.Engine"),
                PIPE_ACCESS_DUPLEX,
                pipe_mode,
                MAX_INSTANCES,
                READ_BUF as u32,
                READ_BUF as u32,
                0,
                None,
            );
            if handle.is_invalid() {
                let e = windows::core::Error::from_win32();
                crate::log::error(format!("CreateNamedPipeW failed: {e}"));
                thread::sleep(Duration::from_millis(500));
                continue;
            }

            match ConnectNamedPipe(handle, None) {
                Ok(()) => {}
                Err(e) => {
                    // 客户端在 CreateNamedPipe 与 ConnectNamedPipe 之间连入 → ERROR_PIPE_CONNECTED，视为成功
                    let win32 = e.code().0 as u32 & 0xFFFF;
                    if win32 != ERROR_PIPE_CONNECTED.0 as u32 {
                        crate::log::error(format!("ConnectNamedPipe failed: {e}"));
                        let _ = CloseHandle(handle);
                        thread::sleep(Duration::from_millis(50));
                        continue;
                    }
                }
            }

            handle_connection(handle);
            let _ = DisconnectNamedPipe(handle);
            let _ = CloseHandle(handle);
        }
    }
}

/// 处理一条连接：首帧必须带 magic 前缀，之后逐帧 JSON-RPC 请求/响应。
fn handle_connection(handle: HANDLE) {
    let deadline = Instant::now() + FIRST_FRAME_TIMEOUT;
    let mut first = loop {
        if Instant::now() > deadline {
            crate::log::info("first frame timeout; dropping connection");
            return;
        }
        match peek_pipe(handle) {
            Peek::Data(_) => match read_message(handle) {
                Ok(Some(m)) => break m,
                Ok(None) => return,
                Err(e) => {
                    crate::log::error(format!("read first frame failed: {e}"));
                    return;
                }
            },
            Peek::Empty => thread::sleep(Duration::from_millis(30)),
            Peek::Closed => return,
            Peek::Error(e) => {
                crate::log::error(format!("peek first frame failed: {e}"));
                return;
            }
        }
    };

    if !first.starts_with(MAGIC.as_bytes()) {
        crate::log::info(format!(
            "dropping connection without magic prefix ({} bytes)",
            first.len()
        ));
        return;
    }

    if let Some(resp) = handle_frame(strip_magic(&mut first)) {
        if !write_message(handle, &resp) {
            return;
        }
    }

    loop {
        match peek_pipe(handle) {
            Peek::Data(_) => match read_message(handle) {
                Ok(Some(mut m)) => {
                    // 活动信号（空闲即退判据）：只有**收到请求**才记 —— 后台周期补扫刻意不算，
                    // 否则引擎永远"非空闲"，退场机制等于不存在。
                    crate::engine::touch_activity();
                    if let Some(resp) = handle_frame(strip_magic(&mut m)) {
                        if !write_message(handle, &resp) {
                            crate::log::error("response write failed; terminating connection");
                            break;
                        }
                    }
                }
                Ok(None) => break, // 客户端断开
                Err(e) => {
                    crate::log::error(format!("connection read error: {e}"));
                    break;
                }
            },
            Peek::Empty => thread::sleep(Duration::from_millis(30)),
            Peek::Closed => break,
            Peek::Error(e) => {
                crate::log::error(format!("peek error: {e}"));
                break;
            }
        }
    }
}

/// PeekNamedPipe 探测结果（非阻塞）。
///
/// `Data` 的字节数仅作「有数据」信号，不参与逻辑（与 engine/src/ipc.rs 同款处理）。
#[allow(dead_code)]
enum Peek {
    /// 管道有数据（字节数）
    Data(u32),
    /// 无数据
    Empty,
    /// 客户端已断开
    Closed,
    /// 异常
    Error(windows::core::Error),
}

fn peek_pipe(handle: HANDLE) -> Peek {
    unsafe {
        let mut total = 0u32;
        match PeekNamedPipe(handle, None, 0, None, Some(&mut total), None) {
            Ok(()) => {
                if total > 0 {
                    Peek::Data(total)
                } else {
                    Peek::Empty
                }
            }
            Err(e) => {
                let win32 = e.code().0 as u32 & 0xFFFF;
                if win32 == ERROR_BROKEN_PIPE.0 as u32 || win32 == ERROR_PIPE_NOT_CONNECTED.0 as u32
                {
                    Peek::Closed
                } else {
                    Peek::Error(e)
                }
            }
        }
    }
}

/// 剥离可选 magic 前缀（每帧统一处理；无前缀视为纯 JSON 帧）。
fn strip_magic(frame: &mut [u8]) -> &[u8] {
    if frame.starts_with(MAGIC.as_bytes()) {
        &frame[MAGIC.len()..]
    } else {
        frame
    }
}

/// 消息模式下读一条消息（ERROR_MORE_DATA = 单条消息超过 buffer，需继续拼）。
/// Ok(None) = 连接正常关闭；Err = 连接异常。
fn read_message(handle: HANDLE) -> Result<Option<Vec<u8>>> {
    unsafe {
        let mut collected: Vec<u8> = Vec::new();
        let mut chunk = [0u8; READ_BUF];
        loop {
            if collected.len() > MAX_INBOUND_FRAME {
                crate::log::error("inbound frame exceeds 32MB; dropping connection");
                return Ok(None);
            }
            let mut read = 0u32;
            match ReadFile(handle, Some(&mut chunk[..]), Some(&mut read), None) {
                Ok(()) => {
                    collected.extend_from_slice(&chunk[..read as usize]);
                    break;
                }
                Err(e) => {
                    let win32 = e.code().0 as u32 & 0xFFFF;
                    if win32 == ERROR_MORE_DATA.0 as u32 {
                        // 【2026-09-14 修复】只取 ReadFile 实际写入的字节数（与上面成功分支一致）。
                        // 此前拼的是整块 64KB：消息模式管道下缓冲通常恰好填满所以"看着对"，
                        // 但那是实现细节而非 API 契约；一旦出现 read < READ_BUF，
                        // 未写入的尾随旧数据会被当成本帧内容 → JSON 解析错乱。
                        collected.extend_from_slice(&chunk[..(read as usize).min(chunk.len())]);
                        continue;
                    }
                    if collected.is_empty()
                        && (win32 == ERROR_BROKEN_PIPE.0 as u32
                            || win32 == ERROR_PIPE_NOT_CONNECTED.0 as u32)
                    {
                        return Ok(None);
                    }
                    return Err(e);
                }
            }
        }
        Ok(Some(collected))
    }
}

/// 解析一帧（纯函数，可单测）。
/// Ok(None) = 空帧（跳过）；Ok(Some) = 合法请求（含通知：id 为 None）；Err = 应回错误响应。
pub fn parse_request(frame: &[u8]) -> std::result::Result<Option<Request>, RpcFailure> {
    let text = String::from_utf8_lossy(frame);
    let text = text.trim();
    if text.is_empty() {
        return Ok(None);
    }

    // JSON-RPC 2.0 严格校验（713 纪律）
    let value: Value = match serde_json::from_str(text) {
        Ok(v) => v,
        Err(e) => {
            crate::log::error(format!("JSON parse error: {e}"));
            return Err(RpcFailure {
                id: None,
                code: ERR_PARSE,
                message: "Parse error.".into(),
            });
        }
    };

    let id = value.get("id").cloned();
    let jsonrpc_ok = value
        .get("jsonrpc")
        .and_then(|v| v.as_str())
        .is_some_and(|s| s == "2.0");
    let method_ok = value.get("method").is_some_and(|m| m.is_string());
    let id_ok =
        id.is_none() || id.as_ref().is_some_and(|v| v.is_string() || v.is_number() || v.is_null());

    if !jsonrpc_ok || !method_ok || !id_ok {
        return Err(RpcFailure {
            id,
            code: ERR_INVALID_REQUEST,
            message: "Invalid request.".into(),
        });
    }

    Ok(Some(Request {
        id,
        method: value["method"].as_str().unwrap_or_default().to_string(),
        params: value.get("params").cloned(),
    }))
}

/// 处理单个帧（已剥离 magic）。返回响应行；None = 无需响应（通知或空帧）。
fn handle_frame(frame: &[u8]) -> Option<Vec<u8>> {
    match parse_request(frame) {
        Ok(None) => None,
        Ok(Some(req)) => {
            // 通知（无 id）不返回响应
            if req.id.is_none() {
                let _ = crate::engine::dispatch(&req.method, req.params);
                return None;
            }
            match crate::engine::dispatch(&req.method, req.params) {
                Ok(result) => Some(rpc_result(req.id, result)),
                Err((code, message)) => Some(rpc_error(req.id, code, &message)),
            }
        }
        Err(f) => Some(rpc_error(f.id, f.code, &f.message)),
    }
}

fn rpc_result(id: Option<Value>, result: Value) -> Vec<u8> {
    let resp = serde_json::json!({ "jsonrpc": "2.0", "id": id, "result": result });
    let mut line = serde_json::to_vec(&resp).unwrap_or_default();
    line.push(b'\n');
    line
}

fn rpc_error(id: Option<Value>, code: i64, message: &str) -> Vec<u8> {
    let resp = serde_json::json!({
        "jsonrpc": "2.0",
        "id": id,
        "error": { "code": code, "message": message }
    });
    let mut line = serde_json::to_vec(&resp).unwrap_or_default();
    line.push(b'\n');
    line
}

/// 写一条响应消息。返回 false = 写出失败（对端已断，应终止本连接）。
fn write_message(handle: HANDLE, payload: &[u8]) -> bool {
    unsafe {
        let mut written = 0u32;
        match WriteFile(handle, Some(payload), Some(&mut written), None) {
            Ok(()) => {
                let _ = FlushFileBuffers(handle);
                true
            }
            Err(e) => {
                // 对端在响应写出前关闭属正常竞态（客户端退出/断线重连），不得记 ERROR 刷爆日志
                let win32 = e.code().0 as u32 & 0xFFFF;
                const ERROR_NO_DATA_U32: u32 = 232; // 0x800700E8「管道正在被关闭」
                if win32 == ERROR_BROKEN_PIPE.0 as u32
                    || win32 == ERROR_PIPE_NOT_CONNECTED.0 as u32
                    || win32 == ERROR_NO_DATA_U32
                {
                    crate::log::info("peer closed before response write; dropping connection");
                } else {
                    crate::log::error(format!("WriteFile failed: {e}"));
                }
                false
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_valid_request() {
        let r = parse_request(br#"{"jsonrpc":"2.0","id":1,"method":"ping"}"#)
            .unwrap()
            .unwrap();
        assert_eq!(r.method, "ping");
        assert_eq!(r.id, Some(serde_json::json!(1)));
        assert!(r.params.is_none());
    }

    #[test]
    fn parses_notification_without_id() {
        let r = parse_request(br#"{"jsonrpc":"2.0","method":"ping"}"#)
            .unwrap()
            .unwrap();
        assert_eq!(r.method, "ping");
        assert!(r.id.is_none());
    }

    #[test]
    fn empty_frame_is_skipped() {
        assert!(parse_request(b"   \r\n").unwrap().is_none());
        assert!(parse_request(b"").unwrap().is_none());
    }

    #[test]
    fn malformed_json_is_parse_error() {
        let f = parse_request(b"{not json").unwrap_err();
        assert_eq!(f.code, ERR_PARSE);
        assert!(f.id.is_none());
    }

    /// 严格校验矩阵：jsonrpc / method / id 三类非法都必须 -32600。
    #[test]
    fn strict_validation_rejects_invalid_shapes() {
        let cases: Vec<&[u8]> = vec![
            br#"{"jsonrpc":"1.0","id":1,"method":"ping"}"#,
            br#"{"id":1,"method":"ping"}"#,
            br#"{"jsonrpc":"2.0","id":1,"method":123}"#,
            br#"{"jsonrpc":"2.0","id":{"a":1},"method":"ping"}"#,
        ];
        for c in cases {
            let f = parse_request(c).unwrap_err();
            assert_eq!(f.code, ERR_INVALID_REQUEST, "frame={:?}", String::from_utf8_lossy(c));
        }
    }

    #[test]
    fn strip_magic_handles_prefixed_and_bare_frames() {
        let mut prefixed = b"BDIX1|{\"jsonrpc\":\"2.0\"}".to_vec();
        assert_eq!(strip_magic(&mut prefixed), br#"{"jsonrpc":"2.0"}"#);
        let mut bare = br#"{"jsonrpc":"2.0"}"#.to_vec();
        assert_eq!(strip_magic(&mut bare), br#"{"jsonrpc":"2.0"}"#);
    }

    /// 首帧 magic 判定：不带 magic 的连接必须被丢弃。
    #[test]
    fn magic_prefix_required_for_first_frame() {
        assert!(b"BDIX1|{}".starts_with(MAGIC.as_bytes()));
        assert!(!b"BDCB1|{}".starts_with(MAGIC.as_bytes()));
        assert!(!b"{}".starts_with(MAGIC.as_bytes()));
    }
}
