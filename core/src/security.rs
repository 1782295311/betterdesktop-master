//! 安全基线：管道的访问控制（ACL）与调用者身份校验。
//!
//! # 两层防线（缺一不可）
//!
//! 1. **ACL（创建期）**：管道的 DACL 只放行当前用户 + SYSTEM，显式拒绝 Anonymous / Network。
//!    连不上就谈不上伪造。
//! 2. **调用者校验（连接后、读消息前）**：`ImpersonateNamedPipeClient` 取得调用者令牌，
//!    比对**用户 SID** 与**会话 ID**。
//!
//! # 为什么两层都要
//!
//! ACL 是"内核替我挡"，但它依赖安全描述符写对（顺序、掩码、SID 都可能写错且**不报错**）。
//! 调用者校验是"我自己再确认一次"，即便 DACL 写歪了也还有一道。任一道生效即安全。
//!
//! # 顺序红线
//!
//! - **DACL 里 DENY 必须排在 GRANT 之前**。`SetEntriesInAclW` 按数组顺序写入 ACE，
//!   不做规范化重排；顺序反了 DENY 就不生效 —— 这是"看起来有 ACL 其实没有"的经典成因。
//! - **调用者校验必须在读取任何消息之前完成**。实现方式见 [`validate_client`] 的说明 ——
//!   注意**不能**用 `ImpersonateNamedPipeClient` 达成这一点（实测该 API 要求管道上已有数据，
//!   与"读取前校验"结构性冲突）。
//!
//! # 关于测试的重要提醒
//!
//! `CreateRestrictedToken(DISABLE_MAX_PRIVILEGE)` **不改变用户 SID**，用它测"只允许当前用户 SID"
//! 的 ACL 会得到**假绿色**（进程仍持当前用户 SID → 仍被放行）。真验必须换**另一个用户账户**
//! （见 `scripts/test-pipe-acl.ps1`）。

use std::ffi::c_void;

use windows::Win32::Foundation::{BOOL, CloseHandle, HANDLE, HLOCAL, LocalFree};
use windows::Win32::Security::Authorization::{
    ACCESS_MODE, ConvertSidToStringSidW, DENY_ACCESS, EXPLICIT_ACCESS_W, GRANT_ACCESS,
    NO_MULTIPLE_TRUSTEE, SetEntriesInAclW, TRUSTEE_IS_SID, TRUSTEE_IS_USER, TRUSTEE_W,
};
use windows::Win32::Security::{
    ACL, CreateWellKnownSid, GetLengthSid, GetTokenInformation, InitializeSecurityDescriptor,
    NO_INHERITANCE, PSECURITY_DESCRIPTOR, PSID, SECURITY_ATTRIBUTES, SECURITY_DESCRIPTOR,
    SetSecurityDescriptorDacl, TOKEN_QUERY, TOKEN_USER, TokenSessionId, TokenUser,
    WELL_KNOWN_SID_TYPE, WinAnonymousSid, WinNetworkSid,
};
use windows::Win32::System::Memory::{LPTR, LocalAlloc};
use windows::Win32::System::Pipes::GetNamedPipeClientProcessId;
use windows::Win32::System::SystemServices::SECURITY_DESCRIPTOR_REVISION;
use windows::Win32::System::Threading::{
    GetCurrentProcess, OpenProcess, OpenProcessToken, PROCESS_QUERY_INFORMATION,
    PROCESS_QUERY_LIMITED_INFORMATION,
};
use windows::core::PWSTR;

/// `SECURITY_MAX_SID_SIZE`（Win32 常量，SID 结构上限）。
const MAX_SID_SIZE: usize = 68;

/// 管道访问掩码：`GENERIC_READ | GENERIC_WRITE`（够读写即可，不给更多）。
const PIPE_ACCESS_MASK: u32 = 0x8000_0000 | 0x4000_0000;

/// 当前进程所属用户的安全标识与登录会话。
#[derive(Debug, Clone)]
pub struct CurrentUser {
    sid: Vec<u8>,
    session_id: u32,
}

impl CurrentUser {
    /// 读取当前进程令牌的用户 SID 与会话 ID。
    ///
    /// 失败即返回 `Err`（**不得降级为"跳过校验"**——那等于把第二层防线整条拆掉）。
    pub fn load() -> Result<Self, String> {
        let mut token = HANDLE::default();
        unsafe {
            OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token)
                .map_err(|e| format!("OpenProcessToken failed: {e}"))?;
        }

