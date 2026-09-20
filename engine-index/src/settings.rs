//! 引擎配置：默认值 + 宿主 `settings.json` 的 `extensions.index.*` 节读取。
//!
//! 【键位约定】与剪贴板引擎（`extensions.clipboard-history.*`）同构：宿主侧完整键为
//! `extensions.index.app-source-backend` / `extensions.index.max-entries` /
//! `extensions.index.scan-roots` / `extensions.index.enabled`。
//! 引擎侧字段用 kebab-case 与节内键一一对应（serde rename_all）。
//!
//! 【错误分层】读取失败一律「保持默认 + 记日志」，绝不 panic（config 缺失是首次运行的正常路径）；
//! 而**写入**（apply_settings）走严格校验：非法值返回错误给调用方，不静默接受。

use serde::{Deserialize, Serialize};

/// `app-source-backend` 的唯一合法值集合（工具型项目纪律：输入校验显式化，不静默兜底）。
pub const BACKEND_ENGINE: &str = "engine";
pub const BACKEND_LOCAL: &str = "local";

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case", default)]
pub struct Settings {
    /// 应用源后端：`engine` = 查索引引擎；`local` = 强制走 C# 本地全量扫描（回退/排障开关）。
    pub app_source_backend: String,
    /// 索引条目上限（应用 + 文件合计的硬上限；超限拒收并记降级原因）。
    pub max_entries: usize,
    /// 文件索引根目录覆盖（空数组 = 用引擎内置根集语义，即现有 C# 兜底扫描的根集）。
    pub scan_roots: Vec<String>,
    /// 索引总开关（false = 引擎空转不构建，消费者自动回退本地）。
    pub enabled: bool,
}

impl Default for Settings {
    fn default() -> Self {
        Settings {
            app_source_backend: BACKEND_ENGINE.to_string(),
            max_entries: 200_000,
            scan_roots: Vec::new(),
            enabled: true,
        }
    }
}

impl Settings {
    /// 是否使用引擎后端（非法值在 apply_settings 阶段已被拒，此处只做等值判定）。
    ///
    /// M2 起由索引任务与消费者侧判定使用；M1 仅测试引用。
    #[allow(dead_code)]
    pub fn backend_is_engine(&self) -> bool {
        self.app_source_backend == BACKEND_ENGINE
    }
}

/// `apply_settings` 的部分更新载荷：字段全部可选，只覆盖显式给出的键。
#[derive(Clone, Debug, Default, Deserialize)]
#[serde(rename_all = "kebab-case", default)]
pub struct SettingsPatch {
    pub app_source_backend: Option<String>,
    pub max_entries: Option<usize>,
    pub scan_roots: Option<Vec<String>>,
    pub enabled: Option<bool>,
}

/// 校验并应用增量设置。Err = 非法输入（调用方按 -32602 Invalid params 返回，不得部分生效）。
pub fn apply_patch(settings: &mut Settings, patch: &SettingsPatch) -> Result<(), String> {
    // 先整体校验（原子性：任一字段非法则一个字段都不改——避免"半套配置"这种最难排查的状态）
    if let Some(backend) = &patch.app_source_backend {
        if backend != BACKEND_ENGINE && backend != BACKEND_LOCAL {
            return Err(format!(
                "app-source-backend 非法值 '{backend}'（合法值：{BACKEND_ENGINE} | {BACKEND_LOCAL}）"
            ));
        }
    }

    if let Some(backend) = &patch.app_source_backend {
        settings.app_source_backend = backend.clone();
    }
    if let Some(max) = patch.max_entries {
        settings.max_entries = max;
    }
    if let Some(roots) = &patch.scan_roots {
        settings.scan_roots = roots.clone();
    }
    if let Some(enabled) = patch.enabled {
        settings.enabled = enabled;
    }
    Ok(())
}

