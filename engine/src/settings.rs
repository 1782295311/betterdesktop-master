//! 引擎配置（默认值与 settings.json 读取；S5 接入完整热更新）。
//! 设置键与宿主扩展中心 `extensions.clipboard-history.*` 对齐。

use serde::{Deserialize, Serialize};

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case", default)]
pub struct Settings {
    /// 历史最大条数（超限驱逐最旧未收藏）。
    pub capacity: usize,
    /// 收藏条目上限。
    pub pinned_limit: usize,
    /// 未收藏条目保留天数（超期驱逐）。
    pub retention_days: u64,
    /// 文本/HTML/RTF 单条原始上限（字节，超限拒绝捕获）。
    pub max_text_bytes: usize,
    /// 文本总量上限（MB，压缩后）。
    pub max_content_total_mb: usize,
    /// 单图上限（MB）。
    pub max_image_mb: usize,
    /// 单图像素上限（宽×高）。
    pub max_image_pixels: u64,
    /// 缩略图宽度（px）。
    pub thumb_width: u32,
    /// 存储模式：full=原图+缩略图+文件副本；paths-only=图片仅缩略图、文件仅路径。
    pub storage_mode: StorageMode,
    /// 统一总量预算（MB：图片原图+文件副本+HTML 提取图+文本压缩）。
    pub max_total_mb: usize,
    /// 文件副本单文件上限（MB，超限仅记路径）。
    pub file_copy_max_mb: usize,
    /// HTML data URI 提取图单张上限（MB）。
    pub max_html_image_mb: usize,
    /// 监听开关（扩展中心 enabled）。
    pub enabled: bool,
    /// 剪贴板更新事件推送开关（灵动岛等只读订阅方）。
    pub push_events: bool,
    // ---------------- 【2026-09-13 · TieZ 对标 P1-4/P1-5/P2】新增项 ----------------
    /// 【P1-5 按应用清洗规则】用户自设规则表（忽略某应用 / 正则丢弃 / 正则替换）。
    /// 空数组 = 零行为变化（默认）。非法正则由 `rules::compile` 跳过该条并记日志，不 panic。
    pub app_rules: Vec<AppRule>,
    /// 【P1-4 命名格式透传】总开关：捕获 Excel/WPS 等应用的自定义命名格式并在写回时还原。
    pub named_format_passthrough: bool,
    /// 【P1-4】单条目最多保留的命名格式数（超限跳过）。
    pub named_format_max_count: usize,
    /// 【P1-4】单个命名格式字节上限（KB，超限跳过该格式——截断后的二进制是坏数据）。
    pub named_format_max_kb: usize,
    /// 【P1-4】单条目命名格式总字节上限（KB，超限停止采集）。
    pub named_format_total_kb: usize,
    /// 【P2-2 敏感信息】识别开关：命中手机号/身份证/邮箱/银行卡/密钥 → 打标记 + 面板预览遮罩。
    pub sensitive_detection: bool,
    /// 【P2-2】遮罩保留的前缀可见字符数。
    pub sensitive_mask_leading: usize,
    /// 【P2-2】遮罩保留的后缀可见字符数。
    pub sensitive_mask_trailing: usize,
    // ---------------- 【2026-09-16 · 热键配置驱动】新增项 ----------------
    /// 【热键】打开面板的全局热键（"Ctrl+Shift+V" 格式；空串 = 停用）。
    /// 由设置中心写入 `extensions.clipboard-history.open-panel-hotkey`，引擎启动时按此注册。
    pub open_panel_hotkey: String,
    /// 【热键】收藏视图的全局热键。
    pub favorites_hotkey: String,
    /// 【热键】暂停/恢复监听的全局热键。
    pub toggle_pause_hotkey: String,
}

/// 【P1-5 按应用清洗规则 · 2026-09-13】一条用户规则。
///
/// 匹配语义：`app` 为空 = 匹配**所有**应用（相当于内容级规则）；否则用「进程名或窗口标题
/// 的小写子串」匹配（与隐私黑名单同口径，用户不必记精确进程名）。
#[derive(Clone, Debug, Default, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "kebab-case", default)]
pub struct AppRule {
    /// 应用匹配串（进程名或窗口标题子串；空 = 所有应用）。
    pub app: String,
    /// 动作：`ignore` 整个应用不记录 / `drop` 命中正则即丢弃 / `replace` 命中正则即替换。
    pub action: AppRuleAction,
    /// 正则（`ignore` 时忽略；`replace`/`drop` 必填）。
    pub pattern: String,
    /// 替换文本（仅 `replace` 使用；支持 `$1` 分组引用）。
    pub replacement: String,
}

/// 按应用规则的动作。
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum AppRuleAction {
    /// 该应用的复制内容**一律不入库**。
    #[default]
    Ignore,
    /// 命中 `pattern` 的内容**丢弃**（不入库）。
    Drop,
    /// 命中 `pattern` 的内容**替换** `replacement` 后入库。
    Replace,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "kebab-case")]
pub enum StorageMode {
    #[default]
    Full,
    PathsOnly,
}

