// run 执行链（C# HeadlessExecutor 批协议 + ConversionService.ConvertAsync 路由的 Rust 等价）：
// stdin JSON 请求 → 路由（拆分/合并/合成/加密/逐文件）→ 引擎执行 → 产物验证 → 原子发布
// → NDJSON 事件流（progress 阶段行 + result 末行）。
// 契约红线：密码经 stdin JSON 传入、绝不 argv；输出路径绝不等于输入；错误码六分类冻结。
use serde::{Deserialize, Serialize};
use std::path::{Path, PathBuf};

use crate::contract::{ConversionTarget, EngineKind};
use crate::engines::EngineProbe;
use crate::error::{ConvertError, Error};
use crate::matrix::ConversionMatrix;
use crate::service::{publish_all, validate_input, verify_products, TempDir};

/// 批协议版本（与 C# BatchProtocolVersion=1 对齐）。
pub const BATCH_PROTOCOL_VERSION: u32 = 1;

/// stdin JSON 请求体（密码字段可选：仅 pdf-encrypt/pdf-decrypt 使用）。
#[derive(Debug, Deserialize)]
pub struct RunRequest {
    pub version: u32,
    /// 目标格式（含 pdf-merge/pdf-compose/pdf-split/pdf-encrypt/pdf-decrypt 特殊目标）。
    pub target: String,
    pub paths: Vec<String>,
    /// 加密/解密密码（红线：仅走 stdin，绝不进 argv）。
    #[serde(default)]
    pub password: Option<String>,
}

/// 一次转换作业（C# ConversionJob 等价物）：产物必须写 temp_dir，由服务层原子发布。
pub struct Job<'a> {
    pub sources: Vec<PathBuf>,
    pub target: &'a ConversionTarget,
    pub temp_dir: &'a Path,
    /// 加密/解密密码（红线：仅经 stdin JSON 注入，绝不进 argv；非安全作业为 None）。
    pub password: Option<&'a str>,
}

impl Job<'_> {
    pub fn primary_source(&self) -> &Path {
        &self.sources[0]
    }
}

/// 引擎执行契约（S5 实现 6 子进程引擎 + 托管引擎并注册）。
pub trait Engine {
    fn kind(&self) -> EngineKind;
    fn can_handle(&self, sources: &[PathBuf], target: &ConversionTarget) -> bool;
    /// 执行转换：产物写 job.temp_dir，返回产物路径集（失败抛 Error；禁止把引擎缺失伪装成转换失败）。
    fn run(&self, job: &Job) -> Result<Vec<PathBuf>, Error>;
}

/// 进度阶段（S8 供给通知中心/灵动岛的 phase 序列；无精确进度的引擎按阶段推进，禁止假精确）。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum Phase {
    Running,
    Verifying,
    Publishing,
    Finalizing,
}

impl Phase {
    pub fn as_str(&self) -> &'static str {
        match self {
            Phase::Running => "running",
            Phase::Verifying => "verifying",
            Phase::Publishing => "publishing",
            Phase::Finalizing => "finalizing",
        }
    }
}

/// progress 事件行（NDJSON）。
#[derive(Serialize)]
pub struct ProgressEvent {
    #[serde(rename = "type")]
    pub kind: &'static str,
    pub source: String,
    pub target: String,
    pub engine: String,
    pub phase: &'static str,
    /// 精确进度（0-100）；None = 阶段推进（诚实，不编造）。
    #[serde(skip_serializing_if = "Option::is_none")]
    pub percent: Option<u8>,
    pub elapsed_ms: u64,
}

/// result 事件行（NDJSON 末行；契约红线：字段冻结向后兼容）。
#[derive(Serialize)]
pub struct ResultEvent {
    #[serde(rename = "type")]
    pub kind: &'static str,
    pub ok: bool,
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub outputs: Vec<String>,
    pub engine: String,
    pub elapsed_ms: u64,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub error: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub message: Option<String>,
}

