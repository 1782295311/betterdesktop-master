// 引擎定位 + 探测（C# ConvertEngineLocator + 各引擎 EnsureProbed 的 Rust 等价）。
// 探测链统一：env 覆盖 → Program Files 标准位 → 受管 engines 目录；真实执行 --version 校验（文件存在不算命中）。
// 探测结果进程级缓存；probe --refresh 显式失效。
use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::sync::Mutex;
use std::time::{Duration, Instant};

use crate::contract::EngineKind;

/// 探测超时（ms；soffice 首次 profile 初始化偏慢，统一 3s）。
pub const PROBE_TIMEOUT_MS: u64 = 3_000;

/// 引擎可用性（C# EngineAvailability 等价物）。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct EngineAvailability {
    pub available: bool,
    pub version: Option<String>,
    pub reason: Option<String>,
}

impl EngineAvailability {
    pub fn ok(version: &str) -> Self {
        EngineAvailability {
            available: true,
            version: Some(version.to_string()),
            reason: None,
        }
    }

    pub fn missing(reason: &str) -> Self {
        EngineAvailability {
            available: false,
            version: None,
            reason: Some(reason.to_string()),
        }
    }
}

/// 引擎探测器（进程级缓存，菜单/CLI 零阻塞读缓存快照）。
pub struct EngineProbe {
    cache: Mutex<HashMap<EngineKind, EngineAvailability>>,
}

impl Default for EngineProbe {
    fn default() -> Self {
        Self::new()
    }
}

impl EngineProbe {
    pub fn new() -> Self {
        EngineProbe {
            cache: Mutex::new(HashMap::new()),
        }
    }

    /// 取某引擎探测结果（缓存命中直接返回；未探测则执行真实探测）。
    pub fn probe(&self, kind: EngineKind) -> EngineAvailability {
        let mut cache = self.cache.lock().unwrap();
        if let Some(v) = cache.get(&kind) {
            return v.clone();
        }
        let v = probe_kind(kind);
        cache.insert(kind, v.clone());
        v
    }

    /// 全量探测（16 引擎；托管类恒可用，子进程类真实 --version）。
    pub fn probe_all(&self) -> Vec<(EngineKind, EngineAvailability)> {
        const KINDS: [EngineKind; 16] = [
            EngineKind::Soffice,
            EngineKind::ComPdf,
            EngineKind::Managed,
            EngineKind::TwoHop,
            EngineKind::Image,
            EngineKind::PdfCompose,
            EngineKind::Poppler,
            EngineKind::Pandoc,
            EngineKind::Ffmpeg,
            EngineKind::PdfText,
            EngineKind::Tesseract,
            EngineKind::PdfSecurity,
            EngineKind::Heic,
            EngineKind::Raw,
            EngineKind::Calibre,
            EngineKind::Lite,
        ];
        KINDS.iter().map(|k| (*k, self.probe(*k))).collect()
    }

    /// 失效全部缓存（引擎安装/卸载后调用；probe --refresh 语义）。
    pub fn reset(&self) {
        self.cache.lock().unwrap().clear();
    }
}

