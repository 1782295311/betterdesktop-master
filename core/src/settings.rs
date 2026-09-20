//! settings.json 的**扁平键读 / 写**视图。
//!
//! # 写者身份（2026-09-19 变更，见计划 §13.19 ADR）
//!
//! **core 现在是唯一的 settings 写者**（此前只有 C# `SettingsService`；`@ctl set` 当时刻意未实现）。
//! `SettingsService` 降为共享库，其互斥与原子写实现作为**兼容期**的另一侧保留。
//! 兼容期 = S5-2a → S7，两侧都可能写 ⇒ 靠**同名互斥**（[`SAVE_MUTEX_NAME`]）保证不冲突。
//!
//! # 两种键形式都要认（§13.20 的真 bug）
//!
//! 生产形式是**扁平点分键**（`"components.desktop": false`）—— C# 写出的就是这种；
//! 而读取器原先只走**嵌套**路径 ⇒ 凡由 C# 写的开关对 core **从未生效**（自 S4-3 的 gate 起）。
//! 现在：读**两种都认**（扁平优先），写**只写扁平**。
//!
//! # 纪律（读侧，移植自 `engine/src/settings.rs`）
//!
//!   - 容错 UTF-8 BOM（PowerShell / 编辑器写文件可能带 EF BB BF）；
//!   - 解析失败**保持默认值并记日志**，绝不 panic（配置坏掉不能让 core 起不来）；
//!   - 文件缺失 = 默认值（首次运行正常路径）。

use indexmap::IndexMap;
use std::path::PathBuf;

/// 设置文件只读快照。
pub struct Settings {
    root: serde_json::Value,
}

impl Settings {
    /// 读取 `%APPDATA%\BetterDesktop\settings.json`（缺失/损坏 → 空对象 = 全默认）。
    pub fn load() -> Self {
        Self::load_from(&default_path())
    }

    /// 可注入路径（单测锚点）。
    pub fn load_from(path: &std::path::Path) -> Self {
        match std::fs::read_to_string(path) {
            Ok(text) => Self::from_str(&text),
            Err(_) => Self::empty(),
        }
    }

    /// 从文本构造（纯函数，不含 I/O）—— `load_from` 与单测共用，保证"测的路径 = 跑的路径"。
    pub fn from_str(text: &str) -> Self {
        let text = text.strip_prefix('\u{feff}').unwrap_or(text);
        match serde_json::from_str::<serde_json::Value>(text) {
            Ok(root) if root.is_object() => Self { root },
            Ok(_) => {
                crate::log::warn("settings.json root is not an object; using defaults");
                Self::empty()
            }
            Err(e) => {
                crate::log::warn(format!("settings.json parse failed ({e}); using defaults"));
                Self::empty()
            }
        }
    }

    /// 空配置（= 全默认）。
    pub fn empty() -> Self {
        Self {
            root: serde_json::Value::Object(Default::default()),
        }
    }

    /// 按点分键路径取 bool。类型不符（如字符串 "true"）→ 默认值 + 警告，不猜。
    pub fn get_bool(&self, key: &str, default: bool) -> bool {
        match self.get(key) {
            Some(serde_json::Value::Bool(b)) => *b,
            Some(other) => {
                crate::log::warn(format!(
                    "settings key '{key}' is not a bool ({}); using default {default}",
                    kind_of(other)
                ));
                default
            }
            None => default,
        }
    }

    /// 按点分键路径取 bool，**键不存在或类型不符时返回 `None`**（不套默认值）。
    ///
    /// 与 [`Self::get_bool`] 的区别：`get_bool` 用于"core 自己判开关"（需要默认值兜底），
    /// 本方法用于"把值原样报给调用方"（`@ctl get`）—— 那里编造默认值等于撒谎。
    pub fn try_get_bool(&self, key: &str) -> Option<bool> {
        match self.get(key) {
            Some(serde_json::Value::Bool(b)) => Some(*b),
            _ => None,
        }
    }

