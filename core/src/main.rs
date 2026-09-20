//! BetterDesktop Core（Rust）—— 唯一常驻进程。
//!
//! # 职责边界（禁止清单 · 计划 §6.1-B）
//!
//! core **只做六件事**：托盘图标、全局热键、控制管道服务、监护、ShellMenu 快照触发、独占能力维护。
//!
//! core **不得**：
//!   - 加载任何 UI 插件（dock / menu-bar / start-menu / island / quick-note / hotkeys-panel）；
//!   - 渲染任何面板（菜单栏弹层 / Dock / 开始菜单 / 剪贴板面板 / 设置中心）；
//!   - 持有任何业务状态（不缓存应用列表、不存剪贴板内容、不存最近项、不存索引）；
//!   - 读业务数据文件（除 `settings.json` 的扁平键、`components.json`、留痕 flag）；
//!   - 引用任何 C# 程序集或业务包。
//!
//! 新增能力（未来 bd-infer / bd-world / GPU 仲裁）**只加 `components.json` 表项**，不改本文件结构。
//!
//! # 当前实现状态
//!
//! S1（本文件）：单实例 + 隐藏顶层窗口 + 托盘图标 + 组件菜单 + 拉起。
//! S2 起：控制管道（`BetterDesktop.MenuCmd` 服务端 + `@ctl` 形态）、监护/reconcile、热键、ShellMenu 触发。

mod autostart;
mod cli;
mod components;
mod hotkeys;
mod log;
mod pipe;
mod power;
mod process;
mod protocol;
mod security;
mod settings;
mod shellmenu;
mod supervisor;
mod task;
mod tray;
mod uninstall;

use std::process::ExitCode;

use windows::Win32::Foundation::{
    CloseHandle, HINSTANCE, HWND, LPARAM, LRESULT, WAIT_ABANDONED, WAIT_OBJECT_0, WAIT_TIMEOUT,
    WPARAM,
};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::System::Threading::{CreateMutexW, WaitForSingleObject};
use windows::Win32::UI::HiDpi::{
    DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2, SetProcessDpiAwarenessContext,
};
use windows::Win32::UI::Shell::ShellExecuteW;
use windows::Win32::UI::WindowsAndMessaging::{
    CreateWindowExW, DefWindowProcW, DispatchMessageW, GetMessageW, HMENU, MB_ICONINFORMATION,
    MB_OK, MSG, MessageBoxW, PostQuitMessage, RegisterClassW, SW_SHOWNORMAL, TranslateMessage,
    WM_CONTEXTMENU, WM_DESTROY, WM_ENDSESSION, WM_HOTKEY, WM_LBUTTONUP, WM_POWERBROADCAST,
    WM_RBUTTONUP, WNDCLASSW, WS_EX_NOACTIVATE, WS_EX_TOOLWINDOW, WS_POPUP,
};
use windows::core::{PCWSTR, w};

/// 隐藏顶层窗口句柄（窗口过程需要；HWND 含裸指针，用 usize 存以跨线程安全）。
static WINDOW: std::sync::OnceLock<usize> = std::sync::OnceLock::new();

/// 组件表（数据驱动；窗口过程经此取菜单项与拉起目标）。
static COMPONENTS: std::sync::OnceLock<Vec<components::Component>> = std::sync::OnceLock::new();

/// 控制管道的安全描述符（ACL）。进程级持有，供管道服务端创建各实例时取用。
static PIPE_SECURITY: std::sync::OnceLock<security::PipeSecurity> = std::sync::OnceLock::new();

/// 监护器（S3）—— core 里**唯一**的生命周期所有者。
static SUPERVISOR: std::sync::OnceLock<supervisor::Supervisor> = std::sync::OnceLock::new();

thread_local! {
    /// 托盘图标。**只在主线程（消息循环线程）访问** —— 窗口过程的 `TaskbarCreated` 分支与
    /// `power::on_resume`（也由窗口过程驱动）都跑在这里。
    ///
    /// 用 `thread_local` 而不是 `static Mutex`：`TrayIcon` 含 `HWND`/`HICON`，不是 `Send`，
    /// 放进进程级静态需要一句 `unsafe impl`；而它本来就没有跨线程需求，不该为省一个局部变量
    /// 去写不安全断言。
    static TRAY: std::cell::RefCell<Option<tray::TrayIcon>> = const { std::cell::RefCell::new(None) };
}

/// 重新注册托盘图标（`TaskbarCreated` / 唤醒后调用）。
///
/// **必须幂等且不得 panic**：它跑在窗口过程里，抛出就是消息泵崩掉。
pub fn heal_tray_icon(why: &str) {
    TRAY.with(|slot| match slot.borrow_mut().as_mut() {
        Some(icon) => icon.heal(why),
        None => log::warn(format!(
            "cannot re-register the tray icon ({why}): it has not been created yet"
        )),
    });
}

/// 取管道 ACL（管道服务端用）。为 `None` 表示启动期构造失败 —— 但那种情况下 core 已经退出，
/// 所以运行期拿到 `None` 只可能是调用顺序错误，`pipe::start` 会显式拒绝启动。
pub fn pipe_security() -> Option<&'static security::PipeSecurity> {
    PIPE_SECURITY.get()
}

