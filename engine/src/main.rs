//! BetterDesktop 剪贴板历史独立引擎入口。
//! 职责（S1 骨架）：单实例 Mutex（1408 变体 A，Local\ 会话级多会话隔离）+
//! 隐藏消息窗口（收 WM_CLIPBOARDUPDATE / WM_HOTKEY / WM_ENDSESSION）+
//! 消息循环 + IPC server（多客户端）。
//! S3 接入捕获（WM_CLIPBOARDUPDATE → listener），S5 接入热键，S2/S4 接入 store 后 WM_ENDSESSION 改为立即 flush。

mod analyzer;
mod capture;
mod dpapi;
mod engine;
mod fingerprint;
mod formats;
mod hotkey;
mod html;
mod ipc;
mod listener;
mod log;
mod model;
mod privacy;
mod rules;
mod settings;
mod store;
mod thumb;

use std::process::ExitCode;
use std::sync::{Arc, OnceLock};

use windows::core::{w, PCWSTR};
use windows::Win32::Foundation::{
    CloseHandle, HANDLE, HINSTANCE, HWND, LPARAM, LRESULT, WAIT_ABANDONED, WAIT_OBJECT_0,
    WAIT_TIMEOUT, WPARAM,
};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::System::Threading::{CreateMutexW, WaitForSingleObject};
use windows::Win32::UI::WindowsAndMessaging::{
    CreateWindowExW, DefWindowProcW, DispatchMessageW, GetMessageW, HMENU, PostQuitMessage,
    RegisterClassW, TranslateMessage, WINDOW_EX_STYLE, WINDOW_STYLE, HWND_MESSAGE, MSG, WNDCLASSW,
    WM_CLIPBOARDUPDATE, WM_DESTROY, WM_ENDSESSION, WM_HOTKEY,
};

/// 隐藏窗口句柄（窗口过程经 OnceLock 访问；HWND 含裸指针不可直接跨线程，存 usize）。
static HIDDEN_WINDOW: OnceLock<usize> = OnceLock::new();

