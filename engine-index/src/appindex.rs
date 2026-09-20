//! 应用索引（M2 磁盘两源）：开始菜单（全递归）+ Program Files（深度 3）。
//!
//! 【权威源纪律 · 计划 §5.1】本模块只产出**磁盘事实**——「哪些可执行文件存在于开始菜单 / Program Files」，
//! 输出 raw `AppCandidate`（路径 + 名称候选 + 源标记）。**不做** lnk 目标解析、显示名解析、
//! 排除词过滤、AppItem 语义——那些仍是 C# `shell-app-source` 的职责（引擎把路径列表交给它，
//! 它经既有 `ResolveFromPath` / `ShellLinkResolver` 升格为 AppItem）。这条边界保证不产生双份事实。
//!
//! 【结果集平价纪律（接线前必须逐条对齐当前 C# 源码，本文件为权威锚点）】
//! 1. 扩展白名单 ≡ `ShellLinkResolver.ExecutableExtensions`（`.exe/.bat/.cmd/.com/.msc/.appref-ms/.url/.lnk`，
//!    `ShellLinkResolver.cs:16-26`）；
//! 2. Program Files 根集 ≡ `GetAllProgramRoots`（ProgramFiles + ProgramFilesX86 + `%LocalAppData%\Programs`
//!    + 各固定盘 `X:\Program Files`，`AppSourceService.cs:840-880`）；深度 3 ≡ `maxDepth:3`（`:766`）；
//! 3. 开始菜单全递归、**不跳 `\startup`**——当前 C# `ScanDirectory`（`:452` `SearchOption.AllDirectories`）
//!    未落实 506 红线「跳 startup」，为结果集对齐，引擎亦不跳（若 C# 补红线，两处同批改）。
//!    引擎仍保留 `skip_startup` 机制与单测（506 能力位），`build` 默认不启用。
//!
//! 【注册表 Uninstall 三视图暂不在本模块】其过滤链复杂且毫秒级，谨慎起见 M2 先留 C#（见计划「实现进度」）。

use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::Mutex;
use std::time::Instant;

use windows::core::PCWSTR;
use windows::Win32::Storage::FileSystem::GetDriveTypeW;

/// `GetDriveTypeW` 返回 DRIVE_FIXED 的常量值（windows 0.58 未在 FileSystem 模块导出该常量，
/// 值稳定且为 Win32 公开约定：3 = 固定盘）。
const DRIVE_FIXED: u32 = 3;

use crate::model::AppCandidate;
use crate::settings::Settings;

/// 引擎侧应用候选快照（`list_apps` 返回体来源；构建线程写入，读线程持锁快照）。
pub static APPS: Mutex<Vec<AppCandidate>> = Mutex::new(Vec::new());

/// 可执行扩展白名单（≡ C# `ShellLinkResolver.ExecutableExtensions`，含 `.cmd`；大小写不敏感）。
const EXECUTABLE_EXTS: [&str; 8] = ["exe", "bat", "cmd", "com", "lnk", "msc", "appref-ms", "url"];

/// 程序文件深度上限（与 C# `ScanAllPrograms` 的 `maxDepth: 3` 对齐，`AppSourceService.cs:766`）。
const PROGRAM_FILES_DEPTH: u32 = 3;

/// 开始菜单全递归（C# `ScanDirectory` 用 `SearchOption.AllDirectories`，无深度上限）。
const START_MENU_DEPTH: u32 = u32::MAX;

pub const SOURCE_START_MENU: &str = "start-menu";
pub const SOURCE_PROGRAM_FILES: &str = "program-files";