/// 取隐藏顶层窗口句柄（热键注册需要窗口；托盘重建也需要）。
pub fn window() -> Option<HWND> {
    WINDOW.get().map(|raw| HWND(*raw as *mut std::ffi::c_void))
}

/// 取监护器（管道分派与托盘菜单用）。
///
/// **不变量**：`process::spawn_detached` / `process::stop_by_exe_name` 的业务调用者**只有**
/// `supervisor`（`process` 模块内的单测除外）。任何"就地拉起/就地杀掉"的新代码都会让
/// "每个进程有且仅有一个生命周期所有者"这条约束失效 —— 那正是旧世界成环的根因。
pub fn supervisor() -> Option<&'static supervisor::Supervisor> {
    SUPERVISOR.get()
}

fn main() -> ExitCode {
    log::init();
    log::info(format!("core starting, version {}", env!("CARGO_PKG_VERSION")));

    // 每显示器 DPI 感知：托盘菜单坐标才不会被系统二次缩放（失败不致命，老系统没有此 API）。
    unsafe {
        let _ = SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    }

    // 单实例：**唯一常驻者**，第二个实例必须立刻退出而不是并存。
    // 这也是「计划任务每分钟执行 core.exe」能安全幂等的原因 —— 无需 --ensure 之类的专用开关：
    // 已有一个在跑时，本次启动只是抢锁失败并静默退出（< 100ms、约几 MB 峰值）。
    let mutex = match acquire_single_instance() {
        Ok(h) => h,
        Err(()) => {
            log::info("another core instance is running; exiting");
            return ExitCode::SUCCESS;
        }
    };
    log::info("single-instance mutex acquired");

    // 组件表：core 的唯一业务数据（新增能力只加表项）。
    let table = components::load();
    log::info(format!("component table: {} entries", table.len()));
    // 逐条记录 **tier 分派结果 + 开关求值结果** —— "为什么 X 没在跑" 是最高频的排查问题，
    // 启动日志必须直接给出答案（gate 关 = 用户主动关掉的，core 不许拉回）。
    let settings = settings::Settings::load();
    for c in &table {
        let gate_open = match &c.gate {
            Some(key) => settings.get_bool(key, components::GATE_DEFAULT),
            None => true,
        };
        let gate_desc = match &c.gate {
            Some(key) => format!("{key}={gate_open}"),
            None => "no-gate".to_string(),
        };
        let ensure = components::auto_start(c, gate_open);
        let stop = components::must_stop(c, gate_open);
        log::info(format!(
            "  - {} ({}) desired={:?} tier={:?} type={:?} gate={} => ensure={} stop={}",
            c.name, c.exe, c.desired, c.tier, c.component_type, gate_desc, ensure, stop
        ));
        // 非默认电源策略才记（默认 freeze/reconcile/false 是绝大多数，记了只是噪声）
        if c.power != components::PowerPolicy::default() {
            log::info(format!(
                "      power: onSuspend={:?} onResume={:?} keepAwake={:?}",
                c.power.on_suspend, c.power.on_resume, c.power.keep_awake
            ));
        }
    }
    let _ = COMPONENTS.set(table);

    // 安全基线（S2.5）：构造控制管道的安全描述符。
    // **失败即退出**：宁可 core 起不来，也不要用一个"没有 ACL 的管道"对外服务 ——
    // 那等于让同机任意进程都能冒充扩展。这正是"失败不得正常化"的适用场景。
    let current_user = match security::CurrentUser::load() {
        Ok(u) => u,
        Err(e) => {
            log::error(format!("cannot determine current user, refusing to start: {e}"));
            unsafe {
                let _ = CloseHandle(mutex);
            }
            return ExitCode::FAILURE;
        }
    };
    log::info(security::describe(&current_user));
    match security::build_pipe_security(&current_user) {
        Ok(sec) => {
            log::info(sec.summary());
            // 存进静态：生命周期拉到进程级（管道服务端随后从这里取属性）
            let _ = PIPE_SECURITY.set(sec);
        }
        Err(e) => {
            log::error(format!("cannot build pipe ACL, refusing to start: {e}"));
            unsafe {
                let _ = CloseHandle(mutex);
            }
            return ExitCode::FAILURE;
        }
    }

    // 隐藏**顶层**窗口：托盘回调 + 菜单 owner 都需要它（C9：绝不能用 HWND_MESSAGE）。
    let hwnd = match create_hidden_top_level_window() {
        Ok(h) => h,
        Err(e) => {
            log::error(format!("failed to create core window: {e}"));
            unsafe {
                let _ = CloseHandle(mutex);
            }
            return ExitCode::FAILURE;
        }
    };

    // 托盘图标（RAII：退出时自动移除，不留死图标）。
    // 存进 thread_local 而非局部变量：窗口过程需要能在 `TaskbarCreated`（explorer 重启）时重加它。
    TRAY.with(|slot| *slot.borrow_mut() = Some(tray::TrayIcon::add(hwnd, "BetterDesktop")));

    // `TaskbarCreated`（explorer 重启后会广播）—— 不注册它，explorer 一重启托盘图标就永久消失。
    tray::register_taskbar_created();

    // 全局热键（S4）：core 接管"必须活过壳退出且消费者不常驻"的键（当前只有截图 Win+Shift+B）。
    // **必须在主线程首次注册**：RegisterHotKey 要求调用线程拥有该窗口的消息队列
    //（从别的线程注册会得到 0x580 假占用，真机实录见 hotkeys.rs 模块头）。
    hotkeys::refresh("startup");
    hotkeys::start_config_watch(hwnd);

    let table = COMPONENTS.get().cloned().unwrap_or_default();

    // 监护器（S3）：core 现在是**唯一**的生命周期所有者。
    // 必须在 `pipe::start` 之前建好 —— 管道线程一起来就可能收到 `start` 请求，
    // 那时若监护器还没就位，请求会被判成"未初始化"而不是被落实。
    let _ = SUPERVISOR.set(supervisor::Supervisor::new(table.clone()));

    // 控制管道服务端（S2）：常驻在这里，壳在不在都能连通。
    // 启动失败**不退出进程**（托盘仍可用，且退出会造成无意义的崩溃循环），但必须显式记 ERROR ——
    // 管道不通等于所有入口失效，绝不允许它静默。
    if let Err(e) = pipe::start(table) {
        log::error(format!(
            "control pipe FAILED to start: {e} — external entries (bdctl / shell / system menu) cannot reach core"
        ));
    }

    // 启动期对账（S3-2 reconcile）：**先看实际态，再对差集动手**。
    // 这是"core 崩溃重启后不重复拉起已在跑的组件"的落点 —— 与运行期走同一个函数，
    // 因而不存在第二条拉起路径（唯一的替代做法是"启动就拉一遍"，那必然重复拉起）。
    match supervisor() {
        Some(sup) => {
            sup.reconcile("startup");
            if let Err(e) = supervisor::start_loop(sup) {
                log::error(format!(
                    "supervisor loop FAILED to start: {e} — resident components will not be healed"
                ));
            }
        }
        None => log::error("supervisor not initialized; resident components will not be healed"),
    }

    // 计划任务（S3-4）：core 崩溃且用户不再触达任何入口时的兜底。
    // 放后台线程：它要起 schtasks.exe 并等回话（数百毫秒），不该挡住托盘与管道就绪。
    spawn_task_ensure();

    // 系统右键扩展注册状态的**只读**巡检（S4-2 第 1 步）。
    spawn_shellmenu_watch();

    log::info("core running; entering message loop");
    run_message_loop();

    // 退出收尾（顺序要紧：先释放全局输入占用，再摘图标）。
    if let Some(hwnd) = window() {
        hotkeys::unregister_all(hwnd);
    }

    // 图标移除必须在消息循环之后（否则删除通知不会被处理）。
    TRAY.with(|slot| {
        if let Some(icon) = slot.borrow_mut().as_mut() {
            icon.remove();
        }
    });
    log::info("core exited");
    unsafe {
        let _ = CloseHandle(mutex);
    }
    ExitCode::SUCCESS
}