impl Default for Settings {
    fn default() -> Self {
        Settings {
            capacity: 10000,
            pinned_limit: 200,
            retention_days: 90,
            max_text_bytes: 10 * 1024 * 1024, // 10MB
            max_content_total_mb: 100,
            max_image_mb: 64,
            max_image_pixels: 100_000_000, // 100MP
            thumb_width: 480,
            storage_mode: StorageMode::Full,
            max_total_mb: 1024,
            file_copy_max_mb: 64,
            max_html_image_mb: 2,
            enabled: true,
            push_events: true,
            app_rules: Vec::new(),
            named_format_passthrough: true,
            named_format_max_count: 8,
            named_format_max_kb: 1024,
            named_format_total_kb: 4096,
            sensitive_detection: true,
            sensitive_mask_leading: 3,
            sensitive_mask_trailing: 2,
            open_panel_hotkey: "Ctrl+Shift+V".to_string(),
            favorites_hotkey: "Ctrl+Shift+P".to_string(),
            toggle_pause_hotkey: "Ctrl+Shift+Backspace".to_string(),
        }
    }
}

/// 从宿主 settings.json 合并 `extensions.clipboard-history.*`（缺省用默认值；解析失败保持默认）。
/// 宿主结构：`{"extensions": {"clipboard-history": {…}}}`；引擎侧字段 kebab-case 直接对应节内键。
pub fn load(path: Option<&std::path::Path>) -> Settings {
    let settings = Settings::default();
    let Some(path) = path else { return settings };
    let Ok(text) = std::fs::read_to_string(path) else {
        return settings;
    };
    // 容错 UTF-8 BOM（PowerShell/编辑器写文件可能带 EF BB BF）
    let text = text.strip_prefix('\u{feff}').unwrap_or(&text);
    let Ok(root) = serde_json::from_str::<serde_json::Value>(text) else {
        crate::log::warn("settings.json parse failed; using defaults");
        return settings;
    };
    let Some(section) = root
        .get("extensions")
        .and_then(|e| e.get("clipboard-history"))
    else {
        // 宿主尚未写入剪贴板扩展配置 → 默认值（首次运行正常路径）
        return settings;
    };
    match serde_json::from_value::<Settings>(section.clone()) {
        Ok(s) => s,
        Err(e) => {
            crate::log::warn(format!(
                "extensions.clipboard-history section invalid ({e}); using defaults"
            ));
            settings
        }
    }
}

/// 100KB：超过此阈值的文本内容进 content.bin（Deflate+DPAPI 懒加载）。
pub const CONTENT_EXTERNAL_THRESHOLD: usize = 100 * 1024;

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn defaults_sane() {
        let s = Settings::default();
        assert_eq!(s.capacity, 10000);
        assert_eq!(s.thumb_width, 480);
        assert_eq!(s.storage_mode, StorageMode::Full);
        assert!(s.enabled);
    }

    #[test]
    fn parse_partial_settings() {
        let dir = std::env::temp_dir().join(format!("bd-engine-settings-test-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let p = dir.join("settings.json");
        // 宿主结构：extensions.clipboard-history 节
        std::fs::write(
            &p,
            r#"{"extensions": {"clipboard-history": {"capacity": 500, "storage-mode": "paths-only"}}}"#,
        )
        .unwrap();
        let raw = std::fs::read_to_string(&p).unwrap();
        let parsed: Result<serde_json::Value, _> = serde_json::from_str(&raw);
        assert!(parsed.is_ok(), "parse failed: {parsed:?}");
        let s = load(Some(&p));
        assert_eq!(s.capacity, 500);
        assert_eq!(s.storage_mode, StorageMode::PathsOnly);
        // 未指定的字段用默认
        assert_eq!(s.thumb_width, 480);
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn root_level_without_extensions_uses_defaults() {
        let dir = std::env::temp_dir().join(format!("bd-engine-settings-root-test-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let p = dir.join("settings.json");
        // 宿主有配置但无 clipboard-history 节 → 默认（引擎尚未被配置）
        std::fs::write(&p, r#"{"extensions": {"other": 1}}"#).unwrap();
        let s = load(Some(&p));
        assert_eq!(s.capacity, 10000);
        assert!(s.enabled);
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn missing_file_defaults() {
        let s = load(Some(&std::path::Path::new("Z:/definitely-missing/settings.json")));
        assert_eq!(s.capacity, 10000);
    }

    #[test]
    fn utf8_bom_tolerated() {
        let dir = std::env::temp_dir().join(format!("bd-engine-settings-bom-test-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let p = dir.join("settings.json");
        // 带 UTF-8 BOM 的宿主配置
        let mut bytes = vec![0xEF, 0xBB, 0xBF];
        bytes.extend_from_slice(
            br#"{"extensions": {"clipboard-history": {"capacity": 7}}}"#,
        );
        std::fs::write(&p, &bytes).unwrap();
        let s = load(Some(&p));
        assert_eq!(s.capacity, 7);
        let _ = std::fs::remove_dir_all(&dir);
    }
}
