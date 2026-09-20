//! 剪贴板监听：AddClipboardFormatListener 广播（WM_CLIPBOARDUPDATE）+ 500ms 序列号轮询兜底（Win11 26200 广播不可用场景）。
//! 双通道防双触发：两路都走 `should_capture`，按剪贴板序列号去重（同一序列号只处理一次）。
//!
//! **回环抑制（2026-09-13 重做）**：写回剪贴板后把"刚写进去的内容指纹"登记在这里；
//! 捕获侧读到快照后算同一指纹，命中即跳过入库。详见 `mark_echo` / `take_echo`。

use std::sync::atomic::{AtomicBool, AtomicU32, Ordering};
use std::sync::{Arc, Mutex};
use std::thread;
use std::time::{Duration, Instant};

use windows::Win32::Foundation::HWND;
use windows::Win32::System::DataExchange::{AddClipboardFormatListener, GetClipboardSequenceNumber};

pub const POLL_INTERVAL_MS: u64 = 500;

/// 回环指纹的有效窗口。
///
/// 【为什么是 2 秒 · 2026-09-13】窗口越短，越不会误吞"用户紧接着真的复制了同一内容"。
/// 写回到系统更新剪贴板序列号之间的延迟是毫秒级，2 秒已远超所需；
/// 对比同类实现（TieZ 用 10 秒）我们取更保守的值 —— 我们的场景只需覆盖"写回后立刻被自己捕获"。
const ECHO_WINDOW: Duration = Duration::from_secs(2);

pub struct ClipboardListener {
    /// **回环指纹**：`(内容指纹, 写入时刻)` —— 写回后登记，捕获时比对。
    ///
    /// 【为什么取代"消费式抑制计数" · 2026-09-13】旧实现 `suppress(n)` 要求"登记次数"与
    /// "实际触发的捕获次数"**严丝合缝**，而真实世界不会：
    /// - 写回可能触发 **0 次**捕获（广播失效 / 轮询错过）→ 令牌残留 → 下一次**用户真的复制**被误吞；
    /// - 也可能触发 **多次**（广播 + 轮询双通道、目标应用异步读取后重写剪贴板）→ 令牌不够 →
    ///   我们自己的写回被**当成新内容记一条**（列表里凭空多出刚粘贴过的那条）。
    /// 两种偏差都是"数错就出错"。改为**内容指纹比对**后，判据变成"这次的内容是不是我刚写的那份"，
    /// **与触发次数无关**，天然免疫上述两种错法。
    echo: Mutex<Option<(String, Instant)>>,
    /// 最近处理的剪贴板序列号（防双触发）。
    last_seq: AtomicU32,
    /// 监听开关（扩展中心 enabled 控制）。
    enabled: AtomicBool,
}

impl ClipboardListener {
    pub fn new() -> Arc<Self> {
        Arc::new(ClipboardListener {
            echo: Mutex::new(None),
            last_seq: AtomicU32::new(0),
            enabled: AtomicBool::new(true),
        })
    }

    /// 注册剪贴板广播监听（隐藏窗口创建后调用）。
    pub fn register(&self, hwnd: HWND) -> windows::core::Result<()> {
        unsafe { AddClipboardFormatListener(hwnd) }
    }

    /// 启停监听（扩展中心开关 apply_settings 控制）。
    pub fn set_enabled(&self, enabled: bool) {
        self.enabled.store(enabled, Ordering::Release);
    }

    pub fn is_enabled(&self) -> bool {
        self.enabled.load(Ordering::Acquire)
    }

    /// 登记"刚写回剪贴板的内容指纹"（写回成功后调用）。
    ///
    /// 指纹必须与捕获侧用**同一个**函数算（`ClipboardEntry::fingerprint`，单一真相源）——
    /// 两处各写一套算法会永远比不中，回环抑制等于没有。
    pub fn mark_echo(&self, fingerprint: &str) {
        *self.echo.lock().unwrap_or_else(|e| e.into_inner()) =
            Some((fingerprint.to_string(), Instant::now()));
    }

    /// 清除回环标记（**写回失败时**调用：避免残留标记在窗口内误吞用户随后的真实复制）。
    pub fn clear_echo(&self) {
        *self.echo.lock().unwrap_or_else(|e| e.into_inner()) = None;
    }

    /// 本次捕获的内容是否**就是我们刚写回的那份**（回环）。命中即**一次性消费**。
    ///
    /// 三态行为（每一条都有对应测试）：
    /// - 无登记 / 已过期 → false（正常捕获）；
    /// - 窗口内且**指纹相同** → true 并清除标记（自己的回环，只吞这一次）；
    /// - 窗口内但**指纹不同** → false 且**不消费**（用户真的复制了别的内容；随后自己那次回环仍应被吞）。
    pub fn take_echo(&self, fingerprint: &str) -> bool {
        let mut guard = self.echo.lock().unwrap_or_else(|e| e.into_inner());
        let Some((recorded, at)) = guard.as_ref() else {
            return false;
        };
        if at.elapsed() >= ECHO_WINDOW {
            *guard = None;
            return false;
        }
        if recorded != fingerprint {
            return false;
        }
        *guard = None;
        true
    }

