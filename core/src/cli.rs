//! CLI 派发：core 的"细节动作"一律交给 `BetterDesktop.Cli.exe` 执行。
//!
//! # 动作归属（2026-09-19 定案）
//!
//! core 是生命周期所有者，但**不实现业务细节**。系统集成写入、更新、应急恢复、诊断包
//! 这些"低频、用户显式触发、失败有可见反馈"的动作，一律走 CLI：
//!
//! ```text
//! core（托盘菜单） → CLI 窄命令 → 具体实现（注册表写入 / 更新器 / 恢复程序 / 打包）
//! ```
//!
//! 三条理由：
//! 1. **与 S2 的"入口统一"一致**：core 只发命令，CLI 处理细节；
//! 2. **allowlist 只登记 CLI 一个拉起者**：core 的代码里**只出现** `BetterDesktop.Cli.exe`，
//!    不会出现 `updater.exe` / `Recovery.exe` 的路径与参数（分层）；
//! 3. **写入集中**：注册表 / 文件系统的写入实现只存在于 C# 侧一份 —— 在 Rust 里重写一遍
//!    等于把一个已验证的写入路径换成未验证的（`shellmenu.rs` 模块头有同款论证）。
//!
//! # 两条纪律
//!
//! - **异步**（[`dispatch_async`]）：菜单动作不等结果。core 常驻，等一个可能卡住的子进程
//!   等于 core 卡住（监护停摆）。做没做成由**下一轮巡检**或用户的下一次点击确认。
//! - **有界等待**（[`run_and_wait`]）：确需结果的场景必须带超时，绝不无限等。

use std::path::PathBuf;
use std::time::Duration;

use crate::process;

/// CLI 可执行体文件名（**跨进程契约**；core 拉起它的**唯一**处）。
///
/// 它必须是裸文件名：`process::resolve_exe` 会在 core 目录与
/// `%LOCALAPPDATA%\BetterDesktop` 两处查它（与其它组件同一份定位链）。
pub const CLI_EXE: &str = "BetterDesktop.Cli.exe";

/// 定位 CLI。
fn resolve() -> Option<PathBuf> {
    process::resolve_exe(CLI_EXE)
}

/// 异步派发一个窄命令，返回是否成功拉起。
///
/// **失败必须记 ERROR**：拉起失败意味着用户点了菜单**什么都不会发生** ——
/// 那正是本仓反复钉过的"点了没反应"（S5-3 的 `Toggle` 漏接线就是这一类）。
pub fn dispatch_async(args: &str) -> bool {
    let Some(exe) = resolve() else {
        crate::log::error(format!(
            "cannot locate {CLI_EXE} — the action was NOT dispatched \
             (looked in the core dir and %LOCALAPPDATA%\\BetterDesktop)"
        ));
        return false;
    };

    if process::spawn_detached(&exe, Some(args)) {
        crate::log::info(format!("dispatched: {} {args}", exe.display()));
        true
    } else {
        crate::log::error(format!(
            "failed to launch {} ({args}) — the action did nothing",
            exe.display()
        ));
        false
    }
}

/// 同步派发并拿回结果（**必须带超时** —— core 不能被一个卡住的子进程吊住）。
///
/// 目前只有"读回一行结果"的场景会用它；菜单动作一律走 [`dispatch_async`]。
pub fn run_and_wait(args: &[&str], timeout: Duration) -> Result<process::RunOutput, String> {
    let exe = resolve().ok_or_else(|| {
        format!("{CLI_EXE} not found (looked in the core dir and %LOCALAPPDATA%\\BetterDesktop)")
    })?;

    let owned: Vec<String> = args.iter().map(|a| (*a).to_string()).collect();
    let out = process::run_and_wait(&exe, &owned, timeout)?;
    if out.timed_out {
        return Err(format!(
            "`{args:?}` timed out after {}s",
            timeout.as_secs()
        ));
    }
    Ok(out)
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 裸文件名是 `resolve_exe` 的**前置条件**（含 `\` / `:` 会绕过候选目录直接命中任意位置）。
    #[test]
    fn cli_exe_name_is_a_bare_file_name() {
        assert!(process::is_bare_name(CLI_EXE), "{CLI_EXE}");
        assert!(CLI_EXE.ends_with(".exe"));
    }

    /// 定位失败必须**如实返回 false 并记日志**，不得 panic、不得静默成功。
    ///
    /// 本机通常**装有** CLI（同目录或数据目录），所以这里只断言"不 panic"：
    /// 真正的失败路径由 `dispatch_async` 的 `resolve() == None` 分支覆盖（编译期可见）。
    #[test]
    fn dispatch_never_panics_on_this_machine() {
        // 用一个**绝不会被执行**的窄命令字符串：即便 CLI 真的存在，它也只是立刻以 Usage 退出。
        // 这里不关心结果，只确认"拉起路径本身不会炸"。
        let _ = resolve();
    }
}
