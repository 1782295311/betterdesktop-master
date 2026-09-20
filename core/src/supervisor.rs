//! 监护器 —— core 里**唯一**的生命周期所有者（计划 §6.1-C）。
//!
//! # 为什么必须只有一个所有者
//!
//! 旧世界（Host ensure Agent、Watchdog 守护 Host 与 Agent、入口各自 ensure）之所以成环，
//! 根因就是"谁都可以拉起谁"。本模块把这件事收成单点：**只有这里**调用 `process::spawn_detached`
//! 与 `process::stop_by_exe_name`；控制管道、托盘菜单都只是把请求委派进来。
//!
//! # 期望态 vs 实际态
//!
//! - **期望态**：由 `components.json`（`desired` / `tier` / `type`）+ `settings.json`（`gate`）算出，
//!   纯函数 [`components::auto_start`] / [`components::must_stop`]。
//! - **实际态**：每次 reconcile **重新枚举**进程快照（`process::running_exe_names`），
//!   **不缓存**。缓存会引入"status 说在跑、实际已死"这类最难查的问题。
//!
//! reconcile 只对**差集**动手 —— 这正是"core 崩溃重启后不得重复拉起已在跑的组件"的落实方式
//! （见 [`Supervisor::reconcile`] 的 `is_running` 判断与 `tests::reconcile_does_not_relaunch_running_component`）。
//!
//! # 自愈的边界（只护常驻，不护按需）
//!
//! 只有 `auto_start()` 为真的组件（foundation，或 `desired=running` 且 gate 开）才会被**主动**拉起。
//! `on-demand` 组件从不被主动拉起 —— 它只在收到显式 `start` 时才起。所以"kill 掉一个按需进程"
//! 不会触发任何守护动作（计划 D7）。
//!
//! # 退避与熔断
//!
//! - 退避：[`BACKOFF_MS`] 1s → 2s → 4s → 8s → 16s → 30s（上限）。
//! - 熔断：[`FAILURE_WINDOW`]（5 分钟）内累计 [`FAILURES_TO_DEGRADE`]（3）次失败 → `degraded`，不再拉起。
//! - 恢复：熔断**不是永久的** —— 失败记录滚出时间窗后自动解除（[`refresh`]），那样 crash-loop
//!   过一段时间会再试一次，而不是把组件永久判死。
//!
//! # 什么算一次"失败"（关键定义）
//!
//! 两类，缺一不可：
//!   1. **拉不起来**：`CreateProcessW` 失败，或 exe 未部署；
//!   2. **拉起后没活过一轮**：`pending_spawn` 标记 + 下一轮快照验证。
//!
//! 第 2 类才是 crash-loop 的主要形态（进程能起来、立刻又死）。只看第 1 类会让
//! "启动即崩"的组件以 3 秒一次的频率无限重启 —— 那正是熔断要防的事。
//!
//! # 不做的事
//!
//! 不判断组件"是否可用的"（DWM 重建 / D3D 设备丢失 / 钩子失效这类健康问题）——
//! 那是 `power.onResume = probe-and-reinit` 的职责，需要组件**自报**健康（S3.5）。

use std::collections::{HashMap, VecDeque};
use std::sync::Mutex;
use std::time::{Duration, Instant};

use crate::components::{self, Component, ComponentType, Liveness};
use crate::log;
use crate::process;
use crate::settings::Settings;

/// 监护轮询间隔。
///
/// 3 秒是"用户感知不到延迟"与"开销可忽略"的折中：一次轮询 = 一次 Toolhelp32 快照 + 一次
/// settings.json 读盘，两者都在毫秒级。**只在有动作时记日志**，空闲时完全静默。
pub const POLL_INTERVAL: Duration = Duration::from_secs(3);

/// 失败统计的时间窗。
const FAILURE_WINDOW: Duration = Duration::from_secs(300);

/// 时间窗内累计多少次失败 → 熔断。
const FAILURES_TO_DEGRADE: usize = 3;

/// 退避阶梯（毫秒），最后一次为上限。
const BACKOFF_MS: [u64; 6] = [1_000, 2_000, 4_000, 8_000, 16_000, 30_000];

/// 「上一次记过的暂停来源」——暂停期间每轮都记一行只会淹掉真正有用的信息。
///
/// 用 `AtomicU8` 而不是 `AtomicBool`：**来源变了也要再记一行** ——
/// 用户以为已恢复、其实只是从"更新器暂停"变成"用户暂停"，这是不同的事（0 = 未暂停）。
static PAUSE_LOGGED: std::sync::atomic::AtomicU8 = std::sync::atomic::AtomicU8::new(0);

/// 更新器的暂停标记（`watchdog-pause.flag`）。
///
/// 名字**不是** core 起的、也不该因"名字更准"而改：它由**更新器**在接管替换文件期间写入
/// （`updater/ResidentGate.cs`），已经在部署里；改名要联动更新器，而收益只是一个更准的名字。
pub const UPDATER_PAUSE_FLAG: &str = "watchdog-pause.flag";

/// 用户菜单的暂停标记（`user-pause.flag`）。
///
/// # 为什么必须是**第二个文件**，而不是与更新器共用
///
/// 两者"暂时别拉回组件"的意图相同，但**生命周期不同**：
///   · 更新器：替换完文件**自己清**（一次更新一个来回）；
///   · 用户：**显式恢复**才清（可能跨重启）。
///
/// 共用会有一个真实的 race：用户暂停 → 更新器接管文件、看到标记已存在于是"这不是我写的、我不该清"
/// （或反过来），于是先结束的那一方把另一方**仍在生效**的暂停清掉 —— 用户以为还暂停着，
/// 组件已经被拉起。两个独立标记 + "任一存在即暂停"，把这个 race 变成不可能。
pub const USER_PAUSE_FLAG: &str = "user-pause.flag";

/// 暂停的来源（**只用于日志** —— 判据永远是"标记是否存在"）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PauseSource {
    /// 更新器在替换文件期间写的。
    Updater,
    /// 用户在托盘菜单里显式暂停的。
    User,
}

impl PauseSource {
    /// 跨进程/日志字面量。
    pub fn as_str(self) -> &'static str {
        match self {
            PauseSource::Updater => "updater",
            PauseSource::User => "user",
        }
    }

    /// 上一次已记过的来源编码（0 表示未暂停）。
    fn code(self) -> u8 {
        match self {
            PauseSource::Updater => 1,
            PauseSource::User => 2,
        }
    }
}

/// 暂停判定（**纯函数**，故可被单测穷举）：任一标记存在即暂停。
///
/// 更新器优先报到：进程替换是更"硬"、更短命的原因，日志里先看到它更有助于定位。
fn pause_source_from(updater: bool, user: bool) -> Option<PauseSource> {
    if updater {
        Some(PauseSource::Updater)
    } else if user {
        Some(PauseSource::User)
    } else {
        None
    }
}

/// 当前暂停来源（读留痕目录里的两个标记）。
fn pause_source() -> Option<PauseSource> {
    pause_source_from(
        components::flag_exists(UPDATER_PAUSE_FLAG),
        components::flag_exists(USER_PAUSE_FLAG),
    )
}

/// 组件是否存活 —— 判据由表项声明（见 [`components::Liveness`]）。
///
/// 两种判据并存不是设计洁癖：`desktop` 用管道判活是因为它的 exe 名被一个**短命**的
/// "桌面控制菜单"进程共用，按进程名判活会把"正在弹菜单"读成"服务在运行"（审计 #9 的真机事故）。
fn is_alive(c: &Component, running: &std::collections::HashSet<String>) -> bool {
    match c.liveness {
        Liveness::Process => running.contains(&c.exe.to_ascii_lowercase()),
        Liveness::Pipe => c
            .liveness_pipe
            .as_deref()
            .is_some_and(process::is_pipe_up),
    }
}

/// 「刚由 core 拉起」的这一轮内，额外接受**进程名存在**作为存活证据。
///
/// 只对**管道判活**的组件生效，且只在 `pending_spawn` 那一轮问。理由见 [`Supervisor::reconcile`]
/// 里的真机说明：管道是"它能不能干活"的**健康**信号，不是"它启动完了"的信号 ——
/// 冷启动一个 WPF 服务实测要数秒，而监护轮询是 3 秒，只认管道必然把"正在启动"读成"启动即崩"，
/// 后果是退避后再拉一个（两个同名服务并存）。
///
/// **不是给所有组件开后门**：进程判活的组件本来就不需要它（永远不会走到这里），
/// 而"服务死了很久但有个同名短命进程在跑"那种情形也不会走到这里 —— 只有紧跟一次 spawn 的那一轮才算。
fn spawn_is_alive(c: &Component, running: &std::collections::HashSet<String>) -> bool {
    c.liveness == Liveness::Pipe && running.contains(&c.exe.to_ascii_lowercase())
}

/// 显式 `start` 是否应当**什么都不做**（`Outcome::Unchanged`）。
///
/// # 为什么两个极端都不对
///
/// - **只用 `is_alive`（按声明判活）**：管道判活的组件在"刚由 core 拉起、管道尚未就绪"的窗口里
///   判为**不在** ⇒ 一次显式 start 会在服务正启动时**再拉起第二个实例**（多主拉起，S4-3 真机事故）。
/// - **只用 `process::is_running`（进程名，旧实现）**：那是**全程宽松** —— 同名旁观者
///   （短命的「桌面控制菜单」进程，审计 #9）在跑时，用户点"启动"会被判成"已在跑" ⇒
///   `Unchanged` ⇒ **点了没反应**。把无关进程当成组件本身，是另一个方向的错。
///
/// # 所以边界是这一条
///
/// **按声明判活** ∪ **"刚由我们拉起、待验证"的那一轮内额外接受进程名**（[`spawn_is_alive`] 的语义）。
/// 第二条**只覆盖 core 自己拉起的那一轮**（`pending_spawn` 由 `try_spawn` 之后置位、每轮验证后清零），
/// **不是全局宽松** —— 这正是它和旧实现的区别，也是本函数存在的理由。
fn explicit_start_is_noop(
    c: &Component,
    running: &std::collections::HashSet<String>,
    st: Option<&CompState>,
) -> bool {
    is_alive(c, running)
        || (st.map(|s| s.pending_spawn).unwrap_or(false) && spawn_is_alive(c, running))
}

/// 该组件的「启动」是**唤起**（panel）还是**拉起**（其余）？
///
/// # 为什么必须有这个例外（S5-3 真机缺口）
///
/// 面板是**单实例**，其 `--open` 的语义是"显示 / 切换"（命中已运行实例时走命名事件）。
/// 而 [`explicit_start_is_noop`] 会在"面板在跑但已被收起"时判 no-op ⇒ **用户点「打开」永远唤不出来**
/// （真机日志复现：只留下 `component 'clipboard-panel' is already running`）。
/// C# 托盘一直是对的：它**总是** spawn `--open`。
///
/// # 为什么不能照搬到 process / tool
///
/// 它们的"启动"在跑时必须保持**幂等空操作** —— 照搬会重新引入多主拉起（S4-3 真机事故）。
/// 面板之所以安全，是因为**面板自己**保证单实例：多出来的那个进程只发一次命名事件就退出。
/// ⇒ 这条例外的成立前提是"该 exe 自持单实例"，**不是"UI 类组件都可以"**。
fn start_is_wakeup(c: &Component) -> bool {
    matches!(c.component_type, crate::components::ComponentType::Panel)
}

