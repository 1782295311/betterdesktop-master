//! 控制管道服务端：`\\.\pipe\BetterDesktop.MenuCmd`。
//!
//! 从宿主（C# `host/MenuCommandPipe.cs`）迁到 core —— 壳是按需的，管道的服务端必须常驻，
//! 否则"壳没跑"就等于"所有入口都连不上"。
//!
//! # 协议
//!
//! 权威是 `protocols/bdmc1-test-vectors.json`（本模块只实现它，不定义它）。
//! 两种形态：legacy（写后即忘）与 control（请求/应答）。
//!
//! # 分帧：字节模式 + 累积到换行（**不是消息模式**）
//!
//! 刻意用 `PIPE_TYPE_BYTE`：向量里的 `framing` 已冻结为"一行一条消息，CRLF/LF 均接受"，
//! 而现有 C# 客户端用 `StreamWriter.WriteLine + AutoFlush`，**可能把字符串与换行分两次写**。
//! 消息模式下这就是两条消息，行解析器会拿到半行 —— 与已封版的契约冲突。
//!
//! # 并发模型：固定实例数 + 每实例一线程（blocking）
//!
//! 不是 IOCP / async（YAGNI）：客户端只有 CLI、托盘、系统右键降级路径，数量少且都是短命令。
//! N 个实例各自阻塞在 `ConnectNamedPipe`，连上就服务一个客户端，服务完重建实例再等下一个。
//!
//! **已知天花板（有意保留）**：`ConnectNamedPipe` 未做 overlapped 超时 ——
//! 一个连上却不发数据的客户端会占住**一个**实例线程，直到读超时（[`READ_DEADLINE_MS`]）把它踢掉。
//! 因为并发度被实例数硬限制且 ACL 已把非同用户挡在门外，这个占用是可接受的。
//! 升级触发条件：实测出现"实例被占满导致正常请求排队"。
//!
//! # 顺序红线
//!
//! [`serve_client`] 的第一件事就是 [`security::validate_client`] —— **在读任何消息之前**。
//! 位置刻意放在函数最前面，让它物理上不可能被后来的改动绕过。
//!
//! # 管道名所有权：**只属于 core**
//!
//! `\\.\pipe\BetterDesktop.MenuCmd` 的合法服务端**只有 core 一个**。
//! 2026-09-19 之前，`host/MenuCommandPipe.cs`（+ `Bootstrap.cs:391`）也建**同一个名字**的服务端，
//! 而两侧动词集不相交 → 客户端连上谁不确定（掷硬币）。现已把它改为独立管道名
//! **`BetterDesktop.HostCmd`**（legacy 形态归它，core 只管 `@ctl`）——
//! 归属契约见 `protocols/bdmc1-test-vectors.json` 的 `_routing`。
//! **因此本名字现在只归 core**；若仍创建失败，原因在它处（见下面的候选扫描）。
//!
//! 两件事让这个冲突比"多一条日志"严重得多：
//!
//! 1. **`FILE_FLAG_FIRST_PIPE_INSTANCE` 的行为是"名字被占就失败"** —— 于是 core 会**完全无法
//!    服务控制管道**。托盘图标照旧（它不依赖管道），用户看不出异常，但所有入口
//!    （`bdctl` / 右键降级 / 面板）都不通。这是 core 的核心职责失效。
//! 2. 只有**第一个**实例能带那个标志，所以其余实例线程若照常创建，会**附着到占用者的同名管道对象上**
//!    （Windows 允许同名管道由多进程各建实例）—— 变成"部分请求归 core、部分归占用者"的**劈裂**。
//!
//! 故本模块把所有权做成**显式**的：0 号线程确权（唯一带标志的那个），1..N 号**等确权成功**才创建。
//! 确权失败时 core 对控制面彻底不在场，并给出**能回答"谁占的 / 为什么 / 怎么办"**的诊断
//! （见 [`describe_create_failure`]）与有界退避（见 [`backoff_delay`]）。

use std::sync::OnceLock;
use std::time::{Duration, Instant};

use windows::Win32::Foundation::{
    CloseHandle, ERROR_ACCESS_DENIED, ERROR_PIPE_BUSY, GetLastError, HANDLE,
};
use windows::Win32::Storage::FileSystem::{
    FILE_FLAG_FIRST_PIPE_INSTANCE, PIPE_ACCESS_DUPLEX,
};
use windows::Win32::System::Pipes::{
    ConnectNamedPipe, CreateNamedPipeW, DisconnectNamedPipe, PIPE_READMODE_BYTE, PIPE_TYPE_BYTE,
    PIPE_WAIT, PeekNamedPipe,
};
use windows::core::PCWSTR;

use crate::components::{self, Component, Desired};
use crate::protocol::{self, ErrorCode, Message, Response};
use crate::security::{self, CurrentUser};
use crate::settings::Settings;
use crate::supervisor::Outcome;

/// 管道名（与 C# 侧 `MenuCommandPipeClient.PipeName` 必须逐字一致）。
pub const PIPE_NAME: &str = r"\\.\pipe\BetterDesktop.MenuCmd";

/// 并行实例数（= 同时可服务的客户端上限）。
const INSTANCE_COUNT: usize = 4;

/// 管道缓冲（单侧 64 KiB，与既有实现同量级）。
const PIPE_BUFFER_BYTES: u32 = 64 * 1024;

/// `PeekNamedPipe` 轮询间隔。
const POLL_INTERVAL: Duration = Duration::from_millis(10);

/// 单条消息的读超时：超过则断开（防"连上但不发数据"占住实例）。
const READ_DEADLINE: Duration = Duration::from_millis(2000);

/// 单个连接的**最长生命周期**：超过则强制断开并回收实例。
///
/// [`READ_DEADLINE`] 只能踢掉"连上就不说话"的客户端；一个**每隔 1.9 秒发一条请求**的客户端
/// 可以永远占住一个实例。4 个这样的客户端就能把控制面彻底饿死。
/// 30 秒足够任何正常客户端（CLI 一次请求、壳几条命令）用完，却能保证实例必然回流。
const MAX_CONNECTION_LIFETIME: Duration = Duration::from_secs(30);

/// 单次 `ReadFile` 的块大小（不需要大于缓冲；小一点能让超长消息更早被发现）。
const READ_CHUNK: usize = 4096;

/// 创建实例失败后的重试退避上限（秒）。
const RETRY_BACKOFF_MAX_SECS: u64 = 30;

/// 到达退避上限后，每隔多少次再提醒一次（`30s × 10 ≈ 5 分钟`）。
const RETRY_REMIND_EVERY: u32 = 10;

/// 仓库里**可能**占着 [`PIPE_NAME`] 的进程名（小写，与 [`process::running_exe_names`] 同域）。
///
/// 这是"**候选**"而不是"确证"：真正的归属要靠句柄枚举（`NtQuerySystemInformation` +
/// 复制句柄），需要重权限且实现量可观。而候选扫描已经足以把排查方向从
/// "`GetLastError=5` 是什么意思"缩短到"哪个进程在跑"—— 后者才是人真正要问的问题。
const PIPE_NAME_CANDIDATES: &[&str] = &[
    // 另一个 core（单实例锁失效、或以别的用户身份运行）
    "betterdesktop-core.exe",
    // 【2026-09-19 已移除 "betterdesktop.host.exe"】它曾是这里的候选，因为 Host 也建同名服务端。
    // 现在 Host 的 legacy 服务端改用独立的 `BetterDesktop.HostCmd`，**不再抢本名字** →
    // 留着它会让每一个排查本名字被占的人去怀疑一个已经不可能的原因（诊断信息必须与现实同步，
    // 否则比没有诊断更糟：它把人引向错误方向）。
    // 可能内嵌宿主 bootstrap 的入口
    "betterdesktop.desktopcontrol.exe",
    "betterdesktop.tray.exe",
];

/// 创建实例失败的原因（**保留错误码** —— 调用方要靠它区分"名字被占"与其它失败）。
struct CreateError {
    /// Win32 错误码。
    code: u32,
    /// 原始描述（进日志）。
    text: String,
}

