//! 全局热键（S4）—— core 接管"必须活过壳退出"的那些键。
//!
//! # 归属判据（Q2 定案 + S4 细化）
//!
//! > core 独占**"必须活过壳退出"且"消费者本身不常驻"**的热键；壳只注册"随壳生灭"的临时键。
//!
//! 后半句（"消费者本身不常驻"）是 S4 侦察时补上的，否则会把引擎的三键也抢过来 ——
//! 那三键的消费者是**常驻的剪贴板引擎**，它自己注册、自己消费，core 抢过来只会多一层 IPC
//! 且毫无收益。故按此判据清点：
//!
//! | 热键 | 谁该持有 | 本次动作 |
//! |---|---|---|
//! | 截图 `Win+Shift+B` | **core** | ✅ 从 Agent 迁移过来（截图 exe 是**一次性**的，键必须有人常驻持有） |
//! | 剪贴板 `Ctrl+Shift+V/P/Backspace` | 剪贴板引擎（它自己常驻） | 不动 |
//! | 侧板 `Ctrl+Alt+H` | 壳（随壳生灭） | 不动 |
//! | 面板"粘贴回原窗口" | 面板（仅可见期间注册） | 不动 |
//! | 开始菜单 Win 键 | 壳（键盘钩子） | 不动 |
//!
//! # 两条真机踩出来的纪律（**不要"优化"掉**）
//!
//! ## 1. `RegisterHotKey` 必须在**拥有该窗口消息队列的线程**上调用
//!
//! 2026-09-17 真机实录：Agent 从配置轮询的线程池线程里注册，得到**同参数全 `0x580` 的假占用**
//! —— 看起来"键被别人抢了"，实际是注册线程不对。故本模块：
//! - 注册/注销**只在主线程**执行（[`refresh`] 会自校验，非主线程直接拒绝并记 ERROR）；
//! - 后台配置轮询线程通过 `PostMessage` 请求主线程执行，而不是自己去注册。
//!
//! ## 2. 与截图 exe 自己注册**不冲突**，且不需要显式交还
//!
//! 截图 exe 启动时也会注册同一枚键。既有结论（见 `agent/Capabilities/CaptureHotkeyOwner.cs` 头部）：
//! 谁先谁赢，输的一方只记日志不崩 —— 截图 exe 那边已 `catch` 并降级为"仅托盘可用"。
//! 所以 core 拿到键后直接以 `--capture-now` 拉起截图即可，**不需要**先注销再拉起
//! （若真去注销，切换窗口期的"键归了 core、截图却不来"才会出现）。

use std::sync::Mutex;
use std::sync::atomic::{AtomicBool, Ordering};

use windows::Win32::Foundation::HWND;
use windows::Win32::System::Threading::GetCurrentThreadId;
use windows::Win32::UI::Input::KeyboardAndMouse::{
    HOT_KEY_MODIFIERS, RegisterHotKey, UnregisterHotKey,
};
use windows::Win32::UI::WindowsAndMessaging::{GetWindowThreadProcessId, WM_APP};

use crate::log;
use betterdesktop_hotkey_spec::HotkeyBinding;
use betterdesktop_hotkey_spec::modifiers::NO_REPEAT;

/// 主线程"请刷新热键"的自定义消息（后台轮询线程 PostMessage 用）。
pub const WM_REFRESH_HOTKEYS: u32 = WM_APP + 2;

/// 截图热键的注册 ID。
///
/// 沿用原 Agent 的 `'MC'`(0x4D43)：与截图 exe 自己的 `'MA'`(0x4D41) 区分开，
/// 真机排障时"这枚键归谁"一眼可辨（迁移动机之一就是保住这个可诊断性）。
const ID_CAPTURE: i32 = 0x4D43;

/// 截图热键配置的轮询粒度：设置里改了热键/开关后，无需重启 core 即生效。
const CONFIG_POLL: std::time::Duration = std::time::Duration::from_secs(10);

/// 功能总开关（与截图 exe、看门狗同源）：关掉截图就不该持键。
const SCREENSHOT_ENABLED_KEY: &str = "extensions.screenshot.enabled";

/// 归 core 的热键。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Action {
    /// 按下即按需拉起截图（`--capture-now`，截完即退）。
    Capture,
}