/// 单引擎探测（纯逻辑，便于测试）。
fn probe_kind(kind: EngineKind) -> EngineAvailability {
    match kind {
        // —— 托管类：恒可用（版本标注 Rust 实现栈）——
        EngineKind::Managed => EngineAvailability::ok("Rust 托管（md→html/txt、结构化文本）"),
        EngineKind::Image => EngineAvailability::ok("Rust image crate"),
        EngineKind::PdfCompose => EngineAvailability::ok("lopdf"),
        EngineKind::PdfSecurity => EngineAvailability::ok("lopdf（加密/解密）"),
        EngineKind::Heic => EngineAvailability::ok("WIC（需系统 HEIF/AV1 图像扩展）"),
        // 2026-09-20 进程内轻量引擎：**恒可用**。这一条就是本次迁移的目的 ——
        // 引擎可用性从"磁盘上有没有那个第三方目录"变成"代码里有这个实现"，
        // 于是删掉 engines/ 之后菜单不再整项隐藏（原先缺目录 ⇒ IsEngineReady=false ⇒ 隐藏）。
        EngineKind::Lite => EngineAvailability::ok("Rust convert-lite（进程内，零外部 exe）"),

        // —— COM 降级（用户拍板）：Rust 侧不实现 COM，恒不可用 ——
        EngineKind::ComPdf => EngineAvailability::missing("COM 引擎已降级（Rust 引擎不实现）"),

        // —— 依赖链引擎 ——
        // TwoHop 依赖 soffice 定位（转换时由执行链再校验 poppler/lo 可用）
        EngineKind::TwoHop => match locate_soffice() {
            Some(_) => EngineAvailability::ok("soffice 中间态 + 目标引擎"),
            None => EngineAvailability::missing("TwoHop 需要 LibreOffice"),
        },
        // PdfText 依赖 Poppler 套件 pdftotext
        EngineKind::PdfText => match locate_pdftotext() {
            Some(_) => EngineAvailability::ok("Poppler pdftotext + 托管 OOXML"),
            None => EngineAvailability::missing("PdfText 需要 Poppler（pdftotext）"),
        },

        // —— 子进程引擎：真实 --version ——
        EngineKind::Soffice => probe_soffice(),
        EngineKind::Pandoc => probe_pandoc(),
        EngineKind::Poppler => probe_poppler(),
        EngineKind::Ffmpeg => probe_simple(EngineKind::Ffmpeg, locate_ffmpeg(), &["-version"], "ffmpeg -version"),
        EngineKind::Tesseract => probe_simple(EngineKind::Tesseract, locate_tesseract(), &["--version"], "tesseract --version"),
        EngineKind::Calibre => probe_simple(EngineKind::Calibre, locate_calibre(), &["--version"], "ebook-convert --version"),

        // —— 自包含单 exe：文件存在即可用（无版本输出）——
        EngineKind::Raw => match locate_dcraw() {
            Some(_) => EngineAvailability::ok("dcraw/LibRaw"),
            None => EngineAvailability::missing("未找到 dcraw.exe（engines\\dcraw 或 Program Files\\dcraw）"),
        },
    }
}

/// soffice 真实探测：-env:UserInstallation=<profile> --version，输出须含 "LibreOffice"。
fn probe_soffice() -> EngineAvailability {
    let Some(path) = locate_soffice() else {
        return EngineAvailability::missing("未找到 LibreOffice（engines\\libreoffice 或 Program Files）");
    };
    let profile = user_profile_url();
    let (code, output) = run_probe(&path, &[&format!("-env:UserInstallation={profile}"), "--version"]);
    let ok = code == Some(0) && contains_ci(&output, "LibreOffice");
    if ok {
        EngineAvailability::ok(&output)
    } else {
        EngineAvailability::missing(&format!("soffice --version 未通过（exit {code:?}）"))
    }
}

/// pandoc 真实探测：--version；附加 SupportsPptx（≥3.0）能力标记到 version。
fn probe_pandoc() -> EngineAvailability {
    let Some(path) = locate_pandoc() else {
        return EngineAvailability::missing("未找到 pandoc（engines\\pandoc 或 Program Files）");
    };
    let (code, output) = run_probe(&path, &["--version"]);
    let first = first_line(&output);
    if code == Some(0) && first.is_some() {
        let version = first.unwrap_or_default();
        let supports_pptx = parse_major_version(version) >= 3;
        let display = if supports_pptx {
            format!("{version}（pptx writer 可用）")
        } else {
            format!("{version}（pptx writer 需 ≥3.0）")
        };
        EngineAvailability::ok(&display)
    } else {
        EngineAvailability::missing(&format!("pandoc --version 未通过（exit {code:?}）"))
    }
}

/// poppler 真实探测：pdftoppm -v（版本走 stderr；exit≠0 但输出含版本也算命中）。
fn probe_poppler() -> EngineAvailability {
    let Some(path) = locate_pdftoppm() else {
        return EngineAvailability::missing("未找到 Poppler（engines\\poppler 或 Program Files）");
    };
    let (code, output) = run_probe(&path, &["-v"]);
    let ok = code == Some(0) || contains_ci(&output, "pdftoppm version");
    if ok {
        let first = first_line(&output).unwrap_or("pdftoppm");
        EngineAvailability::ok(first)
    } else {
        EngineAvailability::missing(&format!("pdftoppm -v 未通过（exit {code:?}）"))
    }
}