/// 创建失败后的重试退避：`1s → 2s → 4s → 8s → 16s → 30s → 30s → …`
///
/// **为什么必须退避**：这条失败路径原本是"每秒重试一次、永不放弃"。那有两个后果：
/// 日志被同义 ERROR 淹掉（真问题反而看不见），以及在一个**需要人来处理**的状态下
/// 持续空转。退避之后，日志本身就变成了"这个问题持续了多久、试到第几档"的证据。
fn backoff_delay(attempt: u32) -> Duration {
    // attempt 封顶到 5 ⇒ 1<<5 = 32，再与上限取小 ⇒ 30；之后的档位都是 30
    let secs = 1u64 << attempt.min(5);
    Duration::from_secs(secs.min(RETRY_BACKOFF_MAX_SECS))
}

/// 这一次退避重试该不该记一行日志。
///
/// ① 首次失败**必记**；② 每次退避**档位变化**必记（诊断价值最高的一行：1s→2s→…→30s）；
/// ③ 到达上限后每 [`RETRY_REMIND_EVERY`] 次（≈5 分钟）提醒一次。
///
/// ③ 是刻意的：**不能**在触顶后彻底静默 —— "管道名一直被占"正是最该被人看见、最需要人来处理的状态，
/// 让它沉底比刷屏更糟。每一档一次 + 触顶后每 5 分钟一次，是"不刷屏"与"不沉底"之间的取舍。
fn should_log_retry(attempt: u32) -> bool {
    if attempt == 0 {
        return true;
    }
    let now = backoff_delay(attempt);
    if now != backoff_delay(attempt - 1) {
        return true;
    }
    now >= Duration::from_secs(RETRY_BACKOFF_MAX_SECS) && (attempt + 1) % RETRY_REMIND_EVERY == 0
}

/// 当前在跑的 [`PIPE_NAME_CANDIDATES`]（**排除自己**）。
///
/// 排除自己不是优化：core 一定在跑，把它列进"嫌疑名单"只会误导每一个读日志的人。
fn running_candidates() -> String {
    let running = crate::process::running_exe_names();
    let self_name = std::env::current_exe()
        .ok()
        .and_then(|p| p.file_name().map(|n| n.to_string_lossy().to_ascii_lowercase()));

    let hits: Vec<&str> = PIPE_NAME_CANDIDATES
        .iter()
        .copied()
        .filter(|name| Some(name.to_string()) != self_name)
        .filter(|name| running.contains(*name))
        .collect();

    if hits.is_empty() {
        "（未扫到已知候选；占用者可能是已改名或非 BetterDesktop 的进程）".to_string()
    } else {
        hits.join(", ")
    }
}

/// 把创建失败翻译成一行**能回答"谁占的 / 为什么 / 怎么办"**的日志。
///
/// 裸的 `GetLastError=5` 看不出原因。在 [`FILE_FLAG_FIRST_PIPE_INSTANCE`] 下，
/// `ERROR_ACCESS_DENIED` 的**确切**含义是"这个名字已经被别的进程建过了"，
/// 与"权限不足"毫无关系 —— 这个区别决定了排查方向（去查权限 = 白费一天）。
fn describe_create_failure(index: usize, e: &CreateError, attempt: u32, delay: Duration) -> String {
    let mut text = format!("pipe instance {index}: create failed: {}", e.text);

    // 两个错误码，同一个结论（"这个名字不归我们"），但**成因不同**，必须都说清 ——
    // 只认其中一个会让另一个场景下的人去查错方向。真机两种都实测到过：
    //   · 5  = ERROR_ACCESS_DENIED —— 占用者也是以"独占首实例"方式建的（core 自己就是这么建的）；
    //   · 231= ERROR_PIPE_BUSY   —— 占用者不是那样建的，或它的实例正被占用。
    let cause = match e.code {
        c if c == ERROR_ACCESS_DENIED.0 => Some(
            "占用者也是以 FILE_FLAG_FIRST_PIPE_INSTANCE 方式建的（本 core 自己就是这么建的，\
             所以对方很可能是**另一个同名服务端**）。",
        ),
        c if c == ERROR_PIPE_BUSY.0 => {
            Some("该名字已被创建（占用者不是独占方式建的，或其实例正被占用）。")
        }
        _ => None,
    };

    if let Some(cause) = cause {
        text.push_str(&format!(
            "\n    ⇒ '{PIPE_NAME}' 不归本 core：{cause}\
             \n    ⇒ 两个错误码在这里都**与权限无关**，不要去查 ACL —— 根因是「这个名字已被别人建过」。\
             \n    ⇒ 后果：本 core **无法服务控制管道** —— 托盘图标照旧（它不依赖管道），\
             但所有入口（bdctl / 右键降级 / 面板）都不通。这是 core 核心职责失效，不是日志噪声。\
             \n    ⇒ 在跑的候选：{}。\
             \n    ⇒ 退避重试第 {} 次，{:?} 后重试（上限 {}s）。占用者退出后会自动恢复，无需重启 core。",
            running_candidates(),
            attempt + 1,
            delay,
            RETRY_BACKOFF_MAX_SECS
        ));
    }
    text
}

/// core 启动时刻（用于 `status` 的 uptime）。
static STARTED_AT: OnceLock<Instant> = OnceLock::new();

/// 实例代数：唤醒等事件会推进它，各实例线程在下一轮循环头检查（仅用于可观测性）。
static GENERATION: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);

/// 唤醒后的管道恢复（S3.5 第 ③ 步）。
///
/// # 为什么这里**不需要**真的去"重建"什么
///
/// 实例线程的结构决定了管道天生自愈：每服务完一个客户端就 `DisconnectNamedPipe` + `CloseHandle`
/// 并**重建**一个全新实例。所以"陈旧连接"只可能存在于**当时正处于连接中**的那些实例上，
/// 而它们最多 [`READ_DEADLINE`]（2 秒）就会因读超时而回收。
///
/// 那为什么不做 `overlapped` + `CancelIoEx` 去**主动打断**阻塞在 `ConnectNamedPipe` 的实例？
/// 因为**那些实例正是唯一没有陈旧连接问题的那些** —— 它们还没被任何客户端连上，没有任何状态需要重置。
/// 为它们引入 overlapped I/O 只会增加复杂度，不解决任何实际问题。
///
/// 于是本函数只做两件事：推进代数（让每个实例在回收时**留下一行可追溯的日志**）、
/// 把当前的到期上界写进日志（便于事后确认"唤醒后多久管道恢复"）。
pub fn on_resume() {
    let generation = GENERATION.fetch_add(1, std::sync::atomic::Ordering::SeqCst) + 1;
    crate::log::info(format!(
        "pipe(resume): generation -> {generation}; any connection live at suspend recycles within {}s \
         (instances waiting on ConnectNamedPipe hold no stale state and need no reset)",
        READ_DEADLINE.as_secs_f64()
    ));
}

/// 读循环的终止原因。
#[derive(Debug)]
enum ReadOutcome {
    /// 拿到一整行（**含**行终止符，交由 `parse_line` 剥离）。
    Line(Vec<u8>),
    /// 超时未凑齐一行。
    Timeout,
    /// 超过 [`protocol::MAX_MESSAGE_BYTES`]。
    TooLarge,
    /// 对端关闭。
    Closed,
    /// 其它 I/O 失败。
    Io(String),
}

