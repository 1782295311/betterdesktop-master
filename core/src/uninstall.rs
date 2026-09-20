//! 「卸载 BetterDesktop」：core 托盘菜单里**唯一一个会终结 core 自己**的动作。
//!
//! # 为什么它不走 CLI（与其它菜单项的分工不同）
//!
//! 其它业务动作是"core 指挥别人干活"（见 [`crate::cli`]）。卸载不是：
//! `uninstall-betterdesktop.ps1` 的第一步就是**停掉所有组件、并删掉 core 的计划任务** ——
//! 它要停的，正是 core 自己。所以这条链的形态是"**core 主动交出控制权**"：
//!
//! 这是登记在案的**刻意例外**（`docs/known-exceptions.md` #1）：改经 CLI 会形成
//! "core 派发 CLI → CLI 停 core"的循环依赖。
//!
//! ```text
//! ① 写 user-pause.flag   （立刻停止监护）
//! ② 异步拉起卸载脚本     （绝不等待）
//! ③ 退出 core
//! ```
//!
//! # ① 不是保险，是必需
//!
//! core 是**监护者**。脚本来删文件时若 core 还在跑，它会在删除窗口里把"刚落线的组件"
//! 当成"掉线了"**复活** —— 而那些可执行文件正在被删。这与 `docs/defensive-patterns.md`
//! 第九节「替换被守护的二进制：先停守护者」是同一类问题，只是对象从"替换"变成"删除"。
//!
//! 所以写不进暂停标记时**中止卸载**：明知会打架还动手，比不动手更糟。
//!
//! 用的是 [`crate::supervisor::USER_PAUSE_FLAG`] 而不是更新器那个 ——
//! 语义恰好就是"**用户显式要求的暂停**"，与更新器的 `watchdog-pause.flag` 是两个独立标记。
//!
//! # ② 绝不等待
//!
//! 脚本第一步停的就是 core 自己：同步等待等于**先自锁再自杀**
//!（.NET 侧 `ProcessBridge.StartPowerShellScript` 的注释记过同款坑）。

use std::path::{Path, PathBuf};

use windows::core::PCWSTR;
use windows::Win32::Foundation::HWND;
use windows::Win32::UI::WindowsAndMessaging::{
    IDOK, MB_ICONWARNING, MB_OKCANCEL, MessageBoxW,
};

/// 卸载脚本的文件名（**跨进程契约**）。
///
/// 必须与 `scripts/publish.ps1` 的必检清单、`.NET tray` 的 `AppPaths.UninstallScript`
/// 逐字一致 —— 对不上就是"点了菜单只弹一句找不到脚本"。
pub const UNINSTALL_SCRIPT: &str = "uninstall-betterdesktop.ps1";

/// 定位卸载脚本（**纯函数**，故可单测）。
///
/// 顺序与仓库既有组件定位链一致：**进程目录 → 安装根**。
///
/// # 为什么刻意**不**回退到 `%LOCALAPPDATA%\BetterDesktop`
///
/// 那是**数据目录**。卸载脚本是**程序文件**：从数据目录里翻出一个"可能是旧版本留下的"脚本
/// 去删当前程序，属于最危险的那种"看起来很贴心"的回退 —— 旧脚本删新程序，或者更糟，
/// 删到别的地方。宁可如实报"未找到"，也不猜。
///
/// （登记为刻意的非目标：`docs/known-exceptions.md` #7 —— 防止后人"顺手补全"这条回退。）
pub fn locate_script(process_dir: Option<&Path>, install_root: Option<&Path>) -> Option<PathBuf> {
    for dir in [process_dir, install_root].into_iter().flatten() {
        let candidate = dir.join(UNINSTALL_SCRIPT);
        if candidate.is_file() {
            return Some(candidate);
        }
    }
    None
}

/// 发起动作的进程所在目录。
fn process_dir() -> Option<PathBuf> {
    std::env::current_exe()
        .ok()?
        .parent()
        .map(Path::to_path_buf)
}

/// `powershell.exe` 的绝对路径。
///
/// 不靠 `PATH` 解析：这是一个**删程序文件**的动作，不该受"用户机器上的 PATH 长什么样"影响。
/// 只有 `%SystemRoot%` 缺失（几乎不可能）才回退到裸名字。
fn powershell_path() -> PathBuf {
    std::env::var("SystemRoot")
        .map(|root| PathBuf::from(root).join(r"System32\WindowsPowerShell\v1.0\powershell.exe"))
        .unwrap_or_else(|_| PathBuf::from("powershell.exe"))
}

/// 二次确认。返回用户是否点了「确定」。
///
/// 文案抄自 .NET tray 的既有实现并保持同义 —— 两个入口说同一件事，
/// 用户不会因为"从哪个图标点的卸载"而看到不同的后果描述。
fn confirm(hwnd: HWND) -> bool {
    let body = "将注销右键扩展与系统菜单包、清除开机自启、还原任务栏与桌面图标，并删除程序文件。\n\
                用户设置与剪贴板数据默认保留。\n\n\
                过程中桌面与任务栏会闪一下（explorer 重启），属正常现象。";
    let body = crate::tray::to_wide(body);
    let title = crate::tray::to_wide("卸载 BetterDesktop");
    unsafe {
        MessageBoxW(
            hwnd,
            PCWSTR(body.as_ptr()),
            PCWSTR(title.as_ptr()),
            MB_OKCANCEL | MB_ICONWARNING,
        ) == IDOK
    }
}

