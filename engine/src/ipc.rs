//! IPC server：NamedPipe 多客户端（4 连接槽），JSON-RPC 2.0 行协议（713 纪律）。
//! 管道：`\\.\pipe\BetterDesktop.Clipboard.Engine`，magic 前缀 `BDCB1|`（C1 安全模型：magic 校验后才派发，丢弃误连/低技能攻击）。
//! 每连接：读线程统一读写（响应 + 事件串行，同一线程避免并发操作管道句柄——实测并发 ReadFile/WriteFile 会让 WriteFile 挂起）。
//! 读超时 100ms：空闲时读线程周期性醒来消费事件队列（事件推送延迟 ≤100ms，无事件零开销）。
//! 事件推送按连接独立维护（subscribe_events 注册；无订阅者零开销）。

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc;
use std::thread;
use std::time::Duration;

use windows::core::{w, Result};
use windows::Win32::Foundation::{
    CloseHandle, ERROR_BROKEN_PIPE, ERROR_MORE_DATA, ERROR_PIPE_CONNECTED,
    ERROR_PIPE_NOT_CONNECTED, HANDLE,
};
use windows::Win32::Storage::FileSystem::{
    FlushFileBuffers, ReadFile, WriteFile, PIPE_ACCESS_DUPLEX,
};
use windows::Win32::System::Pipes::{
    ConnectNamedPipe, CreateNamedPipeW, DisconnectNamedPipe, PeekNamedPipe, NAMED_PIPE_MODE,
    PIPE_READMODE_MESSAGE, PIPE_TYPE_MESSAGE, PIPE_WAIT,
};

pub const PIPE_NAME: &str = r"\\.\pipe\BetterDesktop.Clipboard.Engine";
pub const MAGIC: &str = "BDCB1|";
pub const MAX_INSTANCES: u32 = 4;

const READ_BUF: usize = 64 * 1024;

/// 启动 IPC server：MAX_INSTANCES 个连接线程，每个循环 等待连接→处理→断开→再等待。
pub fn start() {
    for _ in 0..MAX_INSTANCES {
        thread::spawn(instance_loop);
    }
    crate::log::info(format!("ipc server started: {PIPE_NAME} ({MAX_INSTANCES} slots)"));
}