/// 启动管道服务端（非阻塞：起 [`INSTANCE_COUNT`] 个线程后立即返回）。
///
/// 失败返回 `Err` —— 调用方**不得**静默继续（没有管道的 core 等于所有入口都断了）。
pub fn start(components: Vec<Component>) -> Result<(), String> {
    let _ = STARTED_AT.set(Instant::now());
    let current = CurrentUser::load()?;

    // ACL 在启动路径已经构造好并存进静态；这里只做存在性检查，避免"以为有 ACL 实际没有"。
    if crate::pipe_security().is_none() {
        return Err("pipe ACL is not initialized; refusing to start the control pipe".to_string());
    }

    let shared = std::sync::Arc::new((components, current));
    let ownership = std::sync::Arc::new(Ownership::default());
    for index in 0..INSTANCE_COUNT {
        let shared = std::sync::Arc::clone(&shared);
        let ownership = std::sync::Arc::clone(&ownership);
        std::thread::Builder::new()
            .name(format!("bd-core-pipe-{index}"))
            .spawn(move || instance_loop(index, &ownership, &shared.0, &shared.1))
            .map_err(|e| format!("failed to spawn pipe instance thread {index}: {e}"))?;
    }
    // 【别在这里说 "serving"】实例是在**各自的线程里**创建并可能失败的 —— 此处用完成时态宣告
    // "已在服务"是**假话**（真机实测：这一行紧跟着就是 `instance 0: create failed`）。
    // 真正的"开始服务"由 0 号线程确权成功后记（见 `instance_loop`）。
    crate::log::info(format!(
        "control pipe: {INSTANCE_COUNT} instance threads started for {PIPE_NAME} \
         (serving begins once instance 0 establishes ownership)"
    ));
    Ok(())
}

/// 「本进程已经建立起管道名的所有权」的共享标志（见 [`Ownership`]）。
#[derive(Default)]
struct Ownership {
    established: std::sync::atomic::AtomicBool,
}

/// 0 号线程确权失败期间，其余线程的等待间隔。
///
/// 200ms 是取舍：正常情况下 0 号在微秒级就有结论，这个等待**根本不会被感知**；
/// 而确权真的失败时，1..3 号每 200ms 醒一次去看标志，代价可以忽略。
const OWNERSHIP_WAIT: Duration = Duration::from_millis(200);

/// 单个实例的生命周期：建实例 → 等连接 → 服务一个客户端 → 断开 → 重建。
///
/// # 为什么要区分"0 号"与"其余"
///
/// `FILE_FLAG_FIRST_PIPE_INSTANCE` **只能用在第一个实例**上（后续实例再带它会与自己的第一个冲突）。
/// 于是"这个名字是不是已经被别人占了"**只有 0 号线程能发现**。
///
/// 若 1..3 号照常创建，它们会**附着到别人那个同名管道对象上** —— Windows 允许同名管道由多个进程
/// 各自添加实例。后果不是"失败"，而是**劈裂**：一部分客户端被 core 服务、一部分被占用者接走，
/// 谁也不知道哪条请求去了哪边。那比"core 干脆不服务"难排查得多。
///
/// 所以：**0 号负责确权，其余等确权成功**。确权不成功时 core 对控制面是**彻底不在场**的 ——
/// 这是"所有权"这个词唯一诚实的实现方式。
fn instance_loop(
    index: usize,
    ownership: &Ownership,
    components: &[Component],
    current: &CurrentUser,
) {
    let establishes_ownership = index == 0;
    // 仅 0 号第一次尝试带 FIRST_PIPE_INSTANCE；成功后（含它自己后续的重建）都不再带。
    let mut owns_attempt = establishes_ownership;
    let mut failed_attempts: u32 = 0;

    loop {
        // 等待 0 号确权。**不能**在这里直接创建：那正是上面说的劈裂。
        if !establishes_ownership
            && !ownership
                .established
                .load(std::sync::atomic::Ordering::SeqCst)
        {
            std::thread::sleep(OWNERSHIP_WAIT);
            continue;
        }

        let generation = GENERATION.load(std::sync::atomic::Ordering::SeqCst);
        let pipe = match create_instance(owns_attempt) {
            Ok(h) => {
                failed_attempts = 0;
                if owns_attempt {
                    ownership
                        .established
                        .store(true, std::sync::atomic::Ordering::SeqCst);
                    owns_attempt = false;
                    crate::log::info(format!(
                        "pipe: ownership of {PIPE_NAME} established by instance 0 \
                         (missing instance threads 1..{INSTANCE_COUNT} will now attach)"
                    ));
                }
                h
            }
            Err(e) => {
                let delay = backoff_delay(failed_attempts);
                if should_log_retry(failed_attempts) {
                    crate::log::error(describe_create_failure(index, &e, failed_attempts, delay));
                }
                failed_attempts = failed_attempts.saturating_add(1);
                std::thread::sleep(delay);
                continue;
            }
        };

        if let Err(e) = unsafe { ConnectNamedPipe(pipe, None) } {
            crate::log::warn(format!("pipe instance {index}: ConnectNamedPipe failed: {e}"));
            unsafe {
                let _ = CloseHandle(pipe);
            }
            continue;
        }

        serve_client(pipe, components, current);

        unsafe {
            let _ = DisconnectNamedPipe(pipe);
            let _ = CloseHandle(pipe);
        }

        // 暂停期间发生过唤醒 → 记一行，让"唤醒后管道多久恢复服务"可追溯
        let after = GENERATION.load(std::sync::atomic::Ordering::SeqCst);
        if after != generation {
            crate::log::info(format!(
                "pipe instance {index}: recycled on a fresh instance after a resume event \
                 (generation {generation} -> {after})"
            ));
        }
    }
}

/// 创建一个管道实例。
///
/// `first_ever` 时附加 `FILE_FLAG_FIRST_PIPE_INSTANCE`：若已有同名管道存在则**创建失败**，
/// 从而阻止"别的进程抢先建同名管道冒充服务端"。该标志**只能用于首个实例**，
/// 后续实例再带它就会自己和自己冲突。
fn create_instance(ownership_attempt: bool) -> Result<HANDLE, CreateError> {
    let name = pcwstr_from_str(PIPE_NAME);
    let open_mode = if ownership_attempt {
        PIPE_ACCESS_DUPLEX | FILE_FLAG_FIRST_PIPE_INSTANCE
    } else {
        PIPE_ACCESS_DUPLEX
    };
    let attrs = crate::pipe_security().map(|s| s.as_attributes());

    // CreateNamedPipeW 直接返回 HANDLE（失败为 INVALID_HANDLE_VALUE），不是 Result —— 必须自己判无效。
    let handle = unsafe {
        CreateNamedPipeW(
            PCWSTR(name.as_ptr()),
            open_mode,
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
            INSTANCE_COUNT as u32,
            PIPE_BUFFER_BYTES,
            PIPE_BUFFER_BYTES,
            0,
            attrs.as_ref().map(|a| a as *const _),
        )
    };
    if handle.is_invalid() {
        // 错误码必须**原样带出去**：调用方要靠它区分"名字被占"（`ERROR_ACCESS_DENIED`）
        // 与其它失败，两者对读日志的人来说是完全不同的两件事。
        let code = unsafe { GetLastError().0 };
        return Err(CreateError {
            code,
            text: format!("CreateNamedPipeW({PIPE_NAME}) failed (GetLastError={code})"),
        });
    }
    Ok(handle)
}

