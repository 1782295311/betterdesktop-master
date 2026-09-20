//! 开机自启：`HKCU\...\Run` 里的 `BetterDesktop.Core` 值。
//!
//! # 为什么 core **自己**写，而不是走 CLI
//!
//! 它和**计划任务**（`task.rs`）是同一类动作：都是"持久化 core 自己的启动路径"。
//! 二者放同一处（core），就不会出现"两个写者、两份实现"—— 本仓库反复吃过这个亏。
//!
//! 而系统右键扩展的注册表写入走 CLI（`shellmenu.rs`），是因为那是**改 explorer 的行为**：
//! CLSID / `InprocServer32` 写错会让 explorer 加载失败，风险高一个量级，且实现已在 C# 侧验证过。
//! 判据一句话：**改自己启动路径的（core 直接写），改别人行为的（core 触发、CLI 执行）**。
//!
//! # 与计划任务**共用同一条守卫**
//!
//! 只在 [`crate::task::is_stable_location`]（安装根 / `%LOCALAPPDATA%\BetterDesktop` 之下）才写。
//! 把开发 bin 的路径写进 Run 键，那个 bin 一次 clean 之后每次开机都失败 ——
//! 与"把开发目录写进计划任务"是同款负债，而且同样**没有任何地方会报错**。
//!
//! # 值名是跨进程契约
//!
//! 只留 [`VALUE_NAME`] 一个值名。历史上写过的 `BetterDesktop.Tray` / `.Watchdog` / `BetterDesktop`
//! 由卸载程序与恢复程序负责**清理**（它们只按名字删，不生成定义，因此不构成第二份实现）。

use std::path::{Path, PathBuf};

use windows::Win32::Foundation::{ERROR_FILE_NOT_FOUND, ERROR_SUCCESS};
use windows::Win32::System::Registry::{
    HKEY, HKEY_CURRENT_USER, KEY_QUERY_VALUE, KEY_SET_VALUE, REG_OPTION_NON_VOLATILE, REG_SZ,
    RegCloseKey, RegCreateKeyExW, RegDeleteValueW, RegOpenKeyExW, RegQueryValueExW, RegSetValueExW,
};
use windows::core::PCWSTR;

/// `HKCU\...\Run` 下的值名（**跨进程契约**：卸载程序 / `recovery --clean-autostart` 按它清理）。
pub const VALUE_NAME: &str = "BetterDesktop.Core";

/// `HKCU` 下的 Run 键路径。
const RUN_KEY: &str = r"Software\Microsoft\Windows\CurrentVersion\Run";

/// 当前是否已登记自启（且指向**当前这个** core）。
///
/// 指向别处的旧值**不算已启用**：那说明用户在别的位置装过一份 —— 此时"勾选态"若显示为真，
/// 用户会以为自启指向的是自己正在用的这一份。
pub fn is_enabled() -> bool {
    let Ok(exe) = current_exe() else {
        return false;
    };
    read_value().is_some_and(|v| value_matches_path(&v, &exe))
}

/// 打开（必要时创建）自启登记。返回 `Err` 含 Win32 错误码，**绝不静默**。
pub fn enable() -> Result<(), String> {
    let exe = current_exe()?;
    ensure_stable_location(&exe)?;

    let key = open_run_key(true)?;
    // 带引号：路径含空格时 Run 键的解析器会把参数切错（与 `CreateProcessW` 同款语义）。
    write_value(&key, &format!("\"{}\"", exe.display()))
}

/// 删除自启登记（**幂等**：键或值本来就不存在算成功）。
pub fn disable() -> Result<(), String> {
    // 键不存在 ⇒ 值必然不存在 ⇒ 已经是关的。用 create=false 打开，避免"关一次反而建出空键"。
    let Ok(key) = open_run_key(false) else {
        return Ok(());
    };

    let name = to_wide(VALUE_NAME);
    let result = unsafe { RegDeleteValueW(key.0, PCWSTR(name.as_ptr())) };
    if result == ERROR_SUCCESS || result == ERROR_FILE_NOT_FOUND {
        Ok(())
    } else {
        Err(format!(
            "cannot delete HKCU\\{RUN_KEY}\\{VALUE_NAME}: Win32 error {}",
            result.0
        ))
    }
}

/// 翻转自启并返回**翻转后**的状态（`true` = 已启用）。
///
/// 翻转前先读**真实值**（不猜、不缓存）—— 与托盘菜单"打开时刷新"同一条纪律。
pub fn toggle() -> Result<bool, String> {
    if is_enabled() {
        disable()?;
        Ok(false)
    } else {
        enable()?;
        Ok(true)
    }
}

// ───────────────────────────── 纯逻辑（可单测） ─────────────────────────────

/// 值（可能带引号）是否指向给定的 exe。
///
/// 比较用**共享契约**的 [`crate::shellmenu::path_eq`]：不在这里再写一份"什么算同一条路径"，
/// 否则大小写/分隔符差异会让勾选态与实际登记来回跳（本仓已有同族教训）。
fn value_matches_path(value: &str, exe: &Path) -> bool {
    let cleaned = value.trim().trim_matches('"').trim();
    !cleaned.is_empty() && crate::shellmenu::path_eq(cleaned, &exe.to_string_lossy())
}

/// 只在**稳定位置**才允许写系统级引用（与计划任务同一条守卫，见模块头）。
fn ensure_stable_location(exe: &Path) -> Result<(), String> {
    let install_root = crate::shellmenu::install_root();
    let local_appdata = std::env::var("LOCALAPPDATA").ok().map(PathBuf::from);

    if crate::task::is_stable_location(exe, install_root.as_deref(), local_appdata.as_deref()) {
        Ok(())
    } else {
        Err(format!(
            "this core lives at {} which is neither under the install root nor under \
             %LOCALAPPDATA%\\BetterDesktop — refusing to write a boot-time reference to it",
            exe.display()
        ))
    }
}