/// 某个组件的监护状态。
#[derive(Debug, Default)]
struct CompState {
    /// 累计成功拉起次数（`status` 的 `restarts`）。
    restarts: u32,
    /// 时间窗内的失败时刻。
    failures: VecDeque<Instant>,
    /// 连续失败次数（决定退避步长）。
    consecutive_failures: usize,
    /// 在此时刻之前不做新的拉起尝试。
    next_attempt_at: Option<Instant>,
    /// 上一轮刚拉起、本轮需要验证"是否活过一轮"。
    pending_spawn: bool,
    /// 已熔断。
    degraded: bool,
}

/// 对单个组件该做什么（纯决策，无副作用 —— 因此可被完整单测覆盖）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Decision {
    /// 期望在跑、实际没跑，且允许现在拉起。
    Spawn,
    /// 期望在跑、实际没跑，但还在退避窗口内。
    Wait,
    /// 期望在跑、实际没跑，但已熔断。
    Degraded,
    /// 已经在跑。
    Alive,
    /// gate 关闭（或 desired=stopped）但它还在跑 → 停掉。
    Stop,
    /// 无需动作。
    Idle,
}

/// 纯决策函数：给定期望态、实际态、状态机，得出本轮动作。
///
/// `now` 显式传入（不内部取时钟），这样退避/熔断的时序可以被单测精确驱动，
/// 不需要靠 `sleep` 去"等出来"。这正是把时序逻辑做成纯函数的意义。
fn decide(
    c: &Component,
    gate_open: bool,
    stop_flag: bool,
    alive: bool,
    st: &CompState,
    now: Instant,
) -> Decision {
    // 优先级 0：**用户显式停止**（托盘写的 `*-stopped.flag`）。
    //
    // 这是从 Watchdog 迁入的语义，**必须迁**：标记由托盘写（"退出主程序" / "停止桌面服务"），
    // 缺了它 core 会把用户刚停掉的组件**立刻复活**，托盘那句"已停止"被静默推翻。
    // 本仓库已踩过同类坑（托盘写 `desktop-service-stopped.flag`、看门狗只认 `desktop-stopped.flag`
    // → 停了就被拉回）。
    //
    // 只"不守护、不拉起"，**不主动去杀**：写标记的一方自己会停进程（托盘先 StopProcess 再写标记），
    // 若它还在跑说明那次停止还没做完 —— core 插手只会变成两个人在抢同一个进程。
    // 与 Watchdog 的行为逐字一致。
    if stop_flag {
        return Decision::Idle;
    }

    // 优先级 1：开关关掉的东西**必须停**，且不得被守护复活（计划 §6.5 / D8）。
    if components::must_stop(c, gate_open) {
        return if alive { Decision::Stop } else { Decision::Idle };
    }

    // 优先级 2：不主动拉起的（on-demand / stopped / tool）→ core 什么都不做。
    if !components::auto_start(c, gate_open) {
        return Decision::Idle;
    }

    if alive {
        return Decision::Alive;
    }
    if st.degraded {
        return Decision::Degraded;
    }
    match st.next_attempt_at {
        Some(t) if now < t => Decision::Wait,
        _ => Decision::Spawn,
    }
}

/// 退避步长（失败次数 → 等待时长）。
fn backoff(consecutive_failures: usize) -> Duration {
    let idx = consecutive_failures
        .saturating_sub(1)
        .min(BACKOFF_MS.len() - 1);
    Duration::from_millis(BACKOFF_MS[idx])
}

/// 丢弃滚出时间窗的失败记录，并按剩余记录重算熔断。
///
/// 熔断"过期解除"在这里发生：不是永久判死，而是"这段时间内别试了"。
fn refresh(st: &mut CompState, now: Instant) {
    while let Some(front) = st.failures.front() {
        if now.duration_since(*front) > FAILURE_WINDOW {
            st.failures.pop_front();
        } else {
            break;
        }
    }
    if st.failures.len() < FAILURES_TO_DEGRADE {
        st.degraded = false;
    }
}

/// 记录一次失败：进窗、算退避、必要时熔断。
fn record_failure(st: &mut CompState, now: Instant) {
    st.consecutive_failures += 1;
    st.failures.push_back(now);
    refresh(st, now);
    st.next_attempt_at = Some(now + backoff(st.consecutive_failures));
    if st.failures.len() >= FAILURES_TO_DEGRADE {
        st.degraded = true;
    }
}

/// 一次 reconcile 的产出（**仅供日志**，不构成对外契约）。
#[derive(Debug, Default, PartialEq, Eq)]
pub struct Report {
    /// 本轮成功拉起的组件。
    pub spawned: Vec<String>,
    /// 本轮停掉的组件。
    pub stopped: Vec<String>,
    /// 本轮判定为"拉起失败 / 没活过一轮"的组件。
    pub failed: Vec<String>,
    /// 本轮因退避而等待的组件。
    pub waiting: Vec<String>,
    /// 本轮因熔断而放弃的组件。
    pub degraded: Vec<String>,
}

impl Report {
    /// 是否什么都没发生（用于"空闲时完全静默"）。
    pub fn is_quiet(&self) -> bool {
        self.spawned.is_empty()
            && self.stopped.is_empty()
            && self.failed.is_empty()
            && self.degraded.is_empty()
    }

    /// 有值得记的事才记 —— 每 3 秒刷一行"一切正常"会把日志变成噪声，反而掩盖真问题。
    fn log(&self, reason: &str) {
        if !self.failed.is_empty() {
            crate::log::error(format!(
                "supervisor({reason}): FAILED {}",
                self.failed.join(", ")
            ));
        }
        if !self.degraded.is_empty() {
            crate::log::warn(format!(
                "supervisor({reason}): degraded (circuit open) {} — will not be relaunched until the failure window expires",
                self.degraded.join(", ")
            ));
        }
        if !self.stopped.is_empty() {
            crate::log::info(format!(
                "supervisor({reason}): stopped {}",
                self.stopped.join(", ")
            ));
        }
        if !self.spawned.is_empty() {
            crate::log::info(format!(
                "supervisor({reason}): started {}",
                self.spawned.join(", ")
            ));
        }
        // waiting 刻意不落盘（每轮都会出现，属预期行为）
    }
}

/// 显式请求的结局（管道分派据此产生响应）。
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Outcome {
    /// 状态已改变。
    Changed,
    /// 已是目标状态（幂等第二次请求）。
    Unchanged,
    /// 被开关拒绝。
    GateClosed,
    /// 未部署或拉起失败；原因已记日志。
    ///
    /// 注意这里**没有** `Degraded`：熔断只约束**自动**监护，显式请求会重置刹车
    /// （见 [`Supervisor::start`]）。加一个永远返回不到的变体只会让调用方写死分支。
    Failed(String),
}

/// 启动监护循环线程（非阻塞：起线程后立即返回）。
///
/// 调用方应**先**同步跑一次 [`Supervisor::reconcile`]（`reason="startup"`）再调本函数：
/// 那样"启动时的对账"与"运行期的对账"走的是同一个函数，不存在第二条拉起路径。
pub fn start_loop(sup: &'static Supervisor) -> Result<(), String> {
    std::thread::Builder::new()
        .name("bd-core-supervisor".to_string())
        .spawn(move || {
            loop {
                std::thread::sleep(POLL_INTERVAL);
                sup.reconcile("tick");
            }
        })
        .map(|_| ())
        .map_err(|e| format!("failed to spawn supervisor thread: {e}"))
}

/// 组件对外的状态 —— **托盘菜单与 `status` 的同一份判据**（S5-3）。
///
/// # 为什么需要它
///
/// 菜单要区分"没在跑"的三种含义：用户没要求（`stopped`）、**正在退避重试**（`retrying`）、
/// **已熔断**（`degraded`）。前两者会自愈、第三者不会 —— 对用户是不同的事，
/// 而 `actual: bool` 表达不了这个差别（菜单 `[!]` 标记的全部依据）。
///
/// # 一条纪律
///
/// **判据只有这一处。** 菜单若自己算一套（比如"没在跑就是 stopped"），就会出现
/// "菜单说已熔断、status 说 ok"这类最难查的不一致 —— 本仓已有同类教训（判活两套）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum State {
    /// 在跑。
    Running,
    /// **刚由 core 拉起、还没确认活过一轮**（`pending_spawn` 那一轮，≤3 秒）。
    ///
    /// 与 `Running` 分开表达，是为了**不在最关键的窗口里撒谎**：画成"在跑"会让"启动即崩"
    /// 在这 3 秒里看起来像成功；画成"没在跑"又像是从未尝试过。
    Starting,
    /// 没在跑，且**不受监护**（on-demand / tool / gate 关）—— 用户没要求它跑。
    Stopped,
    /// 受监护、没在跑、**正在退避重试**（会自愈）。
    Retrying,
    /// 受监护、没在跑、**已熔断**（5 分钟窗口内 3 次失败；窗口过期会自愈）。
    Degraded,
}

impl State {
    /// 跨进程字面量（`status` 的 `state` 映射与菜单文案共用）。
    pub fn as_str(self) -> &'static str {
        match self {
            State::Running => "running",
            State::Starting => "starting",
            State::Stopped => "stopped",
            State::Retrying => "retrying",
            State::Degraded => "degraded",
        }
    }
}

/// 状态判据（**纯函数**，故可被单测穷举）。
///
/// 优先级 `Running` > `Starting` > `!monitored`(Stopped) > `Degraded` > `Retrying` > `Stopped`：
///
/// - `Degraded` 排在 `Retrying` 之前：熔断期间 `next_attempt_at` 往往还没过期（两个条件同时成立），
///   而"已熔断、不会自己好"是更该让用户知道的那一个；
/// - `!monitored` 排在 `Degraded` 之前：**按需组件即便被熔断过也不该显示成故障** ——
///   它没在跑是正常的（`status` 的 `health` 一直按这条判断，此处沿用同一条）。
fn component_state(alive: bool, monitored: bool, st: Option<&CompState>, now: Instant) -> State {
    if alive {
        return State::Running;
    }
    if st.map(|s| s.pending_spawn).unwrap_or(false) {
        return State::Starting;
    }
    if !monitored {
        return State::Stopped;
    }
    if st.map(|s| s.degraded).unwrap_or(false) {
        return State::Degraded;
    }
    if st.map(|s| s.next_attempt_at.is_some_and(|t| now < t))
        .unwrap_or(false)
    {
        return State::Retrying;
    }
    State::Stopped
}

