//! 文件索引（M3）：受控目录枚举 + 常驻内存索引 + 文件名查询。
//!
//! 【为什么要有】C# `FileSearchProvider` 的兜底扫描是**每次查询现扫磁盘**（深度 2 / 1.5s 超时 / 上限 20 条），
//! 磁盘扫描是**常态开销而非降级路径**（索引会命中同名子串的无关文件，故两条腿始终并行）。
//! 常驻索引把「磁盘上有哪些文件」变成一次遍历 + 常驻内存，查询退化为内存子串匹配。
//!
//! 【根集语义】用户「下载 / 桌面 / 文档」+ 各固定盘顶层目录；**非管理员**（计划 §5.2：M1–M3 不得要求提权，
//! 故不碰 USN Journal / MFT，那是 M6 可选快路径）。
//!
//! 【剪枝纪律】必须跳过系统大目录（`Windows` / `Program Files*` / `$Recycle.Bin` / `System Volume Information`
//! / `ProgramData` / `AppData`）与噪声目录（`node_modules` / `.git`）——否则首次构建会退化成全盘遍历，
//! 且索引里 99% 是用户永远不会搜的文件。
//!
//! 【上限纪律】条目总数受 `settings.max_entries` 约束；触顶即停止收录并**标记降级**（降级必须可见，禁止静默）。
//!
//! 【权威源纪律 · 计划 §5.1】本模块只产出文件**事实**（路径 / 名称 / 大小 / 修改时间），
//! **不排序、不分类、不判分**——排序与 `Category` 仍由 C# `FileSearchProvider` / `ScoreFileName` 负责。

use std::collections::HashSet;
use std::path::{Path, PathBuf};
use std::sync::Mutex;
use std::time::Instant;

use crate::model::FileHit;
use crate::settings::Settings;

/// 库内单条记录（`name_lower` 预存，避免每次查询重复小写化）。
#[derive(Clone, Debug)]
pub struct FileEntry {
    pub path: PathBuf,
    /// 文件名（小写，查询用）。
    pub name_lower: String,
    /// 完整路径（小写，查询用）——支持按目录名 / 路径片段（含中文）命中，
    /// 如 `D:\迅雷下载\完蛋.txt` 可查「迅雷下载」。原 C# 兜底扫描与 Windows Search
    /// 对未索引中文目录均无此能力（2026-09-17 计划：补回重写后丢失的能力）。
    pub path_lower: String,
    pub size_bytes: u64,
    pub modified_ms: i64,
}

/// 常驻文件索引（构建线程写，查询线程持锁读）。
pub static FILES: Mutex<Vec<FileEntry>> = Mutex::new(Vec::new());

/// 递归深度上限（相对每个根）。下载/桌面/文档这类用户目录 3 层足够覆盖常见层级，
/// 再深基本是解压后的项目树，收益低且索引膨胀快。
pub const MAX_DEPTH: u32 = 3;

/// 必须剪枝的目录名（大小写不敏感，整段相等）。
///
/// 【与 C# 侧对齐】≡ `FileSearchProvider.SkippedRootDirNames`（18 项）：
/// `users` 必须剪——否则 `C:\Users\*` 会被整体索引（用户目录已在根集里单独加入，重复且爆量）；
/// 厂商驱动目录（`intel`/`amd`/`nvidia`/`drivers`/`dell`/`swsetup`）一并剪，与既有兜底扫描语义一致。
const PRUNED_DIRS: [&str; 18] = [
    "windows",
    "windows.old",
    "program files",
    "program files (x86)",
    "$recycle.bin",
    "system volume information",
    "programdata",
    "users",
    "perflogs",
    "recovery",
    "appdata",
    "node_modules",
    ".git",
    ".svn",
    "intel",
    "amd",
    "nvidia",
    "drivers",
];