/// 执行一条请求，返回 NDJSON 事件行序列（调用方逐行输出）。
pub fn run_conversion(
    req: &RunRequest,
    matrix: &ConversionMatrix,
    probe: &EngineProbe,
    engines: &[Box<dyn Engine>],
) -> Vec<String> {
    let mut lines = Vec::new();
    let started = std::time::Instant::now();

    if req.version != BATCH_PROTOCOL_VERSION {
        lines.push(result_json(&ResultEvent {
            kind: "result",
            ok: false,
            outputs: vec![],
            engine: "-".to_string(),
            elapsed_ms: started.elapsed().as_millis() as u64,
            error: Some(ConvertError::InputInvalid.as_str().to_string()),
            message: Some(format!("批协议版本不支持: {}（需要 {BATCH_PROTOCOL_VERSION}）", req.version)),
        }));
        return lines;
    }
    if req.paths.is_empty() {
        lines.push(result_json(&ResultEvent {
            kind: "result",
            ok: false,
            outputs: vec![],
            engine: "-".to_string(),
            elapsed_ms: started.elapsed().as_millis() as u64,
            error: Some(ConvertError::InputInvalid.as_str().to_string()),
            message: Some("输入为空".to_string()),
        }));
        return lines;
    }

    // 特殊目标路由（对齐 C# ConvertAsync）：
    // pdf-split / 多输入合并 / 多输入合成 / 加密解密 → 单作业；其余逐文件批量。
    let special = match req.target.as_str() {
        "pdf-split" => Some(SpecialOp::Split),
        "pdf-merge" => Some(SpecialOp::Merge),
        "pdf-compose" => Some(SpecialOp::Compose),
        "pdf-encrypt" => Some(SpecialOp::Encrypt),
        "pdf-decrypt" => Some(SpecialOp::Decrypt),
        _ => None,
    };

    if let Some(op) = special {
        run_special(op, req, matrix, probe, engines, started, &mut lines);
        return lines;
    }

    // 多输入 → pdf：全 pdf 走合并、全图片走合成（与 C# ConvertAsync 同判定）。
    if req.paths.len() > 1 && req.target == "pdf" {
        let paths: Vec<PathBuf> = req.paths.iter().map(PathBuf::from).collect();
        if ConversionMatrix::all_pdf(&req.paths) {
            run_special(SpecialOp::Merge, req, matrix, probe, engines, started, &mut lines);
            return lines;
        }
        if ConversionMatrix::all_images(&req.paths) {
            run_special(SpecialOp::Compose, req, matrix, probe, engines, started, &mut lines);
            return lines;
        }
    }

    // 逐文件批量：每文件独立结果，一项失败不影响其他（C# 红线 13）。
    let mut results = Vec::new();
    let mut seen = std::collections::HashSet::new();
    for path in &req.paths {
        if !seen.insert(path.to_lowercase()) {
            continue; // 批量内同文件不重复入队
        }
        let r = run_one(path, &req.target, matrix, probe, engines, started, &mut lines);
        lines.push(result_json(&r));
        results.push(r);
    }
    // 批量汇总（门面层按 convert/batch-finished 口径消费；此处结果行已逐条输出）。
    let _ = results;
    lines
}

#[derive(Clone, Copy)]
enum SpecialOp {
    Split,
    Merge,
    Compose,
    Encrypt,
    Decrypt,
}

fn special_target(op: SpecialOp) -> ConversionTarget {
    match op {
        SpecialOp::Split => crate::matrix::split_pdf(),
        SpecialOp::Merge => crate::matrix::merge_pdf(),
        SpecialOp::Compose => crate::matrix::compose_pdf(),
        SpecialOp::Encrypt => crate::matrix::encrypt_pdf(),
        SpecialOp::Decrypt => crate::matrix::decrypt_pdf(),
    }
}

