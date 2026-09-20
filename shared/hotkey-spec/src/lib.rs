//! 热键规范串解析 —— **唯一真相源**。
//!
//! # 为什么这是一个独立 crate 而不是各自实现一份
//!
//! 规范串（`"Ctrl+Shift+V"` / `"Win+Shift+B"`）在三个地方被**生产**（设置中心的热键面板、
//! 截图自己的设置界面、settings.json 的手工编辑）而在至少两个 Rust 进程里被**消费**
//! （`engine` 的剪贴板三键、`core` 接管后的截图键）。
//!
//! 消费侧一旦各写一份，"两边必然漂移"就不是理论风险 —— 本仓库已经吃过一次同源教训：
//! 剪贴板引擎曾把 P 绑成暂停、把 Backspace 绑成删除，而设置页与 tooltip 写的是另一套，
//! 用户照着提示操作**静默删掉了数据**（见 `engine/src/hotkey.rs` 的 2026-09-14 记录）。
//!
//! C# 侧 `HotkeySpec` 是同一语义的权威实现；本 crate 是它在 Rust 侧的**唯一**对应物。
//! **不要**在别处再写一个解析器 —— 要么用这个，要么改这个。
//!
//! # 零依赖
//!
//! 只做 `&str → (修饰键位, 虚拟键码)` 的纯映射，不依赖 `windows` crate。
//! 调用方把裸 `u32` 包成自己的类型（`engine` 包成 `HOT_KEY_MODIFIERS`，`core` 直接用作 `u32`）。

/// 修饰键位（与 Win32 `MOD_*` 取值一致，故调用方可直接使用）。
pub mod modifiers {
    /// `MOD_ALT`
    pub const ALT: u32 = 0x0001;
    /// `MOD_CONTROL`
    pub const CONTROL: u32 = 0x0002;
    /// `MOD_SHIFT`
    pub const SHIFT: u32 = 0x0004;
    /// `MOD_WIN`
    pub const WIN: u32 = 0x0008;
    /// `MOD_NOREPEAT` —— 按住不连发。**注册时必须带上**（否则按住热键会连触发，
    /// 对"拉起截图"这种动作意味着一次长按弹出几十个截图进程）。
    pub const NO_REPEAT: u32 = 0x4000;
}

/// 一条解析成功的热键绑定。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct HotkeyBinding {
    /// 修饰键位（`modifiers::*` 的按位或；**不含** `NO_REPEAT` —— 那是注册策略不是用户意图）。
    pub modifiers: u32,
    /// 主键虚拟键码。
    pub vk: u32,
    /// 归一化后的规范串（固定顺序 `Ctrl+Alt+Shift+Win+主键`）。
    ///
    /// 用途是**变更检测**（"配置没变就别重注册"）与日志。它**不**与 C# `HotkeySpec.Pretty`
    /// 逐字对齐 —— 那边只用于显示。契约只有 `(modifiers, vk)` 这一对。
    pub canonical: String,
}

/// 解析规范串 → 绑定。无法解析返回 `None`（调用方应记日志并跳过，**不得**静默退回默认键）。
///
/// 语义（与 C# `HotkeySpec` 一致）：
/// - 修饰键名不区分大小写、顺序任意：`Ctrl`/`Control`、`Alt`、`Shift`、`Win`；
/// - **必须至少含一个修饰键**（与设置中心一致）—— 裸 `K` 会抢占全局按键，绝不能放行；
/// - 主键为**最后一个非修饰键**片段，支持 A-Z、D0-D9、F1-F24 与一批常见非字母键；
/// - 片段间允许空格（`"Ctrl + Shift + V"` 可用）。
pub fn parse(spec: &str) -> Option<HotkeyBinding> {
    let mut mods = 0u32;
    let mut main: Option<&str> = None;

    for part in spec.split('+').map(str::trim).filter(|p| !p.is_empty()) {
        match part.to_ascii_uppercase().as_str() {
            "CTRL" | "CONTROL" => mods |= modifiers::CONTROL,
            "ALT" => mods |= modifiers::ALT,
            "SHIFT" => mods |= modifiers::SHIFT,
            "WIN" => mods |= modifiers::WIN,
            _ => main = Some(part),
        }
    }

    let main = main?;
    let vk = main_key_vk(main)?;
    if mods == 0 {
        return None; // 裸主键会抢全局输入 —— 不放行
    }

    Some(HotkeyBinding {
        modifiers: mods,
        vk,
        canonical: canonical(mods, &main.to_ascii_uppercase()),
    })
}

/// 规范串（固定顺序，便于日志比对与变更检测）。
fn canonical(mods: u32, main_upper: &str) -> String {
    let mut parts: Vec<&str> = Vec::with_capacity(5);
    if mods & modifiers::CONTROL != 0 {
        parts.push("Ctrl");
    }
    if mods & modifiers::ALT != 0 {
        parts.push("Alt");
    }
    if mods & modifiers::SHIFT != 0 {
        parts.push("Shift");
    }
    if mods & modifiers::WIN != 0 {
        parts.push("Win");
    }
    let mut out = parts.join("+");
    if !out.is_empty() {
        out.push('+');
    }
    out.push_str(main_upper);
    out
}

