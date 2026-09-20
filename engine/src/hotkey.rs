//! 全局热键（3101/7413 纪律）：
//! - RegisterHotKey 必须在宿主窗口所在线程执行（引擎主线程 = 隐藏窗口线程 ✓，main.rs 注册）
//! - 热键 ID 用 GlobalAddAtom 原子值防冲突（7413；失败退回高位常量 ID）
//! - MOD_NOREPEAT 防按住连触发（7413）
//! - 0x581 冲突捕获不崩、跳过该键；退出配对释放（3101）
//! 组合键（计划 §6.1）：Ctrl+Shift+V=打开面板 / Ctrl+Shift+P=收藏视图 / Ctrl+Shift+Backspace=暂停恢复。
//! 【2026-09-16 配置驱动】三键默认值同上，但可由设置中心写入 `extensions.clipboard-history.*-hotkey`
//! 覆盖（"Ctrl+Shift+K" 格式；空串 = 停用），引擎启动时按配置注册。

use std::sync::{Mutex, OnceLock};

use windows::core::w;
use windows::Win32::Foundation::HWND;
use windows::Win32::System::DataExchange::GlobalAddAtomW;
use windows::Win32::UI::Input::KeyboardAndMouse::{
    RegisterHotKey, UnregisterHotKey, HOT_KEY_MODIFIERS, MOD_CONTROL, MOD_NOREPEAT, MOD_SHIFT,
    MOD_WIN,
};

const VK_V: u32 = 0x56;
const VK_P: u32 = 0x50;
const VK_BACK: u32 = 0x08;

/// 原子 ID 失败时的固定回退基址（高位段避开常见程序）。
const FALLBACK_ID_BASE: i32 = 0xB001;

/// 热键动作语义。
///
/// 【2026-09-14 语义对齐 legacy · 用户拍板方案 A】
/// 三键必须与宿主内实现（`ClipboardManager.HandleHotKey` / `HotKeyId*` 常量）**逐一致**，
/// 否则屏幕上写的提示与实际动作不符 —— 实测后果极严重：
/// 设置页与面板 tooltip 都写着「Ctrl+Shift+Backspace 暂停/恢复捕获」，
/// 而引擎此前把它绑成**删除最近一条**（连级联文件一起删）→ 用户照着提示按就静默删数据；
/// 同时引擎把 P 绑成暂停，而文档写的 P 是「收藏视图」→ 收藏入口整体不存在。
/// 现在：V=打开面板 / P=收藏视图 / Backspace=暂停恢复，与 legacy 与文档三处一致。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum HotkeyAction {
    /// 打开完整面板（open_panel）
    OpenPanel,
    /// 收藏视图（打开面板并切到「收藏」筛选）
    OpenFavorites,
    /// 暂停/恢复监听
    TogglePause,
}

/// 全局热键注册表（主线程注册/注销；wnd_proc 经 HOTKEYS 查动作）。
pub struct Hotkeys {
    binds: Vec<(i32, HotkeyAction)>,
}

impl Hotkeys {
    pub fn new() -> Self {
        Self { binds: Vec::new() }
    }

    /// 注册全部热键（必须主线程调用）。键位取自配置（spec 格式 "Ctrl+Shift+V"；空串停用；
    /// 解析失败跳过并记日志——不静默注册默认键，避免"改了不生效还装着生效"）。
    pub fn register(&mut self, hwnd: HWND, settings: &crate::settings::Settings) {
        unsafe {
            // 原子 ID（7413）：GlobalAddAtom 返回 0 表示失败 → 用固定回退
            let atom = GlobalAddAtomW(w!("BetterDesktop.Clipboard.Hotkeys"));
            let base = if atom != 0 { atom as i32 } else { FALLBACK_ID_BASE };
            let binds = [
                (
                    settings.open_panel_hotkey.as_str(),
                    VK_V,
                    HotkeyAction::OpenPanel,
                    "打开面板",
                ),
                (
                    settings.favorites_hotkey.as_str(),
                    VK_P,
                    HotkeyAction::OpenFavorites,
                    "收藏视图",
                ),
                (
                    settings.toggle_pause_hotkey.as_str(),
                    VK_BACK,
                    HotkeyAction::TogglePause,
                    "暂停/恢复监听",
                ),
            ];
            for (idx, (spec, _, action, label)) in binds.iter().enumerate() {
                if spec.trim().is_empty() {
                    crate::log::info(format!("hotkey disabled by config: {label}"));
                    continue;
                }
                let Some((mods, vk)) = parse_spec(spec).or_else(|| {
                    crate::log::warn(format!("hotkey spec unparsable, skipping: {label} ({spec})"));
                    None
                }) else {
                    continue;
                };
                let id = base + idx as i32;
                // MOD_NOREPEAT 防按住连发（7413）
                match RegisterHotKey(hwnd, id, mods | MOD_NOREPEAT, vk) {
                    Ok(()) => {
                        self.binds.push((id, *action));
                        crate::log::info(format!("hotkey registered: {label} ({spec}, id={id})"));
                    }
                    Err(e) => {
                        let win32 = e.code().0 as u32 & 0xFFFF;
                        if win32 == 0x581 {
                            crate::log::warn(format!(
                                "hotkey already registered by another app, skipping: {label} ({spec})"
                            ));
                        } else {
                            crate::log::warn(format!("hotkey register failed {label}: {e}"));
                        }
                    }
                }
            }
        }
    }