        let token = OwnedHandle(token);
        let sid = query_token_user_sid(token.0)?;
        let session_id = query_token_session_id(token.0)?;
        Ok(Self { sid, session_id })
    }

    /// SID 的字符串形式（`S-1-5-21-…`），用于计划任务的 `<UserId>`。
    ///
    /// 用 SID 而不是 `DOMAIN\user`：账户显示名可以改名、随系统语言本地化，SID 不会。
    /// 计划任务的身份一旦写错，表现是"任务注册成功但从不生效"，极难排查。
    pub fn sid_string(&self) -> Result<String, String> {
        unsafe {
            let mut raw = PWSTR::null();
            ConvertSidToStringSidW(PSID(self.sid.as_ptr() as *mut c_void), &mut raw)
                .map_err(|e| format!("ConvertSidToStringSidW failed: {e}"))?;

            // 顺序要紧：必须**先**把 LocalAlloc 出来的字符串取成 String，再 LocalFree ——
            // 反过来就是对已释放内存解引用。
            let text = raw
                .to_string()
                .map_err(|e| format!("SID string is not valid UTF-16: {e}"))?;
            let _ = LocalFree(HLOCAL(raw.0 as *mut c_void));
            Ok(text)
        }
    }
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

fn query_token_user_sid(token: HANDLE) -> Result<Vec<u8>, String> {
    let mut needed = 0u32;
    unsafe {
        // 第一次调用只为取长度（预期失败 ERROR_INSUFFICIENT_BUFFER，故忽略返回值）
        let _ = GetTokenInformation(token, TokenUser, None, 0, &mut needed);
    }
    if needed == 0 {
        return Err("GetTokenInformation(TokenUser) returned zero size".to_string());
    }
    let mut buf = vec![0u8; needed as usize];
    unsafe {
        GetTokenInformation(
            token,
            TokenUser,
            Some(buf.as_mut_ptr() as *mut c_void),
            needed,
            &mut needed,
        )
        .map_err(|e| format!("GetTokenInformation(TokenUser) failed: {e}"))?;

        let user = &*(buf.as_ptr() as *const TOKEN_USER);
        let sid = user.User.Sid;
        let len = GetLengthSid(sid) as usize;
        if len == 0 || len > MAX_SID_SIZE {
            return Err(format!("token user SID length looks wrong: {len}"));
        }
        let mut out = vec![0u8; len];
        std::ptr::copy_nonoverlapping(sid.0 as *const u8, out.as_mut_ptr(), len);
        Ok(out)
    }
}

fn query_token_session_id(token: HANDLE) -> Result<u32, String> {
    let mut value = 0u32;
    let mut returned = 0u32;
    unsafe {
        GetTokenInformation(
            token,
            TokenSessionId,
            Some(&mut value as *mut u32 as *mut c_void),
            std::mem::size_of::<u32>() as u32,
            &mut returned,
        )
        .map_err(|e| format!("GetTokenInformation(TokenSessionId) failed: {e}"))?;
    }
    Ok(value)
}

/// 管道的安全描述符持有者。
///
/// 生命周期拉到**进程级**：`CreateNamedPipeW` 只在创建瞬间读取 `lpSecurityAttributes`，
/// 但把 SD 存活期拉长可以彻底避免"提前释放 → 下次创建拿到悬垂指针"这类难查崩溃。
pub struct PipeSecurity {
    sd: PSECURITY_DESCRIPTOR,
    acl: *mut ACL,
}

// 安全：构造后 `sd`/`acl` **只读**，且只在创建管道时被内核读取（不由我们改写）。
// 因此跨线程共享只读指针是安全的。注意：`Drop` 会 `LocalFree`，故 `PipeSecurity` 不得被复制
// （未实现 `Clone`，由类型系统保证唯一所有权）。
unsafe impl Send for PipeSecurity {}
unsafe impl Sync for PipeSecurity {}

impl Drop for PipeSecurity {
    fn drop(&mut self) {
        unsafe {
            if !self.acl.is_null() {
                let _ = LocalFree(HLOCAL(self.acl as *mut c_void));
            }
            if !self.sd.0.is_null() {
                let _ = LocalFree(HLOCAL(self.sd.0));
            }
        }
    }
}

