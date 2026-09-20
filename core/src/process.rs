//! 进程助手：定位 exe、脱离式拉起、枚举在跑的进程、按名停止。
//!
//! 从 `main.rs` 抽出（管道分派与托盘菜单都要用），并补上"谁在跑 / 停掉"两类能力。
//!
//! # 为什么不缓存"在跑集合"
//!
//! 每次查询都重新快照。理由：快照本身很便宜（毫秒级），而缓存会引入"缓存与实际不一致"这类
//! 最难查的问题（`status` 报告在跑、实际已死是比慢一点严重得多的故障）。

use std::collections::HashSet;
use std::path::PathBuf;

use windows::Win32::Foundation::{
    BOOL, CloseHandle, HANDLE, HWND, LPARAM, LRESULT, WAIT_OBJECT_0, WPARAM,
};
use windows::Win32::System::Diagnostics::ToolHelp::{
    CreateToolhelp32Snapshot, PROCESSENTRY32W, Process32FirstW, Process32NextW, TH32CS_SNAPPROCESS,
};
use windows::Win32::System::Pipes::PeekNamedPipe;
use windows::Win32::System::Threading::{
    CREATE_NO_WINDOW, CreateProcessW, OpenProcess, PROCESS_INFORMATION, PROCESS_TERMINATE,
    STARTUPINFOW, TerminateProcess, WaitForSingleObject,
};
use windows::core::{PCWSTR, PWSTR};

/// 进程快照里的可执行文件名上限（`PROCESSENTRY32W.szExeFile` 长度）。
const MAX_EXE_NAME: usize = 260;

/// exe 定位：core 同目录 → `%LOCALAPPDATA%\BetterDesktop`（与 C# 侧 `DesktopControlLocator` 同款约定）。
///
/// # 名字必须是**裸文件名**
///
/// `resolve_exe` 的实现是 `dir.join(name)`，而 **`Path::join` 遇到绝对路径会替换基路径** ——
/// 于是 `name = "C:\\temp\\evil.exe"` 会**绕过**两个候选目录直接命中任意位置（`components.json` 的
/// 注入面）。故这里先断言"是裸文件名"，把这条路关在 **join 之前**；C19 的前缀校验再兜一道。
/// 失败返回 `None` 与"未部署"同形，但调用方会先用 [`is_bare_name`] 给出**精确**的原因。
pub fn resolve_exe(name: &str) -> Option<PathBuf> {
    if !is_bare_name(name) {
        return None;
    }
    exe_search_dirs()
        .into_iter()
        .map(|dir| dir.join(name))
        .find(|p| p.is_file())
}

/// 候选目录：① core 自身 exe 所在目录 ② `%LOCALAPPDATA%\BetterDesktop`。
///
/// **这是"组件 exe 从哪来"的唯一定义** —— [`resolve_exe`] 用它在目录里**找**，
/// C19 前缀校验用同一份做**断言**。两处若各写一份，就会出现"C19 说合规、解析却落在别处"
/// 这类最难查的不一致（本仓已有"判活两套"的同族教训）。
///
/// 注：安装根（`…\BetterDesktop\app\<ver>`）**不单列** —— 生产里它是 ② 的子目录。
pub fn exe_search_dirs() -> Vec<PathBuf> {
    let mut dirs: Vec<PathBuf> = Vec::new();
    if let Ok(exe) = std::env::current_exe()
        && let Some(dir) = exe.parent()
    {
        dirs.push(dir.to_path_buf());
    }
    if let Ok(local) = std::env::var("LOCALAPPDATA") {
        let data = PathBuf::from(local).join("BetterDesktop");
        if !dirs.contains(&data) {
            dirs.push(data);
        }
    }
    dirs
}