    /// 按键路径取值：**两种形式都认**（顺序有意）。
    ///
    /// 1. **扁平点分键** —— `"components.desktop"` 作为**字面量顶层键**。这是**生产形式**：
    ///    C# `SettingsService` 写出的就是这种（磁盘实证见计划 §13.20）。
    /// 2. **嵌套路径** —— `{"components":{"desktop":…}}`。兼容历史与手工编辑的形式。
    ///
    /// 【为什么扁平优先】它是写入器的实际输出，命中率最高；更重要的是，当两种形式**同时存在且值不同**时，
    /// 这个顺序给出**确定**的取法。若反过来（嵌套优先），历史遗留的嵌套值会**静默压过**
    /// 用户刚在托盘里改出的扁平值 —— 用户会看到"我明明关了它却还开着"，且无从排查。
    ///
    /// 【为什么必须两种都认】读取器（Rust core）与写入器（C# `SettingsService`）是两种语言的实现，
    /// 兼容期（S5-2a → S7）内两种形式可能同时存在于同一份文件。**只认一种 = 另一种静默失效** ——
    /// 那正是 §13.20"gate 从未生效"的成因。
    fn get(&self, key: &str) -> Option<&serde_json::Value> {
        // ① 扁平点分键（生产形式）
        if let Some(flat) = self.root.get(key) {
            return Some(flat);
        }
        // ② 嵌套路径（兼容形式）
        let mut cur = &self.root;
        for part in key.split('.') {
            cur = cur.get(part)?;
        }
        Some(cur)
    }
}

fn kind_of(v: &serde_json::Value) -> &'static str {
    match v {
        serde_json::Value::Null => "null",
        serde_json::Value::Bool(_) => "bool",
        serde_json::Value::Number(_) => "number",
        serde_json::Value::String(_) => "string",
        serde_json::Value::Array(_) => "array",
        serde_json::Value::Object(_) => "object",
    }
}

pub fn default_path() -> PathBuf {
    let roaming = std::env::var("APPDATA").unwrap_or_else(|_| ".".to_string());
    PathBuf::from(roaming)
        .join("BetterDesktop")
        .join("settings.json")
}

// ───────────────────────────── 写入（S5-2a） ─────────────────────────────

/// 跨进程落盘互斥名 —— **与 C# `SettingsService.SaveMutexName` 逐字一致**。
///
/// 它是契约的一部分：名字不一致 = 两侧各拿一把锁 = **真双写**（兼容期最危险的失败形态）。
/// 故用单测钉住本字面量（同 `verify-protocol-contract` 的跨侧字面量手法）。
pub const SAVE_MUTEX_NAME: &str = "Local\\BetterDesktop.Settings.json";

/// 互斥获取超时（**与 C# 一致**：2 秒拿不到就跳过本次写入，不阻塞调用方）。
const MUTEX_TIMEOUT_MS: u32 = 2000;

/// 落盘互斥（RAII：Drop 时释放并关闭句柄）。
struct SaveMutex(windows::Win32::Foundation::HANDLE);

impl SaveMutex {
    /// 获取互斥；`None` = 超时（调用方必须**跳过**本次写入，而不是硬写）。
    ///
    /// 覆盖范围是**整个「读-合并-写」**，不是只有"写" —— 互斥本身只保护"写-写"，
    /// 而 A 读 / B 读 / A 写 / B 写（基于旧读）会把 A 的修改静默写回旧值。
    fn acquire() -> Option<Self> {
        use windows::Win32::System::Threading::{CreateMutexW, WaitForSingleObject};

        // `WAIT_OBJECT_0`(0) / `WAIT_ABANDONED`(0x80)：用数值直接比较，
        // 省掉一处依赖 windows-rs 常量所在模块的导入（它们的路径随版本变过）。
        const WAIT_OBJECT_0: u32 = 0;
        const WAIT_ABANDONED: u32 = 0x80;

        let name = pcwstr(SAVE_MUTEX_NAME);
        let handle =
            unsafe { CreateMutexW(None, false, windows::core::PCWSTR(name.as_ptr())) }.ok()?;
        let wait = unsafe { WaitForSingleObject(handle, MUTEX_TIMEOUT_MS) }.0;
        if wait == WAIT_OBJECT_0 || wait == WAIT_ABANDONED {
            // WAIT_ABANDONED：上一个持有者异常退出，互斥量已归本线程（与 C# 的处理一致）
            Some(Self(handle))
        } else {
            unsafe {
                let _ = windows::Win32::Foundation::CloseHandle(handle);
            }
            None
        }
    }
}