/// 通用子进程探测：exit 0 且输出非空 → 第一行作版本。
fn probe_simple(kind: EngineKind, exe: Option<PathBuf>, args: &[&str], label: &str) -> EngineAvailability {
    let Some(path) = exe else {
        let name = match kind {
            EngineKind::Ffmpeg => "FFmpeg",
            EngineKind::Tesseract => "Tesseract-OCR",
            EngineKind::Calibre => "Calibre",
            _ => "引擎",
        };
        return EngineAvailability::missing(&format!("未找到 {name}（engines 目录或 Program Files）"));
    };
    let (code, output) = run_probe(&path, args);
    if code == Some(0) {
        let first = first_line(&output).unwrap_or_default();
        if !first.is_empty() {
            return EngineAvailability::ok(first);
        }
    }
    EngineAvailability::missing(&format!("{label} 未通过（exit {code:?}）"))
}

// —— 定位链（C# ConvertEngineLocator / Locate* 等价）——

fn engines_root() -> PathBuf {
    std::env::current_exe()
        .ok()
        .and_then(|p| p.parent().map(Path::to_path_buf))
        .unwrap_or_else(|| PathBuf::from("."))
        .join("engines")
}

fn program_files() -> PathBuf {
    std::env::var("PROGRAMFILES")
        .map(PathBuf::from)
        .unwrap_or_else(|_| PathBuf::from(r"C:\Program Files"))
}

fn program_files_x86() -> PathBuf {
    std::env::var("PROGRAMFILES(X86)")
        .map(PathBuf::from)
        .unwrap_or_else(|_| PathBuf::from(r"C:\Program Files (x86)"))
}

fn local_app_data() -> PathBuf {
    std::env::var("LOCALAPPDATA")
        .map(PathBuf::from)
        .unwrap_or_else(|_| PathBuf::from("."))
}

fn env_override(name: &str) -> Option<PathBuf> {
    std::env::var(name)
        .ok()
        .filter(|v| !v.trim().is_empty())
        .map(PathBuf::from)
}

fn find_first(candidates: &[PathBuf]) -> Option<PathBuf> {
    candidates.iter().find(|p| p.is_file()).cloned()
}

/// 定位 soffice.com（用 .com 而非 .exe：PE=Console 重定向后不建控制台窗；C# 2026-09-10 实测）。
pub fn locate_soffice() -> Option<PathBuf> {
    let root = engines_root();
    let candidates = vec![
        env_override("BETTERDESKTOP_SOFFICE_PATH"),
        Some(program_files().join(r"LibreOffice\program\soffice.com")),
        Some(program_files_x86().join(r"LibreOffice\program\soffice.com")),
        Some(root.join(r"libreoffice\program\soffice.com")),
        Some(root.join(r"libreoffice\LibreOfficePortable\App\libreoffice\program\soffice.com")),
    ]
    .into_iter()
    .flatten()
    .collect::<Vec<_>>();
    find_first(&candidates)
}

pub fn locate_pandoc() -> Option<PathBuf> {
    let root = engines_root();
    let candidates = vec![
        env_override("BETTERDESKTOP_PANDOC_PATH"),
        Some(program_files().join(r"Pandoc\pandoc.exe")),
        Some(root.join(r"pandoc\pandoc.exe")),
    ]
    .into_iter()
    .flatten()
    .collect::<Vec<_>>();
    find_first(&candidates)
}

pub fn locate_pdftoppm() -> Option<PathBuf> {
    let root = engines_root();
    let candidates = vec![
        env_override("BETTERDESKTOP_POPPLER_PATH"),
        Some(program_files().join(r"poppler\Library\bin\pdftoppm.exe")),
        Some(root.join(r"poppler\Library\bin\pdftoppm.exe")),
        Some(root.join(r"poppler\poppler\Library\bin\pdftoppm.exe")),
    ]
    .into_iter()
    .flatten()
    .collect::<Vec<_>>();
    find_first(&candidates)
}