/// 构建文件索引，返回 (条目数, 耗时毫秒)。永不 panic——单个根失败只记日志并继续。
///
/// `capped` = 是否因触达 `max_entries` 上限而截断（调用方据此标记降级）。
pub fn build(settings: &Settings) -> (usize, u64, bool) {
    let start = Instant::now();
    let mut entries: Vec<FileEntry> = Vec::new();
    let mut capped = false;

    if !settings.enabled {
        crate::log::warn("index disabled by settings; file index empty");
        let ms = start.elapsed().as_millis() as u64;
        *FILES.lock().unwrap_or_else(|e| e.into_inner()) = entries;
        return (0, ms, false);
    }

    let limit = settings.max_entries.max(1);
    for root in file_roots(settings) {
        if entries.len() >= limit {
            capped = true;
            crate::log::warn(format!(
                "file index reached max_entries={limit}; remaining roots skipped"
            ));
            break;
        }
        collect_files(&root, 0, limit, &mut entries, &mut capped);
    }

    let count = entries.len();
    *FILES.lock().unwrap_or_else(|e| e.into_inner()) = entries;
    let ms = start.elapsed().as_millis() as u64;
    crate::log::info(format!(
        "file index built: {count} files in {ms}ms (capped={capped})"
    ));
    (count, ms, capped)
}

/// 查询：文件名 / 完整路径**子串**匹配（大小写不敏感）。
///
/// 排序纪律：引擎只做「前缀命中优先」的三轮收集（文件名前缀 → 文件名包含 → 纯路径包含），
/// **limit 只控制最终返回体积，不参与候选选择**——三桶收集**全部**命中，
/// 再由调用方（C# `ScoreFileName`）排序截断。这是 2026-09-17 MAA 反例的修复：
/// 旧实现每桶按 `<limit` 贪心截断 + 按 FILES 遍历序（C 盘先于 D 盘）取先到者，
/// 导致 `C:\temp\maafw-agent-*.sock`（116 个 0 字节 socket 文件，前缀命中）占满前缀桶，
/// `D:\迅雷下载\MAA` 下同前缀命中（MAA.exe 等）被整体丢弃——目录在索引里却搜不到。
/// 路径匹配补回「按中文目录名 / 路径片段搜文件」能力（如 `D:\迅雷下载\完蛋.txt` 查「迅雷下载」）——
/// 原 C# 兜底扫描与 Windows Search 对未索引中文目录均无此能力。空查询返回空集（不返回全量）。
pub fn search(query: &str, limit: usize) -> Vec<FileHit> {
    let needle = query.trim().to_lowercase();
    if needle.is_empty() || limit == 0 {
        return Vec::new();
    }

    let files = FILES.lock().unwrap_or_else(|e| e.into_inner());

    let mut prefix: Vec<FileHit> = Vec::new();
    let mut name_contains: Vec<FileHit> = Vec::new();
    let mut path_contains: Vec<FileHit> = Vec::new();

    for entry in files.iter() {
        let name_hit = entry.name_lower.contains(&needle);
        let path_hit = entry.path_lower.contains(&needle);
        if !name_hit && !path_hit {
            continue;
        }

        let hit = FileHit {
            path: entry.path.to_string_lossy().into_owned(),
            name: entry
                .path
                .file_name()
                .map(|n| n.to_string_lossy().into_owned())
                .unwrap_or_default(),
            size_bytes: entry.size_bytes,
            modified_ms: entry.modified_ms,
        };

        if name_hit && entry.name_lower.starts_with(&needle) {
            prefix.push(hit);
        } else if name_hit {
            name_contains.push(hit);
        } else if path_hit {
            path_contains.push(hit);
        }
    }

    // 合并顺序 = 文件名前缀 > 文件名包含 > 纯路径包含（真实排名仍在 C#）；
    // 三桶各自全量保留，`limit` 仅在最终合并后截断返回体积。
    prefix.extend(name_contains);
    prefix.extend(path_contains);
    prefix.truncate(limit);
    prefix
}