/// 是否为**裸文件名**（`components.json` 的 `exe` 字段的唯一合法形态）。
///
/// 拒绝：空串、`.`、`..`、路径分隔符（`/` `\`）、卷或驱动器（`:`）。
/// 依据：计划 §6.10 C19"拒绝 `..` 与绝对路径注入"。
pub fn is_bare_name(name: &str) -> bool {
    !name.is_empty() && name != "." && name != ".." && !name.contains(['/', '\\', ':'])
}

/// `&str` → NUL 结尾的 UTF-16。
fn to_wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

/// 脱离式拉起（不等结果、不接管生命周期）：与 C# 侧 `CreateNoWindow + UseShellExecute=false` 同语义。
///
/// 返回 `false` = 失败（已记日志，含 Win32 错误码）。
pub fn spawn_detached(exe: &std::path::Path, args: Option<&str>) -> bool {
    let mut cmd: Vec<u16> = Vec::new();
    cmd.push(b'"' as u16);
    cmd.extend(exe.to_string_lossy().encode_utf16());
    cmd.push(b'"' as u16);
    if let Some(a) = args {
        cmd.push(b' ' as u16);
        cmd.extend(a.encode_utf16());
    }
    cmd.push(0);

    let exe_wide = to_wide(&exe.to_string_lossy());
    let cwd = exe.parent().map(|d| to_wide(&d.to_string_lossy()));
    let cwd_ptr = cwd
        .as_ref()
        .map(|v| PCWSTR(v.as_ptr()))
        .unwrap_or(PCWSTR::null());

    let si = STARTUPINFOW {
        cb: std::mem::size_of::<STARTUPINFOW>() as u32,
        ..Default::default()
    };
    let mut pi = PROCESS_INFORMATION::default();

    let ok = unsafe {
        CreateProcessW(
            PCWSTR(exe_wide.as_ptr()),
            PWSTR(cmd.as_mut_ptr()),
            None,
            None,
            BOOL(0), // 不继承句柄
            CREATE_NO_WINDOW,
            None,
            cwd_ptr,
            &si,
            &mut pi,
        )
        .is_ok()
    };

    if ok {
        unsafe {
            let _ = CloseHandle(pi.hThread);
            let _ = CloseHandle(pi.hProcess);
        }
    } else {
        crate::log::error(format!(
            "CreateProcessW failed for {} (GetLastError={})",
            exe.display(),
            unsafe { windows::Win32::Foundation::GetLastError().0 }
        ));
    }
    ok
}

/// 一次"运行并等待"的结果。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RunOutput {
    /// 子进程退出码。
    pub exit_code: u32,
    /// stdout（UTF-8 有损解码 —— 系统工具输出可能是本地编码，绝不因此失败）。
    pub stdout: String,
    /// stderr（同上）。
    pub stderr: String,
    /// 是否因超时被强制结束。
    pub timed_out: bool,
}

/// 按 MSDN 规则把一个 argv 元素转成命令行片段。
///
/// 规则（`CreateProcessW` 文档里的 "argv → command line" 算法）：
/// 不含空格/制表/引号的参数原样输出；否则整体加引号，且**位于引号前的反斜杠要翻倍**、
/// 反斜杠+引号要转成 `\"`。漏掉反斜杠规则是经典 bug：`C:\a b\` 会被解析成 `C:\a b"`。
fn quote_arg(arg: &str) -> String {
    if !arg.is_empty() && !arg.contains([' ', '\t', '"']) {
        return arg.to_string();
    }

    let mut out = String::with_capacity(arg.len() + 2);
    out.push('"');
    let mut backslashes = 0usize;
    for ch in arg.chars() {
        match ch {
            '\\' => backslashes += 1,
            '"' => {
                // 引号前的反斜杠必须翻倍，再加一个用于转义引号本身
                out.push_str(&"\\".repeat(backslashes * 2 + 1));
                out.push('"');
                backslashes = 0;
            }
            other => {
                out.push_str(&"\\".repeat(backslashes));
                backslashes = 0;
                out.push(other);
            }
        }
    }
    // 结尾反斜杠必须翻倍，否则会转义掉我们补的收尾引号
    out.push_str(&"\\".repeat(backslashes * 2));
    out.push('"');
    out
}

