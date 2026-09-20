//! Office 旧格式桥：.doc/.xls/.ppt -> 现代格式（docx/xlsx/pptx），供主链继续转换。
//!
//! 引擎优先级（用户点名 LibreOffice，未装则回退 Microsoft Office COM）：
//!   1. LibreOffice：soffice --headless --convert-to（跨平台）
//!   2. Microsoft Office：PowerShell 驱动 COM（Word/Excel/PowerPoint 另存为）
//!
//! 诚实边界：需要本机安装 LibreOffice 或 MS Office，否则明确报错；
//!   路径含单引号时 COM 脚本会失败（PowerShell 单引号字符串无法转义）；
//!   转换保真度取决于宿主 Office 应用，本模块只保证格式外壳转换。

use std::path::{Path, PathBuf};

#[derive(Debug)]
pub enum Engine {
    LibreOffice(PathBuf),
    MsOffice,
}

const LO_PATHS: [&str; 2] = [
    r"C:\Program Files\LibreOffice\program\soffice.exe",
    r"C:\Program Files (x86)\LibreOffice\program\soffice.exe",
];
const MS_WORD_PATH: &str = r"C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE";

/// 探测可用引擎：LibreOffice 优先，MS Office 回退。
pub fn find_engine() -> Option<Engine> {
    for p in LO_PATHS {
        if Path::new(p).exists() {
            return Some(Engine::LibreOffice(PathBuf::from(p)));
        }
    }
    if Path::new(MS_WORD_PATH).exists() {
        return Some(Engine::MsOffice);
    }
    None
}

/// 旧版格式 -> 现代等价格式。返回中间文件路径（系统临时目录）。
pub fn to_modern(input: &Path, in_ext: &str) -> Result<PathBuf, String> {
    let ext = match in_ext {
        "doc" => "docx",
        "xls" => "xlsx",
        "ppt" => "pptx",
        _ => return Err(format!("office 桥不支持输入扩展名：{in_ext}")),
    };
    let engine = find_engine().ok_or_else(|| {
        "转换 .doc/.xls/.ppt 需要 LibreOffice 或 Microsoft Office（未检测到，请安装其一）".to_string()
    })?;
    let dir = std::env::temp_dir();
    let stem = input
        .file_stem()
        .and_then(|s| s.to_str())
        .ok_or_else(|| "输入文件名无效".to_string())?;
    let mid = dir.join(format!("{stem}.{ext}"));
    match engine {
        Engine::LibreOffice(soffice) => lo_convert(&soffice, input, ext, &mid),
        Engine::MsOffice => ms_convert(input, in_ext, ext, &mid),
    }
}

fn lo_convert(soffice: &Path, input: &Path, ext: &str, mid: &Path) -> Result<PathBuf, String> {
    let outdir = mid.parent().ok_or_else(|| "临时目录无效".to_string())?;
    let out = std::process::Command::new(soffice)
        .arg("--headless")
        .arg("--convert-to")
        .arg(ext)
        .arg("--outdir")
        .arg(outdir)
        .arg(input)
        .output()
        .map_err(|e| format!("启动 LibreOffice 失败：{e}"))?;
    if !out.status.success() {
        return Err(format!(
            "LibreOffice 转换失败：{}",
            String::from_utf8_lossy(&out.stderr).trim()
        ));
    }
    if !mid.exists() {
        return Err("LibreOffice 未产出目标文件".to_string());
    }
    Ok(mid.to_path_buf())
}

fn ms_convert(input: &Path, in_ext: &str, ext: &str, mid: &Path) -> Result<PathBuf, String> {
    let script = match (in_ext, ext) {
        ("doc", "docx") => format!(
            "$w=New-Object -ComObject Word.Application;$w.Visible=$false;$w.DisplayAlerts=0;\
             $d=$w.Documents.Open('{}');$d.SaveAs2('{}',16);$d.Close();$w.Quit()",
            input.display(),
            mid.display()
        ),
        ("xls", "xlsx") => format!(
            "$x=New-Object -ComObject Excel.Application;$x.Visible=$false;$x.DisplayAlerts=0;\
             $b=$x.Workbooks.Open('{}');$b.SaveAs('{}',51);$b.Close();$x.Quit()",
            input.display(),
            mid.display()
        ),
        ("ppt", "pptx") => format!(
            "$p=New-Object -ComObject PowerPoint.Application;$p.Visible=1;\
             $pr=$p.Presentations.Open('{}',$true,$false,$false);$pr.SaveAs('{}',24);$pr.Close();$p.Quit()",
            input.display(),
            mid.display()
        ),
        _ => return Err(format!("MS Office 桥不支持 {in_ext} -> {ext}")),
    };
    // PowerShell 5.1 无 BOM 的 .ps1 按 ANSI 读取，中文路径会乱码 -> 写 UTF-8 BOM
    let ps_path = mid.with_extension("ps1");
    let mut bom = vec![0xEF, 0xBB, 0xBF];
    bom.extend_from_slice(script.as_bytes());
    std::fs::write(&ps_path, &bom).map_err(|e| format!("写转换脚本失败：{e}"))?;
    let out = std::process::Command::new("powershell")
        .arg("-NoProfile")
        .arg("-ExecutionPolicy")
        .arg("Bypass")
        .arg("-File")
        .arg(&ps_path)
        .output()
        .map_err(|e| format!("启动 PowerShell 失败：{e}"))?;
    let _ = std::fs::remove_file(&ps_path);
    if !out.status.success() {
        return Err(format!(
            "Office COM 转换失败：{}",
            String::from_utf8_lossy(&out.stderr).trim()
        ));
    }
    if !mid.exists() {
        return Err(
            "Office COM 未产出目标文件（可能弹窗被拦截或文件受保护）".to_string(),
        );
    }
    Ok(mid.to_path_buf())
}

/// 旧版 Office 扩展名？
pub fn is_legacy_office(ext: &str) -> bool {
    matches!(ext, "doc" | "xls" | "ppt")
}

/// 原生 Rust 解析已支持的目标（无需外部引擎）。
/// 覆盖：dispatch 直接边 + combo 交叉边（旧格式 -> md(临时) -> 目标）。
pub fn is_native_supported(in_ext: &str, target: &str) -> bool {
    let combo_targets = [
        "html", "docx", "epub", "ipynb", "mediawiki", "org", "rtf", "odt", "docbook", "jats",
        "latex", "tex", "rst", "dokuwiki", "textile", "txt", "pdf",
    ];
    match in_ext {
        "doc" => matches!(target, "md" | "markdown" | "docx") || combo_targets.contains(&target),
        "xls" => matches!(target, "csv" | "xlsx" | "ods" | "pdf"),
        "ppt" => {
            matches!(target, "md" | "markdown" | "html" | "pdf")
                || combo_targets.contains(&target)
        }
        _ => false,
    }
}

/// 目标是否等于旧格式的现代等价物（doc->docx / xls->xlsx / ppt->pptx）。
pub fn is_modern_equivalent(in_ext: &str, target: &str) -> bool {
    matches!(
        (in_ext, target),
        ("doc", "docx") | ("xls", "xlsx") | ("ppt", "pptx")
    )
}