/// 文件索引根集：用户「下载 / 桌面 / 文档」+ 各固定盘顶层目录。
///
/// `settings.scan_roots` 非空时**覆盖**前两者（供 C# 侧显式指定范围 / 测试注入）。
fn file_roots(settings: &Settings) -> Vec<PathBuf> {
    if !settings.scan_roots.is_empty() {
        return settings
            .scan_roots
            .iter()
            .map(PathBuf::from)
            .filter(|p| p.is_dir())
            .collect();
    }

    let mut roots = Vec::new();

    if let Ok(profile) = std::env::var("USERPROFILE") {
        for leaf in ["Downloads", "Desktop", "Documents"] {
            let candidate = PathBuf::from(&profile).join(leaf);
            if candidate.is_dir() {
                roots.push(candidate);
            }
        }
    }

    // 各固定盘顶层目录（盘根本身不直接作为根：那会退化成全盘遍历且必然触顶上限）
    for drive in crate::appindex::fixed_drives() {
        let base = PathBuf::from(&drive);
        let Ok(entries) = std::fs::read_dir(&base) else {
            continue;
        };
        for entry in entries.flatten() {
            let path = entry.path();
            if path.is_dir() && !is_pruned(&path) {
                roots.push(path);
            }
        }
    }

    dedup_paths(&mut roots);
    roots
}

fn dedup_paths(roots: &mut Vec<PathBuf>) {
    let mut seen = HashSet::new();
    roots.retain(|p| seen.insert(p.to_string_lossy().to_lowercase()));
}

/// 目录是否应剪枝（系统大目录 / 噪声目录；整段相等，大小写不敏感）。
pub fn is_pruned(path: &Path) -> bool {
    path.file_name()
        .map(|n| {
            let name = n.to_string_lossy().to_lowercase();
            PRUNED_DIRS.contains(&name.as_str())
        })
        .unwrap_or(false)
}

/// 递归收集文件（不筛扩展名——文件搜索要搜任意类型，与「程序枚举只收可执行」不同）。
fn collect_files(
    dir: &Path,
    depth: u32,
    limit: usize,
    out: &mut Vec<FileEntry>,
    capped: &mut bool,
) {
    if !dir.is_dir() {
        return;
    }

    let entries = match std::fs::read_dir(dir) {
        Ok(e) => e,
        Err(e) => {
            // 权限拒绝 / 目录消失：记日志继续（D7 要求不崩且有日志）
            crate::log::warn(format!("read_dir failed: {} ({e})", dir.display()));
            return;
        }
    };

    for entry in entries.flatten() {
        if out.len() >= limit {
            *capped = true;
            return;
        }

        let path = entry.path();
        let Ok(meta) = entry.metadata() else { continue };

        if meta.is_dir() {
            if depth < MAX_DEPTH && !is_pruned(&path) {
                collect_files(&path, depth + 1, limit, out, capped);
            }
            continue;
        }

        if !meta.is_file() {
            continue;
        }

        // 过滤 Windows socket 文件（`.sock`，大小写不敏感）：0 字节、无文件内容，
        // 是 IPC 管道残留（如 maafw-agent-*.sock）而非用户文件。旧实现收录后，
        // 这类同名前缀文件会占满查询前缀桶，把真实目标（如 D:\迅雷下载\MAA\MAA.exe）挤掉
        // （2026-09-17 MAA 反例根因之一）。
        if path
            .extension()
            .map(|e| e.to_string_lossy().eq_ignore_ascii_case("sock"))
            .unwrap_or(false)
        {
            continue;
        }

        let name_lower = path
            .file_name()
            .map(|n| n.to_string_lossy().to_lowercase())
            .unwrap_or_default();
        if name_lower.is_empty() {
            continue;
        }

        let path_lower = path.to_string_lossy().to_lowercase();
        out.push(FileEntry {
            path,
            name_lower,
            path_lower,
            size_bytes: meta.len(),
            modified_ms: modified_ms(&meta),
        });
    }
}

/// 修改时间 → Unix 毫秒（取不到为 0，与 `FileHit.modified_ms` 契约一致）。
fn modified_ms(meta: &std::fs::Metadata) -> i64 {
    meta.modified()
        .ok()
        .and_then(|t| t.duration_since(std::time::UNIX_EPOCH).ok())
        .map(|d| d.as_millis() as i64)
        .unwrap_or(0)
}

#[cfg(test)]
mod tests {
    use super::*;

    /// `FILES` 是全局静态：所有读写它的测试须持同一把测试锁串行执行，
    /// 否则 cargo 并行线程的 set→search→reset 窗口会互踩数据（2026-09-17 起显性）。
    static TEST_FILES_LOCK: Mutex<()> = Mutex::new(());