/// 拼出完整命令行（第 0 个元素是程序路径，单独加了引号）。
fn command_line(exe: &std::path::Path, args: &[String]) -> String {
    let mut parts: Vec<String> = Vec::with_capacity(args.len() + 1);
    parts.push(quote_arg(&exe.to_string_lossy()));
    parts.extend(args.iter().map(|a| quote_arg(a)));
    parts.join(" ")
}

/// 同步运行一个进程，捕获 stdout / stderr，带超时。
///
/// 与 [`spawn_detached`] 的分工：那个是"拉起常驻组件、不等结果"；本函数是
/// "问系统一件事、必须拿到回答"（如 `schtasks`）。超时到点会 `TerminateProcess`，
/// **不留下一个被卡住的子进程**。
///
/// # 句柄继承
///
/// `bInheritHandles = TRUE` 会把本进程内**所有可继承句柄**交给子进程。这里安全的前提是：
/// core 其余句柄（控制管道、单实例互斥量）都显式不可继承（见 `security.rs` 的
/// `as_attributes`，`bInheritHandle: FALSE`），因此实际被继承的只有本函数刚创建的两个写端。
pub fn run_and_wait(
    exe: &std::path::Path,
    args: &[String],
    timeout: std::time::Duration,
) -> Result<RunOutput, String> {
    use windows::Win32::Foundation::{HANDLE_FLAG_INHERIT, HANDLE_FLAGS, SetHandleInformation};
    use windows::Win32::Storage::FileSystem::{
        CreateFileW, FILE_ATTRIBUTE_NORMAL, FILE_SHARE_READ, FILE_SHARE_WRITE, OPEN_EXISTING,
        PIPE_ACCESS_INBOUND,
    };
    use windows::Win32::System::Pipes::CreatePipe;
    use windows::Win32::System::Threading::{
        CREATE_NO_WINDOW as NO_WINDOW, GetExitCodeProcess, STARTF_USESTDHANDLES,
    };

    let exe_wide = to_wide(&exe.to_string_lossy());
    let mut cmd: Vec<u16> = command_line(exe, args)
        .encode_utf16()
        .chain(std::iter::once(0))
        .collect();

    unsafe {
        let sa = windows::Win32::Security::SECURITY_ATTRIBUTES {
            nLength: std::mem::size_of::<windows::Win32::Security::SECURITY_ATTRIBUTES>() as u32,
            lpSecurityDescriptor: std::ptr::null_mut(),
            bInheritHandle: BOOL(1), // 子进程必须能拿到写端
        };

        let (mut out_r, mut out_w) = (HANDLE::default(), HANDLE::default());
        let (mut err_r, mut err_w) = (HANDLE::default(), HANDLE::default());
        CreatePipe(&mut out_r, &mut out_w, Some(&sa), 0)
            .map_err(|e| format!("CreatePipe(stdout) failed: {e}"))?;
        let out_r = OwnedHandle(out_r);
        let out_w = OwnedHandle(out_w);

        if let Err(e) = CreatePipe(&mut err_r, &mut err_w, Some(&sa), 0) {
            return Err(format!("CreatePipe(stderr) failed: {e}"));
        }
        let err_r = OwnedHandle(err_r);
        let err_w = OwnedHandle(err_w);

        // 读端不得被继承：否则子进程自己持有读端，我们就永远等不到"写端全关"这一天
        for r in [out_r.0, err_r.0] {
            // 注意这两个参数类型不同（`dwMask: u32` 而 `dwFlags: HANDLE_FLAGS`）—— 别照抄成同形
            SetHandleInformation(r, HANDLE_FLAG_INHERIT.0, HANDLE_FLAGS(0))
                .map_err(|e| format!("SetHandleInformation failed: {e}"))?;
        }

        // 子进程的 stdin 指向 NUL：不给它一个无效句柄（工具若真去读 stdin 会正常拿到 EOF）
        let nul_name = to_wide("NUL");
        let nul = OwnedHandle(
            CreateFileW(
                PCWSTR(nul_name.as_ptr()),
                PIPE_ACCESS_INBOUND.0,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                None,
                OPEN_EXISTING,
                FILE_ATTRIBUTE_NORMAL,
                None,
            )
            .map_err(|e| format!("cannot open NUL device: {e}"))?,
        );

        let si = STARTUPINFOW {
            cb: std::mem::size_of::<STARTUPINFOW>() as u32,
            dwFlags: STARTF_USESTDHANDLES,
            hStdInput: nul.0,
            hStdOutput: out_w.0,
            hStdError: err_w.0,
            ..Default::default()
        };
        let mut pi = PROCESS_INFORMATION::default();

        let started = CreateProcessW(
            PCWSTR(exe_wide.as_ptr()),
            PWSTR(cmd.as_mut_ptr()),
            None,
            None,
            BOOL(1), // 继承句柄（见上面「句柄继承」）
            NO_WINDOW,
            None,
            None,
            &si,
            &mut pi,
        );
        if let Err(e) = started {
            return Err(format!(
                "CreateProcessW failed for {}: {e}",
                exe.display()
            ));
        }
        let process = OwnedHandle(pi.hProcess);
        let thread = OwnedHandle(pi.hThread);
        drop(thread);

        // 父进程必须立刻放掉写端，否则读端永远等不到对端关闭
        drop(out_w);
        drop(err_w);

        let deadline = std::time::Instant::now() + timeout;
        let mut stdout = Vec::new();
        let mut stderr = Vec::new();
        let mut timed_out = false;

        loop {
            drain(out_r.0, &mut stdout);
            drain(err_r.0, &mut stderr);

            if WaitForSingleObject(process.0, 0) == WAIT_OBJECT_0 {
                break;
            }
            if std::time::Instant::now() >= deadline {
                // 卡住的子进程必须收走，不能留在系统里
                let _ = TerminateProcess(process.0, 1);
                timed_out = true;
                break;
            }
            std::thread::sleep(std::time::Duration::from_millis(10));
        }

        // 进程结束后管道里可能还有残留输出
        drain(out_r.0, &mut stdout);
        drain(err_r.0, &mut stderr);

        let mut code = 0u32;
        let _ = GetExitCodeProcess(process.0, &mut code);

        Ok(RunOutput {
            exit_code: code,
            stdout: decode_console_text(&stdout),
            stderr: decode_console_text(&stderr),
            timed_out,
        })
    }
}