/// 当前注册状态。
#[derive(Default)]
struct State {
    /// 已注册的绑定（`None` = 当前没有注册任何键）。
    active: Option<(i32, Action, String)>,
    /// 已完成首次刷新（避免"配置没变"逻辑在启动时误跳过）。
    initialized: bool,
}

static STATE: Mutex<State> = Mutex::new(State { active: None, initialized: false });

/// 是否已成功注册截图热键（供 `status` / 诊断用）。
static CAPTURE_REGISTERED: AtomicBool = AtomicBool::new(false);

fn lock() -> std::sync::MutexGuard<'static, State> {
    STATE.lock().unwrap_or_else(|e| e.into_inner())
}

/// 截图热键当前是否由 core 持有。
pub fn capture_registered() -> bool {
    CAPTURE_REGISTERED.load(Ordering::SeqCst)
}

/// 刷新热键注册（**必须主线程调用**，见模块头纪律 1）。
///
/// `force` = 无条件先注销再注册。用于**唤醒后**：热键在睡眠中是否被系统回收不属于可依赖的行为，
/// 强制重注册的代价约 1ms，换来"醒来后热键一定归 core"。
pub fn refresh(reason: &str) {
    refresh_inner(reason, false);
}

/// [`refresh`] 的强制版（唤醒后用）。
pub fn refresh_force(reason: &str) {
    refresh_inner(reason, true);
}

/// "配置未变 ⇒ 空转"的短路判据（**纯函数**，故可单测）。
///
/// # 为什么把它抽出来
///
/// 原先的测试写的是 `assert!(!(!true && unchanged), ...)` —— 那是**恒真式**（化简后恒为 `true`）：
/// 它看起来在验证"force=true 绕过短路"，实际**什么都没验证**（clippy 也如实指出）。
/// 判据提到一个函数里之后，实现与测试钉的是**同一份**逻辑，测试才真的在测东西。
fn should_skip_refresh(force: bool, active_canonical: Option<&str>, desired: &str) -> bool {
    !force && active_canonical == Some(desired)
}

fn refresh_inner(reason: &str, force: bool) {
    let Some(hwnd) = crate::window() else {
        log::warn(format!(
            "hotkeys({reason}): core window is not available yet; nothing registered"
        ));
        return;
    };

    // 纪律 1 的自校验：不在消息队列线程上注册会得到**假占用**（真机实录，见模块头）。
    if !owns_window_thread(hwnd) {
        log::error(format!(
            "hotkeys({reason}): refresh was called from a thread that does not own the window's \
             message queue — refusing, because registering here yields fake ERROR_HOTKEY_ALREADY_REGISTERED(0x580)"
        ));
        return;
    }

    let desired = match resolve_capture_spec() {
        SpecOutcome::Spec(spec) => spec,
        SpecOutcome::Disabled(why) => {
            unregister(hwnd, reason, &why);
            return;
        }
    };

    let Some(binding) = betterdesktop_hotkey_spec::parse(&desired) else {
        // 解析失败**不回退默认键**：那会让"改了配置不生效"变成"改了配置悄悄用了别的键"。
        unregister(hwnd, reason, &format!("hotkey spec is unparsable ({desired})"));
        return;
    };

    {
        let state = lock();
        let active = state.active.as_ref().map(|(_, _, canonical)| canonical.as_str());
        if should_skip_refresh(force, active, &binding.canonical) {
            return; // 配置未变，空转
        }
    }

    unregister(hwnd, reason, "re-registering");
    register(hwnd, &binding, &desired, reason);
}