    fn temp_tree(name: &str) -> PathBuf {
        let dir = std::env::temp_dir().join(format!("bd-fileindex-{name}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        dir
    }

    fn entry(name: &str) -> FileEntry {
        FileEntry {
            path: PathBuf::from(format!("C:/x/{name}")),
            name_lower: name.to_lowercase(),
            path_lower: format!("c:/x/{}", name.to_lowercase()),
            size_bytes: 1,
            modified_ms: 0,
        }
    }

    /// 按完整路径构造条目（路径匹配测试用：中文目录 / 任意层级）。
    fn entry_at(path: &str) -> FileEntry {
        let p = PathBuf::from(path);
        let name = p
            .file_name()
            .map(|n| n.to_string_lossy().into_owned())
            .unwrap_or_default();
        FileEntry {
            path_lower: path.to_lowercase(),
            name_lower: name.to_lowercase(),
            path: p,
            size_bytes: 1,
            modified_ms: 0,
        }
    }

    #[test]
    fn prune_covers_system_and_noise_dirs() {
        assert!(is_pruned(Path::new("C:/Windows")));
        assert!(is_pruned(Path::new("C:/Windows.old")));
        assert!(is_pruned(Path::new("D:/Program Files")));
        assert!(is_pruned(Path::new("D:/Program Files (x86)")));
        assert!(is_pruned(Path::new("C:/$Recycle.Bin")));
        assert!(is_pruned(Path::new("C:/a/node_modules")));
        assert!(is_pruned(Path::new("C:/a/.git")));
        // 与 C# 兜底扫描对齐：users 必须剪（用户目录已在根集单独加入，重复且爆量）
        assert!(is_pruned(Path::new("C:/Users")));
        assert!(is_pruned(Path::new("C:/NVIDIA")));
        assert!(is_pruned(Path::new("C:/AMD")));
        // 普通目录不得被剪
        assert!(!is_pruned(Path::new("D:/迅雷下载")));
        assert!(!is_pruned(Path::new("D:/Projects")));
    }

    /// 剪枝 + 深度护栏：被剪目录内的文件不收；超出 MAX_DEPTH 的不收；浅层任意类型都收。
    #[test]
    fn collect_prunes_and_respects_depth() {
        let root = temp_tree("depth");
        let windows = root.join("Windows");
        std::fs::create_dir_all(&windows).unwrap();
        std::fs::write(windows.join("system.dll"), b"").unwrap();

        let deep = root.join("a").join("b").join("c").join("d");
        std::fs::create_dir_all(&deep).unwrap();
        std::fs::write(deep.join("deep.txt"), b"").unwrap();

        std::fs::write(root.join("report.pdf"), b"").unwrap();

        let mut out = Vec::new();
        let mut capped = false;
        collect_files(&root, 0, 1000, &mut out, &mut capped);

        let names: Vec<String> = out.iter().map(|e| e.name_lower.clone()).collect();
        assert!(names.contains(&"report.pdf".to_string()), "浅层任意类型应收录: {names:?}");
        assert!(!names.contains(&"system.dll".to_string()), "Windows 目录必须剪枝");
        assert!(!names.contains(&"deep.txt".to_string()), "超深文件不应收录");
        assert!(!capped);

        let _ = std::fs::remove_dir_all(&root);
    }

    /// 上限：达到 max_entries 即停止并标记 capped（降级可见）。
    #[test]
    fn collect_stops_at_limit_and_flags_capped() {
        let root = temp_tree("limit");
        for i in 0..10 {
            std::fs::write(root.join(format!("f{i}.txt")), b"").unwrap();
        }

        let mut out = Vec::new();
        let mut capped = false;
        collect_files(&root, 0, 3, &mut out, &mut capped);

        assert_eq!(out.len(), 3);
        assert!(capped, "触顶必须标记 capped（调用方据此降级）");

        let _ = std::fs::remove_dir_all(&root);
    }

    /// 空目录 / 不存在的根不得 panic（D7）。
    #[test]
    fn collect_tolerates_empty_and_missing() {
        let root = temp_tree("empty");
        let mut out = Vec::new();
        let mut capped = false;
        collect_files(&root, 0, 100, &mut out, &mut capped);
        assert!(out.is_empty());

        collect_files(Path::new("Z:/definitely-missing"), 0, 100, &mut out, &mut capped);
        assert!(out.is_empty());

        let _ = std::fs::remove_dir_all(&root);
    }

    /// Windows socket 文件（`.sock`）不收录——0 字节 IPC 残留，收录后会占满
    /// 查询前缀桶、挤掉同前缀真实文件（2026-09-17 MAA 反例根因之一）。
    #[test]
    fn collect_skips_socket_files() {
        let root = temp_tree("sock");
        std::fs::write(root.join("maafw-agent-abc123.sock"), b"").unwrap();
        std::fs::write(root.join("maafw-agent-xyz789.SOCK"), b"").unwrap();
        std::fs::write(root.join("MAA.exe"), b"x").unwrap();

        let mut out = Vec::new();
        let mut capped = false;
        collect_files(&root, 0, 100, &mut out, &mut capped);

        let names: Vec<String> = out.iter().map(|e| e.name_lower.clone()).collect();
        assert!(names.contains(&"maa.exe".to_string()), "普通文件应收录: {names:?}");
        assert!(!names.iter().any(|n| n.ends_with(".sock")), ".sock 不得收录: {names:?}");
        assert!(!capped);

        let _ = std::fs::remove_dir_all(&root);
    }

    /// MAA 反例回归（2026-09-17）：C 盘先遍历的同前缀命中 + 噪声 .sock 曾把
    /// `D:\迅雷下载\MAA` 的后遍历同前缀命中（MAA.exe 等）挤出结果。
    /// 修复后：limit 内的跨盘前缀命中必须全部保留（防未来回退到「桶内 <limit 截断」）。
    #[test]
    fn search_keeps_late_prefix_matches_across_drives() {
        let _guard = TEST_FILES_LOCK.lock().unwrap();
        let mut entries: Vec<FileEntry> = Vec::new();
        for i in 0..15 {
            entries.push(entry_at(&format!(
                "C:/Users/u/Desktop/fangzhoujiaoben/终末地/Maa{i}.dll"
            )));
        }
        for i in 0..20 {
            entries.push(entry_at(&format!("D:/迅雷下载/MAA/MAA{i}.exe")));
        }
        *FILES.lock().unwrap() = entries;

        let hits = search("maa", 100);
        assert_eq!(hits.len(), 35, "前缀命中应全量进入结果");
        let d = hits
            .iter()
            .filter(|h| h.path.starts_with("D:/迅雷下载/MAA"))
            .count();
        assert_eq!(d, 20, "limit 内后遍历的 D 盘命中必须保留（旧实现桶内 <limit 截断会丢）");

        *FILES.lock().unwrap() = Vec::new();
    }

    /// 查询：大小写不敏感子串；空查询 / limit=0 返回空集（不返回全量）。
    #[test]
    fn search_is_case_insensitive_and_guards_empty_query() {
        let _guard = TEST_FILES_LOCK.lock().unwrap();
        *FILES.lock().unwrap() = vec![entry("Report.PDF"), entry("notes.txt")];

        assert_eq!(search("report", 10).len(), 1);
        assert_eq!(search("REPORT", 10).len(), 1);
        assert_eq!(search("notes", 10)[0].name, "notes.txt");
        assert!(search("", 10).is_empty());
        assert!(search("   ", 10).is_empty());
        assert!(search("report", 0).is_empty());
        assert!(search("nothing-matches", 10).is_empty());

        *FILES.lock().unwrap() = Vec::new();
    }

    /// 前缀命中优先于子串命中——避免截断丢掉最相关的候选（真正的排名仍在 C#）。
    #[test]
    fn search_prefers_prefix_matches() {
        let _guard = TEST_FILES_LOCK.lock().unwrap();
        *FILES.lock().unwrap() = vec![entry("my-report.txt"), entry("report.txt")];

        let hits = search("report", 2);

        assert_eq!(hits[0].name, "report.txt", "前缀命中必须排在前面");
        assert_eq!(hits[1].name, "my-report.txt");

        *FILES.lock().unwrap() = Vec::new();
    }

    /// limit 生效（截断），且结果不超过 limit。
    #[test]
    fn search_respects_limit() {
        let _guard = TEST_FILES_LOCK.lock().unwrap();
        *FILES.lock().unwrap() = vec![entry("a1.txt"), entry("a2.txt"), entry("a3.txt")];

        assert_eq!(search("a", 2).len(), 2);

        *FILES.lock().unwrap() = Vec::new();
    }

    /// `scan_roots` 显式指定时必须覆盖默认根集（测试注入 / 用户自定义范围的通道）。
    #[test]
    fn explicit_scan_roots_override_defaults() {
        let root = temp_tree("roots");
        let settings = Settings {
            scan_roots: vec![root.to_string_lossy().into_owned()],
            ..Settings::default()
        };

        let roots = file_roots(&settings);
        assert_eq!(roots.len(), 1);
        assert_eq!(roots[0], root);

        let _ = std::fs::remove_dir_all(&root);
    }

    /// 路径匹配：按中文目录名 / 路径片段命中其下文件（2026-09-17 补回的能力）。
    #[test]
    fn search_matches_chinese_path_segments() {
        let _guard = TEST_FILES_LOCK.lock().unwrap();
        *FILES.lock().unwrap() = vec![
            entry_at("C:/Users/17822/Desktop/自动化脚本/网易云音乐.lnk"),
            entry_at("D:/迅雷下载/完蛋.txt"),
        ];

        let hits = search("自动化脚本", 10);
        assert_eq!(hits.len(), 1, "按中文目录名应命中其下文件");
        assert_eq!(hits[0].name, "网易云音乐.lnk");

        let hits = search("迅雷下载", 10);
        assert_eq!(hits.len(), 1);
        assert_eq!(hits[0].name, "完蛋.txt");

        *FILES.lock().unwrap() = Vec::new();
    }

    /// 路径匹配大小写不敏感（含盘符 + 中文目录前缀场景）。
    #[test]
    fn search_path_match_is_case_insensitive() {
        let _guard = TEST_FILES_LOCK.lock().unwrap();
        *FILES.lock().unwrap() = vec![entry_at("D:/迅雷下载/完蛋.txt")];

        assert_eq!(search("d:/迅雷", 10).len(), 1, "盘符小写+中文目录前缀应命中");
        assert_eq!(search("D:/XUNLEI", 10).len(), 0, "大小写不敏感但文字不同仍不命中");

        *FILES.lock().unwrap() = Vec::new();
    }

    /// 三轮桶序：文件名前缀 > 文件名包含 > 纯路径包含（name 命中永远先于纯路径命中）。
    #[test]
    fn search_orders_name_hits_before_path_only() {
        let _guard = TEST_FILES_LOCK.lock().unwrap();
        *FILES.lock().unwrap() = vec![
            entry_at("D:/Projects/my-report-2026.pdf"), // 文件名包含（非前缀）report
            entry_at("D:/report/notes.txt"),         // 纯路径包含 report
            entry_at("D:/report-final.txt"),         // 文件名前缀 report
        ];

        let hits = search("report", 10);

        assert_eq!(hits[0].name, "report-final.txt", "文件名前缀第一");
        assert_eq!(hits[1].name, "my-report-2026.pdf", "文件名包含第二");
        assert_eq!(hits[2].name, "notes.txt", "纯路径包含第三");

        *FILES.lock().unwrap() = Vec::new();
    }

    /// 截断对路径命中同样生效。
    #[test]
    fn search_limit_applies_to_path_matches() {
        let _guard = TEST_FILES_LOCK.lock().unwrap();
        *FILES.lock().unwrap() = vec![
            entry_at("D:/a/report/1.txt"),
            entry_at("D:/a/report/2.txt"),
            entry_at("D:/a/report/3.txt"),
        ];

        assert_eq!(search("report", 2).len(), 2);

        *FILES.lock().unwrap() = Vec::new();
    }
}