impl PipeSecurity {
    /// 构造 `SECURITY_ATTRIBUTES`（供 `CreateNamedPipeW`）。
    pub fn as_attributes(&self) -> SECURITY_ATTRIBUTES {
        SECURITY_ATTRIBUTES {
            nLength: std::mem::size_of::<SECURITY_ATTRIBUTES>() as u32,
            lpSecurityDescriptor: self.sd.0,
            bInheritHandle: BOOL(0),
        }
    }

    /// 人类可读摘要（日志用）：说明放行了谁、拒绝了谁。
    pub fn summary(&self) -> String {
        "ACL: DENY anonymous(S-1-5-7) + network(S-1-5-2); GRANT current-user only".to_string()
    }
}

/// 构造"只放行当前用户、拒绝 Anonymous/Network"的 DACL。
///
/// **不放行 SYSTEM**（最小权限）：ensure 计划任务是**用户级**的（HKCU、无管理员），
/// 以当前用户身份运行，不需要 SYSTEM 令牌。少给一个主体就少一条被利用的路径。
/// 若将来计划任务改为以 SYSTEM 运行，必须同步在这里加回 `WinLocalSystemSid` 的 GRANT —— 否则
/// 自愈会静默失败（连不上管道）。
///
/// **DENY 条目排在 GRANT 之前** —— 顺序写反则 DENY 不生效（见模块头顺序红线）。
pub fn build_pipe_security(current: &CurrentUser) -> Result<PipeSecurity, String> {
    // 良构 SID 的本地缓冲：构造 EXPLICIT_ACCESS 与 SetEntriesInAclW 期间必须存活
    let mut anon_buf = vec![0u8; MAX_SID_SIZE];
    let mut net_buf = vec![0u8; MAX_SID_SIZE];
    let anon = make_well_known(WinAnonymousSid, &mut anon_buf)?;
    let net = make_well_known(WinNetworkSid, &mut net_buf)?;

    let user = PSID(current.sid.as_ptr() as *mut c_void);

    // 条目顺序 = DENY, DENY, GRANT（**不可调换**）
    let entries = [
        explicit_entry(anon, DENY_ACCESS),
        explicit_entry(net, DENY_ACCESS),
        explicit_entry(user, GRANT_ACCESS),
    ];

    let mut acl: *mut ACL = std::ptr::null_mut();
    unsafe {
        let rc = SetEntriesInAclW(Some(&entries), None, &mut acl);
        if rc.0 != 0 {
            return Err(format!("SetEntriesInAclW failed with win32 error {}", rc.0));
        }
    }

    let sd = unsafe {
        let mem = LocalAlloc(LPTR, std::mem::size_of::<SECURITY_DESCRIPTOR>())
            .map_err(|e| format!("LocalAlloc(SECURITY_DESCRIPTOR) failed: {e}"))?;
        PSECURITY_DESCRIPTOR(mem.0)
    };
    unsafe {
        InitializeSecurityDescriptor(sd, SECURITY_DESCRIPTOR_REVISION)
            .map_err(|e| format!("InitializeSecurityDescriptor failed: {e}"))?;
        SetSecurityDescriptorDacl(sd, true, Some(acl as *const ACL), false)
            .map_err(|e| format!("SetSecurityDescriptorDacl failed: {e}"))?;
    }

    Ok(PipeSecurity { sd, acl })
}

fn make_well_known(kind: WELL_KNOWN_SID_TYPE, buf: &mut [u8]) -> Result<PSID, String> {
    unsafe {
        let mut size = buf.len() as u32;
        CreateWellKnownSid(kind, None, PSID(buf.as_mut_ptr() as *mut c_void), &mut size)
            .map_err(|e| format!("CreateWellKnownSid({kind:?}) failed: {e}"))?;
        Ok(PSID(buf.as_mut_ptr() as *mut c_void))
    }
}

fn explicit_entry(sid: PSID, mode: ACCESS_MODE) -> EXPLICIT_ACCESS_W {
    EXPLICIT_ACCESS_W {
        grfAccessPermissions: PIPE_ACCESS_MASK,
        grfAccessMode: mode,
        grfInheritance: NO_INHERITANCE,
        Trustee: TRUSTEE_W {
            pMultipleTrustee: std::ptr::null_mut(),
            MultipleTrusteeOperation: NO_MULTIPLE_TRUSTEE,
            TrusteeForm: TRUSTEE_IS_SID,
            TrusteeType: TRUSTEE_IS_USER,
            ptstrName: PWSTR(sid.0 as *mut u16),
        },
    }
}