/// 实际发起 `RegisterHotKey`。
fn register(hwnd: HWND, binding: &HotkeyBinding, spec: &str, reason: &str) {
    // MOD_NOREPEAT：按住不连发。少了它，长按热键会一次性弹出几十个截图进程。
    // 用共享 crate 的 `NO_REPEAT`（裸 u32）而不是 windows crate 的 `MOD_NOREPEAT`（newtype）——
    // 前者能与 `binding.modifiers` 直接按位或，且把"这个位是策略不是用户意图"表达得更清楚。
    let mods = binding.modifiers | NO_REPEAT;

    let ok = unsafe {
        RegisterHotKey(hwnd, ID_CAPTURE, HOT_KEY_MODIFIERS(mods), binding.vk)
    }
    .is_ok();
    if ok {
        let mut state = lock();
        state.active = Some((ID_CAPTURE, Action::Capture, binding.canonical.clone()));
        state.initialized = true;
        drop(state);
        CAPTURE_REGISTERED.store(true, Ordering::SeqCst);
        log::info(format!(
            "hotkeys({reason}): registered 截图热键 {} (id=0x{ID_CAPTURE:X}, spec=\"{spec}\") — 按下即按需拉起截图",
            binding.canonical
        ));
        return;
    }

    let err = unsafe { windows::Win32::Foundation::GetLastError().0 };
    CAPTURE_REGISTERED.store(false, Ordering::SeqCst);
    if err == 0x581 {
        // 已被别的程序（多半是截图 exe 自己，或残留的旧 Agent）持有 —— 不崩、跳过。
        // **注意 0x580 与 0x581 的区别**：0x581 = 真被别人占了；0x580 在"注册线程不对"时也会出现
        // （见模块头纪律 1），故两者必须分别报，否则会把线程问题误诊成冲突。
        log::warn(format!(
            "hotkeys({reason}): 截图热键 {} 已被其它程序注册（0x581），跳过；热键仍由原持有者响应",
            binding.canonical
        ));
    } else {
        log::error(format!(
            "hotkeys({reason}): 截图热键 {} 注册失败 0x{:08X}（不是 0x581 冲突 —— 需排查注册机制/线程）",
            binding.canonical, err
        ));
    }
}

/// 注销（幂等）。`why` 只进日志。
fn unregister(hwnd: HWND, reason: &str, why: &str) {
    let previous = {
        let mut state = lock();
        state.active.take()
    };
    let Some((id, _, canonical)) = previous else {
        return;
    };

    unsafe {
        let _ = UnregisterHotKey(hwnd, id);
    }
    CAPTURE_REGISTERED.store(false, Ordering::SeqCst);
    log::info(format!(
        "hotkeys({reason}): released 截图热键 {canonical}（{why}）"
    ));
}

/// 退出前配对释放（3101 红线）。
pub fn unregister_all(hwnd: HWND) {
    unregister(hwnd, "shutdown", "core 正在退出");
}

/// 处理 `WM_HOTKEY`。返回 `true` = 是本模块的热键且已处理。
pub fn on_hotkey(id: i32) -> bool {
    let action = {
        let state = lock();
        state
            .active
            .as_ref()
            .filter(|(registered, _, _)| *registered == id)
            .map(|(_, action, _)| *action)
    };

    let Some(action) = action else {
        return false;
    };

    match action {
        Action::Capture => launch_capture(),
    }
    true
}

/// 按需拉起截图（一次性模式）。
///
/// **走监护器而不是自己 `CreateProcess`**：core 里"谁该被拉起"只有一个所有者（`supervisor`），
/// 热键只是一个触发源。顺带的好处是 exe 定位与 `--capture-now` 参数都来自 `components.json`
/// 的 `capture` 条目 —— 不在这里硬编码第二个答案。
fn launch_capture() {
    let Some(sup) = crate::supervisor() else {
        log::error("hotkeys: cannot launch capture — the supervisor is not initialized");
        return;
    };

    match sup.start("capture") {
        // 成功路径由 supervisor 记（含 exe 绝对路径）
        crate::supervisor::Outcome::Changed => {}
        crate::supervisor::Outcome::Unchanged => {
            log::info("hotkeys: a capture process is already running; not launching another")
        }
        crate::supervisor::Outcome::GateClosed => {
            log::warn("hotkeys: capture is disabled by its switch; ignoring the hotkey")
        }
        crate::supervisor::Outcome::Failed(e) => {
            log::error(format!("hotkeys: failed to launch capture: {e}"))
        }
    }
}