/// 服务一个已连接的客户端。
///
/// **顺序红线**：第一件事就是校验调用者身份，之后才开始读消息。
fn serve_client(pipe: HANDLE, components: &[Component], current: &CurrentUser) {
    if let Err(reason) = security::validate_client(pipe, current) {
        // 详细原因只进本地日志（不外泄 SID/会话等细节给一个身份不符的连接）。
        crate::log::warn(format!("rejected pipe client: {reason}"));
        // 但仍回一个**结构化拒绝码**：否则调用方只看到"管道被挂断"，无法区分
        // "core 没跑" 与 "我被拒绝"。此时代理身份已撤销，是以我们自己的身份回写。
        let _ = write_line(pipe, &Response::err("", ErrorCode::Forbidden, "caller not authorized").encode());
        return;
    }

    let connected_at = Instant::now();
    loop {
        // 拖住实例的第二种方式（第一种是"连上不发数据"，由 READ_DEADLINE 处理）：
        // "每隔 1.9 秒发一条请求"的客户端可以永不超过读超时，从而永久占住一个实例。
        if connected_at.elapsed() >= MAX_CONNECTION_LIFETIME {
            crate::log::info(format!(
                "pipe connection exceeded its {}s lifetime; recycling the instance",
                MAX_CONNECTION_LIFETIME.as_secs()
            ));
            return;
        }

        let raw = match read_line_bounded(pipe) {
            ReadOutcome::Line(bytes) => bytes,
            ReadOutcome::Timeout => {
                crate::log::warn("pipe client read timeout; dropping connection");
                return;
            }
            ReadOutcome::TooLarge => {
                // 只有在"看起来是控制请求"时才回结构化错误 —— 其余畸形输入不欠回复
                reply_if_control(
                    pipe,
                    b"",
                    ErrorCode::PayloadTooLarge,
                    format!(
                        "message exceeds {} bytes",
                        protocol::MAX_MESSAGE_BYTES
                    ),
                );
                return;
            }
            ReadOutcome::Closed => return,
            ReadOutcome::Io(e) => {
                crate::log::warn(format!("pipe read failed: {e}"));
                return;
            }
        };

        let message = match protocol::parse_line(&raw) {
            Ok(m) => m,
            Err(reason) => {
                let code = match reason {
                    protocol::InvalidReason::TooLarge => Some(ErrorCode::PayloadTooLarge),
                    protocol::InvalidReason::EmptyHead => Some(ErrorCode::UnknownVerb),
                    // bad-magic / empty / invalid-utf8：不欠回复（多半不是发给我们管道的）
                    _ => None,
                };
                match code {
                    Some(c) => {
                        reply_if_control(pipe, &raw, c, reason.as_str().to_string());
                        return;
                    }
                    None => {
                        crate::log::warn(format!(
                            "dropping malformed pipe message: {}",
                            reason.as_str()
                        ));
                        return;
                    }
                }
            }
        };

        // 每条请求留一行（可观测性要求：管道请求可追）。arg 是调用方的路径/键，落本地日志。
        crate::log::info(format!(
            "pipe request: kind={} head={} arg={:?}",
            message.kind(),
            message.head(),
            message.arg()
        ));

        match message {
            Message::Legacy { action, arg } => {
                // legacy 写后即忘：不回包（既有 CLI 与系统右键扩展依赖这个语义）
                handle_legacy(&action, &arg, components);
            }
            Message::Control { verb, arg } => {
                // settings 每次请求读一次（不缓存：缓存会引入"缓存与实际不一致"这类难查问题）
                let settings = Settings::load();
                let response = dispatch_control(&verb, &arg, components, &settings);
                if !write_line(pipe, &response.encode()) {
                    return;
                }
            }
        }
    }
}

/// legacy 形态：**不属于本服务端**（2026-09-19 起两侧路由已分开）—— 只做响亮记录，不回包。
///
/// # 为什么"不属于这里"
///
/// 两种形态按 head 段分流（契约：`protocols/bdmc1-test-vectors.json` 的 `_routing`）：
/// `@ctl` → core（本模块），legacy → **Host**（`\\.\pipe\BetterDesktop.HostCmd`）。
/// 在此之前两者**同名**，于是 legacy 落到 core 只是个概率问题（掷硬币），而且 `TrySend` 照样返回
/// `true` → 调用方以为宿主已处理、**连回退都被短路**。
///
/// # 收到它意味着什么
///
/// 意味着**路由错了**：有调用方漏改，或有人新增了形态却没按规则分流。
/// 故这里记 **WARN** 并点名"routing error"，而不是继续以 info 级"未实现"的口吻记录 ——
/// 后者会让人以为"core 迟早会实现它"，而事实是**它永远不该来到这里**（这是防"未来有人加错路由"的兜底）。
///
/// **不回包**：legacy 契约就是"写后即忘"；给它回包等于伪造一种不存在的语义。
/// 这一点在契约的 `_routing.misRoute` 里写明了。
fn handle_legacy(action: &str, arg: &str, _components: &[Component]) {
    crate::log::warn(format!(
        "routing error: legacy action '{action}' (arg={arg:?}) arrived at core's control pipe — \
         legacy belongs to the Host (\\\\.\\pipe\\BetterDesktop.HostCmd); not handled, and no reply \
         is sent because the legacy shape is write-and-forget"
    ));
}

/// 控制形态分派。每个分支都必须**幂等**：重复发同一命令不产生额外副作用。
///
/// `settings` 由调用方传入（不在函数内读盘）：一是避免一次请求多次读配置，
/// 二是让单测能注入配置去覆盖"开关关闭"这类分支 —— 否则那类分支永远测不到。
fn dispatch_control(
    verb: &str,
    arg: &str,
    components: &[Component],
    settings: &Settings,
) -> Response {
    match verb {
        "status" => status_response(components, settings),
        "start" => change_running(components, arg.trim(), true),
        "stop" => change_running(components, arg.trim(), false),
        "toggle" => {
            let name = arg.trim();
            match find_component(components, name) {
                None => unknown_component(verb, name),
                Some(c) => {
                    let running = crate::process::is_running(&c.exe);
                    change_running(components, name, !running)
                }
            }
        }
        "get" => get_setting(arg.trim(), settings),
        "set" => set_setting(arg.trim()),
        "task" => task_command(arg.trim()),
        // 注：`set` 曾**刻意**返回 unknown-verb（"core 不写 settings，见 §6.7"）。
        // S5-2a 起 core 是**唯一写者**（ADR §13.19），故它成为受支持的 verb。
        other => Response::err(
            other,
            ErrorCode::UnknownVerb,
            format!(
                "verb '{other}' is not supported; supported: status | start | stop | toggle | get | set | task"
            ),
        ),
    }
}

/// `task <status|register|unregister>` —— 计划任务（S3-4）的运维面。
///
/// # 为什么这个动词**必须**存在
///
/// S3-4 的第 1 / 第 5 条决策是"**两者都做**"：安装程序兜底注册、卸载与应急路径要能删任务。
/// 若让 C# 或 PowerShell 各自再写一份任务定义（那份 XML），就是本仓库已经吃过的事故形态复发 ——
/// 两处实现各自自洽、谁也不报错（`protocols/native-dll-path-test-vectors.json` 的由来就是它）。
/// 故把三个动作**暴露成动词**，让 CLI / 安装器 / 后续入口走同一条实现：
///
/// ```text
/// bdctl --core task register | unregister | status
/// ```
///
/// **不是**给 core 自己用的：core 启动时直接调 [`crate::task::ensure`]，不绕管道
///（管道线程起来时监护器/任务都还没就位，绕一圈只会引入顺序耦合）。
///
/// # 为什么 `unregister` 先查询再删
///
/// 为了让回答**诚实**：`task::unregister` 是幂等的（本来就没了也返回 `Ok`），
/// 单看它的返回值无法区分"删掉了"与"本来就没有" —— 而调用方（安装脚本 / 排查的人）
/// 恰恰需要这个区别。多一次 `schtasks /query` 换一个不含糊的回答，值得。
fn task_command(action: &str) -> Response {
    match action {
        "status" => match crate::task::query() {
            Ok(state) => Response::Ok {
                verb: "task".to_string(),
                data: task_data("status", &state),
            },
            Err(e) => Response::err("task", ErrorCode::InternalError, e),
        },

        "register" => match crate::task::ensure() {
            Ok(ensured) => {
                let (outcome, detail) = match &ensured {
                    crate::task::Ensured::Created => ("created", String::new()),
                    crate::task::Ensured::Repaired(why) => ("repaired", why.clone()),
                    crate::task::Ensured::AlreadyCurrent => ("already-current", String::new()),
                    // 拒绝注册是**结论**而不是错误：这台 core 不在稳定位置（dev bin / dist 目录），
                    // 把系统级任务指过去只会得到一条每次必定失败的记录。如实回 "skipped" + 原因，
                    // 让调用方（安装脚本 / 排查的人）能立刻看出"为什么没注册"。
                    crate::task::Ensured::Skipped(why) => ("skipped", why.clone()),
                };
                Response::Ok {
                    verb: "task".to_string(),
                    data: serde_json::json!({
                        "task": crate::task::TASK_NAME,
                        "action": "register",
                        "outcome": outcome,
                        "detail": detail,
                        "changed": matches!(outcome, "created" | "repaired"),
                    }),
                }
            }
            Err(e) => Response::err("task", ErrorCode::InternalError, e),
        },

        "unregister" => {
            let previous = crate::task::query().ok();
            match crate::task::unregister() {
                Ok(()) => Response::Ok {
                    verb: "task".to_string(),
                    data: serde_json::json!({
                        "task": crate::task::TASK_NAME,
                        "action": "unregister",
                        // 幂等删除：`changed` 说的是"之前真的存在吗"，而不是"这次调用做了什么"
                        "previousState": previous.as_ref().map(task_state_name).unwrap_or("unknown"),
                        "changed": !matches!(previous, Some(crate::task::TaskState::Missing)),
                    }),
                },
                Err(e) => Response::err("task", ErrorCode::InternalError, e),
            }
        }

        // 未知**动作**用 `unknown-verb` 而不是 `internal-error`：这是调用方写错了字，
        // 不是 core 内部坏了 —— 混成一个码会把排查方向指错（同 `change_running` 的分工）。
        other => Response::err(
            "task",
            ErrorCode::UnknownVerb,
            format!(
                "unknown task action '{other}'; supported: status | register | unregister"
            ),
        ),
    }
}