/// 校验已连接的调用者是否与当前用户同 SID、同会话。///
/// **调用方必须在读任何消息之前调用它** —— 本实现满足这一点，且是唯一能满足的方式（见下）。
///
/// # 为什么不用 `ImpersonateNamedPipeClient`（**实测纠正**）
///
/// 最初的实现用 `ImpersonateNamedPipeClient` + `OpenThreadToken` 取调用者身份，把它放在
/// [`crate::pipe::serve_client`] 的第一行。**真机直接失败**：
///
/// ```text
/// ImpersonateNamedPipeClient failed: 在使用命名管道读取数据之前，无法经由该管道模拟。(0x80070558)
/// ```
///
/// 即 `ERROR_CANNOT_IMPERSONATE`：**管道上还没有数据流过时，内核根本拿不到客户端令牌**。
/// 所以"先模拟再读消息"在物理上不可能实现 —— 想模拟就必须先读，而先读就等于先把消息
/// 交给了后续解析路径。该方案与"读取前完成校验"这个安全目标**结构性冲突**。
///
/// # 采用的方案：按 PID 取令牌
///
/// `GetNamedPipeClientProcessId` 不需要任何数据流动即可用，拿到内核给出的调用者 PID 后
/// `OpenProcess` → `OpenProcessToken` → 比对用户 SID 与会话 ID。全部在**读第一个字节之前**完成。
///
/// **已知天花板（有意保留）**：理论上存在"客户端连上后立刻退出、PID 被复用"的 TOCTOU 窗口。
/// 窗口只有微秒级，且客户端一退出管道即断开（我们会看到 `Closed`）；而攻击者需要先通过
/// ACL（同用户）才能走到这里 —— 威胁模型内不值得为此引入更重的机制。
/// 升级触发条件：若将来要防御同用户恶意进程，改为"读后追加一次 ImpersonateNamedPipeClient 复核"。
pub fn validate_client(pipe: HANDLE, current: &CurrentUser) -> Result<(), String> {
    let mut pid = 0u32;
    unsafe {
        GetNamedPipeClientProcessId(pipe, &mut pid)
            .map_err(|e| format!("GetNamedPipeClientProcessId failed: {e}"))?;
    }
    if pid == 0 {
        return Err("GetNamedPipeClientProcessId returned pid 0".to_string());
    }

    // 进程句柄：优先用最小权限（QUERY_LIMITED_INFORMATION），不行再退一步
    let process = match unsafe { OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid) } {
        Ok(h) => h,
        Err(first) => unsafe { OpenProcess(PROCESS_QUERY_INFORMATION, false, pid) }
            .map_err(|second| format!("OpenProcess(pid {pid}) failed: {first} / {second}"))?,
    };
    let process = OwnedHandle(process);

    let mut token = HANDLE::default();
    unsafe {
        OpenProcessToken(process.0, TOKEN_QUERY, &mut token)
            .map_err(|e| format!("OpenProcessToken(pid {pid}) failed: {e}"))?;
    }
    let token = OwnedHandle(token);

    let sid = query_token_user_sid(token.0)?;
    if sid != current.sid {
        return Err(format!(
            "caller pid {pid} SID mismatch (caller {} bytes, expected {} bytes)",
            sid.len(),
            current.sid.len()
        ));
    }

    let session = query_token_session_id(token.0)?;
    if session != current.session_id {
        return Err(format!(
            "caller pid {pid} session {session} != current session {}",
            current.session_id
        ));
    }

    Ok(())
}

/// 供日志使用的当前用户摘要（不打印完整 SID 字节）。
pub fn describe(current: &CurrentUser) -> String {
    format!(
        "current user SID ({} bytes), session {}",
        current.sid.len(),
        current.session_id
    )
}

// ────────────── C19：组件 exe 的路径前缀校验（计划 §6.10 S2.5） ──────────────

