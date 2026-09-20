//! 引擎数据模型。
//!
//! 【权威源纪律 · 计划 §5.1】引擎只产出「磁盘/开始菜单/注册表里有哪些 exe」这类**事实**，
//! 一律 **不产出** `AppItem` 语义（固定态、分组、IconCacheKey、分类）——那是 shell-app-source
//! 的职责。此处 `AppCandidate` 是 raw candidate，命名为 candidate 即为此意。
//!
//! JSON 一律 camelCase（与 C# 侧契约对齐，照抄 engine/src/model.rs 的跨语言约定）。

use serde::{Deserialize, Serialize};

/// 应用候选（raw）——三源枚举的产物，尚未经 shell-app-source 升格为 AppItem。
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AppCandidate {
    /// 磁盘路径（.lnk 保留自身路径，目标见 `target_path`）。
    pub path: String,
    /// 解析出的目标路径（非 lnk 时等于 `path`；解析失败回退自身路径——506 红线 3）。
    pub target_path: String,
    /// 显示名候选（优先 `FileVersionInfo.FileDescription`，取不到用文件名——506 红线 4）。
    pub name_hint: String,
    /// 来源：`start-menu` / `program-files` / `registry-uninstall`。
    pub source: String,
    /// 图标键候选（供 M4 图标接管复用；M1 恒为空串）。
    pub icon_key: String,
}

/// 文件命中（查询结果，非全量返回）。
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FileHit {
    pub path: String,
    pub name: String,
    pub size_bytes: u64,
    /// 修改时间（Unix 毫秒；取不到为 0）。
    pub modified_ms: i64,
}

/// 索引服务状态（`status` 方法返回体）。
///
/// 【内存治理 · 计划 §5.4】引擎不在 `IResourceGovernor` 的插件 subject 域内，
/// 故由 `rssBytes`/`appCount`/`fileCount` 自报，供设置中心显示与上限自管。
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct IndexStatus {
    pub version: String,
    pub pid: u32,
    pub uptime_seconds: u64,
    /// 索引构建中（构建期消费者应回退本地实现，不得阻塞等待）。
    pub building: bool,
    pub app_count: usize,
    pub file_count: usize,
    /// 上次构建耗时（毫秒；未构建过为 0）。
    pub last_build_ms: u64,
    /// 上次构建完成时刻（Unix 毫秒；0 = 尚未构建）——供设置中心状态行显示「索引有多新」。
    pub last_build_at_ms: u64,
    /// 是否处于降级态（构建失败/超出上限/根目录不可用）。
    pub degraded: bool,
    /// 降级原因（`degraded=false` 时为 null）——降级必须可见，禁止静默。
    pub degrade_reason: Option<String>,
    /// 进程工作集（字节；采集失败为 0）。
    pub rss_bytes: u64,
    /// 图标缓存条目数（M4；0 = 还没人取过图标）。
    pub icon_cache_entries: usize,
    /// 图标缓存字节数（M4）——**只驻留 PNG 字节**（不是解码后 RGBA），故这个数很小。
    pub icon_cache_bytes: u64,
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 跨语言契约：字段必须序列化为 camelCase（C# 侧按同名属性反序列化）。
    #[test]
    fn serializes_camel_case() {
        let s = serde_json::to_value(AppCandidate {
            path: "C:/a.lnk".into(),
            target_path: "C:/a.exe".into(),
            name_hint: "A".into(),
            source: "start-menu".into(),
            icon_key: String::new(),
        })
        .unwrap();
        assert!(s.get("targetPath").is_some(), "targetPath 必须为 camelCase");
        assert!(s.get("nameHint").is_some());
        assert!(s.get("source").is_some());
        assert!(s.get("path").is_some());
    }

    /// 降级原因缺省为 null（未降级），且 `degraded=false` 与之不矛盾。
    #[test]
    fn status_degrade_reason_null_when_healthy() {
        let s = IndexStatus {
            version: "0.1.0".into(),
            pid: 1,
            uptime_seconds: 0,
            building: false,
            app_count: 0,
            file_count: 0,
            last_build_ms: 0,
            last_build_at_ms: 0,
            degraded: false,
            degrade_reason: None,
            rss_bytes: 0,
            icon_cache_entries: 0,
            icon_cache_bytes: 0,
        };
        let v = serde_json::to_value(&s).unwrap();
        assert!(v["degradeReason"].is_null());
        assert_eq!(v["building"], false);
        // M4 图标缓存占用字段的线格式（C# 侧按同名属性读）。
        assert_eq!(v["iconCacheEntries"], 0);
        assert_eq!(v["iconCacheBytes"], 0);
    }
}