/// 后台确保计划任务存在且配置正确。
///
/// **失败由 core 自己大声报出来**：这条兜底一旦失效，症状是"core 崩了之后再也不回来"，
/// 而那时 core 已经不在、没有任何地方能记这条日志 —— 所以只能在**注册失败发生的那一刻**记。
fn spawn_task_ensure() {
    let spawned = std::thread::Builder::new()
        .name("bd-core-task-ensure".to_string())
        .spawn(|| {
            // 稍等：让托盘与管道先就绪，不与启动路径争 CPU、也不搅乱启动日志的次序
            std::thread::sleep(std::time::Duration::from_secs(2));
            match task::ensure() {
                Ok(task::Ensured::AlreadyCurrent) => {
                    log::info(format!("scheduled task '{}': up to date", task::TASK_NAME))
                }
                Ok(task::Ensured::Created) => log::info(format!(
                    "scheduled task '{}': created (every 5 minutes, current user, no wake)",
                    task::TASK_NAME
                )),
                Ok(task::Ensured::Repaired(reason)) => log::warn(format!(
                    "scheduled task '{}': rebuilt (was stale: {reason})",
                    task::TASK_NAME
                )),
                // 拒绝注册**不是失败**（故 WARN 而不是 ERROR）：这是"这条兜底对当前部署形态不适用"
                // 的如实结论 —— 把系统级任务指向一个会在 build/clean 中消失的目录，
                // 只会留下一条每 5 分钟失败一次的记录，而那时 core 已经不在了、没人会报它。
                Ok(task::Ensured::Skipped(reason)) => log::warn(format!(
                    "scheduled task '{}': NOT registered — {reason}",
                    task::TASK_NAME
                )),
                Err(e) => log::error(format!(
                    "scheduled task '{}' could NOT be ensured ({e}) — \
                     if core crashes while the user touches no entry point, it will not come back",
                    task::TASK_NAME
                )),
            }
        });

    if let Err(e) = spawned {
        log::error(format!("cannot spawn the task-ensure thread: {e}"));
    }
}