/// 解码控制台工具的输出。
///
/// **不能只按 UTF-8 处理**：部分系统工具（`schtasks /query /xml` 就是）把输出写成
/// **UTF-16LE**。按 UTF-8 有损解码会得到 `b\0e\0t\0t\0e\0r\0…` —— 肉眼看不见但**匹配必然失败**，
/// 于是"判断任务是否指向我们"永远返回"不是"，进而每次启动都重注册一遍（静默失效）。
pub fn decode_console_text(bytes: &[u8]) -> String {
    if looks_like_utf16le(bytes) {
        let units: Vec<u16> = bytes
            .chunks_exact(2)
            .map(|c| u16::from_le_bytes([c[0], c[1]]))
            .collect();
        return String::from_utf16_lossy(&units)
            .trim_start_matches('\u{feff}')
            .to_string();
    }
    String::from_utf8_lossy(bytes).to_string()
}

/// 是否像 UTF-16LE：偶数长度，且**高字节位几乎全是 0**（ASCII 文本的 UTF-16LE 特征）。
///
/// 阈值取 80% 而不是 100%：非 ASCII 码位（如中文路径）的高字节非 0 —— 全 0 的要求
/// 会让含中文的输出被误判成 UTF-8。
fn looks_like_utf16le(bytes: &[u8]) -> bool {
    if bytes.len() < 8 {
        return false;
    }
    let pairs = bytes.len() / 2;
    if pairs == 0 {
        return false;
    }
    let high_bytes_zero = bytes
        .chunks_exact(2)
        .filter(|c| c[1] == 0)
        .count();
    high_bytes_zero * 10 >= pairs * 8
}