/// 任务状态 → 结构化响应载荷（纯映射，**无 IO** —— 三个分支因此可被完整单测）。
fn task_data(action: &str, state: &crate::task::TaskState) -> serde_json::Value {
    match state {
        crate::task::TaskState::Missing => serde_json::json!({
            "task": crate::task::TASK_NAME,
            "action": action,
            "state": "missing",
            "command": "",
            "reason": "",
        }),
        crate::task::TaskState::Current => serde_json::json!({
            "task": crate::task::TASK_NAME,
            "action": action,
            "state": "current",
            "command": "",
            "reason": "",
        }),
        crate::task::TaskState::Stale { command, reason } => serde_json::json!({
            "task": crate::task::TASK_NAME,
            "action": action,
            "state": "stale",
            "command": command,
            "reason": reason,
        }),
    }
}

fn task_state_name(state: &crate::task::TaskState) -> &'static str {
    match state {
        crate::task::TaskState::Missing => "missing",
        crate::task::TaskState::Current => "current",
        crate::task::TaskState::Stale { .. } => "stale",
    }
}

fn find_component<'a>(components: &'a [Component], name: &str) -> Option<&'a Component> {
    components.iter().find(|c| c.name == name)
}

fn unknown_component(verb: &str, name: &str) -> Response {
    Response::err(
        verb,
        ErrorCode::UnknownComponent,
        format!("component '{name}' not in components.json"),
    )
}

/// `start` / `stop` 的共同实现（幂等）。
///
/// **只做委派**：真正的生命周期决策在 [`crate::supervisor`]（core 里唯一的所有者）。
/// 本函数负责把它的 [`Outcome`] 翻译成对外的结构化响应 —— 那是**协议层**的职责，
/// 不应该泄进监护器（监护器不知道 BDMC1 的存在）。
fn change_running(components: &[Component], name: &str, want_running: bool) -> Response {
    let verb = if want_running { "start" } else { "stop" };

    // 先判组件是否存在：这样 `unknown-component` 与"监护器未就绪"两类失败分得清
    //（前者是调用方写错了名字，后者是 core 内部坏了 —— 混成一个码会让排查方向完全错）。
    let Some(c) = find_component(components, name) else {
        return unknown_component(verb, name);
    };
    let Some(sup) = crate::supervisor() else {
        return Response::err(
            verb,
            ErrorCode::InternalError,
            "supervisor is not initialized; core is not the lifecycle owner",
        );
    };

    let outcome = if want_running {
        sup.start(name)
    } else {
        sup.stop(name)
    };
    outcome_to_response(verb, name, want_running, c.gate.as_deref(), outcome)
}

/// [`Outcome`] → 协议响应（纯映射，不含任何 IO —— 因此可被完整单测）。
///
/// `gate` 只用于把"被开关拒绝"这条错误说清楚（哪个键关的），不参与决策。
fn outcome_to_response(
    verb: &str,
    name: &str,
    want_running: bool,
    gate: Option<&str>,
    outcome: Outcome,
) -> Response {
    match outcome {
        Outcome::Changed => ok_changed(verb, name, want_running, true),
        // 幂等：第二次发同一条命令 = 成功且 changed=false（不是错误）
        Outcome::Unchanged => ok_changed(verb, name, want_running, false),
        Outcome::GateClosed => Response::err(
            verb,
            ErrorCode::GateClosed,
            format!(
                "component '{name}' is disabled by its switch ({}); refusing to start",
                gate.unwrap_or("")
            ),
        ),
        Outcome::Failed(e) => Response::err(verb, ErrorCode::InternalError, e),
    }
}

/// 幂等成功响应：`data.changed` 是区分"真的做了动作"与"已经是目标状态"的**唯一字段**。
fn ok_changed(verb: &str, name: &str, running: bool, changed: bool) -> Response {
    Response::Ok {
        verb: verb.to_string(),
        data: serde_json::json!({
            "component": name,
            "running": running,
            "changed": changed,
        }),
    }
}

fn get_setting(key: &str, settings: &Settings) -> Response {
    if key.is_empty() {
        return Response::err("get", ErrorCode::UnknownVerb, "get requires a key");
    }
    // 目前只支持 bool 读取（settings 里的开关都是 bool）；键不存在则 value=null（**不编造默认值**）。
    let value = settings.try_get_bool(key);
    Response::Ok {
        verb: "get".to_string(),
        data: serde_json::json!({ "key": key, "value": value }),
    }
}

/// `@ctl|set|<key> <json-literal>` —— 写一个**扁平点分键**（契约见向量的 `_ctl_set`）。
///
/// # 值为什么是 JSON 字面量
///
/// settings.json 里既有值是有类型的（`false` 是 **bool**，不是字符串）。故值按 **JSON 字面量**解析，
/// 落盘类型与既有值一致、无需任何转换；也**不做"键→类型"表** —— 那属于 UI 侧
///（托盘知道 `components.desktop` 是布尔），core 复制一份就等于长出第二份真相。
///
/// # 错误码的诚实标注
///
/// 参数畸形（缺键/缺值/值不是 JSON 字面量）一律回 `unknown-verb` —— **沿用本文件既有先例**
///（`get` 缺键、`task` 未知动作都如此）。更精确的做法是新增一个 `invalid-argument` 码，但那要同时改
/// `protocol.rs` / 向量 / `docs/architecture/error-codes.md` / C# 四处契约，**本轮不做**（记在 S5-2a 待办）。
fn set_setting(arg: &str) -> Response {
    let (key, value) = match parse_set_arg(arg) {
        Ok(kv) => kv,
        Err(msg) => return Response::err("set", ErrorCode::UnknownVerb, msg),
    };

    match crate::settings::set_flat(&key, &value) {
        Ok(()) => Response::Ok {
            verb: "set".to_string(),
            data: serde_json::json!({ "key": key, "value": value }),
        },
        Err(e) => Response::err("set", ErrorCode::InternalError, e),
    }
}

/// 解析 `set` 的参数：`<key> <json-literal>`。
///
/// **纯函数**（不碰磁盘）—— 因此单测完整覆盖它，而测试**绝不会改到真机的 settings.json**
///（本仓纪律：单测不该改真机系统状态；真实写入由真机验收覆盖）。
///
/// 切分点在**第一个空格**：键不含空格，值可以含（如 JSON 字符串 `"hello world"`）。
fn parse_set_arg(arg: &str) -> Result<(String, serde_json::Value), String> {
    let Some((key, raw_value)) = arg.split_once(' ') else {
        return Err(
            "set requires '<key> <json-literal>' (e.g. 'components.desktop false')".to_string(),
        );
    };
    let key = key.trim();
    let raw_value = raw_value.trim();
    if key.is_empty() {
        return Err("set requires a non-empty key".to_string());
    }
    if raw_value.is_empty() {
        return Err("set requires a value".to_string());
    }

    // 值必须是**合法 JSON 字面量** → 落盘类型与既有值一致（bool 就是 bool）
    let value: serde_json::Value = serde_json::from_str(raw_value).map_err(|e| {
        format!("value must be a JSON literal (false / true / 123 / \"text\"); parse failed: {e}")
    })?;
    Ok((key.to_string(), value))
}