/// 从宿主 settings.json 合并 `extensions.index.*`（缺省用默认值；解析失败保持默认 + 记日志）。
/// 宿主结构：`{"extensions": {"index": {…}}}`。
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
    let Some(section) = root.get("extensions").and_then(|e| e.get("index")) else {
        // 宿主尚未写入索引配置 → 默认值（首次运行正常路径）
        return settings;
    };
    match serde_json::from_value::<Settings>(section.clone()) {
        Ok(s) => s,
        Err(e) => {
            crate::log::warn(format!(
                "extensions.index section invalid ({e}); using defaults"
            ));
            settings
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn tmp_settings_file(tag: &str, body: &[u8]) -> std::path::PathBuf {
        let dir = std::env::temp_dir().join(format!(
            "bd-index-settings-{tag}-{}",
            std::process::id()
        ));
        std::fs::create_dir_all(&dir).unwrap();
        let p = dir.join("settings.json");
        std::fs::write(&p, body).unwrap();
        p
    }

    #[test]
    fn defaults_sane() {
        let s = Settings::default();
        assert_eq!(s.app_source_backend, BACKEND_ENGINE);
        assert_eq!(s.max_entries, 200_000);
        assert!(s.scan_roots.is_empty());
        assert!(s.enabled);
        assert!(s.backend_is_engine());
    }

    #[test]
    fn parse_partial_settings() {
        let p = tmp_settings_file(
            "partial",
            br#"{"extensions": {"index": {"app-source-backend": "local", "max-entries": 500}}}"#,
        );
        let s = load(Some(&p));
        assert_eq!(s.app_source_backend, BACKEND_LOCAL);
        assert_eq!(s.max_entries, 500);
        // 未指定的字段用默认
        assert!(s.enabled);
        assert_eq!(s.scan_roots.len(), 0);
        let _ = std::fs::remove_dir_all(p.parent().unwrap());
    }

    #[test]
    fn missing_file_defaults() {
        let s = load(Some(std::path::Path::new(
            "Z:/definitely-missing/settings.json",
        )));
        assert!(s.backend_is_engine());
    }

    #[test]
    fn utf8_bom_tolerated() {
        let mut bytes = vec![0xEF, 0xBB, 0xBF];
        bytes.extend_from_slice(br#"{"extensions": {"index": {"max-entries": 7}}}"#);
        let p = tmp_settings_file("bom", &bytes);
        let s = load(Some(&p));
        assert_eq!(s.max_entries, 7);
        let _ = std::fs::remove_dir_all(p.parent().unwrap());
    }

    /// 边界：无 index 节时不得误读别的扩展配置。
    #[test]
    fn other_extension_sections_ignored() {
        let p = tmp_settings_file(
            "other-section",
            br#"{"extensions": {"clipboard-history": {"capacity": 1}}}"#,
        );
        let s = load(Some(&p));
        assert!(s.backend_is_engine());
        assert_eq!(s.max_entries, 200_000);
        let _ = std::fs::remove_dir_all(p.parent().unwrap());
    }

    #[test]
    fn patch_applies_only_given_fields() {
        let mut s = Settings::default();
        apply_patch(
            &mut s,
            &SettingsPatch {
                max_entries: Some(10),
                ..Default::default()
            },
        )
        .unwrap();
        assert_eq!(s.max_entries, 10);
        assert!(s.enabled, "未给出的字段不得被改动");
        assert_eq!(s.app_source_backend, BACKEND_ENGINE);
    }

    /// 异常：非法 backend 必须被拒，且**不得部分生效**（原子性）。
    #[test]
    fn patch_rejects_invalid_backend_atomically() {
        let mut s = Settings::default();
        let err = apply_patch(
            &mut s,
            &SettingsPatch {
                app_source_backend: Some("bogus".into()),
                max_entries: Some(999),
                ..Default::default()
            },
        )
        .unwrap_err();
        assert!(err.contains("bogus"), "错误信息须含非法值：{err}");
        assert_eq!(s.max_entries, 200_000, "校验失败时不得部分生效");
        assert_eq!(s.app_source_backend, BACKEND_ENGINE);
    }

    #[test]
    fn patch_deserializes_kebab_case() {
        let patch: SettingsPatch =
            serde_json::from_str(r#"{"app-source-backend":"local"}"#).unwrap();
        assert_eq!(patch.app_source_backend.as_deref(), Some(BACKEND_LOCAL));
        let empty: SettingsPatch = serde_json::from_str("{}").unwrap();
        assert!(empty.app_source_backend.is_none());
    }
}