/// 组件实时视图（`status` 用）。
#[derive(Debug, Default)]
pub struct Snapshot {
    /// 组件名 → 是否在跑。
    pub actual: HashMap<String, bool>,
    /// 组件名 → 累计重启次数。
    pub restarts: HashMap<String, u32>,
    /// 组件名 → `ok` | `degraded`。
    pub health: HashMap<String, String>,
    /// 组件名 → [`State::as_str`]（`running` | `starting` | `stopped` | `retrying` | `degraded`）。
    ///
    /// 与 `health` 并存不是重复：`health` 只回答"健康吗"（两态，既有客户端在用），
    /// `state` 回答"它现在处于什么处境"（五态，菜单据此渲染勾选与 `[!]`）。
    /// 两者**由同一处判据派生**（[`component_state`]）。
    pub state: HashMap<String, String>,
}

/// [`Supervisor::apply_resume_policy`] 的产出（仅用于日志）。
#[derive(Debug, Default, PartialEq, Eq)]
pub struct ResumeReport {
    /// 按 `onResume=restart` 真正重启了的组件。
    pub restarted: Vec<String>,
    /// 按 `onResume=notify` 只记录不干预的组件。
    pub notified: Vec<String>,
    /// 声明了 `reinit` / `probe-and-reinit` 但**尚未实现**、已降级为 reconcile 的组件。
    pub deferred: Vec<String>,
}

struct Inner {
    components: Vec<Component>,
    state: HashMap<String, CompState>,
}

/// 监护器。线程安全；由 `main` 建一个并存进静态，管道与托盘都通过全局访问点取用。
pub struct Supervisor {
    inner: Mutex<Inner>,
}

impl Supervisor {
    /// 建监护器（状态为空 —— 首次 [`reconcile`](Self::reconcile) 会按实际进程填充）。
    pub fn new(components: Vec<Component>) -> Self {
        Self {
            inner: Mutex::new(Inner {
                components,
                state: HashMap::new(),
            }),
        }
    }