/// 特殊作业（合并/合成/拆分/加密/解密）：单输入集、单输出（拆分多产物）。
fn run_special(
    op: SpecialOp,
    req: &RunRequest,
    matrix: &ConversionMatrix,
    probe: &EngineProbe,
    engines: &[Box<dyn Engine>],
    started: std::time::Instant,
    lines: &mut Vec<String>,
) {
    let target = special_target(op);
    let label = target.filter.clone().unwrap_or_default();
    let engine_name = target.prefer.as_str().to_string();

    // 加密/解密：单 pdf + 密码非空（C# ConvertWithPasswordAsync 校验）。
    if matches!(op, SpecialOp::Encrypt | SpecialOp::Decrypt) {
        if req.paths.len() != 1 {
            lines.push(result_json(&ResultEvent {
                kind: "result",
                ok: false,
                outputs: vec![],
                engine: engine_name.clone(),
                elapsed_ms: started.elapsed().as_millis() as u64,
                error: Some(ConvertError::InputInvalid.as_str().to_string()),
                message: Some("加密/解密仅支持单个 PDF 文件".to_string()),
            }));
            return;
        }
        let ext = Path::new(&req.paths[0])
            .extension()
            .map(|e| e.to_string_lossy().to_lowercase())
            .unwrap_or_default();
        if ext != "pdf" {
            lines.push(result_json(&ResultEvent {
                kind: "result",
                ok: false,
                outputs: vec![],
                engine: engine_name.clone(),
                elapsed_ms: started.elapsed().as_millis() as u64,
                error: Some(ConvertError::InputInvalid.as_str().to_string()),
                message: Some("加密/解密仅支持 PDF 文件".to_string()),
            }));
            return;
        }
        if req.password.as_deref().unwrap_or("").is_empty() {
            lines.push(result_json(&ResultEvent {
                kind: "result",
                ok: false,
                outputs: vec![],
                engine: engine_name.clone(),
                elapsed_ms: started.elapsed().as_millis() as u64,
                error: Some(ConvertError::InputInvalid.as_str().to_string()),
                message: Some("密码为空（已取消或未输入）".to_string()),
            }));
            return;
        }
    }

    // 输入校验（全部源）
    let mut sources = Vec::with_capacity(req.paths.len());
    for p in &req.paths {
        match validate_input(p, matrix) {
            Ok(c) => sources.push(c),
            Err(e) => {
                lines.push(result_json(&ResultEvent {
                    kind: "result",
                    ok: false,
                    outputs: vec![],
                    engine: engine_name.clone(),
                    elapsed_ms: started.elapsed().as_millis() as u64,
                    error: Some(e.code.as_str().to_string()),
                    message: Some(e.message),
                }));
                return;
            }
        }
    }

    // 拆分/合并/合成前置条件（C# 判定）
    match op {
        SpecialOp::Split => {
            if sources.len() != 1 {
                lines.push(result_json(&ResultEvent {
                    kind: "result",
                    ok: false,
                    outputs: vec![],
                    engine: engine_name.clone(),
                    elapsed_ms: started.elapsed().as_millis() as u64,
                    error: Some(ConvertError::InputInvalid.as_str().to_string()),
                    message: Some("拆分 PDF 仅支持单个文件".to_string()),
                }));
                return;
            }
        }
        SpecialOp::Merge => {
            if sources.len() < 2 {
                lines.push(result_json(&ResultEvent {
                    kind: "result",
                    ok: false,
                    outputs: vec![],
                    engine: engine_name.clone(),
                    elapsed_ms: started.elapsed().as_millis() as u64,
                    error: Some(ConvertError::InputInvalid.as_str().to_string()),
                    message: Some("合并 PDF 至少需要 2 个文件".to_string()),
                }));
                return;
            }
        }
        SpecialOp::Compose => {
            if sources.len() < 2 {
                lines.push(result_json(&ResultEvent {
                    kind: "result",
                    ok: false,
                    outputs: vec![],
                    engine: engine_name.clone(),
                    elapsed_ms: started.elapsed().as_millis() as u64,
                    error: Some(ConvertError::InputInvalid.as_str().to_string()),
                    message: Some("合成 PDF 至少需要 2 张图片".to_string()),
                }));
                return;
            }
        }
        _ => {}
    }

    progress(lines, &sources[0], &target.format, &engine_name, Phase::Running, None, started);

    let temp_dir = match TempDir::create(&sources[0]) {
        Ok(t) => t,
        Err(e) => {
            lines.push(result_json(&ResultEvent {
                kind: "result",
                ok: false,
                outputs: vec![],
                engine: engine_name.clone(),
                elapsed_ms: started.elapsed().as_millis() as u64,
                error: Some(e.code.as_str().to_string()),
                message: Some(e.message),
            }));
            return;
        }
    };

    let engine = match resolve_engine(&sources, &target, probe, engines) {
        Some(e) => e,
        None => {
            let err = Error::engine_missing(format!(
                "内置引擎未就绪（{}）——这不是文件错误",
                target.prefer.as_str()
            ));
            lines.push(result_json(&ResultEvent {
                kind: "result",
                ok: false,
                outputs: vec![],
                engine: engine_name,
                elapsed_ms: started.elapsed().as_millis() as u64,
                error: Some(err.code.as_str().to_string()),
                message: Some(err.message),
            }));
            return;
        }
    };

    let job = Job {
        sources: sources.clone(),
        target: &target,
        temp_dir: temp_dir.path(),
        password: req.password.as_deref(),
    };
    let products = match execute_verified(engine, &job) {
        Ok(p) => p,
        Err(e) => {
            lines.push(result_json(&ResultEvent {
                kind: "result",
                ok: false,
                outputs: vec![],
                engine: engine_name,
                elapsed_ms: started.elapsed().as_millis() as u64,
                error: Some(e.code.as_str().to_string()),
                message: Some(e.message),
            }));
            return;
        }
    };
    progress(lines, &sources[0], &target.format, &engine_name, Phase::Publishing, None, started);

    // 加密/解密：就地替换（用户口径：不产生新文件；产物完整后覆盖，失败原文件不动）。
    if matches!(op, SpecialOp::Encrypt | SpecialOp::Decrypt) {
        let input = &sources[0];
        let result = (|| -> Result<(), Error> {
            let product = &products[0];
            if std::fs::rename(product, input).is_err() {
                return Err(Error::output_failed("就地替换原文件失败"));
            }
            Ok(())
        })();
        match result {
            Ok(()) => {
                lines.push(result_json(&ResultEvent {
                    kind: "result",
                    ok: true,
                    outputs: vec![input.to_string_lossy().to_string()],
                    engine: engine_name,
                    elapsed_ms: started.elapsed().as_millis() as u64,
                    error: None,
                    message: None,
                }));
            }
            Err(e) => {
                lines.push(result_json(&ResultEvent {
                    kind: "result",
                    ok: false,
                    outputs: vec![],
                    engine: engine_name,
                    elapsed_ms: started.elapsed().as_millis() as u64,
                    error: Some(e.code.as_str().to_string()),
                    message: Some(e.message),
                }));
            }
        }
        return;
    }

    // 原子发布（单产物 = 原名；多产物 = 原名-N 或 合并/合成后缀）
    let stem = Path::new(&sources[0])
        .file_stem()
        .map(|s| s.to_string_lossy().to_string())
        .unwrap_or_else(|| "output".to_string());
    let base = match label.as_str() {
        "pdf-merge" => format!("{stem}（合并）"),
        "pdf-compose" => format!("{stem}（合成）"),
        _ => stem,
    };
    let output_format = if target.format == "pdf-split" { "pdf" } else { &target.format };
    match publish_all(&products, &base, output_format, &sources) {
        Ok(outputs) => {
            progress(lines, &sources[0], &target.format, &engine_name, Phase::Finalizing, None, started);
            lines.push(result_json(&ResultEvent {
                kind: "result",
                ok: true,
                outputs: outputs.iter().map(|p| p.to_string_lossy().to_string()).collect(),
                engine: engine_name,
                elapsed_ms: started.elapsed().as_millis() as u64,
                error: None,
                message: None,
            }));
        }
        Err(e) => {
            lines.push(result_json(&ResultEvent {
                kind: "result",
                ok: false,
                outputs: vec![],
                engine: engine_name,
                elapsed_ms: started.elapsed().as_millis() as u64,
                error: Some(e.code.as_str().to_string()),
                message: Some(e.message),
            }));
        }
    }
}