/// 构建应用索引，返回 `(条目数, 耗时毫秒, 降级原因)`。永不 panic——任何根目录失败只记日志并继续。
///
/// 【2026-09-14 修复 · 禁止静默降级】此前只返回 `(条目数, 耗时)`，而 `degraded` 全由文件索引的
/// 「触顶截断」决定 → 「环境变量缺失 / 枚举失败 / 一条都没扫到」这类**应用索引自身**的失败
/// 完全没有出口：`list_apps` 会返回 `{apps: [], degraded: false}`，消费者（C#）据 `degraded`
/// 决定是否回退本地实现 → 它会把"没扫到"当成"系统里真的没有应用"，走空列表。
/// 这与本模块头部与 `runtime-health.md` 的 fail-closed 纪律直接冲突。
/// 现在把「无可用根目录」「结果为空」显式上报（原因串进 `status.degradeReason`）。
pub fn build(settings: &Settings) -> (usize, u64, Option<String>) {
    let start = Instant::now();

    let mut degraded: Option<String> = None;
    let mut seen: HashMap<PathBuf, &'static str> = HashMap::new();
    if settings.enabled {
        let menu_roots = start_menu_roots(settings);
        let prog_roots = program_files_roots(settings);
        if menu_roots.is_empty() && prog_roots.is_empty() {
            degraded = Some(
                "应用索引无可用根目录（APPDATA / ProgramData / ProgramFiles 均取不到，且未配置 scanRoots）"
                    .to_string(),
            );
        }
        for root in &menu_roots {
            // skip_startup=false：结果集对齐当前 C#（见模块头平价纪律第 3 条）
            collect_recursive(root, SOURCE_START_MENU, 0, START_MENU_DEPTH, false, &mut seen);
        }
        for root in &prog_roots {
            collect_recursive(root, SOURCE_PROGRAM_FILES, 0, PROGRAM_FILES_DEPTH, false, &mut seen);
        }
        if seen.is_empty() {
            // 有根但一条都没扫到（根不存在 / 权限拒绝 / 白名单不匹配）——同样必须可见
            degraded.get_or_insert_with(|| {
                format!(
                    "应用索引结果为空：开始菜单 {} 个根 + Program Files {} 个根均未枚举到可执行文件",
                    menu_roots.len(),
                    prog_roots.len()
                )
            });
        }
    } else {
        // 用户显式关闭索引：这不是降级（消费者据 enabled 语义处理），不得混淆二者
        crate::log::warn("index disabled by settings; app index empty");
    }

    let apps: Vec<AppCandidate> = seen
        .into_iter()
        .map(|(path, source)| {
            let name_hint = path
                .file_stem()
                .map(|s| s.to_string_lossy().into_owned())
                .unwrap_or_else(|| path.to_string_lossy().into_owned());
            let p = path.to_string_lossy().into_owned();
            AppCandidate {
                target_path: p.clone(),
                path: p,
                name_hint,
                source: source.to_string(),
                icon_key: String::new(), // M4 才填充
            }
        })
        .collect();

    let count = apps.len();
    *APPS.lock().unwrap_or_else(|e| e.into_inner()) = apps;
    let ms = start.elapsed().as_millis() as u64;
    if let Some(reason) = &degraded {
        crate::log::warn(format!("app index degraded: {reason}"));
    }
    crate::log::info(format!("app index built: {count} apps in {ms}ms"));
    (count, ms, degraded)
}

/// `list_apps` 返回的当前快照（克隆，避免持锁跨 RPC）。
pub fn snapshot() -> Vec<AppCandidate> {
    APPS.lock().unwrap_or_else(|e| e.into_inner()).clone()
}

/// 开始菜单根集：用户 Programs + 公共 Programs（`settings.scan_roots` 非空时用之覆盖）。
fn start_menu_roots(settings: &Settings) -> Vec<PathBuf> {
    if !settings.scan_roots.is_empty() {
        return settings.scan_roots.iter().map(PathBuf::from).collect();
    }
    let mut roots = Vec::new();
    if let Ok(roaming) = std::env::var("APPDATA") {
        roots.push(
            PathBuf::from(roaming)
                .join("Microsoft")
                .join("Windows")
                .join("Start Menu")
                .join("Programs"),
        );
    }
    if let Ok(data) = std::env::var("ProgramData") {
        roots.push(
            PathBuf::from(data)
                .join("Microsoft")
                .join("Windows")
                .join("Start Menu")
                .join("Programs"),
        );
    }
    roots
}

