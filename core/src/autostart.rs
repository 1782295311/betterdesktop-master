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
//! # 与计划任务**共用同一条判据**
//!
//! 判据在 [`crate::ownership`]，本模块只是它的两个消费者之一。两句话：
//!
//! - 位置会在 build/clean 中消失（dev bin / `target/` / 打包中间目录）⇒ 不写。
//!   那个 bin 一次 clean 之后每次开机都失败 —— 与"把开发目录写进计划任务"是同款负债，
//!   而且同样**没有任何地方会报错**。
//! - 产品目录里的**另一份**构建（测试副本 / 旧版本残留）⇒ 也不写。
//!   否则一个测试副本会悄悄成为这台机器的开机自启项，而用户以为自启指向的是他装的那一份。
//!
//! 第二条是 2026-09-20 与计划任务那一侧同批补上的（同一条判据、同一个事故）。
//!
//! # 【边界】`clean_legacy_values` **刻意不**受这条规则管
//!
//! 它删的是已经退役的组件的值名（`LEGACY_VALUE_NAMES`），指向的 exe 早已随 S4-4 删除，
//! 因此**不可能动到任何活着的引用**（这条由单测 `legacy_names_exclude_the_live_launcher` 钉住）。
//! 那属于"清死引用"，而不是"接管别人的引用"—— 后者才是本规则要防的事。
//! 把清理也加上写权判定，只会让一份副本在这台机器上失去"顺手清掉开机报错的死项"的能力。
//!
//! # 值名是跨进程契约
//!
//! 只留 [`VALUE_NAME`] 一个值名。历史上写过的 `BetterDesktop.Tray` / `.Watchdog` / `BetterDesktop`
//! 由卸载程序、恢复程序 **以及 core 自己**负责**清理**（前两者只按名字删，不生成定义，
//! 因此不构成第二份实现；core 的清理见 [`clean_legacy_values`]）。
//!
//! # 【2026-09-20 实证修正】"交给卸载器与恢复程序"**不够**
//!
//! 本模块头原先只写了前两者（卸载程序 / 恢复程序）。真机实测推翻了它：
//! 本机 `HKCU\...\Run` 里 `BetterDesktop.Tray` 与 `BetterDesktop.Watchdog` **都还在**，
//! 而且它们指向的 exe **也还在磁盘上**（旧版 `dist\modules\...` 目录）
//! ⇒ **每次开机都会把旧托盘与旧守护者拉起来**，与 core 的 gate 打架
//!（计划 §7.1 记的"今天三次遇到旧守护者"，几乎肯定就是这个）。
//!
//! 原因是结构性的：**卸载器与应急恢复都是手动入口** —— 前者只在卸载时跑、
//! 后者要用户主动点"应急恢复"。而这个问题**每次开机复现**，
//! 需要一个**每次开机都会执行**的地方 ⇒ core 自己（它由计划任务拉起，5 分钟内必跑一次）。
//!
//! **教训**：把"清理历史遗留"的职责派给**手动入口**，等于假设用户会主动来清 ——
//! 而对**用户不可见**的遗留（开机自启正是典型），这个假设永远不成立。

use std::path::{Path, PathBuf};

use windows::Win32::Foundation::{ERROR_FILE_NOT_FOUND, ERROR_SUCCESS};
use windows::Win32::System::Registry::{
    HKEY, HKEY_CURRENT_USER, KEY_QUERY_VALUE, KEY_SET_VALUE, REG_OPTION_NON_VOLATILE, REG_SZ,
    RegCloseKey, RegCreateKeyExW, RegDeleteValueW, RegOpenKeyExW, RegQueryValueExW, RegSetValueExW,
};
use windows::core::PCWSTR;

/// `HKCU\...\Run` 下的值名（**跨进程契约**：卸载程序 / `recovery --clean-autostart` 按它清理）。
pub const VALUE_NAME: &str = "BetterDesktop.Core";

/// **历史遗留**的 Run 值名 —— 指向 S4-4 之前的组件，由 [`clean_legacy_values`] 清掉。
///
/// # 为什么只收这两个，而**不含** `BetterDesktop`
///
/// `BetterDesktop.Tray` / `BetterDesktop.Watchdog` 指向的组件**已随 S4-4 删除** ⇒ 删值无副作用。
///
/// 而裸名 `BetterDesktop` **不能**收进来：它可能指向**现役的 launcher**
///（`BetterDesktop.exe` 是用户双击的入口）⇒ 删掉它会破坏一个正在用的功能。
/// 判据是"**目标组件是否已退役**"，不是"名字看起来旧不旧"。
pub const LEGACY_VALUE_NAMES: &[&str] = &["BetterDesktop.Tray", "BetterDesktop.Watchdog"];

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

/// 打开（必要时创建）自启登记。
///
/// # 契约
/// 成功 = 值已写入并指向**本进程**；`Err` 含 Win32 错误码或写权拒绝理由，**绝不静默**
/// （调用方是托盘菜单：失败必须回一条气泡，否则用户会以为开关生效了）。
pub fn enable() -> Result<(), String> {
    let exe = current_exe()?;
    require_owner(&exe)?;

    let key = open_run_key(true)?;
    // 带引号：路径含空格时 Run 键的解析器会把参数切错（与 `CreateProcessW` 同款语义）。
    write_value(&key, &format!("\"{}\"", exe.display()))
}