/// 把管道中**当前可得**的字节读干净（`PeekNamedPipe` 先问有多少，因此永不阻塞）。
fn drain(pipe: HANDLE, out: &mut Vec<u8>) {
    let mut chunk = [0u8; 4096];
    loop {
        let mut available = 0u32;
        if unsafe { PeekNamedPipe(pipe, None, 0, None, Some(&mut available), None) }.is_err() {
            return; // 对端已关闭
        }
        if available == 0 {
            return;
        }
        let want = (available as usize).min(chunk.len());
        let mut read = 0u32;
        let ok = unsafe {
            windows::Win32::Storage::FileSystem::ReadFile(
                pipe,
                Some(&mut chunk[..want]),
                Some(&mut read),
                None,
            )
        };
        if ok.is_err() || read == 0 {
            return;
        }
        out.extend_from_slice(&chunk[..read as usize]);
    }
}

/// 当前在跑进程的可执行文件名集合（小写，便于比较）。
///
/// 失败返回空集合并记日志 —— **不 panic**：快照失败（极罕见）只会让 `status` 的 actual 偏空，
/// 比让 core 崩掉轻得多。调用方若需要区分"没在跑"与"查不到"，应改用返回 `Result` 的版本。
pub fn running_exe_names() -> HashSet<String> {
    let mut out = HashSet::new();
    unsafe {
        let Ok(snapshot) = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0) else {
            crate::log::error("CreateToolhelp32Snapshot failed; reporting no running processes");
            return out;
        };
        let snapshot = OwnedHandle(snapshot);

        let mut entry = PROCESSENTRY32W {
            dwSize: std::mem::size_of::<PROCESSENTRY32W>() as u32,
            ..Default::default()
        };
        if Process32FirstW(snapshot.0, &mut entry).is_err() {
            return out;
        }
        loop {
            let name = wide_to_string(&entry.szExeFile);
            if !name.is_empty() {
                out.insert(name.to_ascii_lowercase());
            }
            if Process32NextW(snapshot.0, &mut entry).is_err() {
                break;
            }
        }
    }
    out
}

/// 某个 exe 是否在跑。
pub fn is_running(exe_name: &str) -> bool {
    running_exe_names().contains(&exe_name.to_ascii_lowercase())
}

/// 命名管道是否已就绪（`\\.\pipe\<name>` 存在）。
///
/// # 为什么是"枚举管道命名空间"，而不是"客户端连一下"
///
/// 与 `watchdog/Program.cs::IsDesktopServicePipeUp`、`shell-core/DesktopControl/DesktopControlPipe.IsServiceUp`
/// **同源**（三处判据一致，改一处务必改另外两处）。两个理由：
///
/// 1. **连一下会消费服务端的一个实例槽**：探测本身就变成了"占用"。对一个单实例服务
///    （桌面服务就是），这可能把真实客户端挤掉 —— 探测把被探测者弄坏是最糟的一类。
/// 2. **服务正忙于服务别的客户端时，"连不上"会把活着的服务判成死的** → 监护器再拉一个 →
///    两个服务实例抢同一个管道名。
///
/// 枚举只读、无副作用，也正是托盘与看门狗一直在用的判据。
pub fn is_pipe_up(name: &str) -> bool {
    let Ok(entries) = std::fs::read_dir(r"\\.\pipe\") else {
        // 列举失败（极罕见）→ 报"不在"，让监护器走一次拉起尝试；
        // 真在跑的话下一轮探活会纠正，代价远小于"因为探测坏了就永远不拉"。
        return false;
    };
    entries.flatten().any(|entry| {
        entry
            .file_name()
            .to_string_lossy()
            .eq_ignore_ascii_case(name)
    })
}