/// `status`：desired / actual / state / health / restarts / uptime。
///
/// 五个 `desired` / `actual` / `state` / `health` / `restarts` 都是**按组件名索引的映射**
/// （形状一致，便于客户端并排渲染）。
///
/// - `health` 只有 `ok` 与 `degraded` 两态（后者即监护器熔断），保留给既有客户端；
/// - `state` 是 S5-3 新增的五态：`running` | `starting` | `stopped` | `retrying` | `degraded`。
///   它回答的是"组件现在处于什么处境"——托盘菜单据此渲染勾选与 `[!]`，
///   而**判据只有一处**（`supervisor::component_state`），避免菜单与 `status` 各说一套。
fn status_response(components: &[Component], settings: &Settings) -> Response {
    // 优先用监护器的实时视图（它就负责"谁在跑"这件事，且顺带给出 restarts / health）。
    // 监护器缺席时退化为直读进程快照：`status` 是诊断命令，**宁可少报也不要失败** ——
    // "查不出来" 远好过 "查不了"。
    let snap = crate::supervisor().map(|s| s.snapshot(settings));
    let fallback = if snap.is_none() {
        Some(crate::process::running_exe_names())
    } else {
        None
    };

    let mut desired = serde_json::Map::new();
    let mut actual = serde_json::Map::new();
    let mut health = serde_json::Map::new();
    let mut restarts = serde_json::Map::new();
    let mut state = serde_json::Map::new();

    for c in components {
        let gate_open = match c.gate.as_deref() {
            Some(key) => settings.get_bool(key, components::GATE_DEFAULT),
            None => true,
        };
        let should_run = components::auto_start(c, gate_open);
        let is_running = match &snap {
            Some(s) => s.actual.get(&c.name).copied().unwrap_or(false),
            None => fallback
                .as_ref()
                .is_some_and(|r| r.contains(&c.exe.to_ascii_lowercase())),
        };

        desired.insert(
            c.name.clone(),
            serde_json::json!(if should_run {
                "running"
            } else if gate_open {
                match c.desired {
                    Desired::Running => "running",
                    Desired::OnDemand => "on-demand",
                    Desired::Stopped => "stopped",
                }
            } else {
                "stopped(gate-closed)"
            }),
        );
        actual.insert(c.name.clone(), serde_json::json!(is_running));
        health.insert(
            c.name.clone(),
            serde_json::json!(snap
                .as_ref()
                .and_then(|s| s.health.get(&c.name))
                .cloned()
                .unwrap_or_else(|| "ok".to_string())),
        );
        restarts.insert(
            c.name.clone(),
            serde_json::json!(snap
                .as_ref()
                .and_then(|s| s.restarts.get(&c.name))
                .copied()
                .unwrap_or(0)),
        );
        // 五态状态（S5-3）：菜单与 `status` **共用 `component_state` 这一处判据**。
        // 监护器缺席时降级为"在跑 / 没在跑"—— 没有监护器就谈不上退避与熔断，
        // 而 `status` 是诊断命令，**宁可少报也不要失败**（与上面 `actual` 的降级同一条纪律）。
        state.insert(
            c.name.clone(),
            serde_json::json!(snap
                .as_ref()
                .and_then(|s| s.state.get(&c.name))
                .cloned()
                .unwrap_or_else(|| if is_running { "running" } else { "stopped" }.to_string())),
        );
    }

    let uptime = STARTED_AT
        .get()
        .map(|t| t.elapsed().as_secs())
        .unwrap_or(0);

    Response::Ok {
        verb: "status".to_string(),
        data: serde_json::json!({
            "desired": desired,
            "actual": actual,
            "state": state,
            "health": health,
            "restarts": restarts,
            "uptime": uptime,
            // 全局热键的归属（S4）：`bdctl status` 要能回答"截图键现在归谁" ——
            // 迁移期最需要确认的就是这一条（core 与壳/截图 exe 谁持着键）。
            "hotkeys": serde_json::json!({
                "capture": { "registered": crate::hotkeys::capture_registered() },
            }),
        }),
    }
}

/// 读一行（**含**行终止符），带超时与大小上限。
///
/// 刻意不用"`ReadFile` 到固定大小"：那会把一条消息截断成两条。
/// 累积式读取 + 每次落盘后查 `\n`，既不截断也不无限增长。
fn read_line_bounded(pipe: HANDLE) -> ReadOutcome {
    let deadline = Instant::now() + READ_DEADLINE;
    let mut buf: Vec<u8> = Vec::new();
    let mut chunk = vec![0u8; READ_CHUNK];

    loop {
        let mut available = 0u32;
        let peek = unsafe {
            PeekNamedPipe(pipe, None, 0, None, Some(&mut available), None)
        };
        if peek.is_err() {
            return ReadOutcome::Closed;
        }

        if available == 0 {
            if Instant::now() >= deadline {
                return ReadOutcome::Timeout;
            }
            std::thread::sleep(POLL_INTERVAL);
            continue;
        }

        let want = (available as usize).min(READ_CHUNK);
        let mut read = 0u32;
        // windows-rs 的 ReadFile 用 `&mut [u8]` 表达长度（切片长度即要读的字节数）
        let ok = unsafe {
            windows::Win32::Storage::FileSystem::ReadFile(
                pipe,
                Some(&mut chunk[..want]),
                Some(&mut read),
                None,
            )
        };
        if ok.is_err() {
            return ReadOutcome::Io("ReadFile failed".to_string());
        }
        if read == 0 {
            return ReadOutcome::Closed;
        }

        buf.extend_from_slice(&chunk[..read as usize]);

        if buf.contains(&b'\n') {
            return ReadOutcome::Line(buf);
        }
        // 上限判定：允许恰好 MAX（含换行），再多一个字节即超限
        if buf.len() > protocol::MAX_MESSAGE_BYTES {
            return ReadOutcome::TooLarge;
        }
        if Instant::now() >= deadline {
            return ReadOutcome::Timeout;
        }
    }
}

/// 写一行（自动补 `\n`）。返回 `false` = 写失败，调用方应结束本次连接。
fn write_line(pipe: HANDLE, text: &str) -> bool {
    let mut bytes = text.as_bytes().to_vec();
    bytes.push(b'\n');
    let mut written = 0u32;
    let ok = unsafe {
        windows::Win32::Storage::FileSystem::WriteFile(
            pipe,
            Some(&bytes),
            Some(&mut written),
            None,
        )
    };
    if ok.is_err() {
        crate::log::warn("WriteFile to pipe failed");
        return false;
    }
    written as usize == bytes.len()
}

/// 仅当请求"看起来是控制形态"时才回错误消息。
///
/// 依据：畸形输入里只有能识别出 `BDMC1|@ctl|` 前缀的那些才是**发给我们控制面**的，
/// 其余（连魔数都不对）不欠回复 —— 给它们回 JSON 只是浪费，还会污染日志。
fn reply_if_control(pipe: HANDLE, raw: &[u8], code: ErrorCode, message: String) {
    if !looks_like_control(raw) {
        return;
    }
    let _ = write_line(pipe, &Response::err("", code, message).encode());
}

/// 前缀判别（只看开头，不求完整解析）。
fn looks_like_control(raw: &[u8]) -> bool {
    raw.starts_with(format!("{}@ctl|", protocol::MAGIC).as_bytes())
}