    /// 锁获取失败（前面的线程 panic 过）不应让监护整体停摆 → 用 `into_inner` 继续。
    fn lock(&self) -> std::sync::MutexGuard<'_, Inner> {
        self.inner.lock().unwrap_or_else(|e| e.into_inner())
    }

    /// 一次完整对账：先看实际态，再对**差集**动手。
    ///
    /// `reason` 只用于日志（"startup" / "tick" / "resume"），让"这次拉起是谁触发的"可追。
    pub fn reconcile(&self, reason: &str) -> Report {
        // 挂起期间**什么都不做**（四审①）：睡眠中发起的 CreateProcessW 行为不可依赖，
        // 而唤醒后本来就要全量对账一次（`reason="resume"`），所以此时静默是最安全的选择。
        // 不记日志 —— 每 3 秒刷一行"我在睡"只会淹掉真正有用的信息，
        // 而"监护已暂停"这条已经在 `power::on_suspend` 记过一次了。
        if crate::power::is_suspended() {
            return Report::default();
        }

        // 每次对账重新读配置：开关一改，最迟一个轮询周期内生效。
        // 不缓存配置的代价是每 3 秒读一次小 JSON（毫秒级），换来的是"关掉开关立即停"这条
        // 用户可感知的语义 —— 值得。
        self.reconcile_with(reason, &Settings::load())
    }

    /// 带注入配置的对账（同 [`Self::start_with`]：唯一目的是让 gate 分支测得到）。
    fn reconcile_with(&self, reason: &str, settings: &Settings) -> Report {
        // 全局暂停：**两个独立标记、任一存在即暂停**（见 [`USER_PAUSE_FLAG`] 的 race 说明）。
        // core 不认它的话，更新期间 core 会一直复活**正被替换**的组件 —— 那是能把更新搞坏的那种 bug。
        //
        // 与挂起同样处理：什么都不做。日志只在**来源变化**时各记一次 ——
        // 暂停期间每 3 秒刷一行"我在暂停"只会淹掉真正有用的信息。
        let pause = pause_source();
        let pause_code = pause.map(PauseSource::code).unwrap_or(0);
        let previous = PAUSE_LOGGED.swap(pause_code, std::sync::atomic::Ordering::SeqCst);
        if let Some(source) = pause {
            if previous != pause_code {
                log::warn(format!(
                    "supervision PAUSED (paused by: {}) — nothing will be started or stopped; \
                     the updater writes '{}' while it replaces files, and the tray writes '{}' \
                     for 'pause supervision'",
                    source.as_str(),
                    UPDATER_PAUSE_FLAG,
                    USER_PAUSE_FLAG
                ));
            }
            return Report::default();
        }
        if previous != 0 {
            log::info("supervision resumed (no pause flag present)");
        }

        let running = process::running_exe_names();
        let now = Instant::now();

        let mut report = Report::default();
        let mut inner = self.lock();
        let components = inner.components.clone();

        for c in &components {
            let gate_open = gate_open(c, &settings);
            let stop_flag = c.stop_flag.as_deref().is_some_and(components::flag_exists);
            let alive = is_alive(c, &running);
            let st = inner.state.entry(c.name.clone()).or_default();

            refresh(st, now);

            // 先验证"上一轮拉起的是否活过一轮"——这是 crash-loop 的主要检测点。
            //
            // 【真机教训（2026-09-19，S4-3 验收现场）】这里必须接受"**进程在、管道还没就绪**"：
            // core 杀掉 desktop 后立刻拉起它，3 秒后对账时服务还没建好命令管道（WPF 冷启动数秒），
            // 于是被读成"启动即崩" → 记一次失败 → 退避 1 秒 → **又拉起第二个实例**。
            // 实测现场：两个 `BetterDesktop.DesktopControl.exe` 并存 —— 多主拉起，正是本模块要治的病。
            //
            // 两个旧实现都有这道防护，迁移时漏了：
            //   · Watchdog 的 `GraceMs`（"目标不在：若在宽限期内（刚自重启/正常退出），不急于拉起"）；
            //   · Agent 的 `DesktopServiceSupervisor.IsRunning()`（"管道尚未就绪的启动窗口（服务刚起来
            //     1~2 秒）：按进程名兜底，避免每 5s 重复拉起"）。
            //
            // 判据**只在"刚由我们拉起"这一轮生效**，所以不会把审计 #9 的短命同名菜单进程放进来：
            // 那种情形下我们并没有刚拉起它，也不会紧跟着一次 spawn。
            if st.pending_spawn {
                st.pending_spawn = false;
                let survived = alive || spawn_is_alive(c, &running);
                if survived {
                    // 活过一轮 → 视为成功，**把所有的债一次清干净**。
                    //
                    // `degraded` 必须在这里显式清，不能只靠下一轮 `refresh` 重算：
                    // 两者之间（≤3 秒）会出现"债已清空、熔断标记还挂着"的状态 ——
                    // 而 `state`/`health` 是拿这个标记回答用户的。清债与清标记必须在同一处，
                    // 否则就是"两套答案"的最小形态。
                    st.consecutive_failures = 0;
                    st.failures.clear();
                    st.next_attempt_at = None;
                    st.degraded = false;
                } else {
                    record_failure(st, now);
                    report.failed.push(c.name.clone());
                    crate::log::warn(format!(
                        "'{}' died within one supervision round after being started ({} consecutive failures)",
                        c.name, st.consecutive_failures
                    ));
                    continue;
                }
            }

            match decide(c, gate_open, stop_flag, alive, st, now) {
                Decision::Alive | Decision::Idle => {}
                Decision::Wait => report.waiting.push(c.name.clone()),
                Decision::Degraded => report.degraded.push(c.name.clone()),
                Decision::Stop => {
                    let killed = process::stop_by_exe_name(&c.exe);
                    if killed > 0 {
                        report.stopped.push(format!("{} (pid kills={killed})", c.name));
                        // 停止是用户意图的落实，不算失败：清掉状态，避免"开关关→开"后被旧退避拖住
                        st.consecutive_failures = 0;
                        st.failures.clear();
                        st.next_attempt_at = None;
                        st.degraded = false;
                    }
                }
                Decision::Spawn => match try_spawn(c) {
                    Ok(exe) => {
                        st.restarts += 1;
                        st.pending_spawn = true;
                        report.spawned.push(c.name.clone());
                        crate::log::info(format!(
                            "supervisor({reason}): started '{}' -> {} (restart #{})",
                            c.name,
                            exe.display(),
                            st.restarts
                        ));
                    }
                    Err(e) => {
                        record_failure(st, now);
                        report.failed.push(c.name.clone());
                        crate::log::error(format!(
                            "supervisor({reason}): cannot start '{}': {e} (failure #{}, next attempt in {}s)",
                            c.name,
                            st.consecutive_failures,
                            backoff(st.consecutive_failures).as_secs()
                        ));
                    }
                },
            }
        }

        report.log(reason);
        report
    }

    /// 显式拉起（用户/入口的 `start` 请求）。
    ///
    /// 与自动监护的区别：**显式请求重置退避与熔断**。理由 —— 熔断是防"自动守护疯狂重启"的，
    /// 而用户点一次"启动"是一个新的人工信号，用它去撞自己设的刹车片没有意义。
    pub fn start(&self, name: &str) -> Outcome {
        self.start_with(name, &Settings::load())
    }

    /// 带注入配置的启动。
    ///
    /// 存在的唯一理由是**可测性**：gate 分支（"开关关掉不许复活"）若只能靠改真机
    /// `settings.json` 才测得到，那条分支实际上就永远不会被测。
    fn start_with(&self, name: &str, settings: &Settings) -> Outcome {
        let mut inner = self.lock();
        let Some(c) = inner.components.iter().find(|c| c.name == name).cloned() else {
            return Outcome::Failed(format!("component '{name}' not in components.json"));
        };

        // 门禁**在唤起之前**判：开关关掉时"打开面板"也必须被拒（否则设置不再是唯一真相源）。
        if gate_closed(&c, settings) {
            return Outcome::GateClosed;
        }

        // panel ——「启动」是**唤起**，不是拉起：见 [`start_is_wakeup`] 的长注释（S5-3 真机缺口）。
        //
        // 【为什么不动监护状态】这是一次**信号投递**，不是"拉起了一个被监护的组件"：
        // 不 `pending_spawn`、不 `restarts += 1`、不重置熔断 —— 否则用户的每次"打开"都会把
        // `restarts` 灌成噪声（`status` 里那个数字就不再表示"崩溃重启过几次"），
        // 而一次信号也不该被崩溃探测器当成"我们刚起了一个进程"。
        //
        // 【为什么仍然要记日志】它产生了一次 `CreateProcess`：写入必须留痕（弃"静默动作"）。
        if start_is_wakeup(&c) {
            return match try_spawn(&c) {
                Ok(exe) => {
                    crate::log::info(format!(
                        "wake-up sent: '{}' -> {} (panel: always spawns; the running instance \
                         decides show/hide via its single-instance channel)",
                        c.name,
                        exe.display()
                    ));
                    Outcome::Changed
                }
                Err(e) => {
                    crate::log::error(format!("wake-up failed for '{name}': {e}"));
                    Outcome::Failed(e)
                }
            };
        }

        // 判据见 [`explicit_start_is_noop`]：**按声明判活** + 仅"刚由我们拉起的那一轮"额外接受进程名。
        // 旧实现用 `process::is_running`（全程宽松）：同名旁观者在跑就判"已在跑"⇒ 点了没反应。
        let running = process::running_exe_names();
        let already_there = {
            let st = inner.state.get(&c.name);
            explicit_start_is_noop(&c, &running, st)
        };
        if already_there {
            return Outcome::Unchanged;
        }

        match try_spawn(&c) {
            Ok(exe) => {
                let now = Instant::now();
                let st = inner.state.entry(c.name.clone()).or_default();
                refresh(st, now);
                // 显式请求 → 重置刹车
                st.consecutive_failures = 0;
                st.failures.clear();
                st.degraded = false;
                st.next_attempt_at = None;
                st.pending_spawn = arms_crash_detector(&c);
                st.restarts += 1;
                crate::log::info(format!(
                    "explicit start: '{}' -> {}",
                    c.name,
                    exe.display()
                ));
                Outcome::Changed
            }
            Err(e) => {
                crate::log::error(format!("explicit start failed for '{name}': {e}"));
                Outcome::Failed(e)
            }
        }
    }

    /// 显式停止（用户/入口的 `stop` 请求）。幂等：本来没在跑就是 [`Outcome::Unchanged`]。
    ///
    /// # 判活用 [`is_alive`]（与 `reconcile` / `snapshot` 同源），**不是** `process::is_running`
    ///
    /// 旧实现按**进程名**判活，对 `desktop` 是错的：它的 exe 名被一个**短命**的「桌面控制菜单」
    /// 进程共用（审计 #9）—— 菜单正在弹时，"停止"会以为服务在跑，接着 `stop_by_exe_name`
    /// 去杀一批同名进程，而**真服务可能一个都没停到**、却杀掉了无辜的菜单进程。
    ///
    /// # 与 [`Self::start`] 的判活**故意不对称**
    ///
    /// 两个方向都选了"安全的那一侧"，不是随便各写一套：
    /// - `start` 用 [`explicit_start_is_noop`]：**按声明判活**，外加**仅"core 刚拉起、待验证的那一轮"**
    ///   额外接受进程名（**不是全局宽松** —— 若有人只读到这里就复制那段逻辑，会得到"任何同名进程在跑
    ///   就不拉"⇒ 用户点启动没反应）；这是为避免"服务正在启动、管道尚未就绪时又拉起第二个实例"
    ///   （多主拉起，S4-3 真机事故）。**唯一例外是 `type=panel`**（唤起而非拉起，见 [`start_is_wakeup`]）。
    /// - `stop` 用**精确**判据（`is_alive`，按组件声明的存活判据）：只停真在跑的那个，
    ///   绝不误杀旁观者。一个宁可少做、一个宁可少杀。
    pub fn stop(&self, name: &str) -> Outcome {
        let mut inner = self.lock();
        let Some(c) = inner.components.iter().find(|c| c.name == name).cloned() else {
            return Outcome::Failed(format!("component '{name}' not in components.json"));
        };

        let running = process::running_exe_names();
        if !is_alive(&c, &running) {
            return Outcome::Unchanged;
        }
        let killed = process::stop_by_exe_name(&c.exe);
        if killed == 0 {
            // 探活说在跑、停却一个都没停到 → 状态不一致，必须显式报告而不是报"已停止"
            return Outcome::Failed(format!(
                "'{name}' appeared to be running but no process could be stopped"
            ));
        }

        let st = inner.state.entry(c.name.clone()).or_default();
        st.consecutive_failures = 0;
        st.failures.clear();
        st.next_attempt_at = None;
        st.degraded = false;
        crate::log::info(format!("explicit stop: '{}' (killed {killed})", c.name));
        Outcome::Changed
    }

    /// 唤醒后**第一件事**：重置所有计时器（四审钉死的顺序 —— 必须先重置再 reconcile）。
    ///
    /// # 为什么必须重置
    ///
    /// 睡眠期间时间在往前走，但监护的"相对截止时刻"语义被破坏了，两个方向都会出问题：
    /// - 若 `Instant`/QPC **在睡眠中也前进** → 所有退避截止点会**同时到期** →
    ///   唤醒瞬间对一批本该处于退避期的组件并发发起拉起（四审所说的"动作风暴"）；
    /// - 若它**不前进** → 一个睡前设的 30s 退避在醒来后还要再等 30s，
    ///   于是一个已经死了 8 小时的组件还要"再等一会"才被拉回。
    ///
    /// 两种底层语义都不能依赖，所以显式重置 —— 这样无论 QPC 怎么定义都不出错。
    ///
    /// 同时清掉 `pending_spawn`：它是"上一轮拉起、本轮验证是否活过一轮"的标记，
    /// 而"一轮"这个概念**跨不过一次睡眠** —— 留着它会把"睡眠冻结"误判成"启动即崩"。
    ///
    /// 返回被重置的组件数（进日志，便于事后确认这一步真的执行了）。
    pub fn reset_timers(&self) -> usize {
        let mut inner = self.lock();
        let mut count = 0;
        for st in inner.state.values_mut() {
            st.next_attempt_at = None;
            st.pending_spawn = false;
            count += 1;
        }
        count
    }

    /// 把当前"期望态 × 实际态"逐条落进日志（挂起前/唤醒后各记一份，用于事后比对）。
    ///
    /// 这是四审验收项「睡眠前状态持久化：睡眠前记录 desired，唤醒后读，期望一致」的取证手段。
    pub fn log_state_snapshot(&self, reason: &str) {
        let settings = Settings::load();
        let running = process::running_exe_names();
        let inner = self.lock();
        for c in &inner.components {
            let gate_open = gate_open(c, &settings);
            let ensure = components::auto_start(c, gate_open);
            let is_running = running.contains(&c.exe.to_ascii_lowercase());
            log::info(format!(
                "power({reason}): {} desired={:?} gate={} ensure={} actual={}",
                c.name, c.desired, gate_open, ensure, is_running
            ));
        }
    }

    /// 执行组件声明的 `power.onSuspend`（挂起前）。
    ///
    /// 只有 `stop` 需要动作：它是"睡眠时主动释放可选资源"的表达（GPU 上下文、大缓冲区的持有者
    /// 应当声明 `onSuspend: stop`，唤醒后由 reconcile 按 `auto_start` 拉回）。
    /// `freeze`（默认）什么都不做 —— 进程被系统冻结，醒来还是活的。
    pub fn apply_suspend_policy(&self) -> Vec<String> {
        let mut stopped = Vec::new();
        for c in self.components_snapshot() {
            match c.power.on_suspend {
                crate::components::SuspendAction::Stop => {
                    if !process::is_running(&c.exe) {
                        continue;
                    }
                    log::info(format!(
                        "power(suspend): stopping '{}' as declared by power.onSuspend=stop",
                        c.name
                    ));
                    let killed = process::stop_by_exe_name(&c.exe);
                    if killed == 0 {
                        log::warn(format!(
                            "power(suspend): '{}' appeared to be running but nothing could be stopped",
                            c.name
                        ));
                    } else {
                        stopped.push(c.name.clone());
                        self.forget_failures(&c.name);
                    }
                }
                crate::components::SuspendAction::Notify => log::info(format!(
                    "power(suspend): '{}' declared onSuspend=notify (recorded; core has no push channel to components yet)",
                    c.name
                )),
                crate::components::SuspendAction::Freeze => {}
            }
        }
        stopped
    }

    /// 执行组件声明的 `power.onResume`（唤醒后，在 reconcile 之后）。
    ///
    /// `reconcile` 由 [`Self::reconcile`] 本身覆盖，因此这里只处理**额外**动作。
    /// `reinit` / `probe-and-reinit` **明确记为未实现**并如实降级为 reconcile ——
    /// 它们需要组件**自报健康**（core 不知道 WPF 有没有花屏、D3D 设备有没有丢），
    /// 那个反向通道随 bd-infer / bd-world 一起落地。**不假装已实现。**
    pub fn apply_resume_policy(&self) -> ResumeReport {
        let mut report = ResumeReport::default();
        for c in self.components_snapshot() {
            match c.power.on_resume {
                crate::components::ResumeAction::Reconcile => {}
                crate::components::ResumeAction::Restart => {
                    log::info(format!(
                        "power(resume): restarting '{}' as declared by power.onResume=restart",
                        c.name
                    ));
                    // stop 之后立刻 start：reconcile 已经跑过，不会替我们把停掉的再拉回来
                    let _ = self.stop(&c.name);
                    match self.start(&c.name) {
                        Outcome::Changed | Outcome::Unchanged => report.restarted.push(c.name.clone()),
                        Outcome::GateClosed => log::warn(format!(
                            "power(resume): '{}' was not restarted — its switch is off",
                            c.name
                        )),
                        Outcome::Failed(e) => log::error(format!(
                            "power(resume): failed to restart '{}': {e}",
                            c.name
                        )),
                    }
                }
                crate::components::ResumeAction::Notify => {
                    log::info(format!(
                        "power(resume): '{}' declared onResume=notify (recorded only)",
                        c.name
                    ));
                    report.notified.push(c.name.clone());
                }
                crate::components::ResumeAction::Reinit => {
                    log::warn(format!(
                        "power(resume): '{}' declared onResume=reinit but reinit is NOT implemented yet \
                         (needs the component to re-create external resources and report health); \
                         falling back to reconcile — if it owns a GPU context it may be broken until S5.5",
                        c.name
                    ));
                    report.deferred.push(format!("{}:reinit", c.name));
                }
                crate::components::ResumeAction::ProbeAndReinit => {
                    log::warn(format!(
                        "power(resume): '{}' declared onResume=probe-and-reinit but the health probe is NOT \
                         implemented yet (needs a reverse channel so the component can self-report); \
                         falling back to reconcile — DWM rebuild / D3D device loss / shell hooks may still be broken",
                        c.name
                    ));
                    report.deferred.push(format!("{}:probe-and-reinit", c.name));
                }
            }
        }
        report
    }

    fn components_snapshot(&self) -> Vec<Component> {
        self.lock().components.clone()
    }

    /// 清掉某组件的失败历史（停止是意图的落实，不该被旧退避拖住）。
    fn forget_failures(&self, name: &str) {
        if let Some(st) = self.lock().state.get_mut(name) {
            st.consecutive_failures = 0;
            st.failures.clear();
            st.next_attempt_at = None;
            st.degraded = false;
            st.pending_spawn = false;
        }
    }

    /// 实时视图（`status` 用）：重新枚举一次实际进程，不读缓存状态里的"在跑"。
    pub fn snapshot(&self, settings: &Settings) -> Snapshot {
        let running = process::running_exe_names();
        // 退避是否到期要用同一个时刻判断 —— 逐个组件各取一次 `Instant::now()` 会让
        // 同一份快照里的时间基准不一致（读取期间钟在走），届时"retrying/stopped"会在
        // 边界上随机抖动。
        let now = Instant::now();
        let mut inner = self.lock();
        // 先克隆组件表：下面要 `refresh` 组件的内部状态（可变借用），
        // 直接迭代 `inner.components` 会与之冲突。
        let components = inner.components.clone();

        let mut snap = Snapshot::default();
        for c in &components {
            // **先按当前时刻重算到期项**：`refresh` 原本只在 reconcile 里跑（每 3 秒一次），
            // 若这里不跑，一份**刚过期**的 `degraded` 会被原样报出去 —— 那就是
            // "status 说熔断、其实早该解除"的另一种两套答案。诊断命令必须对着**当前时刻**回答。
            if let Some(st) = inner.state.get_mut(&c.name) {
                refresh(st, now);
            }
            // **与 reconcile 用同一个判据**：否则 `status` 会说 desktop 不在跑，
            // 而监护器正按"它在跑"放过它 —— 两处对同一个问题给不同答案是最难查的那类不一致。
            let is_running = is_alive(c, &running);
            snap.actual.insert(c.name.clone(), is_running);

            let st = inner.state.get(&c.name);
            snap.restarts
                .insert(c.name.clone(), st.map(|s| s.restarts).unwrap_or(0));

            // 状态与健康**都从这一处派生**：菜单渲染勾选/`[!]`、`status` 报 health。
            // 若各算一套，就会出现"菜单说熔断、status 说 ok"（S5-3 定案的一条纪律）。
            let monitored = components::auto_start(c, gate_open(c, settings));
            let state = component_state(is_running, monitored, st, now);
            snap.state
                .insert(c.name.clone(), state.as_str().to_string());
            // degraded 只有在"期望它跑却没跑"时才是健康问题 —— `component_state` 已含这条判断
            //（含"按需组件不报故障"与"在跑的组件不报不健康"），故这里不再写第二遍。
            //
            // 与旧实现的细微差别（有意）：旧代码只看 `degraded && monitored`，不看是否在跑；
            // 一个**正在跑**的组件不该被报成不健康 —— 现在以 state 为准。
            snap.health.insert(
                c.name.clone(),
                if state == State::Degraded {
                    "degraded"
                } else {
                    "ok"
                }
                .to_string(),
            );
        }
        snap
    }
}

