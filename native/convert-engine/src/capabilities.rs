// capabilities 输出：{engines, matrix}。
// engines 数组由探测层填充（真实 --version / 托管恒可用 / COM 降级）。
// 输出字段以 C# 枚举名字符串承载（门面层按 EngineKind.TryParse 映射）。
use serde::Serialize;

use crate::contract::{EngineKind, TargetJson};
use crate::engines::EngineProbe;
use crate::matrix::ConversionMatrix;

/// capabilities 输出中的引擎条目（S2 探测层填充 available/version）。
#[derive(Serialize, Clone)]
pub struct EngineJson<'a> {
    pub kind: &'a str,
    pub available: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub version: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub reason: Option<String>,
}

#[derive(Serialize)]
pub struct RowJson<'a> {
    pub ext: String,
    pub targets: Vec<TargetJson<'a>>,
}

#[derive(Serialize)]
pub struct CapabilitiesJson<'a> {
    pub engines: Vec<EngineJson<'a>>,
    pub matrix: Vec<RowJson<'a>>,
}

/// 由探测层生成 engines 数组（15 引擎全量输出；不可用项带 reason）。
pub fn engines_json(probe: &EngineProbe) -> Vec<EngineJson<'_>> {
    let _ = EngineKind::Soffice; // 类型触达
    probe
        .probe_all()
        .iter()
        .map(|(kind, avail)| EngineJson {
            kind: kind.as_str(),
            available: avail.available,
            version: avail.version.clone(),
            reason: avail.reason.clone(),
        })
        .collect()
}

/// 构造 capabilities JSON 字符串。`engines` 由探测层传入。
pub fn capabilities_json<'a>(matrix: &'a ConversionMatrix, engines: &[EngineJson<'a>]) -> String {
    let rows = matrix
        .all_input_extensions()
        .into_iter()
        .map(|ext| {
            let targets = matrix.get_targets(&ext).iter().map(TargetJson::from).collect();
            RowJson { ext, targets }
        })
        .collect();
    let caps = CapabilitiesJson {
        engines: engines.to_vec(),
        matrix: rows,
    };
    serde_json::to_string(&caps).expect("capabilities 序列化不应失败")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn capabilities_contains_matrix_and_fields() {
        let m = ConversionMatrix::default();
        let json = capabilities_json(&m, &[]);
        let v: serde_json::Value = serde_json::from_str(&json).unwrap();
        assert!(v["engines"].is_array());
        assert!(v["matrix"].is_array());
        let rows = v["matrix"].as_array().unwrap();
        assert!(!rows.is_empty());
        // 首行 .doc：pdf 目标字段齐全（C# 枚举名 + category + lossless）
        let first = &rows[0];
        assert_eq!(first["ext"], ".doc");
        let pdf = &first["targets"][0];
        assert_eq!(pdf["format"], "pdf");
        assert_eq!(pdf["label"], "PDF 文档");
        // 2026-09-20 迁移：.doc → pdf 原为 soffice 主 + COM 兜底，现由进程内 lite 负责（engines 已删）
        assert_eq!(pdf["prefer"], "Lite");
        assert!(pdf["fallback"].is_null());
        assert_eq!(pdf["category"], "Document");
        assert_eq!(pdf["lossless"], true);
        assert_eq!(pdf["hops"], 1);
        assert!(pdf["filter"].is_string());
        // 引擎数组暂为空（S2 填充）
        assert_eq!(v["engines"].as_array().unwrap().len(), 0);
    }

    #[test]
    fn capabilities_row_count_matches_matrix() {
        let m = ConversionMatrix::default();
        let json = capabilities_json(&m, &[]);
        let v: serde_json::Value = serde_json::from_str(&json).unwrap();
        assert_eq!(v["matrix"].as_array().unwrap().len(), m.all_input_extensions().len());
    }
}