/// C19 判据（**纯函数**，故"该拒绝"的分支可被单测穷举）：`exe` 是否落在 `dirs` 任一目录**之内**。
///
/// 复用 [`crate::shellmenu::is_under`]（按**路径段**比较、大小写与斜杠不敏感）——
/// **绝不在这里新写第二处前缀比较**：字符串前缀判会把 `…\BetterDesktopTrap\bin` 误当成
/// `…\BetterDesktop` 之下（那个坑已在 `shellmenu` 的用例里钉住，§C23"一个概念一处实现"）。
pub fn is_within_any(exe: &std::path::Path, dirs: &[std::path::PathBuf]) -> bool {
    dirs.iter().any(|d| crate::shellmenu::is_under(exe, d))
}

/// C19 断言：解析后的组件 exe 必须落在允许目录内（`dirs` 注入，便于单测）。
///
/// **为什么"解析逻辑本来只在两个目录里找"还需要再断言一次**：搜索集是**约定**，前缀校验是**断言** ——
/// 而约定的破坏（将来多一条搜索路径、或某个名字形态让 `join` 拐弯）**不会被约定自己发现**。
/// C19 要求的是一条独立的保证，不是对搜索逻辑的重述。
pub fn validate_component_exe(
    exe: &std::path::Path,
    dirs: &[std::path::PathBuf],
) -> Result<(), String> {
    if is_within_any(exe, dirs) {
        return Ok(());
    }
    Err(format!(
        "component exe {} is outside the allowed directories ({})",
        exe.display(),
        dirs.iter()
            .map(|d| d.display().to_string())
            .collect::<Vec<_>>()
            .join(", ")
    ))
}

/// 用**运行时**的允许目录做校验（唯一生产调用形态）。
///
/// 目录定义来自 [`crate::process::exe_search_dirs`] —— 与解析逻辑**同一份**，
/// 免得"解析在一处、校验在另一处"慢慢漂移。
pub fn validate_resolved_component_exe(exe: &std::path::Path) -> Result<(), String> {
    validate_component_exe(exe, &crate::process::exe_search_dirs())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::{Path, PathBuf};

    fn allowed() -> Vec<PathBuf> {
        vec![
            PathBuf::from(r"C:\App\core"),
            PathBuf::from(r"C:\Users\X\AppData\Local\BetterDesktop"),
        ]
    }

    #[test]
    fn component_exe_inside_allowed_dirs_passes_including_subdirs() {
        let dirs = allowed();
        // core 自身目录
        assert!(is_within_any(Path::new(r"C:\App\core\BetterDesktop.Agent.exe"), &dirs));
        // 数据目录根
        assert!(is_within_any(
            Path::new(r"C:\Users\X\AppData\Local\BetterDesktop\BetterDesktop.Agent.exe"),
            &dirs
        ));
        // 其子目录 —— 安装根在生产里就是它的子目录（故不必单列）
        assert!(is_within_any(
            Path::new(r"C:\Users\X\AppData\Local\BetterDesktop\app\2026.09.17.1610\BetterDesktop.Agent.exe"),
            &dirs
        ));
        // 大小写与斜杠不敏感（Windows 上两种写法都可能出现）
        assert!(is_within_any(
            Path::new(r"c:/users/x/appdata/local/betterdesktop/App.exe"),
            &dirs
        ));
    }

    #[test]
    fn component_exe_outside_allowed_dirs_is_rejected() {
        let dirs = allowed();
        // 任意其它目录（含临时目录 —— 注入的典型落点）
        assert!(!is_within_any(Path::new(r"C:\Other\App.exe"), &dirs));
        assert!(!is_within_any(Path::new(r"C:\Users\X\AppData\Local\Temp\evil.exe"), &dirs));
        // UNC
        assert!(!is_within_any(Path::new(r"\\server\share\App.exe"), &dirs));
        // **同前缀的兄弟目录**：字符串前缀判会误放行，按段比较才正确
        assert!(!is_within_any(
            Path::new(r"C:\Users\X\AppData\Local\BetterDesktopTrap\App.exe"),
            &dirs
        ));
    }

    #[test]
    fn validate_component_exe_error_names_the_offender_and_the_allowed_dirs() {
        let dirs = allowed();
        let err = validate_component_exe(Path::new(r"C:\Temp\evil.exe"), &dirs).unwrap_err();
        assert!(err.contains(r"C:\Temp\evil.exe"), "报错必须点名违规路径：{err}");
        assert!(err.contains("BetterDesktop"), "报错必须给出允许目录：{err}");
    }
}