    /// 配对释放（退出前调用；3101 红线）。
    pub fn unregister(&mut self, hwnd: HWND) {
        unsafe {
            for (id, _) in self.binds.drain(..) {
                let _ = UnregisterHotKey(hwnd, id);
            }
        }
    }

    /// WM_HOTKEY wparam(id) → 动作。
    pub fn action_for(&self, id: i32) -> Option<HotkeyAction> {
        self.binds.iter().find(|(i, _)| *i == id).map(|(_, a)| *a)
    }
}

/// 解析热键 spec → (modifiers, vk)。
///
/// **实现已抽到 `betterdesktop-hotkey-spec` crate**（与 core 共用同一份，避免"两边各写一遍必然漂移"）。
/// 本函数只做一层类型包装：把共享 crate 的裸 `u32` 包成 `HOT_KEY_MODIFIERS`。
/// 语义（修饰键集合、主键表、必须含修饰键）全部在那边定义与测试。
pub fn parse_spec(spec: &str) -> Option<(HOT_KEY_MODIFIERS, u32)> {
    let binding = betterdesktop_hotkey_spec::parse(spec)?;
    Some((HOT_KEY_MODIFIERS(binding.modifiers), binding.vk))
}

/// 全局热键注册表（wnd_proc 访问；主线程注册）。
pub static HOTKEYS: OnceLock<Mutex<Hotkeys>> = OnceLock::new();

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn action_mapping_roundtrip() {
        let mut h = Hotkeys::new();
        h.binds.push((0xB001, HotkeyAction::OpenPanel));
        h.binds.push((0xB002, HotkeyAction::OpenFavorites));
        h.binds.push((0xB003, HotkeyAction::TogglePause));
        assert_eq!(h.action_for(0xB001), Some(HotkeyAction::OpenPanel));
        assert_eq!(h.action_for(0xB002), Some(HotkeyAction::OpenFavorites));
        assert_eq!(h.action_for(0xB003), Some(HotkeyAction::TogglePause));
        assert_eq!(h.action_for(0x9999), None);
    }

    /// 【2026-09-14 回归 · 语义对齐 legacy】三键的 VK → 动作**必须**与 C# 宿主内实现一致
    ///（`ClipboardManager.HotKeyId*`：V=面板 / P=收藏视图 / Backspace=暂停）。
    /// 错位的后果是"按提示操作反而删数据"，本用例把映射钉死。
    #[test]
    fn vk_bindings_match_legacy_semantics() {
        assert_eq!(VK_V, 0x56);
        assert_eq!(VK_P, 0x50);
        assert_eq!(VK_BACK, 0x08);

        let mut h = Hotkeys::new();
        h.binds.push((0xB001, HotkeyAction::OpenPanel));
        h.binds.push((0xB002, HotkeyAction::OpenFavorites));
        h.binds.push((0xB003, HotkeyAction::TogglePause));

        assert_eq!(h.action_for(0xB002), Some(HotkeyAction::OpenFavorites), "P 必须是收藏视图");
        assert_eq!(
            h.action_for(0xB003),
            Some(HotkeyAction::TogglePause),
            "Backspace 必须是暂停/恢复 —— 绝不能是删除（破坏性动作不该占易误触的组合键）"
        );
    }

    /// 【2026-09-16 配置驱动】默认三键解析为 (Ctrl+Shift, V/P/Backspace)。
    #[test]
    fn parse_default_specs() {
        let (m, vk) = parse_spec("Ctrl+Shift+V").unwrap();
        assert_eq!(m, HOT_KEY_MODIFIERS(MOD_CONTROL.0 | MOD_SHIFT.0));
        assert_eq!(vk, 0x56);

        assert_eq!(parse_spec("Ctrl+Shift+P").unwrap().1, 0x50);
        let (m, vk) = parse_spec("Ctrl+Shift+Backspace").unwrap();
        assert_eq!(vk, 0x08);
        assert_eq!(m, HOT_KEY_MODIFIERS(MOD_CONTROL.0 | MOD_SHIFT.0));
    }

    /// 【2026-09-16 配置驱动】大小写/顺序/别名归一。
    #[test]
    fn parse_spec_normalizes() {
        let (m, vk) = parse_spec("shift+ctrl+K").unwrap();
        assert_eq!(m, HOT_KEY_MODIFIERS(MOD_CONTROL.0 | MOD_SHIFT.0));
        assert_eq!(vk, 0x4B);
        assert_eq!(parse_spec("Win+E"), Some((HOT_KEY_MODIFIERS(MOD_WIN.0), 0x45)));
    }

    /// 【2026-09-16 配置驱动】非法 spec：无修饰键 / 未知主键 → None。
    #[test]
    fn parse_spec_rejects_invalid() {
        assert!(parse_spec("K").is_none());
        assert!(parse_spec("Ctrl+Shift+Qwerty").is_none());
        assert!(parse_spec("").is_none());
        assert!(parse_spec("Ctrl+Shift+F25").is_none());
    }

    /// 【2026-09-16 配置驱动】数字键 D1-D9 / F 键 / 符号键映射。
    #[test]
    fn parse_spec_special_keys() {
        assert_eq!(parse_spec("Ctrl+Alt+D1").unwrap().1, 0x31);
        assert_eq!(parse_spec("Ctrl+Shift+F5").unwrap().1, 0x74);
        assert_eq!(parse_spec("Ctrl+Shift+OemPeriod").unwrap().1, 0xBE);
        assert_eq!(parse_spec("Ctrl+Shift+Tab").unwrap().1, 0x09);
        assert_eq!(parse_spec("Ctrl+Shift+Space").unwrap().1, 0x20);
    }
}