/// Program Files 根集（≡ C# `GetAllProgramRoots`，`AppSourceService.cs:840-880`）：
/// ProgramFiles + ProgramFiles(x86) + `%LocalAppData%\Programs` + 各固定盘 `X:\Program Files`。
fn program_files_roots(settings: &Settings) -> Vec<PathBuf> {
    if !settings.scan_roots.is_empty() {
        return settings.scan_roots.iter().map(PathBuf::from).collect();
    }
    let mut roots = Vec::new();
    for var in ["ProgramFiles", "ProgramFiles(x86)"] {
        if let Ok(dir) = std::env::var(var) {
            push_if_dir(&mut roots, PathBuf::from(dir));
        }
    }
    // %LocalAppData%\Programs（对齐 :849-854）
    if let Ok(local) = std::env::var("LOCALAPPDATA") {
        push_if_dir(&mut roots, PathBuf::from(local).join("Programs"));
    }
    // 各固定盘 X:\Program Files + X:\Program Files (x86)
    // （对齐 C# `AppSourceService.cs:856-864`，并补齐其遗漏的 `(x86)`：非系统盘的 32 位程序
    //   原先两边都不覆盖。C 盘的 Program Files (x86) 由 SpecialFolder.ProgramFilesX86 单独提供。）
    for drive in fixed_drives() {
        push_if_dir(&mut roots, PathBuf::from(format!("{drive}Program Files")));
        push_if_dir(&mut roots, PathBuf::from(format!("{drive}Program Files (x86)")));
    }
    dedup_paths(&mut roots);
    roots
}

/// 存在且为目录才收录（根集候选可能不存在）。
fn push_if_dir(roots: &mut Vec<PathBuf>, path: PathBuf) {
    if path.is_dir() {
        roots.push(path);
    }
}

/// 大小写不敏感去重（ProgramFiles 与其它盘路径理论上不重复，防御性处理）。
fn dedup_paths(roots: &mut Vec<PathBuf>) {
    let mut seen = std::collections::HashSet::new();
    roots.retain(|p| seen.insert(p.to_string_lossy().to_lowercase()));
}

/// 枚举固定盘盘符（`X:\` 形态）。pub：`fileindex` 的根集同样需要（受控目录枚举只在固定盘上）。
pub fn fixed_drives() -> Vec<String> {
    let mut drives = Vec::new();
    for c in b'A'..=b'Z' {
        let drive = format!("{}:\\", c as char);
        let wide: Vec<u16> = drive.encode_utf16().chain(std::iter::once(0)).collect();
        let drive_type = unsafe { GetDriveTypeW(PCWSTR(wide.as_ptr())) };
        if drive_type == DRIVE_FIXED {
            drives.push(drive);
        }
    }
    drives
}

/// 递归收集可执行文件（语义与 C# `EnumerateExecutablesRecursive` 对齐）：
/// - `current_depth` 从 0 起，本层目录文件全部收录；
/// - `current_depth >= max_depth` 时不再进入子目录（即文件可出现在 ≤max_depth 的层级）。
/// - `skip_startup` 时跳过名为 `Startup` 的目录（506 红线 1，大小写不敏感）。
fn collect_recursive(
    dir: &Path,
    source: &'static str,
    current_depth: u32,
    max_depth: u32,
    skip_startup: bool,
    out: &mut HashMap<PathBuf, &'static str>,
) {
    if !dir.is_dir() {
        return;
    }
    let entries = match std::fs::read_dir(dir) {
        Ok(e) => e,
        Err(e) => {
            crate::log::warn(format!("enum dir failed: {} ({e})", dir.display()));
            return;
        }
    };

    for entry in entries.flatten() {
        let path = entry.path();
        let Ok(meta) = entry.metadata() else { continue };
        if meta.is_dir() {
            if skip_startup && is_startup_dir(&path) {
                continue; // 506 红线 1：跳过自启动目录
            }
            if current_depth < max_depth {
                collect_recursive(&path, source, current_depth + 1, max_depth, skip_startup, out);
            }
            continue;
        }
        if meta.is_file() && is_executable(&path) {
            out.entry(path).or_insert(source); // 去重：首写源优先（两源罕见重叠）
        }
    }
}

/// 目录名是否 `Startup`（大小写不敏感）。
pub fn is_startup_dir(path: &Path) -> bool {
    path.file_name()
        .map(|n| n.to_string_lossy().eq_ignore_ascii_case("startup"))
        .unwrap_or(false)
}