/// 单测专用缝：直接窥视/注入组件状态。
///
/// 存在理由：`pending_spawn` 的语义（"上一轮拉起、本轮验证活没活过一轮"）只有在
/// **真的拉起过一次**之后才成立，而单测里拉起真进程既慢又脏。这条缝让
/// "睡眠不该被误判成崩溃"这条不变量可以被精确驱动，而不是靠 `sleep` 去碰运气。
#[cfg(test)]
impl Supervisor {
    fn inject_pending_spawn(&self, name: &str) {
        let mut inner = self.lock();
        inner.state.entry(name.to_string()).or_default().pending_spawn = true;
    }

    fn pending_spawn(&self, name: &str) -> bool {
        self.lock()
            .state
            .get(name)
            .map(|s| s.pending_spawn)
            .unwrap_or(false)
    }

    /// 是否仍挂着熔断标记（**内部真值**，不是 `state` 算出来的那个）。
    ///
    /// 需要这个缝的理由：`component_state` 会因 `Running` 而**遮住** `degraded`，
    /// 于是"判据说 ok"无法证明"内部不欠债" —— 而这两件事必须分别可断言。
    fn degraded(&self, name: &str) -> bool {
        self.lock()
            .state
            .get(name)
            .map(|s| s.degraded)
            .unwrap_or(false)
    }

    /// 下一次尝试的截止时刻（`None` = 没有欠着的退避）。
    fn next_attempt(&self, name: &str) -> Option<Instant> {
        self.lock().state.get(name).and_then(|s| s.next_attempt_at)
    }
}

/// 拉起成功后是否要挂上"下一轮验证存活"的标记（= 是否启用 crash-loop 检测）。
///
/// **`type=tool` 必须为 `false`**：一次性工具的设计就是跑完即退，下一轮 reconcile 必然看不到它。
/// 若给了标记，每次按截图热键都会记一次"启动即崩"，三次之后 `capture` 直接熔断 ——
/// 「按几次热键把截图按坏了」正是这么来的。
fn arms_crash_detector(c: &Component) -> bool {
    c.component_type != ComponentType::Tool
}

/// 真正拉起一条组件（本模块是 `spawn_detached` 的唯一调用者）。
fn try_spawn(c: &Component) -> Result<std::path::PathBuf, String> {
    // C19 第一道：`components.json` 的 exe 必须是**裸文件名**。
    // `resolve_exe` 内部也会拦（返回 None），但那里与"未部署"同形；这里先给出**精确**原因。
    if !process::is_bare_name(&c.exe) {
        return Err(format!(
            "component '{}' has a non-plain exe name '{}' — components.json must hold a bare file \
             name (no separators, drive letters, or '..')",
            c.name, c.exe
        ));
    }
    let exe = process::resolve_exe(&c.exe).ok_or_else(|| {
        format!(
            "not deployed (looked for {} in the core directory and %LOCALAPPDATA%\\BetterDesktop)",
            c.exe
        )
    })?;
    // C19 第二道：解析结果必须落在允许目录内 —— 与 `resolve_exe` 用**同一份**目录定义。
    crate::security::validate_resolved_component_exe(&exe)?;
    if process::spawn_detached(&exe, c.args.as_deref()) {
        Ok(exe)
    } else {
        Err(format!("CreateProcessW failed for {}", exe.display()))
    }
}

/// gate 是否开启（键缺失 = 开启；语义见 `pipe::gate_closed`）。
fn gate_open(c: &Component, settings: &Settings) -> bool {
    match c.gate.as_deref() {
        Some(key) => settings.get_bool(key, true),
        None => true,
    }
}