fn instance_loop() {
    unsafe {
        loop {
            // dwpipemode：消息模式（一条消息 = 一行协议帧）
            let pipe_mode =
                NAMED_PIPE_MODE(PIPE_TYPE_MESSAGE.0 | PIPE_READMODE_MESSAGE.0 | PIPE_WAIT.0);
            // 0.58：CreateNamedPipeW 裸返回 HANDLE（非 Result），用 is_invalid 判断失败
            let handle = CreateNamedPipeW(
                w!(r"\\.\pipe\BetterDesktop.Clipboard.Engine"),
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

/// 处理一条连接：首帧必须带 magic 前缀（C1 安全模型：丢弃误连/低技能攻击）；后续每帧剥离可选 magic 后逐帧 JSON-RPC 处理。
/// 读线程统一读写：请求响应 + 事件推送串行（实测并发读/写同一句柄会使 WriteFile 挂起，故不用独立写线程）。
/// 主循环 PeekNamedPipe 非阻塞轮询（PIPE_WAIT 下 ReadFile 无数据会永久阻塞，读超时不生效）：有帧则处理，
/// 无帧则消费事件队列并休眠（事件推送延迟 ≤50ms）。
fn handle_connection(handle: HANDLE) {
    let (tx, rx) = mpsc::channel::<Vec<u8>>();
    let subscribed = std::sync::Arc::new(AtomicBool::new(false));

    // 第一帧：等待客户端首帧（含 magic 校验）后才注册订阅者（订阅前丢事件可接受）
    // 【2026-09-12 修复】加 5s 超时：此前是**无限循环** —— 客户端连上却始终不发首帧
    //（误连、被中间件挂起的连接）会永久占住线程与连接槽；连开 4 个即耗尽 MAX_INSTANCES，
    // 之后所有客户端都连不上（引擎看着还活着，实际完全不可用）。
    let first_deadline = std::time::Instant::now() + Duration::from_secs(5);
    let mut first = loop {
        if std::time::Instant::now() > first_deadline {
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

    // 注册订阅者（连接存续期间可收事件；断开时移除）
    let sub_id = {
        use std::sync::atomic::Ordering;
        let mut subs = crate::engine::SUBSCRIBERS.lock().unwrap_or_else(|e| e.into_inner());
        let id = crate::engine::NEXT_SUB_ID.fetch_add(1, Ordering::Relaxed);
        subs.push(crate::engine::Subscriber {
            id,
            tx: tx.clone(),
            subscribed: std::sync::Arc::clone(&subscribed),
        });
        id
    };

    if let Some(resp) = handle_frame(strip_magic(&mut first), &subscribed) {
        write_message(handle, &resp);
    }

    // 【2026-09-12 审计修复】订阅生效后**立即推一次状态快照**：引擎只在 enabled / paused
    // **变化**时才广播，客户端连上时若引擎已处于「暂停 / 已关闭监听」，客户端会一直按默认值显示
    //（用户看到的开关与实际相反）。客户端首帧固定就是 subscribe_events，此处推送来得及。
    if subscribed.load(Ordering::Acquire) {
        push_state_snapshot(&tx);
    }

    loop {
        match peek_pipe(handle) {
            Peek::Data(_) => match read_message(handle) {
                Ok(Some(mut m)) => {
                    if let Some(resp) = handle_frame(strip_magic(&mut m), &subscribed) {
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
            Peek::Empty => {
                // 空闲：消费事件队列（无事件时轻量空转）
                if !drain_events(handle, &rx) {
                    crate::log::error("event write failed; terminating connection");
                    break;
                }
                thread::sleep(Duration::from_millis(50));
            }
            Peek::Closed => break,
            Peek::Error(e) => {
                crate::log::error(format!("peek error: {e}"));
                break;
            }
        }
    }

    unregister(sub_id);
}

/// PeekNamedPipe 探测结果（非阻塞）。
#[allow(dead_code)] // Data 的字节数暂未使用（仅作"有数据"信号）
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
                if win32 == ERROR_BROKEN_PIPE.0 as u32
                    || win32 == ERROR_PIPE_NOT_CONNECTED.0 as u32
                {
                    Peek::Closed
                } else {
                    Peek::Error(e)
                }
            }
        }
    }
}

/// 消费事件队列并写出（读线程内执行，与响应串行，避免并发句柄操作）。
/// 返回 false = 写出失败（对端已断），主循环应终止本连接（防僵尸订阅连接刷屏/占槽）。
fn drain_events(handle: HANDLE, rx: &mpsc::Receiver<Vec<u8>>) -> bool {
    while let Ok(ev) = rx.try_recv() {
        if !write_message(handle, &ev) {
            return false;
        }
    }
    true
}

/// 从订阅者列表移除本连接（按订阅 id）。
fn unregister(id: u64) {
    // 【2026-09-12 修复】锁中毒时取回内部值继续。此前 `if let Ok` 会静默跳过移除 ——
    // 一旦锁被毒化，订阅项**永不移除**（反复重连的连接持续累积）。
    let mut subs = crate::engine::SUBSCRIBERS
        .lock()
        .unwrap_or_else(|e| e.into_inner());
    subs.retain(|s| s.id != id);
}

/// 剥离可选 magic 前缀（每帧统一处理；无前缀视为纯 JSON 帧）。
fn strip_magic(frame: &mut [u8]) -> &[u8] {
    if frame.starts_with(MAGIC.as_bytes()) {
        &frame[MAGIC.len()..]
    } else {
        frame
    }
}

/// 单条入站消息硬上限（32MB）。`ERROR_MORE_DATA` 分支是**无限累积**的 —
/// 构造超大帧即可吃光引擎内存（OOM abort）。
const MAX_INBOUND_FRAME: usize = 32 * 1024 * 1024;

/// 消息模式下读一条消息（可能跨多次 ReadFile 拼接，ERROR_MORE_DATA = 单条消息超过 buffer）。
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
                        // 【2026-09-14 修复】单条消息超 buffer：只取 ReadFile 实际写入的字节数。
                        // 此前拼的是**整块 64KB** —— 消息模式管道下缓冲通常恰好被填满所以"看着对"，
                        // 但那是实现细节而非 API 契约：一旦 read < READ_BUF（缓冲调大 / 模式变更），
                        // 未写入的尾随旧数据会被当成本帧内容 → JSON 解析错乱。与成功分支保持一致。
                        collected.extend_from_slice(&chunk[..(read as usize).min(chunk.len())]);
                        continue;
                    }
                    if collected.is_empty()
                        && (win32 == ERROR_BROKEN_PIPE.0 as u32
                            || win32 == ERROR_PIPE_NOT_CONNECTED.0 as u32)
                    {
                        return Ok(None); // 客户端正常关闭
                    }
                    return Err(e);
                }
            }
        }
        Ok(Some(collected))
    }
}