/// 扩展名是否在可执行白名单（大小写不敏感）。
pub fn is_executable(path: &Path) -> bool {
    path.extension()
        .map(|e| e.to_string_lossy())
        .map(|e| EXECUTABLE_EXTS.iter().any(|x| e.eq_ignore_ascii_case(x)))
        .unwrap_or(false)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn temp_tree(name: &str) -> PathBuf {
        let dir = std::env::temp_dir().join(format!("bd-appindex-{name}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        dir
    }

    #[test]
    fn is_executable_whitelist() {
        assert!(is_executable(Path::new("a.exe")));
        assert!(is_executable(Path::new("a.LNK"))); // 大小写不敏感
        assert!(is_executable(Path::new("a.appref-ms")));
        assert!(!is_executable(Path::new("a.txt")));
        assert!(!is_executable(Path::new("a"))); // 无扩展名
    }

    #[test]
    fn is_startup_dir_case_insensitive() {
        assert!(is_startup_dir(Path::new("C:/x/Startup")));
        assert!(is_startup_dir(Path::new("C:/x/STARTUP")));
        assert!(!is_startup_dir(Path::new("C:/x/Programs")));
    }

    /// 红线 1 + 深度护栏 + 扩展过滤：startup 跳过；深 4 层不收；非可执行拒收。
    #[test]
    fn collect_skips_startup_and_respects_depth_and_ext() {
        let root = temp_tree("depth");
        let startup = root.join("Startup");
        std::fs::create_dir_all(&startup).unwrap();
        std::fs::write(startup.join("skipme.exe"), b"").unwrap();

        // 深 4 层：root/a/b/c/d/deep.exe（d 在深度 4，Program Files maxDepth=3 不应进入）
        let deep = root.join("a").join("b").join("c").join("d");
        std::fs::create_dir_all(&deep).unwrap();
        std::fs::write(deep.join("deep.exe"), b"").unwrap();
        // 浅 1 层：root/shallow.exe（应收录）
        std::fs::write(root.join("shallow.exe"), b"").unwrap();
        // 非可执行：root/readme.txt（应拒收）
        std::fs::write(root.join("readme.txt"), b"").unwrap();

        let mut out = HashMap::new();
        collect_recursive(&root, SOURCE_PROGRAM_FILES, 0, PROGRAM_FILES_DEPTH, true, &mut out);

        let names: Vec<String> = out
            .keys()
            .map(|p| p.file_name().unwrap().to_string_lossy().into_owned())
            .collect();
        assert!(names.contains(&"shallow.exe".to_string()), "浅层 exe 应收录: {names:?}");
        assert!(!names.contains(&"skipme.exe".to_string()), "startup 目录必须被跳过");
        assert!(!names.contains(&"deep.exe".to_string()), "深度 3 之外的 exe 不应收录");
        assert!(!names.contains(&"readme.txt".to_string()), "非可执行文件应被拒收");

        let _ = std::fs::remove_dir_all(&root);
    }

    /// 异常路径：根目录不存在 / 不可读不得 panic（返回空集即可）。
    #[test]
    fn collect_tolerates_missing_root() {
        let mut out = HashMap::new();
        collect_recursive(
            Path::new("Z:/definitely-missing"),
            SOURCE_START_MENU,
            0,
            START_MENU_DEPTH,
            true,
            &mut out,
        );
        assert!(out.is_empty());
    }

    /// 候选序列化为 camelCase 且 path/targetPath 均为 .lnk 自身路径（不解析目标）。
    #[test]
    fn candidate_has_self_target() {
        let c = AppCandidate {
            path: "C:/a.lnk".into(),
            target_path: "C:/a.lnk".into(),
            name_hint: "a".into(),
            source: SOURCE_START_MENU.into(),
            icon_key: String::new(),
        };
        let v = serde_json::to_value(&c).unwrap();
        assert_eq!(v["path"], "C:/a.lnk");
        assert_eq!(v["targetPath"], "C:/a.lnk");
        assert_eq!(v["source"], "start-menu");
    }
}
