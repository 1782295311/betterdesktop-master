//! BetterDesktop 文件/应用/图标合并索引引擎入口（M1 骨架）。
//!
//! 职责（M1）：单实例 Mutex（1408 变体 A，`Local\` 会话级）+
//! 隐藏消息窗口（HWND_MESSAGE，不显示/无任务栏）+ 消息循环 + IPC server。
//! M2 起接入应用索引，M3 接入文件索引，M4 接入图标。
//!
//! 【为什么现在就要消息循环】M3 的文件增量更新依赖窗口消息（目录变更通知），
//! 且 `shutdown` 方法经 PostMessageW(WM_CLOSE) 触发优雅退出——两者都需要消息泵。

mod appindex;
mod engine;
mod fileindex;
mod icon;
mod ipc;
mod log;
mod model;
mod settings;

use std::process::ExitCode;
use std::sync::OnceLock;

use windows::core::{w, PCWSTR};
use windows::Win32::Foundation::{
    CloseHandle, HANDLE, HINSTANCE, HWND, LPARAM, LRESULT, WAIT_ABANDONED, WAIT_OBJECT_0,
    WAIT_TIMEOUT, WPARAM,
};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::System::Threading::{CreateMutexW, WaitForSingleObject};
use windows::Win32::UI::WindowsAndMessaging::{
    CreateWindowExW, DefWindowProcW, DestroyWindow, DispatchMessageW, GetMessageW, HMENU,
    PostQuitMessage, RegisterClassW, TranslateMessage, WINDOW_EX_STYLE, WINDOW_STYLE, HWND_MESSAGE,
    MSG, WNDCLASSW, WM_CLOSE, WM_DESTROY,
};

/// 隐藏窗口句柄（窗口过程经 OnceLock 访问；HWND 含裸指针不可直接跨线程，存 usize）。
pub static HIDDEN_WINDOW: OnceLock<usize> = OnceLock::new();

/// 补扫轮询粒度（秒）：只是「多久检查一次是否到点」，实际重建间隔由 `engine::RESCAN_INTERVAL_SECS` 决定。
const RESCAN_POLL_SECS: u64 = 30;

/// 空闲检查粒度（秒）。
const IDLE_CHECK_SECS: u64 = 30;

/// 空闲多久即退场（秒）。默认 10 分钟；`BD_INDEX_IDLE_SECS=0` 可关闭（调试期常驻）。
const IDLE_EXIT_SECS: u64 = 600;

/// 构建两套索引并落账（启动时与周期补扫**共用**——两处各写一遍必然漂移）。
fn rebuild_indexes() {
    let settings = engine::settings_snapshot();
    engine::set_building(true);

    let (app_count, app_ms, app_degraded) = appindex::build(&settings);
    let (file_count, file_ms, capped) = fileindex::build(&settings);

    // 降级必须可见（禁止静默）——消费者据 status.degradeReason 展示原因、并据此回退本地实现。
    // 【2026-09-14 修复】此前只汇总了文件索引的「触顶截断」，**应用索引自身的失败没有出口**：
    // 无可用根目录 / 一条都没扫到时 `degraded` 仍是 false，消费者会把空集当权威
    //（表现为"开始菜单/应用列表空了"，而不是回退本地）。现两路原因合并上报。
    let file_reason = capped.then(|| {
        format!(
            "文件索引达到 maxEntries={} 上限已截断（增大 extensions.index.maxEntries 或收窄 scanRoots）",
            settings.max_entries
        )
    });
    // 两路**分开**上报（`list_apps` 只认应用索引、`search_files` 只认文件索引、`status` 取并集）：
    // 合并成一个标志会让"文件索引触顶"连带把应用索引判成降级 → 消费者永久绕过引擎的应用路径。
    engine::set_app_degraded(app_degraded);
    engine::set_file_degraded(file_reason);

    engine::record_build(app_count, file_count, app_ms + file_ms);
    engine::set_building(false);
    log::info(format!(
        "index ready: {app_count} apps ({app_ms}ms) + {file_count} files ({file_ms}ms)"
    ));
}