// ───────────────────────────── 副作用（薄壳） ─────────────────────────────

fn current_exe() -> Result<PathBuf, String> {
    std::env::current_exe().map_err(|e| format!("cannot determine the core executable path: {e}"))
}

fn open_run_key(create: bool) -> Result<OwnedKey, String> {
    let subkey = to_wide(RUN_KEY);
    let mut key = HKEY::default();

    let result = if create {
        unsafe {
            RegCreateKeyExW(
                HKEY_CURRENT_USER,
                PCWSTR(subkey.as_ptr()),
                0,
                None,
                REG_OPTION_NON_VOLATILE,
                KEY_SET_VALUE | KEY_QUERY_VALUE,
                None,
                &mut key,
                None,
            )
        }
    } else {
        unsafe {
            RegOpenKeyExW(
                HKEY_CURRENT_USER,
                PCWSTR(subkey.as_ptr()),
                0,
                KEY_QUERY_VALUE,
                &mut key,
            )
        }
    };

    if result != ERROR_SUCCESS {
        return Err(format!(
            "cannot open HKCU\\{RUN_KEY}: Win32 error {}",
            result.0
        ));
    }
    Ok(OwnedKey(key))
}

fn write_value(key: &OwnedKey, value: &str) -> Result<(), String> {
    let name = to_wide(VALUE_NAME);

    // REG_SZ 的字节形态 = UTF-16LE + 结尾 NUL（cbData 含 NUL）。
    let mut bytes: Vec<u8> = Vec::new();
    for unit in value.encode_utf16() {
        bytes.extend_from_slice(&unit.to_le_bytes());
    }
    bytes.extend_from_slice(&[0, 0]);

    let result = unsafe {
        RegSetValueExW(
            key.0,
            PCWSTR(name.as_ptr()),
            0,
            REG_SZ,
            Some(&bytes),
        )
    };
    if result == ERROR_SUCCESS {
        Ok(())
    } else {
        Err(format!(
            "cannot write HKCU\\{RUN_KEY}\\{VALUE_NAME}: Win32 error {}",
            result.0
        ))
    }
}

/// 读值（`None` = 键/值不存在、非字符串、或读失败）。
fn read_value() -> Option<String> {
    let key = open_run_key(false).ok()?;
    let name = to_wide(VALUE_NAME);

    let mut kind = REG_SZ;
    let mut size = 0u32;
    let queried = unsafe {
        RegQueryValueExW(
            key.0,
            PCWSTR(name.as_ptr()),
            None,
            Some(&mut kind),
            None,
            Some(&mut size),
        )
    };
    if queried != ERROR_SUCCESS || size == 0 || kind != REG_SZ {
        return None;
    }

    let mut buf = vec![0u8; size as usize];
    let queried = unsafe {
        RegQueryValueExW(
            key.0,
            PCWSTR(name.as_ptr()),
            None,
            Some(&mut kind),
            Some(buf.as_mut_ptr()),
            Some(&mut size),
        )
    };
    if queried != ERROR_SUCCESS {
        return None;
    }

    let units: Vec<u16> = buf
        .chunks_exact(2)
        .map(|c| u16::from_le_bytes([c[0], c[1]]))
        .collect();
    let end = units.iter().position(|&c| c == 0).unwrap_or(units.len());
    let text = String::from_utf16_lossy(&units[..end]);
    let text = text.trim().to_string();
    if text.is_empty() { None } else { Some(text) }
}

/// 仅持有所有权、离开作用域即关的注册表键。
struct OwnedKey(HKEY);

impl Drop for OwnedKey {
    fn drop(&mut self) {
        if !self.0.is_invalid() {
            let _ = unsafe { RegCloseKey(self.0) };
        }
    }
}

fn to_wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 值名是**跨进程契约**：卸载程序与恢复程序按它清理 —— 改名而没同步那两处，
    /// 结果就是"卸载后自启残留、每次开机拉起一个已被删的 exe"。
    #[test]
    fn value_name_is_the_cross_process_contract_literal() {
        assert_eq!(VALUE_NAME, "BetterDesktop.Core");
        assert!(VALUE_NAME.is_ascii());
    }

    /// 带引号 / 不带引号 / 大小写 / 分隔符差异都算"指向同一份" —— 否则勾选态会与实际登记来回跳。
    #[test]
    fn value_matching_tolerates_quoting_and_path_spelling() {
        let exe = Path::new(r"C:\Apps\BetterDesktop\betterdesktop-core.exe");

        assert!(value_matches_path(
            r#""C:\Apps\BetterDesktop\betterdesktop-core.exe""#,
            exe
        ));
        assert!(value_matches_path(
            r"c:/apps/betterdesktop/BETTERDESKTOP-CORE.EXE",
            exe
        ));
        assert!(value_matches_path(
            r#"  "C:\Apps\BetterDesktop\betterdesktop-core.exe"  "#,
            exe
        ));

        assert!(!value_matches_path(r"C:\Other\betterdesktop-core.exe", exe));
        assert!(!value_matches_path("", exe), "空值不得被当成等价");
        assert!(!value_matches_path(r#""""#, exe));
    }

    /// 只读探测本机一次：不写、不删任何东西（单测不该改动真机系统状态）。
    #[test]
    fn probing_this_machine_never_writes() {
        // 任何结论都合法，但必须给得出结论、不得 panic / 挂住。
        let _ = is_enabled();
    }
}