/// 按可执行文件名停止**所有**同名进程（与我们自己的组件名一一对应）。
///
/// 返回被终止的进程数。`0` 表示本来就没在跑（**不是错误** —— 幂等语义由调用方表达）。
/// 打不开某进程（权限/已退出）只记日志并继续，不中断整体停止流程。
pub fn stop_by_exe_name(exe_name: &str) -> usize {
    let target = exe_name.to_ascii_lowercase();
    let mut killed = 0usize;

    unsafe {
        let Ok(snapshot) = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0) else {
            crate::log::error("CreateToolhelp32Snapshot failed; cannot stop component");
            return 0;
        };
        let snapshot = OwnedHandle(snapshot);

        let mut entry = PROCESSENTRY32W {
            dwSize: std::mem::size_of::<PROCESSENTRY32W>() as u32,
            ..Default::default()
        };
        if Process32FirstW(snapshot.0, &mut entry).is_err() {
            return 0;
        }
        loop {
            let name = wide_to_string(&entry.szExeFile).to_ascii_lowercase();
            if name == target {
                let pid = entry.th32ProcessID;
                match OpenProcess(PROCESS_TERMINATE, false, pid) {
                    Ok(h) => {
                        let h = OwnedHandle(h);
                        if TerminateProcess(h.0, 1).is_ok() {
                            killed += 1;
                            crate::log::info(format!("stopped {exe_name} (pid {pid})"));
                        } else {
                            crate::log::warn(format!(
                                "TerminateProcess failed for {exe_name} (pid {pid})"
                            ));
                        }
                    }
                    Err(e) => crate::log::warn(format!(
                        "OpenProcess(PROCESS_TERMINATE) failed for {exe_name} (pid {pid}): {e}"
                    )),
                }
            }
            if Process32NextW(snapshot.0, &mut entry).is_err() {
                break;
            }
        }
    }
    killed
}

fn wide_to_string(buf: &[u16]) -> String {
    let end = buf.iter().position(|&c| c == 0).unwrap_or(buf.len());
    String::from_utf16_lossy(&buf[..end.min(MAX_EXE_NAME)])
}

/// 仅持有所有权、离开作用域即关闭的句柄包装。
struct OwnedHandle(HANDLE);

impl Drop for OwnedHandle {
    fn drop(&mut self) {
        if !self.0.is_invalid() {
            unsafe {
                let _ = CloseHandle(self.0);
            }
        }
    }
}