/// 逐文件转换（单源普通转换）。
fn run_one(
    path: &str,
    format: &str,
    matrix: &ConversionMatrix,
    probe: &EngineProbe,
    engines: &[Box<dyn Engine>],
    started: std::time::Instant,
    lines: &mut Vec<String>,
) -> ResultEvent {
    let fail = |code: ConvertError, msg: String| ResultEvent {
        kind: "result",
        ok: false,
        outputs: vec![],
        engine: "-".to_string(),
        elapsed_ms: started.elapsed().as_millis() as u64,
        error: Some(code.as_str().to_string()),
        message: Some(msg),
    };

    let input = match validate_input(path, matrix) {
        Ok(c) => c,
        Err(e) => return fail(e.code, e.message),
    };
    let ext = input
        .extension()
        .map(|e| format!(".{}", e.to_string_lossy().to_lowercase()))
        .unwrap_or_default();
    let target = match matrix.find(&ext, format) {
        Some(t) => t,
        None => {
            return fail(
                ConvertError::InputInvalid,
                format!("不支持的类型或目标: {ext} → {format}"),
            )
        }
    };
    let engine_name = target.prefer.as_str().to_string();

    progress(lines, &input, &target.format, &engine_name, Phase::Running, None, started);

    let engine = match resolve_engine(&[input.clone()], target, probe, engines) {
        Some(e) => e,
        None => {
            let err = Error::engine_missing(format!(
                "内置引擎未就绪（{}）——这不是文件错误",
                target.prefer.as_str()
            ));
            return fail(err.code, err.message);
        }
    };

    let temp_dir = match TempDir::create(&input) {
        Ok(t) => t,
        Err(e) => return fail(e.code, e.message),
    };
    let job = Job {
        sources: vec![input.clone()],
        target,
        temp_dir: temp_dir.path(),
        password: None,
    };
    let products = match execute_verified(engine, &job) {
        Ok(p) => p,
        Err(e) => return fail(e.code, e.message),
    };
    progress(lines, &input, &target.format, &engine_name, Phase::Publishing, None, started);

    let stem = input
        .file_stem()
        .map(|s| s.to_string_lossy().to_string())
        .unwrap_or_else(|| "output".to_string());
    match publish_all(&products, &stem, target.format.as_str(), &[input.clone()]) {
        Ok(outputs) => {
            progress(lines, &input, &target.format, &engine_name, Phase::Finalizing, None, started);
            ResultEvent {
                kind: "result",
                ok: true,
                outputs: outputs.iter().map(|p| p.to_string_lossy().to_string()).collect(),
                engine: engine_name,
                elapsed_ms: started.elapsed().as_millis() as u64,
                error: None,
                message: None,
            }
        }
        Err(e) => fail(e.code, e.message),
    }
}