/// 后台轮询截图热键配置；有变化就 `PostMessage` 请主线程刷新。
///
/// 为什么不由本线程直接注册：见模块头纪律 1。
pub fn start_config_watch(hwnd: HWND) {
    // `HWND` 不是 `Send`，不能直接搬进线程。转成裸地址传递，线程内再还原 ——
    // 这里安全的前提是**线程只用它 `PostMessage`**（该 API 本身线程安全），
    // 绝不在该线程上注册/注销热键（那正是本模块要防的事，见模块头纪律 1）。
    let raw = hwnd.0 as usize;

    let spawned = std::thread::Builder::new()
        .name("bd-core-hotkey-watch".to_string())
        .spawn(move || {
            let hwnd = HWND(raw as *mut std::ffi::c_void);
            let mut last = String::new();
            loop {
                std::thread::sleep(CONFIG_POLL);
                let current = match resolve_capture_spec() {
                    SpecOutcome::Spec(spec) => spec,
                    SpecOutcome::Disabled(why) => format!("<disabled:{why}>"),
                };
                if current == last {
                    continue;
                }
                last = current;
                // 请求主线程执行（本线程不是消息队列线程）
                let posted = unsafe {
                    windows::Win32::UI::WindowsAndMessaging::PostMessageW(
                        hwnd,
                        WM_REFRESH_HOTKEYS,
                        windows::Win32::Foundation::WPARAM(0),
                        windows::Win32::Foundation::LPARAM(0),
                    )
                };
                if posted.is_err() {
                    log::warn("hotkeys: cannot post the config-refresh message to the core window");
                }
            }
        });

    if let Err(e) = spawned {
        log::error(format!("hotkeys: cannot start the config-watch thread: {e}"));
    }
}

/// 配置求值结果。
enum SpecOutcome {
    /// 应注册的规范串。
    Spec(String),
    /// 不该持键（功能关闭 / 热键停用），附原因。
    Disabled(String),
}

/// 解析出应当注册的截图热键规范串。
///
/// 配置来源：`%LOCALAPPDATA%\BetterDesktop\capture\settings.json` → `hotkey.{enabled,modifiers,key}`。
///
/// **这是 core 只读业务配置的一处明确例外**（core 的禁止清单原本只允许 `settings.json` 扁平键 /
/// `components.json` / 留痕 flag）。理由：热键的持有者归 core，而这份配置的**编辑入口**归截图
/// 自己的设置界面 —— 让 core 去写它的配置会制造第二个写者，让截图去注册热键则回到"一次性 exe 持键"
/// 的老问题。故**只读不写**，并把这条例外写在这里与计划里，而不是悄悄扩权。
fn resolve_capture_spec() -> SpecOutcome {
    // 功能总开关（与截图 exe / 旧 Agent 同源）
    if !crate::settings::Settings::load().get_bool(SCREENSHOT_ENABLED_KEY, true) {
        return SpecOutcome::Disabled("扩展中心已停用截图".to_string());
    }

    const FALLBACK_MODS: &str = "Win+Shift";
    const FALLBACK_KEY: &str = "B";

    let Some(path) = capture_settings_path() else {
        return SpecOutcome::Spec(format!("{FALLBACK_MODS}+{FALLBACK_KEY}"));
    };

    let text = match std::fs::read_to_string(&path) {
        Ok(t) => t,
        // 文件缺失是正常路径（用户没动过截图热键设置）→ 用默认值，保证"热键不因配置缺失而消失"
        Err(_) => return SpecOutcome::Spec(format!("{FALLBACK_MODS}+{FALLBACK_KEY}")),
    };
    let text = text.strip_prefix('\u{feff}').unwrap_or(&text);

    let Ok(root) = serde_json::from_str::<serde_json::Value>(text) else {
        log::warn(format!(
            "hotkeys: {} is not valid JSON; falling back to the default capture hotkey",
            path.display()
        ));
        return SpecOutcome::Spec(format!("{FALLBACK_MODS}+{FALLBACK_KEY}"));
    };

    let Some(hotkey) = root.get("hotkey").filter(|v| v.is_object()) else {
        return SpecOutcome::Spec(format!("{FALLBACK_MODS}+{FALLBACK_KEY}"));
    };

    // 明确写 false = 用户主动停用（不是"配置缺失"，不能用默认值覆盖）
    if hotkey.get("enabled").and_then(|v| v.as_bool()) == Some(false) {
        return SpecOutcome::Disabled("截图热键已按配置停用（hotkey.enabled=false）".to_string());
    }

    let mods = hotkey
        .get("modifiers")
        .and_then(|v| v.as_str())
        .map(str::trim)
        .filter(|s| !s.is_empty())
        .unwrap_or(FALLBACK_MODS);
    let key = hotkey
        .get("key")
        .and_then(|v| v.as_str())
        .map(str::trim)
        .filter(|s| !s.is_empty())
        .unwrap_or(FALLBACK_KEY);

    // 截图侧只接受单字符主键（其 `LoadSettings` 把字符直接当 vk）；而规范串用 WPF Key 枚举名，
    // 所以数字要写成 `D1` 这种形式。漏掉这步会让"把热键改成 Win+Shift+1"静默失效。
    let main = match key.chars().count() {
        1 if key.chars().next().is_some_and(|c| c.is_ascii_digit()) => format!("D{key}"),
        _ => key.to_string(),
    };

    SpecOutcome::Spec(if mods.is_empty() {
        main
    } else {
        format!("{mods}+{main}")
    })
}