/// 系统右键扩展注册状态的巡检 **+ 一次性自动修复**（S4-2 第 2 步）。
///
/// 四条纪律：
/// 1. **只在状态变化时记日志**：每 60 秒刷一行"一切正常"会把真问题淹掉；
/// 2. **core 永不写 `HKCU\Software\Classes`**：修复由 core **触发**、CLI **执行**
///    （见 `shellmenu::trigger_repair_async`）—— 两个写入者正是本仓库吃过的那类事故；
/// 3. 频率沿用原 Agent 的 60 秒 —— 自愈要快，但巡检本身必须廉价（几次注册表读，微秒级）；
/// 4. **触发与否不在这里判**：交给 `shellmenu::RepairGate`（退避 5 分钟 / 连续 3 次不一致 → 熔断）。
///    本线程只做"检查 → 状态变化记日志 → 交决策"三步，保持可读。
fn spawn_shellmenu_watch() {
    let spawned = std::thread::Builder::new()
        .name("bd-core-shellmenu-watch".to_string())
        .spawn(|| {
            let mut last: Option<shellmenu::Registration> = None;
            let mut gate = shellmenu::RepairGate::new();
            loop {
                let state = shellmenu::check();
                if last.as_ref() != Some(&state) {
                    shellmenu::log_state(&state);
                    last = Some(state.clone());
                }
                shellmenu::act(&mut gate, &state);
                std::thread::sleep(std::time::Duration::from_secs(60));
            }
        });

    if let Err(e) = spawned {
        log::error(format!("cannot spawn the shellmenu watch thread: {e}"));
    }
}

/// 抢占单实例 Mutex。`Ok` = 取得所有权（含接管前实例崩溃留下的 abandoned mutex）。
fn acquire_single_instance() -> Result<windows::Win32::Foundation::HANDLE, ()> {
    unsafe {
        let handle = CreateMutexW(
            None,
            true,
            w!("Local\\BetterDesktop.Core.SingleInstance-3c1f0a74-9b6e-4a52-8d21-7e5f4c0b9a10"),
        )
        .map_err(|e| {
            log::error(format!("CreateMutexW failed: {e}"));
        })?;
        match WaitForSingleObject(handle, 0) {
            WAIT_OBJECT_0 | WAIT_ABANDONED => Ok(handle),
            WAIT_TIMEOUT => {
                let _ = CloseHandle(handle);
                Err(())
            }
            other => {
                log::error(format!("WaitForSingleObject unexpected: {}", other.0));
                let _ = CloseHandle(handle);
                Err(())
            }
        }
    }
}

/// 创建隐藏的**顶层**窗口（`WS_POPUP` + 工具窗 + 不激活；从不 `ShowWindow`）。
///
/// 之所以不是 `HWND_MESSAGE`：见 `tray.rs` 顶部 C9 说明 —— 消息专用窗口会让
/// `TrackPopupMenuEx` 静默不弹。
fn create_hidden_top_level_window() -> windows::core::Result<HWND> {
    unsafe {
        let hmodule = GetModuleHandleW(None)?;
        let hinstance = HINSTANCE(hmodule.0);
        let class_name: PCWSTR = w!("BetterDesktop.Core.HiddenTopLevelWindow");
        let wc = WNDCLASSW {
            style: Default::default(),
            lpfnWndProc: Some(wnd_proc),
            cbClsExtra: 0,
            cbWndExtra: 0,
            hInstance: hinstance,
            hIcon: Default::default(),
            hCursor: Default::default(),
            hbrBackground: Default::default(),
            lpszMenuName: PCWSTR::null(),
            lpszClassName: class_name,
        };
        // 类已注册（理论上不会重入，单实例已保证）→ 忽略返回值，以 CreateWindowExW 为准
        RegisterClassW(&wc);

        let hwnd = CreateWindowExW(
            WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            class_name,
            w!("BetterDesktop Core"),
            WS_POPUP,
            0,
            0,
            0,
            0,
            HWND::default(), // 无父窗口 = 顶层窗口（C9 的必要条件）
            HMENU::default(),
            hinstance,
            None,
        )?;
        let _ = WINDOW.set(hwnd.0 as usize);
        log::info("hidden top-level window created");
        Ok(hwnd)
    }
}