/// 候选链解析：Prefer 优先，Fallback 兜底；只取 Probe 可用且 CanHandle 的引擎（C# EngineRegistry.Resolve）。
fn resolve_engine<'a>(
    sources: &[PathBuf],
    target: &'a ConversionTarget,
    probe: &EngineProbe,
    engines: &'a [Box<dyn Engine>],
) -> Option<&'a dyn Engine> {
    for kind in [Some(target.prefer), target.fallback] {
        let Some(kind) = kind else { continue };
        if !probe.probe(kind).available {
            continue;
        }
        if let Some(e) = engines.iter().find(|e| e.kind() == kind && e.can_handle(sources, target)) {
            return Some(e.as_ref());
        }
    }
    None
}

/// 执行 + 回读验证（C# RunVerified 等价：产物存在且非空；失败恰好重试一次）。
fn execute_verified(engine: &dyn Engine, job: &Job) -> Result<Vec<PathBuf>, Error> {
    for attempt in 1..=2 {
        let products = engine.run(job)?;
        if verify_products(&products) {
            return Ok(products);
        }
        if attempt >= 2 {
            return Err(Error::conversion_failed(format!(
                "{} 产物验证失败（重试一次后仍无效）",
                engine.kind().as_str()
            )));
        }
    }
    unreachable!()
}