/// `&str` → NUL 结尾的 UTF-16（本地工具，避免跨模块依赖 tray 的实现）。
fn pcwstr_from_str(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::components::{ComponentType, PowerPolicy, Tier};

    fn sample_components() -> Vec<Component> {
        vec![
            Component {
                name: "shell".into(),
                label: "shell".into(),
                exe: "definitely-not-running-shell.exe".into(),
                desired: Desired::OnDemand,
                tier: Tier::Surface,
                component_type: ComponentType::Process,
                power: PowerPolicy::default(),
                gate: None,
                args: None,
                stop_flag: None,
                liveness: crate::components::Liveness::Process,
                liveness_pipe: None,
                remove_at: None,
            },
            Component {
                name: "gated".into(),
                label: "gated".into(),
                exe: "definitely-not-running-gated.exe".into(),
                desired: Desired::Running,
                tier: Tier::Surface,
                component_type: ComponentType::Process,
                power: PowerPolicy::default(),
                gate: Some("definitely.missing.key".into()),
                args: None,
                stop_flag: None,
                liveness: crate::components::Liveness::Process,
                liveness_pipe: None,
                remove_at: None,
            },
        ]
    }

    #[test]
    fn status_is_always_ok_and_has_all_components() {
        let resp = dispatch_control("status", "", &sample_components(), &Settings::empty());
        let Response::Ok { data, .. } = resp else {
            panic!("status must always succeed");
        };
        for c in sample_components() {
            assert!(data["desired"].get(&c.name).is_some(), "{}", c.name);
            assert!(data["actual"].get(&c.name).is_some(), "{}", c.name);
            assert!(data["health"].get(&c.name).is_some(), "{}", c.name);
            // restarts 与其余三项同形（按组件名索引），客户端才能并排渲染
            assert!(data["restarts"].get(&c.name).is_some(), "{}", c.name);
        }
        assert!(data.get("uptime").is_some());
    }

    #[test]
    fn unknown_verb_is_structured_not_free_text() {
        let resp = dispatch_control("frobnicate", "", &sample_components(), &Settings::empty());
        let Response::Err { error, .. } = resp else {
            panic!("unknown verb must fail");
        };
        assert_eq!(error, ErrorCode::UnknownVerb.as_str());
    }

    /// `set` 自 S5-2a 起**是受支持的 verb**（core 成为唯一写者，ADR §13.19）。
    ///
    /// **本用例刻意用畸形参数**：`dispatch_control("set", <合法参数>)` 会**写真机的 settings.json**，
    /// 而单测绝不该改真机系统状态（本仓纪律）—— 真实写入由真机验收覆盖。
    /// 用畸形参数同样能证明"分派确实走到了 `set` 自己的参数解析"，而不是回一句"不认识的 verb"。
    #[test]
    fn set_is_supported_and_malformed_args_are_rejected() {
        let resp = dispatch_control(
            "set",
            "components.dock",
            &sample_components(),
            &Settings::empty(),
        );
        let Response::Err { error, message, .. } = resp else {
            panic!("set without a value must be refused");
        };
        assert_eq!(error, ErrorCode::UnknownVerb.as_str());
        assert!(
            message.contains("json-literal"),
            "错误消息必须说清期望形式（JSON 字面量）: {message}"
        );
        assert!(
            !message.contains("is not supported"),
            "set 现在**是**受支持的 verb，不得再回 'not supported': {message}"
        );
    }

    /// `task` 的未知**动作**必须是结构化的 `unknown-verb`。
    ///
    /// 这条**不触 IO**：动作不认识时在进 `schtasks` 之前就返回了 —— 所以单测里跑它是安全的
    /// （单测绝不该改真机系统状态：注册/删除由真机验收覆盖）。
    #[test]
    fn task_unknown_action_is_structured_and_touches_no_io() {
        let resp = dispatch_control("task", "frobnicate", &sample_components(), &Settings::empty());
        let Response::Err { error, message, .. } = resp else {
            panic!("an unknown task action must fail");
        };
        assert_eq!(error, ErrorCode::UnknownVerb.as_str());
        // 消息必须列出可用动作 —— 否则调用方只能去读源码
        for action in ["status", "register", "unregister"] {
            assert!(message.contains(action), "missing '{action}' in: {message}");
        }
    }

    /// `bdctl --core task`（不带动作）也必须被拒绝，**不得**落到某个默认动作上。
    /// 默认动作在这里格外危险：猜成 `register` 会悄悄注册一个系统级任务，猜成 `unregister` 会删掉兜底。
    #[test]
    fn task_without_an_action_is_rejected_rather_than_guessed() {
        let resp = dispatch_control("task", "", &sample_components(), &Settings::empty());
        let Response::Err { error, .. } = resp else {
            panic!("a task request without an action must fail");
        };
        assert_eq!(error, ErrorCode::UnknownVerb.as_str());
    }

    /// 三种任务状态 → 载荷形状（**纯映射**，无 IO）。
    #[test]
    fn task_state_maps_to_structured_payload() {
        let missing = task_data("status", &crate::task::TaskState::Missing);
        assert_eq!(missing["state"], serde_json::json!("missing"));
        assert_eq!(missing["task"], serde_json::json!(crate::task::TASK_NAME));
        assert_eq!(missing["action"], serde_json::json!("status"));

        assert_eq!(
            task_data("status", &crate::task::TaskState::Current)["state"],
            serde_json::json!("current")
        );

        let stale = task_data(
            "status",
            &crate::task::TaskState::Stale {
                command: r"C:\x\notepad.exe".to_string(),
                reason: "points at a different executable".to_string(),
            },
        );
        assert_eq!(stale["state"], serde_json::json!("stale"));
        // 漂移的**证据**必须带出来：只说一句 "stale" 没法排查是哪一种漂移
        assert_eq!(stale["command"], serde_json::json!(r"C:\x\notepad.exe"));
        assert!(
            stale["reason"].as_str().unwrap().contains("different executable"),
            "{stale}"
        );
    }

    // ───────────── 管道确权失败：有界退避 + 能回答"谁/为什么/怎么办"的诊断 ─────────────

    /// 退避必须**逐档变大且有界**：`1 → 2 → 4 → 8 → 16 → 30`（触顶后恒为 30）。
    ///
    /// 这条锁住的是"不再每秒重试"：原实现是固定 `sleep(1s)`，日志每秒一行、永不收敛，
    /// 而且把"这个问题持续了多久、试到第几档"这条最有用的信息淹掉了。
    #[test]
    fn backoff_grows_and_is_bounded() {
        let secs: Vec<u64> = (0..9).map(|a| backoff_delay(a).as_secs()).collect();
        assert_eq!(secs, vec![1, 2, 4, 8, 16, 30, 30, 30, 30]);
    }

    /// 记录策略：首次必记、**每档变化**必记、触顶后每 [`RETRY_REMIND_EVERY`] 次提醒一次。
    ///
    /// 关键的是**不能静默**：管道名一直被占是最该被人看见、最需要人来处理的状态 ——
    /// 触顶后彻底闭嘴会让它沉底，那比刷屏更糟。
    #[test]
    fn retry_logging_neither_spams_nor_goes_silent() {
        assert!(should_log_retry(0), "第一次失败必须记");
        for attempt in [1, 2, 3, 4, 5] {
            assert!(should_log_retry(attempt), "档位变化必须记: attempt={attempt}");
        }
        assert!(!should_log_retry(6), "触顶后同一档位不应每次都记（否则就是刷屏）");

        let at_cap: Vec<u32> = (5..60).filter(|a| should_log_retry(*a)).collect();
        assert!(at_cap.len() >= 5, "触顶后仍须周期性提醒: {at_cap:?}");
        assert!(
            at_cap.windows(2).all(|w| w[1] - w[0] <= RETRY_REMIND_EVERY),
            "提醒间隔不得超过 {RETRY_REMIND_EVERY} 次退避: {at_cap:?}"
        );
    }

    /// 诊断必须回答**谁占的 / 为什么 / 怎么办**，而不是只丢一个错误码。
    #[test]
    fn name_taken_diagnostic_names_the_cause_and_the_remedy() {
        let e = CreateError {
            code: ERROR_ACCESS_DENIED.0,
            text: format!("CreateNamedPipeW({PIPE_NAME}) failed (GetLastError=5)"),
        };
        let msg = describe_create_failure(0, &e, 0, backoff_delay(0));

        // 「为什么」：必须点明这个错误码**在这个标志下**的确切含义，
        // 否则排查方向会跑偏到"权限不足"上去（白费时间）。
        assert!(msg.contains("与权限无关"), "{msg}");
        assert!(msg.contains("不要去查 ACL"), "{msg}");
        assert!(msg.contains("FILE_FLAG_FIRST_PIPE_INSTANCE 方式建的"), "{msg}");
        // 「后果」：说清是核心职责失效，不是日志噪声。
        assert!(msg.contains("无法服务控制管道"), "{msg}");
        // 「怎么办」：给方向，并说明它会自愈。
        assert!(msg.contains("候选"), "{msg}");
        assert!(msg.contains("无需重启 core"), "{msg}");
        // 「试到第几档」：现场最需要的信息之一。
        assert!(msg.contains("退避重试第 1 次"), "{msg}");
    }

    /// 占用者**不以独占方式建**管道时，我们拿到的是 `ERROR_PIPE_BUSY`（真机实测 231），
    /// 而不是 5。两者是**同一个结论**（名字不归我们）但成因不同，必须都给出诊断 ——
    /// 只认 5 的话，这类场景里的人会以为"没有这条诊断 ⇒ 不是占用问题"，然后去查别处。
    #[test]
    fn pipe_busy_is_also_diagnosed_as_ownership_loss() {
        let e = CreateError {
            code: ERROR_PIPE_BUSY.0,
            text: format!("CreateNamedPipeW({PIPE_NAME}) failed (GetLastError=231)"),
        };
        let msg = describe_create_failure(0, &e, 0, backoff_delay(0));

        assert!(msg.contains("与权限无关"), "{msg}");
        assert!(msg.contains("无法服务控制管道"), "{msg}");
        assert!(msg.contains("不是独占方式建的"), "231 的成因与 5 不同，必须分别说明: {msg}");
        assert!(
            !msg.contains("FILE_FLAG_FIRST_PIPE_INSTANCE 方式建的"),
            "231 不应套用 5 的成因描述: {msg}"
        );
    }

    /// 非"名字被占"的失败**不得**套用那段诊断 —— 否则会把权限/资源类问题指向错误的结论。
    #[test]
    fn unrelated_failures_do_not_claim_the_name_is_taken() {
        let e = CreateError {
            code: 87,
            text: format!("CreateNamedPipeW({PIPE_NAME}) failed (GetLastError=87)"),
        };
        let msg = describe_create_failure(2, &e, 3, backoff_delay(3));
        assert!(msg.contains("GetLastError=87"), "原始错误码必须保留: {msg}");
        assert!(!msg.contains("已被"), "不得误报名字被占: {msg}");
    }

    #[test]
    fn unknown_component_is_structured() {
        let resp = dispatch_control("start", "nope", &sample_components(), &Settings::empty());
        let Response::Err { error, .. } = resp else {
            panic!("unknown component must fail");
        };
        assert_eq!(error, ErrorCode::UnknownComponent.as_str());
    }

    /// `start` / `stop` 的**真正行为**（拉起、击杀、退避、gate）在 `supervisor` 里测试 ——
    /// 那里能用注入的配置驱动全部分支，不必依赖真机设置文件。
    ///
    /// 这里测的是**协议层职责**：`Outcome` → 结构化响应 的映射。
    /// 每个分支都必须落到不同的错误码上，否则调用方无法区分"该重试"与"别重试"。
    #[test]
    fn outcome_maps_to_distinct_structured_responses() {
        // 已改变 → Ok + changed=true
        let resp = outcome_to_response("start", "shell", true, None, Outcome::Changed);
        let Response::Ok { data, .. } = resp else {
            panic!("changed must be Ok")
        };
        assert_eq!(data["changed"], serde_json::json!(true));
        assert_eq!(data["running"], serde_json::json!(true));

        // 幂等（第二次发同一条命令）→ Ok + changed=false，**不是错误**
        let resp = outcome_to_response("stop", "shell", false, None, Outcome::Unchanged);
        let Response::Ok { data, .. } = resp else {
            panic!("unchanged must still be Ok — idempotency is success, not failure")
        };
        assert_eq!(data["changed"], serde_json::json!(false));
        assert_eq!(data["running"], serde_json::json!(false));

        // 被开关拒绝 → 专用错误码（不是笼统的 internal-error）
        let resp = outcome_to_response(
            "start",
            "shell",
            true,
            Some("components.shell"),
            Outcome::GateClosed,
        );
        let Response::Err { error, message, .. } = resp else {
            panic!("gate-closed must be refused")
        };
        assert_eq!(error, ErrorCode::GateClosed.as_str());
        assert!(
            message.contains("components.shell"),
            "错误消息必须点出是哪个开关关的：{message}"
        );

        // 拉起失败 → internal-error，且**原因原文透传**（不能吞成一句"失败"）
        let resp = outcome_to_response(
            "start",
            "shell",
            true,
            None,
            Outcome::Failed("not deployed (shell.exe)".into()),
        );
        let Response::Err { error, message, .. } = resp else {
            panic!("failed must be an error")
        };
        assert_eq!(error, ErrorCode::InternalError.as_str());
        assert!(message.contains("not deployed"), "{message}");
    }

    /// 监护器未初始化时必须**显式报错**，而不是假装"启动成功"或"组件不存在"。
    ///
    /// 这条守卫锁住的是"core 必须是生命周期所有者"：没有监护器就没有合法的拉起路径，
    /// 此时静默成功会让调用方以为事情办了。
    #[test]
    fn start_without_supervisor_is_explicit_internal_error() {
        // 测试进程从不初始化 SUPERVISOR 静态 → 走的正是这条防御分支
        assert!(crate::supervisor().is_none(), "tests must not initialize a supervisor");
        let resp = dispatch_control("start", "shell", &sample_components(), &Settings::empty());
        let Response::Err { error, message, .. } = resp else {
            panic!("start without a supervisor must fail loudly")
        };
        assert_eq!(error, ErrorCode::InternalError.as_str());
        assert!(message.contains("supervisor"), "{message}");
    }

    #[test]
    fn get_requires_a_key() {
        let resp = dispatch_control("get", "  ", &sample_components(), &Settings::empty());
        let Response::Err { error, .. } = resp else {
            panic!("get without key must fail");
        };
        assert_eq!(error, ErrorCode::UnknownVerb.as_str());
    }

    #[test]
    fn get_reports_real_value_and_null_for_missing() {
        let settings = Settings::from_str(r#"{"components":{"dock":false}}"#);

        let resp = dispatch_control("get", "components.dock", &sample_components(), &settings);
        let Response::Ok { data, .. } = resp else {
            panic!("get must succeed")
        };
        assert_eq!(data["value"], serde_json::json!(false));

        let resp = dispatch_control("get", "components.nope", &sample_components(), &settings);
        let Response::Ok { data, .. } = resp else {
            panic!("get must succeed even for a missing key")
        };
        assert_eq!(
            data["value"],
            serde_json::json!(null),
            "missing key must be null, not a fabricated default"
        );
    }

    /// `set` 的值是 **JSON 字面量**（契约 `_ctl_set`）—— 与 settings.json 既有类型一致。
    ///
    /// 只测**纯解析**：`set_setting` 会真写 settings.json，**单测绝不能碰真机设置**（本仓纪律）。
    #[test]
    fn set_parses_json_literals_and_rejects_garbage() {
        let (k, v) = parse_set_arg("components.desktop false").unwrap();
        assert_eq!(k, "components.desktop");
        assert_eq!(v, serde_json::json!(false), "false 必须是 bool，不是字符串");

        let (_, v) = parse_set_arg("some.key \"hello world\"").unwrap();
        assert_eq!(v, serde_json::json!("hello world"), "值可含空格（切分只在第一个空格）");

        let (_, v) = parse_set_arg("n 123").unwrap();
        assert_eq!(v, serde_json::json!(123));

        assert!(
            parse_set_arg("components.desktop notjson").is_err(),
            "非 JSON 字面量必须被拒（否则会落盘成字符串，与既有类型不符）"
        );
        assert!(parse_set_arg("no-value").is_err(), "缺值必须被拒");
        assert!(parse_set_arg(" false").is_err(), "缺键必须被拒");
    }

    #[test]
    fn control_prefix_detection() {
        assert!(looks_like_control(b"BDMC1|@ctl|status|"));
        assert!(!looks_like_control(b"BDMC1|convert|x"));
        assert!(!looks_like_control(b"NOPE"));
        assert!(!looks_like_control(b""));
    }
}
