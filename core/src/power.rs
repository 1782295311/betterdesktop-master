//! 电源事件处理 —— core 职责 G（计划 §6.12 / S3.5）。
//!
//! # 为什么这是 core 的"第一公民职责"而不是补丁
//!
//! 一个 shell 程序如果因为自身设计导致系统无法睡眠、或者睡眠后状态错乱，那是失败。
//! 而这条会**穿透 core 的每一层**：托盘、热键、管道、监护、计划任务、未来的 GPU 用户。
//!
//! | 对象 | 睡眠时会坏在哪 |
//! |---|---|
//! | 监护循环 | 时钟跳变 → 退避/熔断的"相对截止"语义失效（两个方向都会坏，见 [`crate::supervisor::Supervisor::reset_timers`]） |
//! | `Shell_NotifyIconW` 托盘图标 | explorer 重启后丢失（需要 `TaskbarCreated` 重注册） |
//! | `RegisterHotKey` 热键 | 消息队列若重建则丢失 |
//! | 命名管道服务端 | 旧连接失效（实例靠 2s 读超时自愈，见 [`crate::pipe::on_resume`]） |
//! | 独占能力（任务栏/桌面图标钩子） | explorer 重启后失效 |
//! | 子进程 | 被冻结（**仍然存活** —— 不能被误判成崩溃） |
//!
//! # 处理顺序（四审钉死，不可交换）
//!
//! ```text
//! PBT_APMRESUMEAUTOMATIC / PBT_APMRESUMESUSPEND:
//!   ① 重置所有计时器        ← 必须最先，否则唤醒瞬间会有"动作风暴"
//!   ② 重新枚举实际进程 + 对差集 reconcile
//!   ③ 重建管道实例
//!   ④ 重建托盘图标（+ S4 起的热键）
//! ```
//!
//! ①在②之前是硬要求：睡眠期间时间在走，所有退避截止点可能**同时到期**，
//! 于是唤醒瞬间会对一批本该处于退避期的组件并发发起拉起。先重置，再对账。
//!
//! # 禁用清单（计划 §14）
//!
//! core **不得**无条件调用 `SetThreadExecutionState` 或持有 `PowerSetRequest`；
//! 只有用户显式触发的长任务才可临时持有，且**必须带超时**。
//! 本模块因此只**消费**电源事件，从不**改变**电源状态（C11）。

use std::sync::OnceLock;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::{Duration, SystemTime};

use windows::Win32::UI::WindowsAndMessaging::{
    PBT_APMRESUMEAUTOMATIC, PBT_APMRESUMESUSPEND, PBT_APMSUSPEND,
};

use crate::log;

/// 是否处于挂起中（监护循环据此暂停；见 `Supervisor::reconcile` 的早退分支）。
static SUSPENDED: AtomicBool = AtomicBool::new(false);

/// 进入挂起的**墙钟**时刻（用于算真实睡眠时长）。
///
/// 刻意用 `SystemTime`（墙钟）而不是 `Instant`：睡眠期间墙钟**确实**在走，而
/// `Instant`/QPC 在睡眠中是否前进依赖平台实现（这正是 `reset_timers` 存在的理由）。
static SUSPENDED_AT: OnceLock<SystemTime> = OnceLock::new();

/// 是否处于挂起中。
pub fn is_suspended() -> bool {
    SUSPENDED.load(Ordering::SeqCst)
}

/// 处理一条 `WM_POWERBROADCAST`。返回 `true` = 已处理（调用方应回 0 而不是 `DefWindowProc`）。
///
/// 放在这里而不是窗口过程里：窗口过程只管"投递"，"遇到电源事件该做什么"是电源模块的事。
pub fn dispatch(wparam: usize) -> bool {
    match wparam as u32 {
        PBT_APMSUSPEND => {
            on_suspend();
            true
        }
        PBT_APMRESUMEAUTOMATIC => {
            on_resume("automatic");
            true
        }
        PBT_APMRESUMESUSPEND => {
            on_resume("user");
            true
        }
        // 其它电源事件（PBT_APMPOWERSTATUSCHANGE / PBT_POWERSETTINGCHANGE 等）core 不关心：
        // 返回 false 让它走 DefWindowProc，不假装处理过。
        _ => false,
    }
}

/// 挂起前（`PBT_APMSUSPEND`）。
///
/// # 关于"持久化 desired state"
///
/// 四审要求"持久化 desired state，防止睡眠中崩溃丢失"。本实现**不需要额外做这件事**，
/// 原因是架构性的：core **不持有**期望态 —— 它是 `components.json` × `settings.json` 的**纯函数**,
/// 两份都躺在盘上。所以"核心崩了会不会丢意图"这个问题从设计上就不存在。
///
/// 真正会丢的只是**派生态**（失败计数、重启计数），而那些丢了也只影响退避阶梯，
/// 下一次启动从零开始即可。这里记一份快照，用于事后取证（"为什么唤醒后它在跑"）。
pub fn on_suspend() {
    SUSPENDED.store(true, Ordering::SeqCst);
    let _ = SUSPENDED_AT.set(SystemTime::now());

    log::info("power: PBT_APMSUSPEND — supervision paused, holding no power request");
    if let Some(sup) = crate::supervisor() {
        // 快照必须先记（此时还没停任何东西），否则记的是"已经按策略停过"的状态
        sup.log_state_snapshot("suspend");
        let stopped = sup.apply_suspend_policy();
        if !stopped.is_empty() {
            log::info(format!(
                "power(suspend): released {} component(s) declaring onSuspend=stop: {}",
                stopped.len(),
                stopped.join(", ")
            ));
        }
    }
}