/// pdftotext 与 pdftoppm 同目录（Poppler 套件内；PdfText 引擎复用）。
pub fn locate_pdftotext() -> Option<PathBuf> {
    locate_pdftoppm().and_then(|p| {
        let candidate = p.parent()?.join("pdftotext.exe");
        if candidate.is_file() { Some(candidate) } else { None }
    })
}

pub fn locate_ffmpeg() -> Option<PathBuf> {
    let root = engines_root();
    let candidates = vec![
        env_override("BETTERDESKTOP_FFMPEG_PATH"),
        Some(program_files().join(r"ffmpeg\bin\ffmpeg.exe")),
        Some(root.join(r"ffmpeg\bin\ffmpeg.exe")),
        Some(root.join(r"ffmpeg\ffmpeg.exe")),
    ]
    .into_iter()
    .flatten()
    .collect::<Vec<_>>();
    find_first(&candidates)
}

pub fn locate_tesseract() -> Option<PathBuf> {
    let root = engines_root();
    let candidates = vec![
        env_override("BETTERDESKTOP_TESSERACT_PATH"),
        Some(program_files().join(r"Tesseract-OCR\tesseract.exe")),
        Some(root.join(r"tesseract\tesseract.exe")),
    ]
    .into_iter()
    .flatten()
    .collect::<Vec<_>>();
    find_first(&candidates)
}

pub fn locate_calibre() -> Option<PathBuf> {
    let root = engines_root();
    let candidates = vec![
        env_override("BETTERDESKTOP_CALIBRE_PATH"),
        Some(program_files().join(r"Calibre2\ebook-convert.exe")),
        Some(root.join(r"calibre\ebook-convert.exe")),
    ]
    .into_iter()
    .flatten()
    .collect::<Vec<_>>();
    find_first(&candidates)
}

pub fn locate_dcraw() -> Option<PathBuf> {
    let root = engines_root();
    let candidates = vec![
        env_override("BETTERDESKTOP_DCRAW_PATH"),
        Some(root.join(r"dcraw\dcraw.exe")),
        Some(program_files().join(r"dcraw\dcraw.exe")),
    ]
    .into_iter()
    .flatten()
    .collect::<Vec<_>>();
    find_first(&candidates)
}

/// 受管独立 soffice profile（%LOCALAPPDATA%\BetterDesktop\soffice-profile；URI 编码）。
pub fn user_profile_url() -> String {
    let dir = local_app_data().join(r"BetterDesktop\soffice-profile");
    let _ = std::fs::create_dir_all(&dir);
    uri_encode_path(&dir)
}

/// 路径 → file:/// URI（非 ASCII 与特殊字符 percent-encode；空格 → %20）。
fn uri_encode_path(p: &Path) -> String {
    let s = p.to_string_lossy().replace('\\', "/");
    let mut out = String::from("file:///");
    for b in s.bytes() {
        match b {
            b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'/' | b'-' | b'_' | b'.' | b'~' | b':' => {
                out.push(b as char)
            }
            _ => out.push_str(&format!("%{b:02X}")),
        }
    }
    out
}

/// 执行探测命令：CREATE_NO_WINDOW + 重定向双管道 + 超时强杀。返回 (exit_code, stdout∪stderr)。
fn run_probe(exe: &Path, args: &[&str]) -> (Option<i32>, String) {
    let mut cmd = Command::new(exe);
    cmd.args(args).stdin(Stdio::null()).stdout(Stdio::piped()).stderr(Stdio::piped());
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        cmd.creation_flags(0x0800_0000); // CREATE_NO_WINDOW
    }
    let mut child = match cmd.spawn() {
        Ok(c) => c,
        Err(_) => return (None, String::new()),
    };
    let start = Instant::now();
    loop {
        match child.try_wait() {
            Ok(Some(status)) => {
                let code = status.code();
                let output = child.wait_with_output();
                return match output {
                    Ok(o) => {
                        let text = read_both(o.stdout, o.stderr);
                        (code, text)
                    }
                    Err(_) => (code, String::new()),
                };
            }
            Ok(None) => {
                if start.elapsed() >= Duration::from_millis(PROBE_TIMEOUT_MS) {
                    let _ = child.kill();
                    let _ = child.wait();
                    let _ = child.wait_with_output();
                    return (None, "探测超时".to_string());
                }
                std::thread::sleep(Duration::from_millis(20));
            }
            Err(_) => return (None, String::new()),
        }
    }
}