/// `%LOCALAPPDATA%\BetterDesktop\capture\settings.json`。
fn capture_settings_path() -> Option<std::path::PathBuf> {
    let base = std::env::var("LOCALAPPDATA").ok()?;
    Some(
        std::path::PathBuf::from(base)
            .join("BetterDesktop")
            .join("capture")
            .join("settings.json"),
    )
}

/// 当前线程是否就是该窗口消息队列的拥有者（纪律 1 的自校验）。
fn owns_window_thread(hwnd: HWND) -> bool {
    let mut pid = 0u32;
    let window_thread = unsafe { GetWindowThreadProcessId(hwnd, Some(&mut pid)) };
    window_thread == unsafe { GetCurrentThreadId() }
}

#[cfg(test)]
mod tests {
    use super::*;
    use betterdesktop_hotkey_spec::modifiers;

    /// 归 core 的热键 ID 必须与截图 exe 自己的不同 —— 真机排障靠它区分"这枚键归谁"。
    #[test]
    fn capture_id_is_distinct_from_the_capture_exe_id() {
        assert_eq!(ID_CAPTURE, 0x4D43, "'MC'");
        assert_ne!(ID_CAPTURE, 0x4D41, "不能与截图 exe 的 'MA' 撞号");
    }

    #[test]
    fn unrelated_hotkey_ids_are_not_claimed() {
        assert!(!on_hotkey(0x9999), "不认识的 id 必须不被认领");
    }

    /// 唤醒后的强制刷新必须能通过"配置未变"这道短路 —— 否则睡眠中丢掉的热键永远补不回来。
    #[test]
    fn force_refresh_is_distinguishable_from_a_regular_one() {
        let canonical = "Win+Shift+B";

        // ① 配置未变 + 普通刷新 → 空转。
        assert!(
            should_skip_refresh(false, Some(canonical), canonical),
            "配置未变 → 普通刷新应空转"
        );
        // ② 配置**变了** → 不空转。没有这一条，③ 就没有说服力：判据若恒为真，单独的 force 断言也能过。
        assert!(
            !should_skip_refresh(false, Some("Ctrl+Alt+A"), canonical),
            "配置变了 → 必须重注册"
        );
        // ③ force=true → 绕过短路。
        assert!(
            !should_skip_refresh(true, Some(canonical), canonical),
            "force=true 时必须绕过这道短路"
        );
    }

    /// 截图热键默认值必须能解析，且带 `NO_REPEAT`（按住不连发）。
    #[test]
    fn default_capture_spec_parses_with_no_repeat() {
        let binding = betterdesktop_hotkey_spec::parse("Win+Shift+B").expect("默认热键必须可解析");
        assert_eq!(binding.modifiers, modifiers::WIN | modifiers::SHIFT);
        assert_eq!(binding.vk, 0x42);
        let registered = binding.modifiers | NO_REPEAT;
        assert_ne!(registered, binding.modifiers, "注册时必须叠加 MOD_NOREPEAT");
        assert_eq!(registered & NO_REPEAT, NO_REPEAT);
        assert_eq!(NO_REPEAT, 0x4000, "与 Win32 MOD_NOREPEAT 取值一致");
    }
}