/// 执行卸载流程。返回 `true` 表示**调用方应当立刻退出 core**。
///
/// 返回 `false` 的每一种情形都已经把话说清楚了（气泡 / 日志），且**不改变任何状态**
/// —— 唯一的例外是"脚本拉起失败"会把刚写的暂停标记回滚掉（见该分支注释）。
pub fn run(hwnd: HWND) -> bool {
    let install_root = crate::shellmenu::install_root();
    let Some(script) = locate_script(process_dir().as_deref(), install_root.as_deref()) else {
        crate::log::error(format!(
            "uninstall: {UNINSTALL_SCRIPT} not found (looked in the core directory and the install \
             root) — the action did nothing"
        ));
        crate::tray::notify(
            hwnd,
            "卸载已取消",
            &format!("未找到 {UNINSTALL_SCRIPT}（本次构建未随包分发）。"),
        );
        return false;
    };

    if !confirm(hwnd) {
        crate::log::info("uninstall: cancelled by the user");
        return false;
    }

    // ① 立刻停止监护。**必须在拉起脚本之前**：脚本要删的文件正被 core 监护着。
    if let Err(e) = crate::components::write_flag(crate::supervisor::USER_PAUSE_FLAG) {
        crate::log::error(format!(
            "uninstall: cannot write the pause flag ({e}) — ABORTING; without it core would \
             resurrect components while the script deletes them"
        ));
        crate::tray::notify(
            hwnd,
            "卸载已取消",
            "无法写入暂停标记；请先手动退出 core，再运行安装目录下的卸载脚本。",
        );
        return false;
    }
    crate::log::warn(
        "uninstall: supervision PAUSED (user-pause.flag written) — the script takes over from here",
    );

    // ② 异步拉起（绝不等结果）。
    let args = format!(
        "-NoProfile -ExecutionPolicy Bypass -File \"{}\"",
        script.display()
    );
    if !crate::process::spawn_detached(&powershell_path(), Some(&args)) {
        // 【拉起失败要**回滚暂停**】否则用户既没卸载成、监护也没了 ——
        // 而且他不会知道：core 看起来一切正常，只是从此再不管组件了。
        // 静默地改变"监护还在不在"比卸载失败严重得多。
        let _ = crate::components::clear_flag(crate::supervisor::USER_PAUSE_FLAG);
        crate::log::error("uninstall: failed to launch the script — supervision resumed");
        crate::tray::notify(hwnd, "卸载失败", "无法启动卸载脚本（详见日志）。");
        return false;
    }

    crate::log::info(format!(
        "uninstall: dispatched {} — core exits now",
        script.display()
    ));
    true
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 造一个带脚本文件的临时目录。
    fn dir_with_script(tag: &str) -> PathBuf {
        let dir = std::env::temp_dir().join(format!("bdt-uninstall-{tag}"));
        std::fs::create_dir_all(&dir).expect("create temp dir");
        std::fs::write(dir.join(UNINSTALL_SCRIPT), b"").expect("write script");
        dir
    }

    fn dir_without_script(tag: &str) -> PathBuf {
        let dir = std::env::temp_dir().join(format!("bdt-uninstall-{tag}"));
        std::fs::create_dir_all(&dir).expect("create temp dir");
        dir
    }

    /// 两个目录都没有 ⇒ `None`（**如实报"未找到"**，不许猜一个位置出来）。
    #[test]
    fn no_candidate_yields_none() {
        let a = dir_without_script("none-a");
        let b = dir_without_script("none-b");
        assert_eq!(locate_script(Some(&a), Some(&b)), None);
        assert_eq!(locate_script(None, None), None);
    }

    /// 两个目录都有 ⇒ 取**进程目录**：程序文件跟着程序走。
    #[test]
    fn process_dir_wins_over_install_root() {
        let p = dir_with_script("both-proc");
        let r = dir_with_script("both-root");
        assert_eq!(locate_script(Some(&p), Some(&r)), Some(p.join(UNINSTALL_SCRIPT)));
    }

    /// 只有安装根有 ⇒ 用它（这是随包发布的正常位置）。
    #[test]
    fn falls_back_to_install_root() {
        let p = dir_without_script("only-root-proc");
        let r = dir_with_script("only-root-root");
        assert_eq!(locate_script(Some(&p), Some(&r)), Some(r.join(UNINSTALL_SCRIPT)));
    }

    /// 文件名是跨进程契约：与 publish 的必检清单、C# 的 `AppPaths` 逐字一致。
    #[test]
    fn script_name_is_the_cross_process_contract() {
        assert_eq!(UNINSTALL_SCRIPT, "uninstall-betterdesktop.ps1");
        assert!(UNINSTALL_SCRIPT.ends_with(".ps1"));
        // 裸文件名（不含路径分隔符）：由 `locate_script` 负责与目录拼接。
        assert!(!UNINSTALL_SCRIPT.contains('\\') && !UNINSTALL_SCRIPT.contains('/'));
    }
}