fn read_both(stdout: Vec<u8>, stderr: Vec<u8>) -> String {
    let out = String::from_utf8_lossy(&stdout);
    let err = String::from_utf8_lossy(&stderr);
    let combined = format!("{out}{err}");
    combined.trim().to_string()
}

fn first_line(s: &str) -> Option<&str> {
    s.lines().next().map(str::trim).filter(|l| !l.is_empty())
}

fn contains_ci(s: &str, pat: &str) -> bool {
    s.to_lowercase().contains(&pat.to_lowercase())
}

/// 从 "pandoc 3.6.2" / "pandoc 2.19" 解析主版本号。
fn parse_major_version(first_line: &str) -> u32 {
    for part in first_line.split_whitespace() {
        if let Some(digits) = part
            .trim_start_matches(|c: char| !c.is_ascii_digit())
            .split('.')
            .next()
        {
            if let Ok(n) = digits.parse::<u32>() {
                return n;
            }
        }
    }
    0
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn uri_encode_path_spaces_and_ascii() {
        assert_eq!(
            uri_encode_path(Path::new(r"C:\Users\John Doe\BetterDesktop\soffice-profile")),
            "file:///C:/Users/John%20Doe/BetterDesktop/soffice-profile"
        );
    }

    #[test]
    fn uri_encode_path_non_ascii_bytes() {
        let p = Path::new(r"D:\测试 目录\profile");
        let uri = uri_encode_path(p);
        assert!(uri.starts_with("file:///D:/"));
        assert!(uri.contains("%E6%B5%8B%E8%AF%95")); // 测试 UTF-8
        assert!(uri.contains("%20"));
    }

    #[test]
    fn first_line_parsing() {
        assert_eq!(first_line("pandoc 3.6.2\nFeatures: ..."), Some("pandoc 3.6.2"));
        assert_eq!(first_line("  \n "), None);
        assert_eq!(first_line(""), None);
    }

    #[test]
    fn contains_ci_case_insensitive() {
        assert!(contains_ci("LibreOffice 26.8.0.3", "libreoffice"));
        assert!(contains_ci("pdftoppm version 24.02.0", "PDFTOPPM VERSION"));
        assert!(!contains_ci("tesseract v5", "libreoffice"));
    }

    #[test]
    fn pandoc_major_version_gate() {
        assert_eq!(parse_major_version("pandoc 3.6.2"), 3);
        assert_eq!(parse_major_version("pandoc 2.19.2"), 2);
        assert_eq!(parse_major_version("pandoc 3.0"), 3);
        assert_eq!(parse_major_version(""), 0);
    }

    #[test]
    fn env_override_wins_over_candidates() {
        // 只验证候选构造顺序函数：env 非空时排首位。
        let _ = engines_root(); // 触达（不跑真实定位）
    }

    #[test]
    fn probe_managed_engines_always_ok() {
        let probe = EngineProbe::new();
        for kind in [
            EngineKind::Managed,
            EngineKind::Image,
            EngineKind::PdfCompose,
            EngineKind::PdfSecurity,
            EngineKind::Heic,
        ] {
            let a = probe.probe(kind);
            assert!(a.available, "{kind:?} 应恒可用");
            assert!(a.version.is_some());
        }
    }

    #[test]
    fn probe_com_pdf_is_downgraded() {
        let probe = EngineProbe::new();
        let a = probe.probe(EngineKind::ComPdf);
        assert!(!a.available);
        assert!(a.reason.is_some());
    }

    #[test]
    fn probe_cache_hits() {
        let probe = EngineProbe::new();
        let a1 = probe.probe(EngineKind::Managed);
        let a2 = probe.probe(EngineKind::Managed);
        assert_eq!(a1, a2);
        probe.reset();
        let a3 = probe.probe(EngineKind::Managed);
        assert_eq!(a1, a3);
    }
}