fn main() -> ExitCode {
    log::init();
    log::info(format!(
        "index engine starting, version {}",
        env!("CARGO_PKG_VERSION")
    ));

    // 1. 单实例（1408）：Local\ 会话级；前实例崩溃留下的 abandoned mutex 由 WaitForSingleObject 接管
    let mutex = match acquire_single_instance() {
        Ok(h) => h,
        Err(()) => {
            log::info("another index engine instance is running; exiting");
            return ExitCode::SUCCESS;
        }
    };
    log::info("single-instance mutex acquired");

    // 2. 隐藏消息窗口（消息泵：shutdown 与 M3 目录变更通知共用）
    if let Err(e) = start_hidden_window() {
        log::error(format!("failed to create hidden window: {e}"));
        let _ = unsafe { CloseHandle(mutex) };
        return ExitCode::FAILURE;
    }

    // 3. 引擎核心状态（配置来自 %APPDATA%\BetterDesktop\settings.json 的 extensions.index 节）
    let roaming = std::env::var("APPDATA").unwrap_or_else(|_| ".".to_string());
    let settings_path = std::path::Path::new(&roaming)
        .join("BetterDesktop")
        .join("settings.json");
    engine::init(Some(&settings_path));
    engine::init_idle_clock(); // 空闲判定基线（必须在启动空闲监视线程之前；否则基线为 0 → 立即误判为空闲）

    // M2/M3：后台构建应用索引（磁盘两源）+ 文件索引（受控目录）。
    // 构建期 status.building=true，消费者据此回退本地；构建完成清空降级标记。
    // 首个 build 是磁盘遍历，可能秒级——放后台线程，不阻塞消息循环。
    std::thread::spawn(rebuild_indexes);

    // M3c：**周期补扫**。索引只在启动构建一次的话，新装/新下载的文件永远搜不到——
    // 用户侧表现为「明明有这文件却搜不到」的漏搜 bug。
    //
    // 【为什么用周期全量重建，而不是 ReadDirectoryChangesW】
    // 受限索引全量重建真机实测约 2.4s（76k 条目），代价可接受；而数百个根上的目录监听句柄 +
    // 事件合并/失效复扫逻辑复杂度高得多。计划 §7 允许二者择一，此处选简单且不会漏事件的那种。
    // 重建读取**当次**配置快照（apply_settings 后下次补扫即生效）。
    std::thread::spawn(|| loop {
        std::thread::sleep(std::time::Duration::from_secs(RESCAN_POLL_SECS));
        if engine::needs_rescan() {
            log::info("periodic rescan triggered");
            rebuild_indexes();
        }
    });

    // 4. IPC server（宿主 / 搜索 / 应用源并发连接）
    ipc::start();

    // 4.5 空闲即退（2026-09-17 内存预算）：无人检索 N 分钟 → 走既有优雅退出路径 request_shutdown()。
    // 复用现成机制的好处：退出走的是与 IPC `shutdown` 完全相同的路径（销毁隐藏窗口 → 退出消息循环），
    // 不新增第二条退出语义。默认 10 分钟；`BD_INDEX_IDLE_SECS` 可覆盖（诊断/验收用）。
    let idle_exit_secs: u64 = std::env::var("BD_INDEX_IDLE_SECS")
        .ok()
        .and_then(|v| v.parse::<u64>().ok())
        .unwrap_or(IDLE_EXIT_SECS);
    if idle_exit_secs > 0 {
        std::thread::spawn(move || loop {
            std::thread::sleep(std::time::Duration::from_secs(IDLE_CHECK_SECS));
            let idle = engine::idle_secs();
            if idle >= idle_exit_secs {
                log::info(format!(
                    "no IPC activity for {idle}s (>= {idle_exit_secs}s); exiting on idle（按需模式：下次检索会把我拉回来）"
                ));
                if !engine::request_shutdown() {
                    log::warn("idle exit: request_shutdown() failed; will retry next tick");
                } else {
                    break;
                }
            }
        });
    } else {
        log::info("idle exit disabled (BD_INDEX_IDLE_SECS=0); engine will stay resident");
    }

    log::info("index engine running; entering message loop");
    let mut msg = MSG::default();
    unsafe {
        loop {
            // 【2026-09-14 修复】GetMessageW 的 BOOL 是三态：>0 有消息 / 0 = WM_QUIT / -1 = 出错。
            // `as_bool()`（等价 `!= 0`）会把 -1 当成"有消息"→ 在未初始化的 MSG 上自旋，
            // 消息泵已坏而进程"看着还活着"。与剪贴板引擎同批修复。
            let ret = GetMessageW(&mut msg, None, 0, 0);
            if ret.0 == 0 {
                break; // WM_QUIT
            }
            if ret.0 == -1 {
                log::error("GetMessageW returned -1 (error); aborting message loop");
                break;
            }
            let _ = TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }
    log::info("message loop exited; shutting down");
    let _ = unsafe { CloseHandle(mutex) };
    ExitCode::SUCCESS
}

/// 抢占单实例 Mutex。Ok(handle) = 持有所有权（含接管 abandoned）；Err = 已有实例在运行。
fn acquire_single_instance() -> Result<HANDLE, ()> {
    unsafe {
        let handle = CreateMutexW(
            None,
            true,
            // 1408 红线：固定名 + GUID 后缀防碰撞；Local\ = 会话级（多会话互不干扰）
            w!("Local\\BetterDesktop.Index.Engine-3a7d1f42-8c56-4b90-9e21-6f4d0c8b7a15"),
        )
        .map_err(|e| {
            log::error(format!("CreateMutexW failed: {e}"));
        })?;
        match WaitForSingleObject(handle, 0) {
            // AbandonedMutex 必须能接管（前实例崩溃留下的锁）
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

/// 注册消息专用窗口类并创建 HWND_MESSAGE 窗口（不显示、无任务栏）。
fn start_hidden_window() -> windows::core::Result<HWND> {
    unsafe {
        let hmodule = GetModuleHandleW(None)?;
        let hinstance = HINSTANCE(hmodule.0);
        let class_name: PCWSTR = w!("BetterDesktop.Index.Engine.HiddenWindow");
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
        RegisterClassW(&wc);
        let hwnd = CreateWindowExW(
            WINDOW_EX_STYLE(0),
            class_name,
            w!("BetterDesktop Index Engine"),
            WINDOW_STYLE(0),
            0,
            0,
            0,
            0,
            HWND_MESSAGE,
            HMENU::default(),
            hinstance,
            None,
        )?;
        let _ = HIDDEN_WINDOW.set(hwnd.0 as usize);
        log::info("hidden message window created");
        Ok(hwnd)
    }
}

unsafe extern "system" fn wnd_proc(hwnd: HWND, msg: u32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
    match msg {
        // IPC `shutdown` 方法经 PostMessageW(WM_CLOSE) 走这里 → 优雅退出
        WM_CLOSE => {
            log::info("WM_CLOSE received; destroying hidden window");
            let _ = unsafe { DestroyWindow(hwnd) };
            LRESULT(0)
        }
        WM_DESTROY => {
            unsafe { PostQuitMessage(0) };
            LRESULT(0)
        }
        _ => unsafe { DefWindowProcW(hwnd, msg, wparam, lparam) },
    }
}