/// 唤醒后（`PBT_APMRESUMEAUTOMATIC` / `PBT_APMRESUMESUSPEND`）。
///
/// `reason` 只进日志（`automatic` = 系统自动唤醒 / `user` = 用户触发唤醒）。
pub fn on_resume(reason: &str) {
    SUSPENDED.store(false, Ordering::SeqCst);

    let slept = SUSPENDED_AT
        .get()
        .and_then(|t| SystemTime::now().duration_since(*t).ok());
    match slept {
        Some(d) => log::info(format!(
            "power: resumed ({reason}) after ~{} — starting the resume chain",
            humanize(d)
        )),
        None => log::info(format!(
            "power: resumed ({reason}) with no recorded suspend time"
        )),
    }

    // ① 重置计时器（**必须最先**）
    match crate::supervisor() {
        Some(sup) => {
            let count = sup.reset_timers();
            log::info(format!(
                "power(resume): reset {count} supervision timer(s) before reconciling"
            ));
            // ② 重新枚举 + 对差集 reconcile（内部就是"先看实际态、只补真缺的"）
            sup.reconcile("resume");
            sup.log_state_snapshot("resume");
            // 按组件声明的 onResume 做额外动作（restart / notify / 以及如实记为未实现的 reinit）
            sup.apply_resume_policy();
        }
        None => log::error(
            "power(resume): supervisor is not initialized; components will not be reconciled",
        ),
    }

    // ③ 重建管道实例（陈旧连接靠 2s 读超时自愈；此处只做代数推进 + 取证）
    crate::pipe::on_resume();

    // ④ 重建托盘图标。
    //    唤醒本身通常不影响托盘，但 explorer 重启会丢掉它（那条走 TaskbarCreated）。
    //    这里重加一次是幂等的、代价约 1ms，换来"醒来托盘图标一定在"。
    crate::heal_tray_icon("resume");

    // ⑤ 重建全局热键。
    //    用**强制**刷新而不是普通刷新：热键在睡眠中是否被系统回收不属于可依赖的行为，
    //    而普通刷新会因为"配置没变"直接空转，那样"醒来后热键没了"就永远补不回来。
    //    强制重注册的代价约 1ms。必须在主线程 —— `power::dispatch` 由窗口过程调用，满足此约束。
    crate::hotkeys::refresh_force("resume");

    log::info("power(resume): resume chain complete");
}

/// 人类可读的时长（只用于日志）。
fn humanize(d: Duration) -> String {
    let s = d.as_secs();
    if s >= 3600 {
        format!("{}h{}m", s / 3600, (s % 3600) / 60)
    } else if s >= 60 {
        format!("{}m{}s", s / 60, s % 60)
    } else {
        format!("{s}s")
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn starts_out_not_suspended() {
        assert!(!is_suspended());
    }

    /// 未知电源事件必须**不被认领**（返回 false），让窗口过程走 `DefWindowProc`。
    /// 假装处理过会吞掉系统事件。
    #[test]
    fn unrelated_power_events_are_not_claimed() {
        assert!(!dispatch(0x000A)); // PBT_APMPOWERSTATUSCHANGE
        assert!(!dispatch(0x8013)); // PBT_POWERSETTINGCHANGE
        assert!(!dispatch(0));
    }

    /// 挂起 → 唤醒 的往返必须把挂起标志复位（漏复位 = 监护**永久停摆**且没有任何报错）。
    #[test]
    fn suspend_then_resume_clears_the_flag() {
        // 直接测标志状态变换，不经过 wnd_proc（那里需要真窗口）
        SUSPENDED.store(true, Ordering::SeqCst);
        assert!(is_suspended());

        on_resume("test");
        assert!(
            !is_suspended(),
            "唤醒后必须复位挂起标志 —— 否则监护永远不再拉起任何组件"
        );
    }

    #[test]
    fn humanize_formats_ranges() {
        assert_eq!(humanize(Duration::from_secs(45)), "45s");
        assert_eq!(humanize(Duration::from_secs(150)), "2m30s");
        assert_eq!(humanize(Duration::from_secs(7300)), "2h1m");
    }

    /// 挂起标志为真时，监护必须**完全不动手**（睡眠中发起 CreateProcessW 行为不可依赖）。
    #[test]
    fn suspended_supervision_does_nothing() {
        use crate::components::{Component, ComponentType, Desired, PowerPolicy, Tier};

        let c = Component {
            name: "ghost".into(),
            label: "ghost".into(),
            exe: "definitely-not-deployed-ghost.exe".into(),
            desired: Desired::Running,
            tier: Tier::Surface,
            component_type: ComponentType::Process,
            power: PowerPolicy::default(),
            gate: None,
            args: None,
            stop_flag: None,
            liveness: crate::components::Liveness::Process,
            liveness_pipe: None,
            remove_at: None,
        };
        let sup = crate::supervisor::Supervisor::new(vec![c]);

        SUSPENDED.store(true, Ordering::SeqCst);
        let report = sup.reconcile("suspended-test");
        assert_eq!(
            report,
            crate::supervisor::Report::default(),
            "挂起期间不得有任何动作（连「未部署」的失败都不该记）"
        );

        // 反向：复位后同一个组件立刻会被尝试 —— 证明上面的静默确实来自挂起标志，
        // 而不是因为组件恰好没被监护（否则这条测试会在实现改坏后依旧绿）
        SUSPENDED.store(false, Ordering::SeqCst);
        let report = sup.reconcile("awake-test");
        assert!(!report.failed.is_empty(), "醒来后必须重新尝试");
    }
}