/// 主键名（设置中心用 WPF `Key` 枚举名）→ 虚拟键码。未知返回 `None`。
fn main_key_vk(main: &str) -> Option<u32> {
    let up = main.to_ascii_uppercase();
    let bytes = up.as_bytes();

    // 单字母 A-Z → 0x41-0x5A（与 VK 编码同构）
    if bytes.len() == 1 && bytes[0].is_ascii_uppercase() {
        return Some(bytes[0] as u32);
    }

    let vk = match up.as_str() {
        "D0" => 0x30,
        "D1" => 0x31,
        "D2" => 0x32,
        "D3" => 0x33,
        "D4" => 0x34,
        "D5" => 0x35,
        "D6" => 0x36,
        "D7" => 0x37,
        "D8" => 0x38,
        "D9" => 0x39,
        "BACK" | "BACKSPACE" => 0x08,
        "TAB" => 0x09,
        "RETURN" | "ENTER" => 0x0D,
        "ESCAPE" | "ESC" => 0x1B,
        "SPACE" => 0x20,
        "PAGEUP" => 0x21,
        "PAGEDOWN" => 0x22,
        "END" => 0x23,
        "HOME" => 0x24,
        "LEFT" => 0x25,
        "UP" => 0x26,
        "RIGHT" => 0x27,
        "DOWN" => 0x28,
        "INSERT" => 0x2D,
        "DELETE" => 0x2E,
        "OEMPERIOD" | "DECIMAL" => 0xBE,
        "OEMCOMMA" => 0xBC,
        "OEMMINUS" | "SUBTRACT" => 0xBD,
        "OEMPLUS" | "ADD" => 0xBB,
        "CAPSLOCK" => 0x14,
        _ => {
            // F1-F24（VK_F1 = 0x70）
            if let Some(n) = up.strip_prefix('F').and_then(|s| s.parse::<u32>().ok())
                && (1..=24).contains(&n)
            {
                return Some(0x6F + n);
            }
            return None;
        }
    };
    Some(vk)
}

#[cfg(test)]
mod tests {
    use super::modifiers::*;
    use super::*;

    /// 剪贴板引擎三键（engine 的既有默认值）—— 这两条用例原样保留自 `engine/src/hotkey.rs`，
    /// 搬迁时**逐字对照**，确保抽取共享 crate 没有改变任何语义。
    #[test]
    fn parses_engine_default_specs() {
        let b = parse("Ctrl+Shift+V").unwrap();
        assert_eq!(b.modifiers, CONTROL | SHIFT);
        assert_eq!(b.vk, 0x56);

        assert_eq!(parse("Ctrl+Shift+P").unwrap().vk, 0x50);

        let b = parse("Ctrl+Shift+Backspace").unwrap();
        assert_eq!(b.vk, 0x08);
        assert_eq!(b.modifiers, CONTROL | SHIFT);
    }

    /// 截图热键（core 接管的那一枚）。
    #[test]
    fn parses_capture_default_spec() {
        let b = parse("Win+Shift+B").unwrap();
        assert_eq!(b.modifiers, WIN | SHIFT);
        assert_eq!(b.vk, 0x42, "B 必须是 0x42");
        assert_eq!(b.canonical, "Shift+Win+B", "规范串按固定顺序输出");
    }

    #[test]
    fn normalizes_case_and_order() {
        let b = parse("shift+ctrl+K").unwrap();
        assert_eq!(b.modifiers, CONTROL | SHIFT);
        assert_eq!(b.vk, 0x4B);
        assert_eq!(b.canonical, "Ctrl+Shift+K");

        let b = parse("Win+E").unwrap();
        assert_eq!(b.modifiers, WIN);
        assert_eq!(b.vk, 0x45);
    }

    #[test]
    fn tolerates_spaces_around_plus() {
        let b = parse(" Ctrl + Shift + V ").unwrap();
        assert_eq!(b.modifiers, CONTROL | SHIFT);
        assert_eq!(b.vk, 0x56);
    }

    #[test]
    fn rejects_invalid_specs() {
        // 裸主键：会抢全局输入，绝不能放行
        assert!(parse("K").is_none());
        assert!(parse("").is_none());
        assert!(parse("Ctrl+").is_none());
        // 未知主键
        assert!(parse("Ctrl+Shift+Qwerty").is_none());
        // F 键越界
        assert!(parse("Ctrl+Shift+F25").is_none());
        assert!(parse("Ctrl+Shift+F0").is_none());
    }

    #[test]
    fn maps_special_keys() {
        assert_eq!(parse("Ctrl+Alt+D1").unwrap().vk, 0x31);
        assert_eq!(parse("Ctrl+Shift+F5").unwrap().vk, 0x74);
        assert_eq!(parse("Ctrl+Shift+F24").unwrap().vk, 0x87);
        assert_eq!(parse("Ctrl+Shift+OemPeriod").unwrap().vk, 0xBE);
        assert_eq!(parse("Ctrl+Shift+Tab").unwrap().vk, 0x09);
        assert_eq!(parse("Ctrl+Shift+Space").unwrap().vk, 0x20);
        assert_eq!(parse("Ctrl+Shift+Enter").unwrap().vk, 0x0D);
    }

    /// `NO_REPEAT` **不属于用户意图**，解析结果里不得出现 ——
    /// 它是注册时的策略位，混进来会让"配置没变"的比较逻辑产生假差异。
    #[test]
    fn no_repeat_is_not_part_of_the_parsed_modifiers() {
        let b = parse("Win+Shift+B").unwrap();
        assert_eq!(b.modifiers & NO_REPEAT, 0);
    }

    /// 多个修饰键的顺序不影响结果（同一组修饰键 → 同一 bits）。
    #[test]
    fn modifier_order_is_irrelevant() {
        let a = parse("Win+Shift+B").unwrap();
        let b = parse("Shift+Win+B").unwrap();
        assert_eq!(a.modifiers, b.modifiers);
        assert_eq!(a.canonical, b.canonical);
    }
}