impl Drop for SaveMutex {
    fn drop(&mut self) {
        unsafe {
            let _ = windows::Win32::System::Threading::ReleaseMutex(self.0);
            let _ = windows::Win32::Foundation::CloseHandle(self.0);
        }
    }
}

/// `&str` → NUL 结尾的 UTF-16（本地小工具：避免为一个调用引入跨模块依赖）。
fn pcwstr(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

/// 写入一个**扁平点分键**（值以 JSON 字面量给出）。**core 是唯一写者**（§13.19 ADR）。
///
/// # 契约（与 C# `SettingsService.SaveLocked` 逐字对齐）
///
/// 1. 互斥覆盖整个「读-合并-写」（见 [`SaveMutex::acquire`]）；
/// 2. **拿不到互斥就跳过**（2 秒超时）—— 不等待、不硬写；
/// 3. **读不到就不写**：文件存在却解析不了 ⇒ 返回 `Err` 且**不动磁盘**
///    （宁可本次不落盘，也绝不用残缺快照把用户设置抹成"只剩本次改的键"）；
/// 4. **只改这一个键**，其余键与其**顺序**原样保留；
/// 5. **原子替换**：先写 `<file>.tmp`，再 rename 覆盖；
/// 6. **格式复刻 `SettingsService`**：紧凑单行 + [`escape_like_dotnet`] 的 ASCII 转义。
///
/// 为什么复刻 `SettingsService` 而不是 CLI headless 直写的缩进形式：后者是 **S7 要删掉**的
///（§6.7：整文件直写 → 改走控制管道），而 `SettingsService` 是长期存活的写者。
pub fn set_flat(key: &str, value: &serde_json::Value) -> Result<(), String> {
    set_flat_at(&default_path(), key, value)
}

/// 可注入路径（单测锚点 ⇒ **测的路径 = 跑的路径**）。
pub fn set_flat_at(
    path: &std::path::Path,
    key: &str,
    value: &serde_json::Value,
) -> Result<(), String> {
    let Some(_guard) = SaveMutex::acquire() else {
        return Err(format!(
            "settings save mutex ({SAVE_MUTEX_NAME}) timed out after {MUTEX_TIMEOUT_MS}ms; write skipped"
        ));
    };

    // ① 读（读不到就不写）。用 IndexMap 保序：**键序 = 磁盘原序**。
    let mut map: IndexMap<String, serde_json::Value> = if path.exists() {
        let text = std::fs::read_to_string(path).map_err(|e| format!("read failed: {e}"))?;
        let text = text.strip_prefix('\u{feff}').unwrap_or(&text);
        serde_json::from_str(text)
            .map_err(|e| format!("parse failed ({e}); refusing to overwrite the file"))?
    } else {
        IndexMap::new() // 尚无文件：空集合（与 C# TryReadStore 一致）
    };

    // ② 合并：只改这一个键。已存在的键 `insert` **保持原位**（IndexMap 语义）。
    map.insert(key.to_string(), value.clone());

    // ③ 序列化：紧凑 + 复刻 .NET 转义
    let json = escape_like_dotnet(
        &serde_json::to_string(&map).map_err(|e| format!("serialize failed: {e}"))?,
    );

    // ④ 原子替换：临时文件 + rename（Windows 上 rename 会覆盖已存在文件）
    if let Some(dir) = path.parent()
        && !dir.as_os_str().is_empty()
    {
        std::fs::create_dir_all(dir).map_err(|e| format!("create_dir_all failed: {e}"))?;
    }
    let tmp = PathBuf::from(format!("{}.tmp", path.display()));
    std::fs::write(&tmp, json.as_bytes()).map_err(|e| format!("write temp failed: {e}"))?;
    std::fs::rename(&tmp, path).map_err(|e| format!("rename failed: {e}"))?;

    crate::log::info(format!("settings: set '{key}' ({} bytes)", json.len()));
    Ok(())
}

/// 复刻 .NET `JsonSerializer` 默认的转义（serde_json 默认**不**转义这些）。
///
/// 差异点：.NET 默认 `JavaScriptEncoder` 会把 `<` `>` `&` `'` `+` 与**非 ASCII** 转成 `\uXXXX`。
/// 不补齐这点，同一份文件会被两个写者写成**字节不同**的等价 JSON（每切换一次开关就整文件 diff）。
///
/// 安全性：只对**已序列化**的 JSON 文本逐字符替换；JSON 的结构字符全是 ASCII，
/// 非 ASCII 只可能出现在字符串字面量内部 ⇒ 不会破坏结构。
///
/// **已知差异（如实标注）**：码位 > `0xFFFF` 的字符（emoji 等）原样保留而不转成代理对 ——
/// 生成错误的 `\uXXXX` 会**破坏文件**，宁可保留原字符（JSON 合法）而接受这一处分歧。
fn escape_like_dotnet(json: &str) -> String {
    const NEEDS: [(char, &str); 5] = [
        ('<', "\\u003C"),
        ('>', "\\u003E"),
        ('&', "\\u0026"),
        ('\'', "\\u0027"),
        ('+', "\\u002B"),
    ];

    let mut out = String::with_capacity(json.len());
    for ch in json.chars() {
        if let Some((_, esc)) = NEEDS.iter().find(|(c, _)| *c == ch) {
            out.push_str(esc);
        } else if ch as u32 > 0x7F && (ch as u32) <= 0xFFFF {
            out.push_str(&format!("\\u{:04X}", ch as u32));
        } else {
            out.push(ch);
        }
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    fn write_temp(name: &str, body: &[u8]) -> PathBuf {
        let dir = std::env::temp_dir().join(format!("bd-core-settings-{}-{}", std::process::id(), name));
        std::fs::create_dir_all(&dir).unwrap();
        let p = dir.join("settings.json");
        std::fs::write(&p, body).unwrap();
        p
    }

    #[test]
    fn reads_flat_nested_keys() {
        let p = write_temp("nested", br#"{"components":{"dock":false,"wintaskbar":true},"extensions":{"index":{"enabled":false}}}"#);
        let s = Settings::load_from(&p);
        assert!(!s.get_bool("components.dock", true));
        assert!(s.get_bool("components.wintaskbar", false));
        assert!(!s.get_bool("extensions.index.enabled", true));
        // 缺失键 → 默认
        assert!(s.get_bool("components.desktop", true));
    }

    /// **扁平点分键** —— C# `SettingsService` 写出的**生产形式**，必须被读到。
    ///
    /// 磁盘实证（`%APPDATA%\BetterDesktop\settings.json`，152 字节）：
    /// `{"appearance.skin.active":"","appearance.windowTint":"#1F1F22",…}` —— 键是**字面量点分串**，不是嵌套对象。
    ///
    /// 本用例钉的是那个真 bug（§13.20）：读取器原只走嵌套路径 ⇒ C# 写出的键**取不到 → 回落默认** ⇒
    /// **自 S4-3 引入 gate 起，凡由 C# 写入的开关对 core 从未生效**。
    #[test]
    fn reads_flat_dotted_keys_as_written_by_the_csharp_writer() {
        let p = write_temp(
            "flat",
            br##"{"appearance.accent":"#0A84FF","components.desktop":false,"extensions.index.enabled":false}"##,
        );
        let s = Settings::load_from(&p);

        assert!(
            !s.get_bool("components.desktop", true),
            "扁平键 `\"components.desktop\": false` 必须被读到 —— 这是托盘/设置中心写出的生产形式"
        );
        assert!(
            !s.get_bool("extensions.index.enabled", true),
            "同上：扁平键必须生效，否则 gate 对真实用户路径从未生效"
        );
        assert_eq!(
            s.try_get_bool("components.desktop"),
            Some(false),
            "@ctl get 也必须报告用户真正写的值，而不是编造默认"
        );
        // 缺失键仍回落默认（不得因为支持了扁平形式就把"缺失"读成 false）
        assert!(s.get_bool("components.dock", true));
    }

    #[test]
    fn bom_tolerated() {
        let mut bytes = vec![0xEF, 0xBB, 0xBF];
        bytes.extend_from_slice(br#"{"components":{"dock":true}}"#);
        let p = write_temp("bom", &bytes);
        assert!(Settings::load_from(&p).get_bool("components.dock", false));
    }

    #[test]
    fn missing_file_uses_defaults() {
        let p = std::path::Path::new("Z:/definitely-missing/settings.json");
        let s = Settings::load_from(p);
        assert!(s.get_bool("components.dock", true));
    }

    #[test]
    fn corrupt_file_uses_defaults_not_panic() {
        let p = write_temp("corrupt", b"{ this is not json");
        assert!(Settings::load_from(&p).get_bool("components.dock", true));
    }

    #[test]
    fn wrong_type_uses_default() {
        let p = write_temp("wrongtype", br#"{"components":{"dock":"true"}}"#);
        // 字符串 "true" 不得被猜成 true —— 保留默认值
        assert!(Settings::load_from(&p).get_bool("components.dock", true));
        assert!(!Settings::load_from(&p).get_bool("components.dock", false));
    }

    // ───────────────────── S5-2a：写入器 ─────────────────────

    /// 写入 → 读回；**键序保持**、**往返字节稳定**、**紧凑单行**（复刻 `SettingsService`）。
    #[test]
    fn write_keeps_order_and_is_byte_stable() {
        let p = write_temp("w", br##"{"appearance.accent":"#0A84FF","components.dock":true}"##);
        set_flat_at(&p, "components.desktop", &serde_json::Value::Bool(false)).unwrap();
        let first = std::fs::read_to_string(&p).unwrap();

        // 改**同一个**键：位置不变、其余字节不变（只有值不同）
        set_flat_at(&p, "components.desktop", &serde_json::Value::Bool(true)).unwrap();
        let second = std::fs::read_to_string(&p).unwrap();

        assert_eq!(
            first.replace("false", "true"),
            second,
            "改同一个键不得移动它的位置，也不得改动其它任何字节"
        );
        assert!(!second.contains('\n'), "必须是紧凑单行: {second}");
        assert!(second.starts_with(r#"{"appearance.accent"#), "键序必须保持: {second}");
        assert!(
            second.ends_with("\"components.desktop\":true}"),
            "新键应追加在末尾: {second}"
        );
        // 与读取器闭环：写进去的扁平键必须读得出来。
        // 注意此刻磁盘上的值是最新那次写入的 `true`（故默认值取 `false`，以证明"读到的是文件里的值"而非默认值）。
        assert!(Settings::load_from(&p).get_bool("components.desktop", false));
        assert!(Settings::load_from(&p).get_bool("components.dock", true));
    }

    /// 文件存在但坏掉 ⇒ **拒绝写入且不动磁盘**（"读不到就不写"）。
    #[test]
    fn write_refuses_when_existing_file_is_unparseable() {
        let p = write_temp("wbad", b"{ not json");
        let before = std::fs::read(&p).unwrap();
        assert!(set_flat_at(&p, "components.dock", &serde_json::Value::Bool(false)).is_err());
        assert_eq!(
            std::fs::read(&p).unwrap(),
            before,
            "解析不了就必须拒绝写入，绝不能拿残缺快照覆盖用户设置"
        );
    }

    /// 互斥名是**跨语言契约字面量**：改它必须同时改 C# `SettingsService.SaveMutexName`。
    #[test]
    fn save_mutex_name_is_pinned() {
        assert_eq!(SAVE_MUTEX_NAME, "Local\\BetterDesktop.Settings.json");
    }

    /// 转义复刻 .NET 默认行为 —— 否则两个写者写同一份文件会产出**字节不同**的等价 JSON。
    #[test]
    fn escaping_matches_dotnet_defaults() {
        assert_eq!(escape_like_dotnet("<&>'+"), "\\u003C\\u0026\\u003E\\u0027\\u002B");
        assert_eq!(escape_like_dotnet("中"), "\\u4E2D");
        // 结构字符与普通 ASCII 不得被动
        assert_eq!(escape_like_dotnet(r#"{"a":true}"#), r#"{"a":true}"#);
    }
}