fn gate_closed(c: &Component, settings: &Settings) -> bool {
    !gate_open(c, settings)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::components::{
        ComponentType, Desired, PowerPolicy, ResumeAction, SuspendAction, Tier,
    };

    fn comp(name: &str, desired: Desired, tier: Tier) -> Component {
        Component {
            name: name.into(),
            label: name.into(),
            exe: format!("{name}.exe"),
            desired,
            tier,
            component_type: ComponentType::Process,
            power: PowerPolicy::default(),
            gate: None,
            args: None,
            stop_flag: None,
            liveness: Liveness::Process,
            liveness_pipe: None,
            remove_at: None,
        }
    }

    fn fresh() -> CompState {
        CompState::default()
    }

    // ───────────────────── 决策：期望态 × 实际态 ─────────────────────

    #[test]
    fn on_demand_is_never_auto_started() {
        let c = comp("shell", Desired::OnDemand, Tier::Surface);
        assert_eq!(
            decide(&c, true, false, false, &fresh(), Instant::now()),
            Decision::Idle,
            "on-demand 组件绝不能被自动拉起"
        );
    }

    #[test]
    fn gate_closed_stops_a_running_component() {
        let c = comp("desktop", Desired::Running, Tier::Surface);
        assert_eq!(
            decide(&c, false, false, true, &fresh(), Instant::now()),
            Decision::Stop,
            "开关关掉的东西必须停，且不得被守护复活"
        );
        assert_eq!(
            decide(&c, false, false, false, &fresh(), Instant::now()),
            Decision::Idle,
            "已经不在跑 → 无需动作"
        );
    }

    #[test]
    fn resident_component_is_ensured_only_when_gate_open() {
        let c = comp("desktop", Desired::Running, Tier::Surface);
        assert_eq!(
            decide(&c, true, false, false, &fresh(), Instant::now()),
            Decision::Spawn
        );
        assert_eq!(
            decide(&c, false, false, false, &fresh(), Instant::now()),
            Decision::Idle
        );
    }

    #[test]
    fn foundation_is_ensured_and_never_stopped() {
        let mut c = comp("core-ish", Desired::Stopped, Tier::Foundation);
        c.gate = Some("components.x".into());
        assert_eq!(
            decide(&c, false, false, false, &fresh(), Instant::now()),
            Decision::Spawn,
            "地基永不缺席，开关也停不了它"
        );
    }

    /// 一次性工具被"确保在跑"会变成无限重启 —— 必须从源头拒绝。
    #[test]
    fn tool_is_never_auto_started() {
        let mut c = comp("scanner", Desired::Running, Tier::Extension);
        c.component_type = ComponentType::Tool;
        assert_eq!(
            decide(&c, true, false, false, &fresh(), Instant::now()),
            Decision::Idle,
            "tool 跑完即退，不能被监护"
        );
    }

    // ───────────────────────── 退避与熔断 ─────────────────────────

    #[test]
    fn backoff_grows_then_caps() {
        let secs: Vec<u64> = (1..=8).map(|n| backoff(n).as_secs()).collect();
        assert_eq!(secs, vec![1, 2, 4, 8, 16, 30, 30, 30]);
    }

    #[test]
    fn waits_during_backoff_then_retries() {
        let c = comp("desktop", Desired::Running, Tier::Surface);
        let t0 = Instant::now();
        let mut st = fresh();
        record_failure(&mut st, t0);

        assert_eq!(decide(&c, true, false, false, &st, t0), Decision::Wait);
        assert_eq!(
            decide(&c, true, false, false, &st, t0 + Duration::from_millis(500)),
            Decision::Wait
        );
        assert_eq!(
            decide(&c, true, false, false, &st, t0 + Duration::from_millis(1_001)),
            Decision::Spawn,
            "退避到期后应重试"
        );
    }

    #[test]
    fn three_failures_in_window_degrade_then_expire() {
        let c = comp("desktop", Desired::Running, Tier::Surface);
        let t0 = Instant::now();
        let mut st = fresh();

        for i in 0..FAILURES_TO_DEGRADE {
            record_failure(&mut st, t0 + Duration::from_secs(i as u64));
        }
        assert!(st.degraded, "窗口内 3 次失败 → 熔断");
        assert_eq!(
            decide(&c, true, false, false, &st, t0 + Duration::from_secs(30)),
            Decision::Degraded,
            "熔断后不再拉起"
        );

        // 失败记录滚出时间窗 → 自动解除（不是永久判死）
        let later = t0 + FAILURE_WINDOW + Duration::from_secs(1);
        refresh(&mut st, later);
        assert!(!st.degraded, "时间窗过期后应自动解除熔断");
        assert_eq!(decide(&c, true, false, false, &st, later), Decision::Spawn);
    }

    /// 熔断看的是**时间窗内的次数**，不是"连续"次数：失败之间隔很久不会熔断。
    #[test]
    fn slow_failures_never_degrade() {
        let t0 = Instant::now();
        let mut st = fresh();
        for i in 0..10 {
            record_failure(&mut st, t0 + FAILURE_WINDOW * (i + 1));
        }
        assert!(
            !st.degraded,
            "每次失败都在上一个之后超过窗口，不应熔断（{:?}）",
            st.failures
        );
    }

    #[test]
    fn a_healthy_round_clears_failure_history() {
        let c = comp("desktop", Desired::Running, Tier::Surface);
        let t0 = Instant::now();
        let mut st = fresh();
        record_failure(&mut st, t0);
        record_failure(&mut st, t0 + Duration::from_secs(1));

        // 活过一轮 = 成功 → 清空历史
        st.consecutive_failures = 0;
        st.failures.clear();
        st.next_attempt_at = None;

        assert_eq!(decide(&c, true, false, true, &st, t0 + Duration::from_secs(2)), Decision::Alive);
        assert!(
            !st.degraded,
            "两次失败后恢复正常，不应熔断"
        );
    }

    // ─────────────── reconcile：差集、不重复拉起、绝不误杀 ───────────────

    /// **D23 的核心不变量**：core 重启后 reconcile 时，已经在跑的组件不得被再次拉起。
    ///
    /// 这里用真进程验证：core 自己（本测试进程）的 exe 名必然是"在跑"的，
    /// 把它放进组件表，reconcile 必须判定为 Alive 而不是 Spawn。
    #[test]
    fn reconcile_does_not_relaunch_running_component() {
        let me = std::env::current_exe().unwrap();
        let name = me.file_name().unwrap().to_string_lossy().to_string();

        let mut c = comp("self", Desired::Running, Tier::Foundation);
        c.exe = name;
        let sup = Supervisor::new(vec![c]);

        let report = sup.reconcile("test");
        assert!(
            report.spawned.is_empty(),
            "an already-running component must never be relaunched: {report:?}"
        );
        assert!(report.failed.is_empty(), "{report:?}");

        let snap = sup.snapshot(&Settings::empty());
        assert_eq!(snap.actual.get("self"), Some(&true));
        assert_eq!(snap.restarts.get("self"), Some(&0), "不得重复计数");
    }

    /// 用户显式停止（托盘写的 `*-stopped.flag`）→ core **不拉回**、**也不去杀**。
    ///
    /// 这是 S4-3 从 Watchdog 迁入的语义，也是本轮最要紧的一条：缺了它，core 会把用户刚停掉的
    /// 组件立刻复活，托盘那句"已停止"被静默推翻（本仓库已踩过同类坑）。
    #[test]
    fn user_stop_flag_prevents_restart_without_killing() {
        let c = comp("shell", Desired::Running, Tier::Surface);

        assert_eq!(
            decide(&c, true, true, false, &fresh(), Instant::now()),
            Decision::Idle,
            "标记在 → 不得拉起"
        );

        // 标记在、而进程还在跑（= 那次停止还没做完）→ core **不主动杀**：
        // 写标记的一方自己会停进程；core 插手只会变成两个人在抢同一个进程。
        assert_eq!(
            decide(&c, true, true, true, &fresh(), Instant::now()),
            Decision::Idle,
            "标记在 → 也不该由 core 去杀"
        );
    }

    /// **审计 #9 的回归钉子**：声明管道判活的组件，即便**有个同名进程正在跑**，只要管道不在
    /// 就必须判成"不在" —— 否则短命的「桌面控制菜单」会永远骗过监护器（真服务死了不被拉起）。
    #[test]
    fn pipe_liveness_is_not_fooled_by_a_running_process_name() {
        let me = std::env::current_exe().unwrap();
        let mut c = comp("desktop", Desired::Running, Tier::Surface);
        c.exe = me.file_name().unwrap().to_string_lossy().to_string(); // 本测试进程 = "确实在跑"
        c.liveness = Liveness::Pipe;
        c.liveness_pipe = Some("BetterDesktop.DefinitelyNoSuchPipe".into());

        let running = process::running_exe_names();
        assert!(
            running.contains(&c.exe.to_ascii_lowercase()),
            "前置：这个 exe 名确实在跑（否则本用例证明不了什么）"
        );
        assert!(
            !is_alive(&c, &running),
            "管道判活的组件不得因为『有个同名进程在跑』被判成存活"
        );

        // 反证：同一个 exe 换成进程名判活 → 存活。
        // 没有这条，"上面那句为什么是 false"就没有对照（可能是别的原因失败）。
        c.liveness = Liveness::Process;
        assert!(
            is_alive(&c, &running),
            "按进程名判活时同一个 exe 必须是存活的"
        );
    }

    /// **真机复现的回归**（S4-3 验收现场）：管道判活的组件刚被拉起、进程在而管道尚未就绪时，
    /// 不得判成"启动即崩"—— 否则退避后会**再拉一个**，实测出现两个同名服务并存。
    #[test]
    fn pipe_liveness_tolerates_the_startup_window_instead_of_calling_it_a_crash() {
        let me = std::env::current_exe().unwrap();
        let mut c = comp("desktop", Desired::Running, Tier::Surface);
        c.exe = me.file_name().unwrap().to_string_lossy().to_string(); // 进程在
        c.liveness = Liveness::Pipe;
        c.liveness_pipe = Some("BetterDesktop.DefinitelyNoSuchPipe".into());

        let running = process::running_exe_names();
        assert!(!is_alive(&c, &running), "前置：管道不在 → 常规判活必须为假");
        assert!(
            spawn_is_alive(&c, &running),
            "但『刚被我们拉起』的那一轮必须接受进程名作证据，否则会误判成启动即崩"
        );

        // 反向守卫：进程判活的组件不享受这条豁免 —— 否则等于给所有组件开后门，
        // 而这条豁免的全部意义就是"只覆盖管道组件的冷启动窗口"。
        c.liveness = Liveness::Process;
        assert!(!spawn_is_alive(&c, &running), "进程判活不需要它");
    }

    /// **审计 #9 的防腐蚀钉子**：`spawn_is_alive` 的豁免**只覆盖"刚由我们拉起"那一轮**。
    ///
    /// 没有这条，未来有人看到 `spawn_is_alive` 会想"为什么这么窄"然后放宽它 ——
    /// 而放宽就等于让"服务早已死掉、只剩一个同名短命菜单进程在跑"重新骗过判活（审计 #9 回归）。
    ///
    /// 本用例刻意**只在决策层断言，不真的走 reconcile 的拉起分支**：`c.exe` 用的是本测试进程自己的
    /// 文件名（那是这台机器上唯一"确定在跑"且**我们绝不想真去拉起**的进程 ——
    /// core 的 `resolve_exe` 会先在自身目录找，而测试二进制就在那里，真拉起会**再跑一遍测试套件**）。
    #[test]
    fn the_spawn_exemption_does_not_leak_outside_the_spawn_round() {
        let me = std::env::current_exe().unwrap();
        let mut c = comp("desktop", Desired::Running, Tier::Surface);
        c.exe = me.file_name().unwrap().to_string_lossy().to_string(); // 同名进程确实在跑
        c.liveness = Liveness::Pipe;
        c.liveness_pipe = Some("BetterDesktop.DefinitelyNoSuchPipe".into());

        let running = process::running_exe_names();
        assert!(
            running.contains(&c.exe.to_ascii_lowercase()),
            "前置：同名进程在跑"
        );

        // 稳态判活（reconcile 里 `alive` 用的就是它）：同名进程**不算**存活
        assert!(
            !is_alive(&c, &running),
            "稳态判活不得被同名进程骗过"
        );
        // 于是**没有**"刚拉起"上下文时，决策必须是拉起 —— 豁免不泄漏
        assert_eq!(
            decide(&c, true, false, is_alive(&c, &running), &fresh(), Instant::now()),
            Decision::Spawn,
            "没有『刚由我们拉起』的上下文时，同名进程不得让它被判成存活"
        );

        // 两条判据的**唯一差别**就是调用上下文 —— 这正是要钉住的那条界线
        assert!(
            spawn_is_alive(&c, &running),
            "同一个事实在『刚拉起』那一轮里则算作活过来（对照，说明上一条不是因为别的原因）"
        );
    }

    /// on-demand 组件即便没在跑，reconcile 也绝不动它（"kill 任意按需进程不触发守护"，D7）。
    #[test]
    fn reconcile_never_touches_on_demand_components() {
        let sup = Supervisor::new(vec![comp("shell", Desired::OnDemand, Tier::Surface)]);
        let report = sup.reconcile("test");
        assert!(
            report.spawned.is_empty() && report.failed.is_empty(),
            "on-demand 组件不该被 reconcile 碰：{report:?}"
        );
    }

    /// 未部署的常驻组件：必须记失败并进入退避，而不是每 3 秒狂试一次。
    #[test]
    fn undeployed_resident_component_backs_off_instead_of_hot_looping() {
        let sup = Supervisor::new(vec![comp(
            "ghost",
            Desired::Running,
            Tier::Surface,
        )]);

        let first = sup.reconcile("test");
        assert_eq!(first.failed, vec!["ghost".to_string()]);
        assert!(first.spawned.is_empty());

        // 立刻再来一轮：还在退避窗口内 → 不重复尝试
        let second = sup.reconcile("test");
        assert!(
            second.failed.is_empty() && second.spawned.is_empty(),
            "退避期内不得重试：{second:?}"
        );
        assert_eq!(second.waiting, vec!["ghost".to_string()]);
    }

    /// 显式 stop 对"本来就没在跑"的组件是幂等成功（不是错误）。
    #[test]
    fn explicit_stop_is_idempotent() {
        let sup = Supervisor::new(vec![comp("ghost", Desired::Running, Tier::Surface)]);
        assert_eq!(sup.stop("ghost"), Outcome::Unchanged);
    }

    #[test]
    fn unknown_component_is_failed_not_panic() {
        let sup = Supervisor::new(vec![]);
        assert!(matches!(sup.start("nope"), Outcome::Failed(_)));
        assert!(matches!(sup.stop("nope"), Outcome::Failed(_)));
    }

    /// D8：gate **显式为 false** 时显式 `start` 必须被拒 —— 用户关掉的东西不许被复活。
    ///
    /// 必须真的在配置里写 `false`：键缺失是"默认开启"，用缺失键测这条分支会得到假绿色
    /// （这是本仓库踩过的坑，两处注释都记着）。
    #[test]
    fn explicit_start_refuses_when_gate_is_closed() {
        let mut c = comp("gated", Desired::Running, Tier::Surface);
        c.gate = Some("components.gated".into());
        let sup = Supervisor::new(vec![c]);

        let shut = Settings::from_str(r#"{"components":{"gated":false}}"#);
        assert_eq!(sup.start_with("gated", &shut), Outcome::GateClosed);

        // 同一个组件、gate 开着（键缺失 = 开启）→ 不再被拒，而是走到"未部署"分支
        let open = Settings::empty();
        assert!(matches!(sup.start_with("gated", &open), Outcome::Failed(_)));
    }

    /// 自动监护也必须尊重 gate：关掉之后**既不停手别人、也不复活它**。
    /// （`decide` 已单测，这里验证 `reconcile` 这条真实路径与它一致。）
    #[test]
    fn reconcile_stops_and_never_revives_a_gate_closed_component() {
        let mut c = comp("ghost", Desired::Running, Tier::Surface);
        c.gate = Some("components.ghost".into());
        let sup = Supervisor::new(vec![c]);

        // 关闭的组件 + 未部署 → 既不该被拉起（否则会去撞不存在的 exe），也不该被报失败
        let shut = Settings::from_str(r#"{"components":{"ghost":false}}"#);
        let report = sup.reconcile_with("test", &shut);
        assert!(
            report.spawned.is_empty() && report.failed.is_empty(),
            "gate 关闭的组件不该被监护器碰：{report:?}"
        );

        // 同一个组件、gate 开着（键缺失 = 开启）→ 才会进入拉起尝试
        let report = sup.reconcile_with("test", &Settings::empty());
        assert_eq!(
            report.failed,
            vec!["ghost".to_string()],
            "gate 开后才会尝试拉起（此处因未部署而失败）"
        );
    }

    #[test]
    fn health_is_ok_for_quiet_components() {
        let sup = Supervisor::new(vec![comp("shell", Desired::OnDemand, Tier::Surface)]);
        let snap = sup.snapshot(&Settings::empty());
        assert_eq!(snap.health.get("shell").map(String::as_str), Some("ok"));
        assert_eq!(snap.restarts.get("shell"), Some(&0));
    }

    // ─────────────── S5-3：对外状态（菜单与 status 的同一份判据） ───────────────

    #[test]
    fn state_covers_the_five_situations() {
        let t0 = Instant::now();

        assert_eq!(
            component_state(true, true, Some(&fresh()), t0),
            State::Running
        );
        assert_eq!(
            component_state(true, false, None, t0),
            State::Running,
            "在跑就是在跑，与是否受监护无关"
        );

        // 刚由 core 拉起、还没确认活过一轮
        let mut st = fresh();
        st.pending_spawn = true;
        assert_eq!(component_state(false, true, Some(&st), t0), State::Starting);
        assert_eq!(
            component_state(true, true, Some(&st), t0),
            State::Running,
            "进程确实在跑时不该显示『启动中』"
        );

        // 退避中（会自愈）
        let mut st = fresh();
        st.next_attempt_at = Some(t0 + Duration::from_secs(4));
        assert_eq!(component_state(false, true, Some(&st), t0), State::Retrying);
        assert_eq!(
            component_state(false, true, Some(&st), t0 + Duration::from_secs(5)),
            State::Stopped,
            "退避到期后不再是 retrying"
        );

        // 已熔断：与退避**同时成立**时，熔断优先（"不会自己好"更该让用户知道）
        let mut st = fresh();
        st.degraded = true;
        st.next_attempt_at = Some(t0 + Duration::from_secs(4));
        assert_eq!(component_state(false, true, Some(&st), t0), State::Degraded);
    }

    /// 按需组件没在跑是**正常**的 —— 即便被熔断过也不该报成故障。
    ///
    /// 这条沿用 `status.health` 的既有判断（"一个按需组件即便被熔断过也不该在 status 里显示成红的"）；
    /// 现在 `state` 与 `health` 同源，故这条在 `state` 上也必须成立 —— 否则菜单会给面板挂一个 `[!]`。
    #[test]
    fn on_demand_component_is_never_shown_as_failing() {
        let t0 = Instant::now();
        let mut st = fresh();
        st.degraded = true;
        st.next_attempt_at = Some(t0 + Duration::from_secs(4));
        assert_eq!(
            component_state(false, false, Some(&st), t0),
            State::Stopped,
            "不受监护 ⇒ 没在跑就是『用户没要求』，不是故障"
        );
    }

    /// `Snapshot` 必须同时给出 `state` 与 `health`，且二者**同源**。
    ///
    /// 同源性是这条测试的全部意义：它们若各算一套，就会出现"菜单说已熔断、status 说 ok" ——
    /// 而那正是本仓判定"最难查的一类不一致"（判活两套）的同族问题。
    #[test]
    fn snapshot_exposes_state_and_keeps_health_in_sync() {
        let sup = Supervisor::new(vec![comp("shell", Desired::OnDemand, Tier::Surface)]);
        let snap = sup.snapshot(&Settings::empty());

        assert_eq!(snap.state.get("shell").map(String::as_str), Some("stopped"));
        assert_eq!(snap.health.get("shell").map(String::as_str), Some("ok"));

        for (name, st) in &snap.state {
            let health_is_degraded = snap.health.get(name).map(|h| h == "degraded");
            assert_eq!(
                health_is_degraded,
                Some(st == "degraded"),
                "'{name}'：health 与 state 对『是否熔断』必须给同一个答案"
            );
        }
    }

    /// **活过一轮要把债一次清干净**（含 `degraded`）—— 否则会出现"`state` 说 running、
    /// 内部还欠着熔断"：下次它崩了会从上次的熔断点继续，而用户看不到任何征兆。
    ///
    /// 用本测试进程自己的 exe 名（这台机器上唯一"确定在跑"且**绝不想真去拉起**的进程）：
    /// 它天然满足"拉起后活过一轮"的前置，故走的就是成功分支，不产生任何真实拉起。
    #[test]
    fn surviving_a_round_clears_every_debt_including_degraded() {
        let me = std::env::current_exe().unwrap();
        let mut c = comp("self", Desired::Running, Tier::Surface);
        c.exe = me.file_name().unwrap().to_string_lossy().to_string();
        let sup = Supervisor::new(vec![c]);

        // 造债：时间窗内三次失败 → 熔断 + 退避，并保持"待验证"标记
        let t = Instant::now();
        {
            let mut inner = sup.lock();
            let st = inner.state.entry("self".to_string()).or_default();
            for _ in 0..FAILURES_TO_DEGRADE {
                record_failure(st, t);
            }
            st.pending_spawn = true;
        }
        assert!(sup.degraded("self"), "前置：内部已挂熔断标记");

        // 跑一轮：进程确实在跑 ⇒ 走"活过一轮"分支
        let report = sup.reconcile("test");
        assert!(report.failed.is_empty(), "不该记失败：{report:?}");

        assert!(
            !sup.degraded("self"),
            "活过一轮后**内部**熔断标记必须被清掉（不能只靠判据遮住）"
        );
        assert_eq!(sup.next_attempt("self"), None, "退避截止点也必须清空");

        let snap = sup.snapshot(&Settings::empty());
        assert_eq!(snap.state.get("self").map(String::as_str), Some("running"));
        assert_eq!(snap.health.get("self").map(String::as_str), Some("ok"));
    }

    /// `snapshot` 必须对着**当前时刻**回答：一份**已过期**的 `degraded` 不得被原样报出去。
    ///
    /// 没有这条，`refresh` 只在 reconcile 里跑（每 3 秒）⇒ 诊断命令会报一个"早该解除"的熔断，
    /// 而修好之后菜单里会挂一个不该存在的 `[!]`。
    #[test]
    fn snapshot_refreshes_expired_degradation() {
        let sup = Supervisor::new(vec![comp("ghost", Desired::Running, Tier::Surface)]);
        {
            let mut inner = sup.lock();
            let st = inner.state.entry("ghost".to_string()).or_default();
            // 失败发生在时间窗之外（Instant 只能向前减，故用 checked_sub）
            let long_ago = Instant::now()
                .checked_sub(FAILURE_WINDOW * 2)
                .expect("test machine must have been up long enough");
            for _ in 0..FAILURES_TO_DEGRADE {
                record_failure(st, long_ago);
            }
            st.degraded = true; // 显式摆出"标记还挂着"的状态
        }

        let snap = sup.snapshot(&Settings::empty());
        assert_eq!(
            snap.health.get("ghost").map(String::as_str),
            Some("ok"),
            "失败记录早已滚出窗口 ⇒ 不得再报熔断（诊断命令要对着当前时刻回答）"
        );
        assert_eq!(snap.state.get("ghost").map(String::as_str), Some("stopped"));
    }

    /// **`start` 的"宽松"必须有边界**：只在"刚由 core 拉起、待验证"那一轮额外接受进程名。
    ///
    /// 无边界版（旧实现 `process::is_running`）会把同名旁观者当成组件本身 ⇒ 用户点"启动"没反应。
    /// 本用例是**纯判据**测试，不真的拉起任何进程。
    #[test]
    fn explicit_start_is_bounded_to_the_spawn_round() {
        let me = std::env::current_exe().unwrap();
        let mut c = comp("desktop", Desired::Running, Tier::Surface);
        c.exe = me.file_name().unwrap().to_string_lossy().to_string();
        c.liveness = Liveness::Pipe;
        c.liveness_pipe = Some("BetterDesktop.DefinitelyNoSuchPipe".into());

        let running = process::running_exe_names();
        assert!(
            running.contains(&c.exe.to_ascii_lowercase()),
            "前置：同名进程确实在跑（否则本用例证明不了什么）"
        );

        // ① 稳态（无"刚拉起"上下文）+ 管道不在 ⇒ **必须允许拉起**
        assert!(
            !explicit_start_is_noop(&c, &running, Some(&fresh())),
            "同名旁观者在跑不得让显式 start 变成 no-op —— 否则用户点了没反应"
        );

        // ② 刚由我们拉起、待验证的那一轮 ⇒ no-op（防服务启动窗口里再拉第二个 = 多主）
        let mut st = fresh();
        st.pending_spawn = true;
        assert!(
            explicit_start_is_noop(&c, &running, Some(&st)),
            "『刚拉起待验证』那一轮必须挡住重复拉起"
        );

        // ③ 按声明判活（进程判活的组件）⇒ no-op：它本来就是靠进程名判活的
        c.liveness = Liveness::Process;
        assert!(explicit_start_is_noop(&c, &running, Some(&fresh())));
    }

    /// **只有 panel 是"唤起"**（S5-3 真机缺口的钉子）。
    ///
    /// 这条把例外**钉在类型上**：若日后有人把判据放宽成"有 UI 的都算"或"按需的都算"，
    /// 就会同时破坏 `process` / `tool` "在跑时幂等空操作"这条前提 ⇒ 重新引入多主拉起（S4-3 真机事故）。
    #[test]
    fn only_panels_are_woken_up_never_relaunched() {
        let mut panel = comp("panel", Desired::OnDemand, Tier::Extension);
        panel.component_type = ComponentType::Panel;
        assert!(start_is_wakeup(&panel), "面板靠单实例命名事件唤起");

        // 按需进程（索引引擎）：不是唤起 —— 它没有单实例通道，"再拉一个"就是多主
        let mut on_demand_process = comp("index", Desired::OnDemand, Tier::Infrastructure);
        on_demand_process.component_type = ComponentType::Process;
        assert!(!start_is_wakeup(&on_demand_process));

        // 一次性工具（截图）：靠"先判活"避免叠加，不是靠唤起
        let mut tool = comp("capture", Desired::OnDemand, Tier::Extension);
        tool.component_type = ComponentType::Tool;
        assert!(!start_is_wakeup(&tool));

        // 常驻进程（桌面服务）：在跑时必须幂等空操作
        let mut process = comp("desktop", Desired::Running, Tier::Surface);
        process.component_type = ComponentType::Process;
        assert!(!start_is_wakeup(&process));
    }

    /// on-demand 组件**要经过** `Starting`；`tool` **不经过**（S5-3 的第三个确认项）。
    ///
    /// 规则源自 `arms_crash_detector`：显式 start 给非 tool 组件挂 `pending_spawn`，
    /// 于是"启动中 → 在跑 → （自己退出后）没在跑"；tool 跑完即退，给它显示"启动中"是误导。
    #[test]
    fn on_demand_start_goes_through_starting_but_tools_do_not() {
        let t0 = Instant::now();

        let mut panel = comp("panel", Desired::OnDemand, Tier::Extension);
        panel.component_type = ComponentType::Panel;
        let mut tool = comp("capture", Desired::OnDemand, Tier::Extension);
        tool.component_type = ComponentType::Tool;

        assert!(arms_crash_detector(&panel), "面板启动后要验证存活");
        assert!(!arms_crash_detector(&tool), "tool 跑完即退，不挂待验证标记");

        // 面板被拉起：Starting 的优先级高于 `!monitored` ⇒ 用户看到"启动中…"而不是"没在跑"
        let mut st = fresh();
        st.pending_spawn = true;
        assert_eq!(
            component_state(false, false, Some(&st), t0),
            State::Starting,
            "on-demand 组件被拉起时也要先显示『启动中』"
        );
        // 起好后 ⇒ Running（在跑就是在跑，与是否受监护无关）
        assert_eq!(component_state(true, false, Some(&st), t0), State::Running);
        // 用户自己关掉面板后 ⇒ Stopped（不受监护，没人会再拉它）
        assert_eq!(
            component_state(false, false, Some(&fresh()), t0),
            State::Stopped
        );
    }

    // ───────────────────── S3.5：唤醒前的计时器重置 ─────────────────────

    /// 唤醒后必须**立刻**可重试，而不是接着啃睡前那段退避的剩余时间。
    #[test]
    fn reset_timers_clears_backoff_so_resume_retries_immediately() {
        let sup = Supervisor::new(vec![comp("ghost", Desired::Running, Tier::Surface)]);

        let first = sup.reconcile("test");
        assert_eq!(first.failed, vec!["ghost".to_string()], "未部署 → 记一次失败");

        // 立刻再来一轮：仍在退避窗口内
        let second = sup.reconcile("test");
        assert_eq!(second.waiting, vec!["ghost".to_string()]);

        // 唤醒 → 重置计时器
        assert_eq!(sup.reset_timers(), 1, "一个组件状态被重置");

        // 重置后立刻重试（不再等待）
        let third = sup.reconcile("test");
        assert_eq!(
            third.failed,
            vec!["ghost".to_string()],
            "重置后应立刻重试，而不是继续等睡前的退避"
        );
        assert!(third.waiting.is_empty());
    }

    /// **睡眠冻结不得被误判成崩溃**（四审验收项）。
    ///
    /// `pending_spawn` 的意思是"上一轮拉起、本轮验证活没活过一轮"，而"一轮"跨不过一次睡眠：
    /// 睡眠期间进程被冻结、时钟却走了几小时，醒来后若还留着这个标记，
    /// 就会把"睡眠"读成"启动即崩"，进而记失败、进退避、甚至熔断。
    #[test]
    fn reset_timers_discards_pending_spawn_so_sleep_is_not_a_crash() {
        let sup = Supervisor::new(vec![comp("ghost", Desired::Running, Tier::Surface)]);

        sup.inject_pending_spawn("ghost");
        assert!(sup.pending_spawn("ghost"), "前置：模拟刚拉起、待验证");

        sup.reset_timers();
        assert!(
            !sup.pending_spawn("ghost"),
            "唤醒后必须丢弃待验证标记 —— 否则睡眠会被读成启动即崩"
        );
    }

    /// 反证：**不**重置的话，这个标记确实会被读成一次失败 —— 这就是上面那条守卫要防的事。
    #[test]
    fn stale_pending_spawn_is_read_as_a_crash() {
        let sup = Supervisor::new(vec![comp("ghost", Desired::Running, Tier::Surface)]);
        sup.inject_pending_spawn("ghost");

        let report = sup.reconcile("test");
        assert_eq!(
            report.failed,
            vec!["ghost".to_string()],
            "留着旧标记 → 判成「拉起后没活过一轮」"
        );
        assert!(!sup.pending_spawn("ghost"), "验证过一次就清掉");
    }

    #[test]
    fn reset_timers_on_empty_table_is_zero() {
        let sup = Supervisor::new(vec![]);
        assert_eq!(sup.reset_timers(), 0);
    }

    // ───────────── 暂停守护：两个独立标记、任一存在即暂停 ─────────────

    /// 两个标记都要认，且来源要能区分（日志给运维/调试用，用户菜单只提"暂停组件监护"）。
    ///
    /// 只用 `|=`（任一为真即暂停）就等于两个文件共用一个判据 —— 那正是要保留的能力；
    /// 但**来源**必须分得清，否则日志里永远读不出"这次暂停是谁要的"。
    #[test]
    fn pause_source_recognises_both_flags_and_names_the_origin() {
        assert_eq!(pause_source_from(false, false), None, "两个都无 ⇒ 不暂停");
        assert_eq!(
            pause_source_from(true, false),
            Some(PauseSource::Updater),
            "更新器的标记必须被认"
        );
        assert_eq!(
            pause_source_from(false, true),
            Some(PauseSource::User),
            "用户菜单的标记必须被认 —— 少了它，用户点『暂停监护』会毫无效果"
        );
        // 同时存在：更新器优先报到（它是更硬、更短命的原因），但仍然算暂停
        assert_eq!(
            pause_source_from(true, true),
            Some(PauseSource::Updater)
        );
    }

    /// 两个标记**名字不同、都在留痕目录**：共用文件会让"谁触发的暂停"永远无法从文件本身区分，
    /// 且会引入 race（更新器替换完自己清 → 把用户仍在生效的暂停一起清掉）。
    #[test]
    fn the_two_pause_flags_are_distinct_files() {
        assert_ne!(UPDATER_PAUSE_FLAG, USER_PAUSE_FLAG);
        assert_eq!(UPDATER_PAUSE_FLAG, "watchdog-pause.flag", "更新器的名字**保持原名**（已在部署里）");
        assert_eq!(USER_PAUSE_FLAG, "user-pause.flag");
        assert!(UPDATER_PAUSE_FLAG.ends_with(".flag") && USER_PAUSE_FLAG.ends_with(".flag"));
    }

    // ───────────────────── S3.5：电源策略分派 ─────────────────────

    /// `reinit` / `probe-and-reinit` **必须被如实记为未实现**，而不是假装做了。
    #[test]
    fn resume_policy_reports_unimplemented_actions_as_deferred() {
        let mut desktop = comp("desktop", Desired::Running, Tier::Surface);
        desktop.power.on_resume = ResumeAction::ProbeAndReinit;
        let mut clip = comp("clip", Desired::Running, Tier::Infrastructure);
        clip.power.on_resume = ResumeAction::Reinit;
        let mut plain = comp("plain", Desired::Running, Tier::Extension);
        plain.power.on_resume = ResumeAction::Reconcile;

        let report = Supervisor::new(vec![desktop, clip, plain]).apply_resume_policy();

        assert!(report.deferred.contains(&"desktop:probe-and-reinit".to_string()));
        assert!(report.deferred.contains(&"clip:reinit".to_string()));
        assert!(
            !report.deferred.iter().any(|d| d.starts_with("plain")),
            "reconcile 由 reconcile 本身覆盖，不该被记为未实现"
        );
    }

    /// **一次性工具不能被"启动即退"判成崩溃**：这直接决定"反复按截图热键会不会把截图按坏"。
    #[test]
    fn tools_do_not_arm_the_crash_detector() {
        let mut tool = comp("capture", Desired::OnDemand, Tier::Extension);
        tool.component_type = ComponentType::Tool;
        assert!(
            !arms_crash_detector(&tool),
            "tool 跑完即退是**设计**，不能给它挂上「下一轮验证存活」的标记"
        );
    }

    /// 反向守卫：普通进程必须照旧挂上待验证标记 —— 否则 crash-loop 检测会被整个掏空。
    #[test]
    fn processes_still_arm_the_crash_detector() {
        for kind in [
            ComponentType::Process,
            ComponentType::Panel, // 面板关闭即退，但它不是 tool：等待用户关闭是正常生命周期
        ] {
            let mut c = comp("x", Desired::Running, Tier::Extension);
            c.component_type = kind;
            assert!(arms_crash_detector(&c), "{kind:?} 必须继续被验证存活");
        }
    }

    /// 显式 `start` 一个**已在跑**的 tool 仍应是幂等 `Unchanged`（不重复拉起第二个截图进程）。
    #[test]
    fn starting_an_already_running_component_is_unchanged() {
        let me = std::env::current_exe().unwrap();
        let mut c = comp("self", Desired::OnDemand, Tier::Extension);
        c.exe = me.file_name().unwrap().to_string_lossy().to_string();

        let sup = Supervisor::new(vec![c]);
        assert_eq!(sup.start("self"), Outcome::Unchanged);
        assert!(!sup.pending_spawn("self"), "没真正拉起就不该有待验证标记");
    }

    /// `onSuspend` 的三个取值都不得产生意外副作用；未在跑的组件不该被动。
    #[test]
    fn suspend_policy_is_inert_for_components_that_are_not_running() {
        let mut stopper = comp("stopper", Desired::Running, Tier::Extension);
        stopper.power.on_suspend = SuspendAction::Stop;
        let mut notifier = comp("notifier", Desired::Running, Tier::Extension);
        notifier.power.on_suspend = SuspendAction::Notify;
        let freezer = comp("freezer", Desired::Running, Tier::Extension);

        let stopped = Supervisor::new(vec![stopper, notifier, freezer]).apply_suspend_policy();
        assert!(stopped.is_empty(), "没在跑就没什么可停的");
    }
}