/// 向刚订阅成功的连接推一次当前状态（monitoring + pause），用于校正客户端初值。
///
/// 引擎的 `broadcast` 只在状态**变化**时发事件，因此"连接建立时引擎已经是暂停/关闭监听"
/// 这种情形下客户端永远收不到那一次事件 —— 显示与实际相反（2026-09-12 审计修复）。
fn push_state_snapshot(tx: &mpsc::Sender<Vec<u8>>) {
    let enabled = crate::engine::SETTINGS
        .get()
        .map(|s| s.lock().unwrap_or_else(|e| e.into_inner()).enabled)
        .unwrap_or(true);
    let paused = crate::engine::PAUSED.load(Ordering::Acquire);

    for (method, params) in [
        ("monitoring_changed", serde_json::json!({ "enabled": enabled })),
        ("pause_changed", serde_json::json!({ "paused": paused })),
    ] {
        let note = serde_json::json!({ "jsonrpc": "2.0", "method": method, "params": params });
        if let Ok(mut line) = serde_json::to_vec(&note) {
            line.push(b'\n');
            let _ = tx.send(line);
        }
    }
}

/// 处理单个 JSON-RPC 请求帧（剥离 magic 后）。返回响应行；None = 无需响应（通知）。
fn handle_frame(
    frame: &[u8],
    subscribed: &std::sync::Arc<AtomicBool>,
) -> Option<Vec<u8>> {
    let text = String::from_utf8_lossy(frame);
    let text = text.trim();
    if text.is_empty() {
        return None;
    }

    // 713 纪律：JSON-RPC 2.0 严格校验——jsonrpc 必须 '2.0'、method 必须字符串、id 必须 string/number/null
    let value: serde_json::Value = match serde_json::from_str(text) {
        Ok(v) => v,
        Err(e) => {
            crate::log::error(format!(
                "JSON parse error: {e}; frame bytes: {:?}",
                String::from_utf8_lossy(frame)
            ));
            return Some(rpc_error(None, -32700, "Parse error."));
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
        return Some(rpc_error(id, -32600, "Invalid request."));
    }

    let method = value["method"].as_str().unwrap_or_default().to_string();
    let params = value.get("params").cloned();

    // 订阅注册（引擎事件推送；无订阅者零开销）
    if method == "subscribe_events" {
        subscribed.store(true, Ordering::Release);
        return Some(rpc_result(id, serde_json::json!({ "ok": true })));
    }
    // 通知（无 id）不返回响应
    if id.is_none() {
        return None;
    }

    match crate::engine::dispatch(&method, params) {
        Ok(result) => Some(rpc_result(id, result)),
        Err((code, message)) => Some(rpc_error(id, code, &message)),
    }
}

fn rpc_result(id: Option<serde_json::Value>, result: serde_json::Value) -> Vec<u8> {
    let resp = serde_json::json!({ "jsonrpc": "2.0", "id": id, "result": result });
    let mut line = serde_json::to_vec(&resp).unwrap_or_default();
    line.push(b'\n');
    line
}

fn rpc_error(id: Option<serde_json::Value>, code: i64, message: &str) -> Vec<u8> {
    let resp = serde_json::json!({
        "jsonrpc": "2.0",
        "id": id,
        "error": { "code": code, "message": message }
    });
    let mut line = serde_json::to_vec(&resp).unwrap_or_default();
    line.push(b'\n');
    line
}

/// 写一条响应消息（消息模式下一次 WriteFile 即一条消息）。返回 false = 写出失败（对端已断，应终止本连接，
/// 否则会变成僵尸订阅连接：Peek 返回 Empty 而 WriteFile 持续失败，刷日志并占连接槽——2026-09-12 真机发现
/// 引擎日志数千行 `WriteFile failed: 管道正在被关闭(0x800700E8)` 的根因）。
fn write_message(handle: HANDLE, payload: &[u8]) -> bool {
    unsafe {
        let mut written = 0u32;
        match WriteFile(handle, Some(payload), Some(&mut written), None) {
            Ok(()) => {
                let _ = FlushFileBuffers(handle);
                true
            }
            Err(e) => {
                // 【2026-09-12 修复·日志分级】对端在响应写出前关闭是**正常竞态**（客户端退出 /
                // 断线重连 / 超时放弃），按 ERROR 记录会刷爆日志、把真错误淹没（实测 1835 条
                // `WriteFile failed: 管道正在被关闭`）。只有真正的异常才记 error。
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