/// 单实例 Mutex：Local\ 会话级（1408 变体 A），固定 GUID 防碰撞。
/// 注意：w! 宏只接受字面量，故 Mutex/窗口类名不抽常量，调用处内联。
fn main() -> ExitCode {
    log::init();
    log::info(format!(
        "engine starting, version {}",
        env!("CARGO_PKG_VERSION")
    ));

    // 1. 单实例（1408）：Local\ 会话级；前实例崩溃留下的 abandoned mutex 由 WaitForSingleObject 接管
    let mutex = match acquire_single_instance() {
        Ok(h) => h,
        Err(_) => {
            log::info("another engine instance is running; exiting");
            return ExitCode::SUCCESS;
        }
    };
    log::info("single-instance mutex acquired");

    // 2. 隐藏消息窗口 + 消息循环（Windows 消息模型：窗口不显示，仅收广播/热键/会话事件）
    if let Err(e) = start_hidden_window() {
        log::error(format!("failed to create hidden window: {e}"));
        let _ = unsafe { CloseHandle(mutex) };
        return ExitCode::FAILURE;
    }

    // 3. 剪贴板监听：广播注册 + 轮询兜底（双通道，防双触发见 listener.rs）
    let listener = listener::ClipboardListener::new();
    let _ = engine::LISTENER.set(Arc::clone(&listener));
    if let Some(&hwnd) = HIDDEN_WINDOW.get() {
        let hwnd = HWND(hwnd as *mut _);
        if let Err(e) = listener.register(hwnd) {
            log::error(format!("AddClipboardFormatListener failed: {e}"));
        }
    }
    listener.start_polling(Arc::new(on_clipboard_update));
    log::info("clipboard listener started (broadcast + 500ms polling)");

    // 3.5 引擎核心：存储 + 配置 + 防抖保存
    let appdata = std::env::var("LOCALAPPDATA").unwrap_or_else(|_| ".".to_string());
    let root = std::path::Path::new(&appdata).join("BetterDesktop");
    // 宿主配置：%APPDATA%\BetterDesktop\settings.json（与宿主/tray/CLI 单一配置源）
    let roaming = std::env::var("APPDATA").unwrap_or_else(|_| ".".to_string());
    let settings_path = std::path::Path::new(&roaming).join("BetterDesktop").join("settings.json");
    engine::init(root, Some(&settings_path));
    engine::start_autosave();
    engine::start_eviction_sweeper();

    // 3.6 全局热键（3101/7413：主线程注册 + 原子 ID + MOD_NOREPEAT；0x581 冲突跳过不崩）
    // 【2026-09-16 配置驱动】键位取 engine::SETTINGS（settings.json 的
    // extensions.clipboard-history.*-hotkey；空串停用），不再硬编码 Ctrl+Shift+V/P/Backspace。
    let hotkeys = hotkey::Hotkeys::new();
    let _ = hotkey::HOTKEYS.set(std::sync::Mutex::new(hotkeys));
    if let Some(&hwnd) = HIDDEN_WINDOW.get() {
        if let Some(h) = hotkey::HOTKEYS.get() {
            // 锁中毒不得静默跳过注册（与注销路径对称；2026-09-12 审计）
            let mut hk = h.lock().unwrap_or_else(|e| e.into_inner());
            let settings = engine::SETTINGS
                .get()
                .and_then(|s| s.lock().ok())
                .map(|g| g.clone())
                .unwrap_or_default();
            hk.register(HWND(hwnd as *mut _), &settings);
        }
    }

    // 4. IPC server（多客户端：宿主 + 面板 + 灵动岛等并发连接）
    ipc::start();

    log::info("engine running; entering message loop");
    let mut msg = MSG::default();
    unsafe {
        loop {
            // 【2026-09-14 修复】GetMessageW 的 BOOL 是**三态**：>0 有消息 / 0 = WM_QUIT / -1 = 出错。
            // 此前的 `while ...as_bool()`（等价 `!= 0`）会把 -1 当成"有消息"，于是在未初始化的 MSG 上
            // 自旋：CPU 打满、消息泵实际已坏而进程"看着还活着"。MSDN 要求 -1 时中止循环。
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
    // 热键配对释放（3101 红线）+ 立即落盘
    if let Some(&hwnd) = HIDDEN_WINDOW.get() {
        if let Some(h) = hotkey::HOTKEYS.get() {
            // 【2026-09-12 审计】热键配对释放不得因锁中毒被跳过（3101 红线：注册/注销必须配对）
            let mut hk = h.lock().unwrap_or_else(|e| e.into_inner());
            hk.unregister(HWND(hwnd as *mut _));
        }
    }
    if let Some(s) = engine::STORE.get() {
        // 退出兜底落盘同样受"载入可信"守卫约束（载入失败时拒写，保护磁盘原文件）
        let store = s.lock().unwrap_or_else(|e| e.into_inner());
        if store.storage_trusted() {
            let _ = store.save();
        }
    }
    let _ = unsafe { CloseHandle(mutex) };
    ExitCode::SUCCESS
}

/// 抢占单实例 Mutex。Ok(handle) = 持有所有权（含接管 abandoned）；Err = 已有实例在运行。
fn acquire_single_instance() -> Result<HANDLE, ()> {
    unsafe {
        let handle = CreateMutexW(
            None,
            true,
            w!("Local\\BetterDesktop.Clipboard.Engine-7f3c9b2e-4d1a-4e5f-9c6b-2d8a1e5f0a3b"),
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

/// 注册消息专用窗口类并创建 HWND_MESSAGE 窗口（不显示、无任务栏）。
fn start_hidden_window() -> windows::core::Result<HWND> {
    unsafe {
        let hmodule = GetModuleHandleW(None)?;
        let hinstance = HINSTANCE(hmodule.0);
        let class_name: PCWSTR = w!("BetterDesktop.Clipboard.Engine.HiddenWindow");
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
            w!("BetterDesktop Clipboard Engine"),
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

/// 剪贴板更新回调（广播 + 轮询汇聚；S4 起走引擎入库管线）。
fn on_clipboard_update() {
    engine::on_clipboard_update();
}

unsafe extern "system" fn wnd_proc(hwnd: HWND, msg: u32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
    match msg {
        WM_CLIPBOARDUPDATE => {
            // 广播通道（双通道之一）：序列号去重 + 抑制消费后执行捕获
            if let Some(l) = engine::LISTENER.get() {
                if l.should_capture() {
                    on_clipboard_update();
                }
            }
            LRESULT(0)
        }
        WM_HOTKEY => {
            // S5：全局热键（wparam = 注册 id；经 HOTKEYS 映射动作）
            engine::handle_hotkey(wparam.0 as i32);
            LRESULT(0)
        }
        WM_ENDSESSION => {
            if wparam.0 != 0 {
                log::info("WM_ENDSESSION: session ending, flushing pending data");
                // 注销/关机前立即落盘（防抖数据不丢失）；锁中毒不得静默跳过（2026-09-12 审计）
                if let Some(s) = engine::STORE.get() {
                    let store = s.lock().unwrap_or_else(|e| e.into_inner());
                    if store.storage_trusted() {
                        let _ = store.save();
                    }
                }
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