fn run_message_loop() {
    let mut msg = MSG::default();
    unsafe {
        loop {
            // GetMessageW 的 BOOL 是三态：>0 有消息 / 0 = WM_QUIT / -1 = 出错。
            // 把 -1 当"有消息"会在未初始化的 MSG 上自旋（CPU 打满、消息泵已坏而进程看着还活着）。
            let ret = GetMessageW(&mut msg, None, 0, 0);
            if ret.0 == 0 {
                break;
            }
            if ret.0 == -1 {
                log::error("GetMessageW returned -1 (error); aborting message loop");
                break;
            }
            let _ = TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }
}

unsafe extern "system" fn wnd_proc(hwnd: HWND, msg: u32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
    // ① 动态注册的消息：值在运行期才知道，必须先于 `match` 判断（`match` 的分支要求编译期常量）。
    if tray::is_taskbar_created(msg) {
        log::info("TaskbarCreated received (explorer restarted); re-registering the tray icon");
        heal_tray_icon("explorer restart");
        return LRESULT(0);
    }

    // ② 电源事件（S3.5 职责 G）。只有被 `power` 认领的事件才回 0；
    //    其余（电源状态变化等）交给 `DefWindowProc` —— 不假装处理过。
    if msg == WM_POWERBROADCAST && power::dispatch(wparam.0) {
        return LRESULT(0);
    }

    // ③ 热键（S4）：WM_HOTKEY 的 wParam 即注册时的 id。
    if msg == WM_HOTKEY && hotkeys::on_hotkey(wparam.0 as i32) {
        return LRESULT(0);
    }

    // ④ 后台轮询发现配置变化 → 请主线程刷新热键（本窗口过程就跑在消息队列线程上）
    if msg == hotkeys::WM_REFRESH_HOTKEYS {
        hotkeys::refresh("配置轮询");
        return LRESULT(0);
    }

    match msg {
        tray::WM_TRAYICON => {
            // 未调用 NIM_SETVERSION → legacy 语义：lParam = 鼠标消息、wParam = 图标 id。
            let event = (lparam.0 as u32) & 0xFFFF;
            match event {
                WM_RBUTTONUP | WM_CONTEXTMENU | WM_LBUTTONUP => {
                    // S5 再把左键改成"切换主程序"的主动作；当前与右键一致，
                    // 避免"点了一下没反应"被误判为故障。
                    let empty: Vec<components::Component> = Vec::new();
                    let table = COMPONENTS.get().unwrap_or(&empty);
                    // 勾选态以 settings.json 的**真实值**为准 ⇒ 打开菜单前重新读一次（不缓存、不猜）
                    let menu_settings = settings::Settings::load();
                    // 暂停勾选只反映**用户自己按的**那一个标记（更新器那个不该显示成用户的选择）。
                    let user_paused = components::flag_exists(supervisor::USER_PAUSE_FLAG);
                    match tray::show_menu(hwnd, table, &menu_settings, user_paused) {
                        Some(tray::MenuAction::Quit) => {
                            log::info("tray menu: quit requested");
                            tray::request_quit(hwnd);
                        }
                        Some(tray::MenuAction::Start(idx)) => {
                            start_component(&table[idx]);
                        }
                        Some(tray::MenuAction::Stop(idx)) => {
                            stop_component(hwnd, &table[idx]);
                        }
                        Some(tray::MenuAction::Restart(idx)) => {
                            restart_component(&table[idx]);
                        }
                        // 开关翻转：**core 自己就是写者**（S5-2a）—— 不绕 CLI、不绕管道。
                        // 翻转后立刻对账一次：监护器虽然有 3 秒 tick，但"点了立刻见效"是用户能感知的差别。
                        Some(tray::MenuAction::Toggle(idx)) => {
                            match tray::apply_toggle(idx) {
                                Ok(next) => {
                                    log::info(format!("tray menu: toggle #{idx} -> {next}"));
                                    if let Some(sup) = supervisor() {
                                        sup.reconcile("tray-toggle");
                                    }
                                }
                                Err(e) => log::error(format!("tray menu: toggle #{idx} failed: {e}")),
                            }
                        }
                        // ── S5-4：动作归属见 tray::show_menu 的文档表 ──
                        Some(tray::MenuAction::TogglePause) => toggle_user_pause(hwnd),
                        Some(tray::MenuAction::SystemIntegration(action)) => {
                            dispatch_system_integration(hwnd, action)
                        }
                        Some(tray::MenuAction::Update(action)) => dispatch_update(hwnd, action),
                        Some(tray::MenuAction::Recovery) => dispatch_recovery(hwnd),
                        Some(tray::MenuAction::ExportDiagnostics) => export_diagnostics(hwnd),
                        Some(tray::MenuAction::OpenLogs) => open_logs(hwnd),
                        Some(tray::MenuAction::ToggleAutoStart) => toggle_autostart(hwnd),
                        Some(tray::MenuAction::About) => show_about(),
                        // S5-5：卸载是**唯一会终结 core 自己**的动作。它成功返回 `true` 意味着
                        // "脚本已上路、监护已暂停"，此时 core 必须立刻退 —— 它占着安装目录里的
                        // 文件名，而脚本第一步要停的正是它（详见 `uninstall` 模块头）。
                        Some(tray::MenuAction::Uninstall) => {
                            if uninstall::run(hwnd) {
                                tray::request_quit(hwnd);
                            }
                        }
                        None => {}
                    }
                }
                _ => {}
            }
            LRESULT(0)
        }
        WM_ENDSESSION => {
            if wparam.0 != 0 {
                log::info("WM_ENDSESSION: session ending");
            }
            LRESULT(0)
        }
        WM_DESTROY => {
            unsafe { PostQuitMessage(0) };
            LRESULT(0)
        }
        _ => unsafe { DefWindowProcW(hwnd, msg, wparam, lparam) },
    }
}

/// 显式启动（菜单「启动」）：**先解除该组件的停止标记**，再交给监护器拉起。
///
/// 顺序要紧：**标记先删**。否则组件起来了、标记还压着 ⇒ 它下次崩了**不会被自愈**，
/// 而用户刚点过"启动"（"启动"与"解除停止"必须是同一件事，不能只做一半）。
/// 删标记失败时仍尝试拉起（用户要的是它跑起来），但必须大声记 —— 否则
/// "跑起来了却不会被自愈"这个状态会静默存在。
fn start_component(c: &components::Component) {
    if let Some(flag) = c.stop_flag.as_deref()
        && let Err(e) = components::clear_flag(flag)
    {
        log::error(format!(
            "cannot clear stop flag '{flag}' for '{}': {e} — it may run now but will NOT be healed if it dies",
            c.name
        ));
    }
    launch_component(c);
}

/// 显式停止（菜单「停止」）。
///
/// # 持久语义（S5-3 定案，用户确认）
///
/// 对**声明了 `stopFlag`** 的组件：写标记 + 停进程 ⇒ 它**不会被监护拉回**，直到用户显式
/// 「启动」（跨 core 重启、跨下次开机都有效）。菜单文案已写明「直到手动启动」，
/// 因为这与"临时停一下"是完全不同的用户预期。
///
/// 对**没有标记**的组件：**拒绝**并用托盘气泡解释。理由：监护会在 ≤3 秒内把它拉回来，
/// 那样"已停止"就是谎话（本仓库反复强调的那类 bug）。正确的路径是它的**设置开关**
/// —— 用户可持久关闭的唯一真相源。
fn stop_component(hwnd: HWND, c: &components::Component) {
    let Some(flag) = c.stop_flag.as_deref() else {
        let key = c.gate.as_deref().unwrap_or("（此组件无开关）");
        log::info(format!(
            "tray menu: refusing to stop '{}' — it has no stop flag; it is governed by switch '{key}'",
            c.name
        ));
        tray::notify(
            hwnd,
            "无法停止",
            &format!(
                "「{}」受监护，停止后会被自动拉回。请用设置开关「{key}」关闭它。",
                c.label
            ),
        );
        return;
    };

    // **先写标记、再停进程**：万一停进程失败，至少"不复活"这条已经成立；
    // 反过来（先停后写）会留下"停了又被拉回"的窗口 —— 而那正是要防的。
    if let Err(e) = components::write_flag(flag) {
        log::error(format!("cannot write stop flag '{flag}' for '{}': {e}", c.name));
        tray::notify(hwnd, "停止失败", &format!("无法写入停止标记：{e}"));
        return;
    }

    let Some(sup) = supervisor() else {
        log::error(format!("cannot stop '{}': supervisor not initialized", c.name));
        return;
    };
    match sup.stop(&c.name) {
        supervisor::Outcome::Changed => {
            log::info(format!("tray menu: stopped '{}' (persistent)", c.name))
        }
        // 幂等：本来就没在跑 —— 标记已写，语义已达成，不是错误
        supervisor::Outcome::Unchanged => {}
        // `stop` 不会返回它（只有 `start` 会）；列出分支是为了让**新增变体时编译器提醒我们**
        supervisor::Outcome::GateClosed => {}
        supervisor::Outcome::Failed(e) => {
            log::error(format!("failed to stop component '{}': {e}", c.name));
            tray::notify(hwnd, "停止失败", &format!("「{}」停止失败：{e}", c.label));
        }
    }
}

/// 显式重启（菜单「重启」）：`stop` + `start`，**不写停止标记**。
///
/// # 为什么必须是一条独立路径，而不是"停止 + 启动"两个菜单项的组合
///
/// 若实现成 `stop_component` + `start_component`，它会**写标记**（持久副作用）——
/// 于是"重启"变成"永久关掉再打开"，与用户意图相反（S5-3 定案明确否掉这一点）。
/// 重启的意图是"重来一次"，所以它自己不碰标记；而它调用的 `start_component` 会**删掉**
/// 可能存在的旧标记 —— 那是收敛（"重启 = 让它重新受监护地跑起来"），不是新增状态。
fn restart_component(c: &components::Component) {
    let Some(sup) = supervisor() else {
        log::error(format!(
            "cannot restart '{}': supervisor not initialized",
            c.name
        ));
        return;
    };
    log::info(format!("tray menu: restarting '{}'", c.name));
    // 先停；失败/没在跑都不是障碍（`stop` 对"本来没在跑"是幂等 Unchanged）
    let _ = sup.stop(&c.name);
    start_component(c);
}

/// 显式拉起组件表里的一条（托盘菜单的"启动 X"，`on-demand` 语义的落地点）。
///
/// **这不是第二条拉起路径**：它只把请求转给监护器，由监护器统一落实 ——
/// "每个进程有且仅有一个生命周期所有者"这条约束就体现在这里。
fn launch_component(c: &components::Component) {
    let Some(sup) = supervisor() else {
        log::error(format!(
            "cannot start '{}': supervisor not initialized",
            c.name
        ));
        return;
    };

    match sup.start(&c.name) {
        // 成功路径由 supervisor 自己记（含 exe 绝对路径与重启序号），此处不重复记
        supervisor::Outcome::Changed => {}
        supervisor::Outcome::Unchanged => {
            log::info(format!("component '{}' is already running", c.name))
        }
        supervisor::Outcome::GateClosed => log::warn(format!(
            "refusing to start '{}': disabled by its switch ({})",
            c.name,
            c.gate.as_deref().unwrap_or("?")
        )),
        // 失败不得正常化 —— supervisor 已记 ERROR，这里补一行含组件名的上下文
        supervisor::Outcome::Failed(e) => {
            log::error(format!("failed to start component '{}': {e}", c.name))
        }
    }
}

// ───────────────────── S5-4：动作归属（同类动作归同类所有者） ─────────────────────
//
// 四档落地方式见 `tray::show_menu` 的文档表：
//   · 组件生命周期 → `supervisor`（上面三个函数）
//   · 设置         → core 自己写 `settings.json`（`tray::apply_toggle`）
//   · 自身持久化   → core 自己写 `HKCU\...\Run`（`toggle_autostart`）
//   · 业务细节     → **只派发** CLI 窄命令（`dispatch_*`；见 `cli.rs`）
//   · 一次性动作   → core 直接做（`open_logs` / `show_about`）

/// 「暂停组件监护」：写 / 删**用户自己的**暂停标记（`user-pause.flag`）。
///
/// # 为什么与更新器的标记分开
///
/// 见 [`supervisor::USER_PAUSE_FLAG`] 的 race 说明。这里只碰用户那一个文件 ——
/// **绝不能顺手清 `watchdog-pause.flag`**：那会打断正在进行的一次更新（更新器会以为
/// "用户把暂停取消了"，接着去拉正被替换的组件）。
fn toggle_user_pause(hwnd: HWND) {
    let flag = supervisor::USER_PAUSE_FLAG;
    let was_paused = components::flag_exists(flag);
    let result = if was_paused {
        components::clear_flag(flag)
    } else {
        components::write_flag(flag)
    };

    match result {
        Ok(()) => {
            let now_paused = !was_paused;
            log::info(format!(
                "tray menu: user supervision pause -> {now_paused} (flag '{flag}')"
            ));
            // 立刻对账一次：暂停时 reconcile 会在暂停分支直接返回（幂等）；
            // 恢复时用户会期望组件**马上**被拉回，而不是等下一个 3 秒 tick。
            if let Some(sup) = supervisor() {
                sup.reconcile("tray-pause-toggle");
            }
            tray::notify(
                hwnd,
                "组件监护",
                if now_paused {
                    "已暂停：组件继续运行，但崩溃/退出后不会被自动拉起"
                } else {
                    "已恢复：组件消失会被自动拉起"
                },
            );
        }
        Err(e) => {
            log::error(format!("cannot toggle user pause flag '{flag}': {e}"));
            tray::notify(hwnd, "操作失败", &format!("无法写入暂停标记：{e}"));
        }
    }
}

/// 系统集成 —— **经 CLI**。core 不写 `HKCU\Software\Classes`（见 `shellmenu.rs` 模块头）。
fn dispatch_system_integration(hwnd: HWND, action: tray::SysIntegrationAction) {
    let verb = action.cli_verb();
    log::info(format!("tray menu: system integration '{verb}' via CLI"));
    dispatch_and_report(hwnd, "系统集成", &format!("--system-integration {verb}"));
}

/// 更新 —— **经 CLI**（core 不碰更新源、不做文件替换）。
fn dispatch_update(hwnd: HWND, action: tray::UpdateAction) {
    let verb = action.cli_verb();
    log::info(format!("tray menu: update '{verb}' via CLI"));
    dispatch_and_report(hwnd, "更新", &format!("--update {verb}"));
}

/// 应急恢复 —— 经 CLI 定位并拉起**独立的**应急程序。
///
/// 用 [`cli::dispatch_async`]（不等结果）而不是 [`dispatch_and_report`]：恢复程序自己是 GUI，
/// 用户看得见它；而"等它结束"可能很久（它会停在用户操作上）。失败仍如实回一条气泡。
fn dispatch_recovery(hwnd: HWND) {
    log::info("tray menu: emergency recovery via CLI");
    if !cli::dispatch_async("--recovery") {
        tray::notify(hwnd, "应急恢复", "无法派发：CLI 未部署或启动失败（详见日志）");
    }
}

/// 导出诊断包 —— 经 CLI（打包逻辑在 C# 侧一份：`DiagnosticBundle`）。
fn export_diagnostics(hwnd: HWND) {
    log::info("tray menu: export diagnostics via CLI");
    dispatch_and_report(hwnd, "诊断包", "--diagnostics-export");
}

/// 打开日志目录 —— **core 直接做**。
///
/// 它是一次性系统动作（`ShellExecute`），**不涉及任何组件生命周期**：
/// 不进 supervisor、不派发 CLI、也不需要 allowlist 条目（`explorer.exe` 不是我们的 exe）。
fn open_logs(hwnd: HWND) {
    let Some(dir) = log_dir() else {
        tray::notify(hwnd, "打开日志目录", "无法定位日志目录（LOCALAPPDATA 未设置）");
        return;
    };
    if !std::path::Path::new(&dir).is_dir() {
        tray::notify(hwnd, "打开日志目录", &format!("日志目录不存在：{dir}"));
        return;
    }
    if !shell_open(&dir) {
        tray::notify(hwnd, "打开日志目录", "无法打开资源管理器（详见日志）");
    }
}

/// 开机自启 —— **core 自己写**（与计划任务同处：都持久化 core 的启动路径）。
fn toggle_autostart(hwnd: HWND) {
    match autostart::toggle() {
        Ok(true) => {
            log::info("tray menu: autostart enabled");
            tray::notify(hwnd, "开机自启", "已开启：随系统启动 BetterDesktop Core");
        }
        Ok(false) => {
            log::info("tray menu: autostart disabled");
            tray::notify(hwnd, "开机自启", "已关闭");
        }
        Err(e) => {
            log::error(format!("tray menu: autostart toggle failed: {e}"));
            tray::notify(hwnd, "开机自启", &format!("失败：{e}"));
        }
    }
}

/// 关于 —— 最小对话框（用户可见的一次性动作，不需要任何通道）。
fn show_about() {
    let body = format!(
        "BetterDesktop Core {}\n\n\
         唯一常驻进程：托盘 / 全局热键 / 组件监护 / 控制管道。\n\
         壳（菜单栏 / Dock / 面板）按需启动、关掉即退；核心能力不随壳消失。\n\n\
         组件目录：{}",
        env!("CARGO_PKG_VERSION"),
        components_dir_display()
    );
    let title = "关于 BetterDesktop Core";

    let body = tray::to_wide(&body);
    let title = tray::to_wide(title);
    unsafe {
        MessageBoxW(
            HWND::default(),
            PCWSTR(body.as_ptr()),
            PCWSTR(title.as_ptr()),
            MB_OK | MB_ICONINFORMATION,
        );
    }
}

/// 后台跑一条 CLI 窄命令，完成后用**托盘气泡**回报结果。
///
/// # 为什么必须开线程
///
/// core 的主线程是**消息循环线程**：同步等一个可能卡住的 CLI 会冻结托盘
/// （菜单点不开、热键不响应，系统还会把它画成"未响应"）。监护循环虽然跑在独立线程、
/// 不会因此停摆，但"点一个菜单卡 30 秒"本身就不可接受。
///
/// 气泡经 `Shell_NotifyIconW` 从后台线程发出：它是 explorer 侧的调用，不要求同线程。
fn dispatch_and_report(hwnd: HWND, title: &str, args: &str) {
    let title = title.to_string();
    let args = args.to_string();
    let hwnd_raw = hwnd.0 as usize;

    let spawned = std::thread::Builder::new()
        .name("bd-core-cli-action".to_string())
        .spawn(move || {
            // 参数按空格切：本进程只发**我们自己的**窄命令（没有带空格的参数），
            // 因此不需要（也不该）实现一套命令行解析。
            let parts: Vec<&str> = args.split(' ').filter(|s| !s.is_empty()).collect();
            let hwnd = HWND(hwnd_raw as *mut std::ffi::c_void);

            match cli::run_and_wait(&parts, std::time::Duration::from_secs(30)) {
                Ok(out) if out.exit_code == 0 => {
                    let body = first_lines(&out.stdout).unwrap_or_else(|| "完成".to_string());
                    tray::notify(hwnd, &title, &body);
                }
                Ok(out) => {
                    let detail = first_lines(&out.stderr)
                        .or_else(|| first_lines(&out.stdout))
                        .unwrap_or_else(|| "无输出".to_string());
                    tray::notify(
                        hwnd,
                        &format!("{title}（失败）"),
                        &format!("退出码 {}：{detail}", out.exit_code),
                    );
                }
                Err(e) => tray::notify(hwnd, &format!("{title}（未执行）"), &e),
            }
        });

    if let Err(e) = spawned {
        log::error(format!("cannot spawn the CLI-action thread: {e}"));
    }
}

/// 取输出的前几行做气泡正文（气泡显示不了整页文本；全是空行时返回 `None`）。
fn first_lines(text: &str) -> Option<String> {
    let lines: Vec<&str> = text
        .lines()
        .map(str::trim)
        .filter(|l| !l.is_empty())
        .take(4)
        .collect();
    if lines.is_empty() {
        None
    } else {
        Some(lines.join("；"))
    }
}

/// 日志目录（与宿主 `FileLogSink` / 托盘同目录）。
fn log_dir() -> Option<String> {
    std::env::var("LOCALAPPDATA")
        .ok()
        .map(|base| format!(r"{base}\BetterDesktop\logs"))
}

/// 组件目录（"关于"里展示用；core 自己所在的目录）。
fn components_dir_display() -> String {
    std::env::current_exe()
        .ok()
        .and_then(|exe| exe.parent().map(|d| d.display().to_string()))
        .unwrap_or_else(|| "(unknown)".to_string())
}

/// 用系统默认程序打开一个路径（`ShellExecuteW`）。
///
/// 返回值约定：`HINSTANCE <= 32` 表示失败（31 = 没有关联程序）—— 这是 Win32 的历史语义，
/// 不能当普通指针判空。
fn shell_open(target: &str) -> bool {
    let file = tray::to_wide(target);
    let result = unsafe {
        ShellExecuteW(
            HWND::default(),
            w!("open"),
            PCWSTR(file.as_ptr()),
            PCWSTR::null(),
            PCWSTR::null(),
            SW_SHOWNORMAL,
        )
    };

    if result.0 as isize <= 32 {
        log::warn(format!(
            "ShellExecuteW failed for '{target}' (code {})",
            result.0 as isize
        ));
        return false;
    }
    true
}

// exe 定位与拉起在 `process.rs`（原语层，无生命周期决策）；
// **谁该被拉起/停掉**由 `supervisor.rs` 唯一决定，其单测随之迁移。