/// 删除自启登记（**幂等**：键或值本来就不存在算成功）。
///
/// # 为什么"删"也要判写权
/// 值名是单例：删它等于关掉**这台机器**的开机自启。一份副本删掉的是**部署的**自启项 ——
/// 用户看到的是"我点了一下自启开关，装的那份反而不自启了"，而且没有任何日志说明是谁干的。
pub fn disable() -> Result<(), String> {
    require_owner(&current_exe()?)?;

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

/// 清掉[历史遗留的 Run 值](LEGACY_VALUE_NAMES)，返回**实际删掉**的值名。
///
/// 启动时调用一次（见模块头的实证修正）。设计上有三条自律：
///
/// - **幂等**：没有可清的就算成功（返回空表）。
/// - **不建键**：Run 键不存在就什么都不做（用 `RegOpenKeyExW`，不是 `Create`）——
///   "清一次遗留"不该顺手造出一个空键。
/// - **失败不阻塞启动**：删不掉（权限/被占用）不该让 core 起不来。
///   这是启动路径上的一步**尽力而为**的清理，不是一个必须成功的动作。
///   返回值让调用方**如实记录**"清了什么"，而不是静默。
pub fn clean_legacy_values() -> Vec<String> {
    let mut removed = Vec::new();

    // 需要 KEY_SET_VALUE（删除是写操作）；读值那条路只求 KEY_QUERY_VALUE，故不复用它。
    let subkey = to_wide(RUN_KEY);
    let mut key = HKEY::default();
    let opened = unsafe {
        RegOpenKeyExW(
            HKEY_CURRENT_USER,
            PCWSTR(subkey.as_ptr()),
            0,
            KEY_SET_VALUE | KEY_QUERY_VALUE,
            &mut key,
        )
    };
    if opened != ERROR_SUCCESS {
        return removed; // 键不存在 / 打不开 ⇒ 没有可清的
    }
    let key = OwnedKey(key);

    for name in LEGACY_VALUE_NAMES {
        let wide = to_wide(name);
        if unsafe { RegDeleteValueW(key.0, PCWSTR(wide.as_ptr())) } == ERROR_SUCCESS {
            removed.push((*name).to_string());
        }
    }

    removed
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

/// 只有**这份部署自己**才允许写这条系统级引用（判据与计划任务共用，见模块头）。
///
/// # 为什么在这里再包一层，而不是让 `enable` / `disable` 各自调判据
/// [`crate::ownership::of`] 是纯函数，两个入参（安装根、`%LOCALAPPDATA%`）得由调用方从真实环境取。
/// "取值 → 判定 → 取措辞"这三步在本模块里总是一起出现，包一层就只写一遍；
/// 写两遍的后果不是多两行，而是**两个入口的判据会漂移**（本仓反复吃过的那类病）。
///
/// # 契约
/// `Ok(())` = 本进程可以写这个值名；`Err(理由)` = 不可以，理由可直接进日志/气泡。
/// 无 IO 之外的副作用：读一次 `deployment.json` 与环境变量。
fn require_owner(exe: &Path) -> Result<(), String> {
    let install_root = crate::shellmenu::install_root();
    let local_appdata = std::env::var("LOCALAPPDATA").ok().map(PathBuf::from);
    let ownership = crate::ownership::of(exe, install_root.as_deref(), local_appdata.as_deref());

    match crate::ownership::refusal_reason(ownership, exe, install_root.as_deref()) {
        None => Ok(()),
        Some(reason) => Err(reason),
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

    /// **历史值的判据是"目标组件是否已退役"，不是"名字看起来旧"**。
    ///
    /// 这条单测存在的唯一理由是**防手滑**：往 [`LEGACY_VALUE_NAMES`] 里加名字是"清理工作"，
    /// 看起来永远安全 —— 但只要加错一个，core 就会在每次开机时**删掉一个正在用的自启项**。
    #[test]
    fn legacy_names_exclude_the_live_launcher() {
        assert!(LEGACY_VALUE_NAMES.contains(&"BetterDesktop.Tray"));
        assert!(LEGACY_VALUE_NAMES.contains(&"BetterDesktop.Watchdog"));

        assert!(
            !LEGACY_VALUE_NAMES.contains(&"BetterDesktop"),
            "裸名 `BetterDesktop` 可能指向**现役的 launcher**（用户双击的入口）—— \
             删它 = 破坏一个正在用的功能。它的归属要靠实际目标判断，不能靠名字像不像旧的"
        );
        assert!(
            !LEGACY_VALUE_NAMES.contains(&VALUE_NAME),
            "现役值名绝不能被当成遗留值清掉 —— 那会把 core 自己的开机自启删了"
        );
    }

    /// 只读探测本机一次：不写、不删任何东西（单测不该改动真机系统状态）。
    #[test]
    fn probing_this_machine_never_writes() {
        // 任何结论都合法，但必须给得出结论、不得 panic / 挂住。
        let _ = is_enabled();
    }
}