    /// 统一更新入口（广播与轮询共用）：enabled 检查 + 序列号去重。
    ///
    /// 返回 true = 本次应读取快照。**回环判定不在这里** —— 它需要内容指纹，
    /// 而指纹要读完快照才算得出来（见 `engine::on_clipboard_update`）。
    pub fn should_capture(&self) -> bool {
        if !self.is_enabled() {
            return false;
        }
        let seq = unsafe { GetClipboardSequenceNumber() };
        // 【2026-09-14 修复 · 并发 TOCTOU】调用方是**两个线程**：窗口过程（WM_CLIPBOARDUPDATE）
        // 与 500ms 轮询兜底线程。此前用 `load` 判断 + `store` 标记 —— 两者之间是敞开的窗口，
        // 两线程可同时读到旧值、同时放行 → 同一次复制被捕获两遍（copy_count 虚增、双份磁盘写、
        // 面板双刷新）。改用 CAS：只有把 last_seq 从旧值改成本次 seq 的那个线程能放行。
        loop {
            let prev = self.last_seq.load(Ordering::Acquire);
            if seq == prev {
                return false; // 已处理（另一通道先到）
            }
            if self
                .last_seq
                .compare_exchange(prev, seq, Ordering::AcqRel, Ordering::Acquire)
                .is_ok()
            {
                return true;
            }
            // CAS 失败 = 另一线程刚更新过 → 回到循环用最新值重新判定
        }
    }

    /// 启动轮询兜底线程（500ms 序列号比较；广播不可用/漏报时补捕获）。
    pub fn start_polling(self: &Arc<Self>, on_update: Arc<dyn Fn() + Send + Sync>) {
        let listener = Arc::clone(self);
        thread::spawn(move || loop {
            thread::sleep(Duration::from_millis(POLL_INTERVAL_MS));
            if listener.should_capture() {
                on_update();
            }
        });
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 【2026-09-13 回归】自己写回的内容必须被吞掉，且**只吞那一次**。
    #[test]
    fn echo_suppresses_own_write_back() {
        let l = ClipboardListener::new();
        l.mark_echo("fp-own");
        assert!(l.take_echo("fp-own"), "自己写回的指纹应被识别并吞掉");
        assert!(
            !l.take_echo("fp-own"),
            "标记是一次性的：第二次不再吞（否则会吞掉用户后续真实的相同复制）"
        );
    }

    /// 窗口内**别的内容**不得被吞 —— 这是旧计数实现最容易误伤用户的场景。
    #[test]
    fn echo_does_not_swallow_different_content() {
        let l = ClipboardListener::new();
        l.mark_echo("fp-own");
        assert!(!l.take_echo("fp-user"), "内容不同 = 用户真的复制了别的东西，绝不能吞");
        // 不吞也不消费：随后自己那次写回仍应被吞
        assert!(l.take_echo("fp-own"));
    }

    /// 【旧实现的第一种错法】写回触发 0 次捕获 → 标记残留 → 旧计数会误吞用户的下一次复制。
    #[test]
    fn stale_marker_from_zero_trigger_does_not_swallow_user_copy() {
        let l = ClipboardListener::new();
        l.mark_echo("fp-own"); // 写回了，但广播失效 → 没触发任何捕获
        assert!(
            !l.take_echo("fp-user"),
            "残留标记不得误吞用户复制（旧计数实现正是在这里出错）"
        );
    }

    /// 未登记回环时一律不吞（正常捕获路径）。
    #[test]
    fn no_echo_marker_means_no_suppression() {
        let l = ClipboardListener::new();
        assert!(!l.take_echo("anything"));
    }

    #[test]
    fn enabled_gate() {
        let l = ClipboardListener::new();
        assert!(l.is_enabled());
        l.set_enabled(false);
        assert_eq!(l.is_enabled(), false);
    }

    #[test]
    fn seq_dedupe_masks_poll_duplicate() {
        // 双通道去重：同一序列号两次 should_capture 只放行一次
        let l = ClipboardListener::new();
        let first = l.should_capture();
        let second = l.should_capture();
        // 剪贴板可能为空/未变（seq=0 或未变）：两次都拒是合法的（无更新）
        // 关键断言：若第一次放行，第二次必须拒（同 seq）
        if first {
            assert!(!second, "duplicate seq must not capture twice");
        }
    }
}