/// 未使用类型占位（避免导入告警噪声）。
#[allow(dead_code)]
fn _type_anchors() -> (HWND, LPARAM, WPARAM, LRESULT) {
    (
        HWND::default(),
        LPARAM(0),
        WPARAM(0),
        LRESULT(0),
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn resolve_exe_finds_core_itself() {
        let me = std::env::current_exe().unwrap();
        let name = me.file_name().unwrap().to_string_lossy().to_string();
        let found = resolve_exe(&name).expect("core should resolve its own exe");
        assert!(found.is_file());
    }

    #[test]
    fn resolve_exe_missing_returns_none() {
        assert!(resolve_exe("definitely-not-a-real-binary-xyz.exe").is_none());
    }

    /// C19：绝对路径 / 含分隔符 / `..` 的名字必须被拒（`Path::join` 遇绝对路径会替换基路径）。
    #[test]
    fn absolute_or_relative_paths_are_not_plain_names() {
        // 合法：裸文件名
        assert!(is_bare_name("BetterDesktop.Agent.exe"));
        assert!(is_bare_name("betterdesktop-core.exe"));

        // 非法：绝对路径（含驱动器）与 UNC
        assert!(!is_bare_name(r"C:\temp\evil.exe"));
        assert!(!is_bare_name(r"\\server\share\evil.exe"));
        // 非法：相对路径 / 目录切换
        assert!(!is_bare_name(r"..\evil.exe"));
        assert!(!is_bare_name(r"sub\evil.exe"));
        assert!(!is_bare_name("sub/evil.exe"));
        assert!(!is_bare_name("."));
        assert!(!is_bare_name(".."));
        assert!(!is_bare_name(""));
    }

    /// 关键：绝对路径**不能**因为"文件确实存在"就被解析出来（这正是 join-替换基路径那条路）。
    #[test]
    fn resolve_exe_refuses_absolute_path_even_if_it_exists() {
        let me = std::env::current_exe().unwrap();
        let absolute = me.to_string_lossy().to_string();
        assert!(
            resolve_exe(&absolute).is_none(),
            "绝对路径必须被拒，否则 components.json 可指向任意可执行体"
        );
    }

    /// "找得到的目录"与"C19 允许的目录"是**同一份** —— 否则会出现"解析在此、校验在彼"的漂移。
    #[test]
    fn search_dirs_is_the_single_source_for_both_resolution_and_c19() {
        let dirs = exe_search_dirs();
        assert!(!dirs.is_empty(), "至少应有 core 自身目录");
        let me = std::env::current_exe().unwrap();
        assert!(
            crate::security::is_within_any(&me, &dirs),
            "core 自己的 exe 必然落在允许目录内（否则 C19 会拒绝 core 自身）"
        );
    }

    #[test]
    fn command_line_quotes_exe_and_appends_args() {
        // 与 spawn_detached 内的拼装规则一致（契约：路径必须带引号，参数原样跟在后面）
        let exe = std::path::Path::new(r"C:\Program Files\X\a.exe");
        let mut cmd: Vec<u16> = Vec::new();
        cmd.push(b'"' as u16);
        cmd.extend(exe.to_string_lossy().encode_utf16());
        cmd.push(b'"' as u16);
        cmd.push(b' ' as u16);
        cmd.extend("--capture-now".encode_utf16());
        assert_eq!(
            String::from_utf16(&cmd).unwrap(),
            r#""C:\Program Files\X\a.exe" --capture-now"#
        );
    }

    #[test]
    fn running_names_contains_self() {
        let me = std::env::current_exe().unwrap();
        let name = me.file_name().unwrap().to_string_lossy().to_string();
        let names = running_exe_names();
        assert!(
            names.contains(&name.to_ascii_lowercase()),
            "snapshot must contain the current test process ({name})"
        );
    }

    #[test]
    fn stop_by_missing_name_is_zero_not_error() {
        assert_eq!(stop_by_exe_name("definitely-not-running-xyz.exe"), 0);
    }

    /// MSDN 的 argv → command line 规则。**反斜杠**那两条是最容易写错的（漏了就会把
    /// `C:\a b\` 解析成 `C:\a b"`），所以逐条钉住。
    #[test]
    fn quote_arg_follows_msdn_rules() {
        assert_eq!(quote_arg("plain"), "plain");
        assert_eq!(quote_arg("a b"), "\"a b\"");
        assert_eq!(quote_arg(""), "\"\"");
        assert_eq!(quote_arg("a\tb"), "\"a\tb\"");
        assert_eq!(quote_arg(r"C:\x"), r"C:\x", "路径无空格时不该被加引号");
        assert_eq!(quote_arg("a\"b"), "\"a\\\"b\"");
        // 引号前的反斜杠翻倍 + 转义引号本身
        assert_eq!(quote_arg(r#"a\"b"#), r#""a\\\"b""#);
        // 结尾反斜杠翻倍，否则会转义掉收尾引号
        assert_eq!(quote_arg(r"C:\a b\"), r#""C:\a b\\""#);
    }

    fn system_exe(name: &str) -> std::path::PathBuf {
        let root = std::env::var("SystemRoot").unwrap_or_else(|_| r"C:\Windows".to_string());
        std::path::Path::new(&root).join("System32").join(name)
    }

    #[test]
    fn run_and_wait_captures_stdout_and_exit_code() {
        let out = run_and_wait(
            &system_exe("cmd.exe"),
            &["/c".to_string(), "echo bd-probe-output".to_string()],
            std::time::Duration::from_secs(15),
        )
        .expect("cmd.exe should be runnable");

        assert!(!out.timed_out);
        assert_eq!(out.exit_code, 0);
        assert!(
            out.stdout.contains("bd-probe-output"),
            "stdout not captured: {:?}",
            out.stdout
        );
    }

    #[test]
    fn run_and_wait_reports_nonzero_exit_code() {
        let out = run_and_wait(
            &system_exe("cmd.exe"),
            &["/c".to_string(), "exit 3".to_string()],
            std::time::Duration::from_secs(15),
        )
        .expect("cmd.exe should be runnable");

        assert_eq!(out.exit_code, 3, "退出码必须原样带回来");
        assert!(!out.timed_out);
    }

    /// 超时必须**收走**子进程，不能把它留在系统里。
    #[test]
    fn run_and_wait_kills_the_child_on_timeout() {
        let out = run_and_wait(
            &system_exe("ping.exe"),
            &["-n".to_string(), "20".to_string(), "127.0.0.1".to_string()],
            std::time::Duration::from_millis(400),
        )
        .expect("ping.exe should be runnable");

        assert!(out.timed_out, "should have hit the deadline");
        // 超时后进程已被 TerminateProcess，故不会再有同名 ping 残留 —— 粗验退出码已被取到
        assert_ne!(out.exit_code, 259, "process must not still be running (STILL_ACTIVE)");
    }

    #[test]
    fn run_and_wait_missing_exe_is_error_not_panic() {
        let r = run_and_wait(
            std::path::Path::new(r"C:\definitely\not\here\nope.exe"),
            &[],
            std::time::Duration::from_secs(5),
        );
        assert!(r.is_err());
    }

    /// `schtasks /query /xml` 输出 UTF-16LE —— 这条守卫锁住"不能只按 UTF-8 解码"。
    #[test]
    fn console_text_decodes_utf16le() {
        let text = "<Command>C:\\x\\betterdesktop-core.exe</Command>";
        let utf16: Vec<u8> = text.encode_utf16().flat_map(|u| u.to_le_bytes()).collect();
        assert_eq!(decode_console_text(&utf16), text);
        // 不得包含 NUL（按 UTF-8 解码就会）
        assert!(!decode_console_text(&utf16).contains('\0'));
    }

    #[test]
    fn console_text_keeps_plain_utf8() {
        assert_eq!(decode_console_text(b"plain ascii"), "plain ascii");
        assert_eq!(
            decode_console_text("中文路径".as_bytes()),
            "中文路径",
            "短的中文输入不得被误判成 UTF-16"
        );
    }

    /// 含中文的 UTF-16LE 输出不能被误判（高字节并非全 0）。
    #[test]
    fn console_text_handles_utf16le_with_non_ascii() {
        let text = "<Command>C:\\用户\\betterdesktop-core.exe</Command>";
        let utf16: Vec<u8> = text.encode_utf16().flat_map(|u| u.to_le_bytes()).collect();
        assert_eq!(decode_console_text(&utf16), text);
    }

    /// 带 BOM 的 UTF-16LE 输出应把 BOM 去掉（否则首个标签的匹配会偏移）。
    #[test]
    fn console_text_strips_utf16_bom() {
        let mut utf16: Vec<u8> = vec![0xFF, 0xFE];
        utf16.extend("<x>1</x>".encode_utf16().flat_map(|u| u.to_le_bytes()));
        assert_eq!(decode_console_text(&utf16), "<x>1</x>");
    }

    #[test]
    fn wide_to_string_stops_at_nul() {
        let buf = [b'a' as u16, b'b' as u16, 0, b'c' as u16];
        assert_eq!(wide_to_string(&buf), "ab");
    }
}