fn progress(
    lines: &mut Vec<String>,
    source: &Path,
    target: &str,
    engine: &str,
    phase: Phase,
    percent: Option<u8>,
    started: std::time::Instant,
) {
    let ev = ProgressEvent {
        kind: "progress",
        source: source.to_string_lossy().to_string(),
        target: target.to_string(),
        engine: engine.to_string(),
        phase: phase.as_str(),
        percent,
        elapsed_ms: started.elapsed().as_millis() as u64,
    };
    lines.push(progress_json(&ev));
}

fn progress_json(ev: &ProgressEvent) -> String {
    serde_json::to_string(ev).expect("progress 序列化不应失败")
}

fn result_json(ev: &ResultEvent) -> String {
    serde_json::to_string(ev).expect("result 序列化不应失败")
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::{SystemTime, UNIX_EPOCH};

    fn tmp_dir() -> PathBuf {
        let d = std::env::temp_dir().join(format!(
            "bdt-convert-run-test-{}-{:x}",
            std::process::id(),
            SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_nanos()
        ));
        std::fs::create_dir_all(&d).unwrap();
        d
    }

    fn make_docx(dir: &Path, name: &str) -> PathBuf {
        let p = dir.join(name);
        std::fs::write(&p, b"x").unwrap();
        p
    }

    #[test]
    fn batch_version_mismatch_rejected() {
        let m = ConversionMatrix::default();
        let probe = EngineProbe::new();
        let lines = run_conversion(
            &RunRequest { version: 99, target: "pdf".into(), paths: vec!["a.docx".into()], password: None },
            &m,
            &probe,
            &[],
        );
        let last = serde_json::from_str::<serde_json::Value>(lines.last().unwrap()).unwrap();
        assert_eq!(last["type"], "result");
        assert_eq!(last["ok"], false);
        assert_eq!(last["error"], "InputInvalid");
        assert!(last["message"].as_str().unwrap().contains("版本"));
    }

    #[test]
    fn empty_paths_rejected() {
        let m = ConversionMatrix::default();
        let probe = EngineProbe::new();
        let lines = run_conversion(
            &RunRequest { version: 1, target: "pdf".into(), paths: vec![], password: None },
            &m,
            &probe,
            &[],
        );
        let last = serde_json::from_str::<serde_json::Value>(lines.last().unwrap()).unwrap();
        assert_eq!(last["message"], "输入为空");
    }

    #[test]
    fn single_file_engine_missing_is_engine_missing() {
        let dir = tmp_dir();
        let src = make_docx(&dir, "a.docx");
        let m = ConversionMatrix::default();
        let probe = EngineProbe::new();
        let lines = run_conversion(
            &RunRequest {
                version: 1,
                target: "pdf".into(),
                paths: vec![src.to_string_lossy().to_string()],
                password: None,
            },
            &m,
            &probe,
            &[],
        );
        // 事件流：progress(running) + result
        assert!(lines.len() >= 2);
        let first = serde_json::from_str::<serde_json::Value>(&lines[0]).unwrap();
        assert_eq!(first["type"], "progress");
        assert_eq!(first["phase"], "running");
        let last = serde_json::from_str::<serde_json::Value>(lines.last().unwrap()).unwrap();
        assert_eq!(last["ok"], false);
        // 引擎未注册 → EngineMissing（禁止伪装成 ConversionFailed）
        assert_eq!(last["error"], "EngineMissing");
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn encrypt_requires_single_pdf_and_password() {
        let dir = tmp_dir();
        let src = make_docx(&dir, "a.pdf");
        let m = ConversionMatrix::default();
        let probe = EngineProbe::new();
        // 多文件 → InputInvalid
        let lines = run_conversion(
            &RunRequest {
                version: 1,
                target: "pdf-encrypt".into(),
                paths: vec![src.to_string_lossy().to_string(), src.to_string_lossy().to_string()],
                password: Some("pw".into()),
            },
            &m,
            &probe,
            &[],
        );
        let last = serde_json::from_str::<serde_json::Value>(lines.last().unwrap()).unwrap();
        assert_eq!(last["error"], "InputInvalid");
        assert!(last["message"].as_str().unwrap().contains("单个 PDF"));
        // 密码空 → InputInvalid
        let lines = run_conversion(
            &RunRequest {
                version: 1,
                target: "pdf-encrypt".into(),
                paths: vec![src.to_string_lossy().to_string()],
                password: Some("".into()),
            },
            &m,
            &probe,
            &[],
        );
        let last = serde_json::from_str::<serde_json::Value>(lines.last().unwrap()).unwrap();
        assert_eq!(last["error"], "InputInvalid");
        assert!(last["message"].as_str().unwrap().contains("密码为空"));
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn merge_requires_two_files() {
        let dir = tmp_dir();
        let a = make_docx(&dir, "a.pdf");
        let b = make_docx(&dir, "b.pdf");
        let m = ConversionMatrix::default();
        let probe = EngineProbe::new();
        // 单文件合并 → InputInvalid
        let lines = run_conversion(
            &RunRequest {
                version: 1,
                target: "pdf-merge".into(),
                paths: vec![a.to_string_lossy().to_string()],
                password: None,
            },
            &m,
            &probe,
            &[],
        );
        let last = serde_json::from_str::<serde_json::Value>(lines.last().unwrap()).unwrap();
        assert_eq!(last["error"], "InputInvalid");
        // 两文件 → 引擎未注册 EngineMissing（路由正确到达引擎解析）
        let lines = run_conversion(
            &RunRequest {
                version: 1,
                target: "pdf".into(),
                paths: vec![a.to_string_lossy().to_string(), b.to_string_lossy().to_string()],
                password: None,
            },
            &m,
            &probe,
            &[],
        );
        let last = serde_json::from_str::<serde_json::Value>(lines.last().unwrap()).unwrap();
        assert_eq!(last["error"], "EngineMissing");
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn duplicate_paths_deduplicated_in_batch() {
        let dir = tmp_dir();
        let a = make_docx(&dir, "a.docx");
        let m = ConversionMatrix::default();
        let probe = EngineProbe::new();
        let lines = run_conversion(
            &RunRequest {
                version: 1,
                target: "pdf".into(),
                paths: vec![a.to_string_lossy().to_string(), a.to_string_lossy().to_string()],
                password: None,
            },
            &m,
            &probe,
            &[],
        );
        // 去重后只有 1 个文件 → 1 条 progress + 1 条 result
        let results = lines.iter().filter(|l| l.contains("\"result\"")).count();
        assert_eq!(results, 1);
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn progress_event_shape_is_stable() {
        let dir = tmp_dir();
        let a = make_docx(&dir, "a.docx");
        let m = ConversionMatrix::default();
        let probe = EngineProbe::new();
        let lines = run_conversion(
            &RunRequest {
                version: 1,
                target: "pdf".into(),
                paths: vec![a.to_string_lossy().to_string()],
                password: None,
            },
            &m,
            &probe,
            &[],
        );
        let first = serde_json::from_str::<serde_json::Value>(&lines[0]).unwrap();
        assert_eq!(first["type"], "progress");
        assert!(first.get("phase").is_some());
        assert!(first.get("source").is_some());
        assert!(first.get("target").is_some());
        assert!(first.get("engine").is_some());
        assert!(first.get("elapsed_ms").is_some());
        assert_eq!(first["target"], "pdf");
        let _ = std::fs::remove_dir_all(&dir);
    }
}
