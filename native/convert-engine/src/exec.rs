// 通用子进程执行器（大文件稳定性核心件，C# Process 语义等价）：
// CREATE_NO_WINDOW + 双管道线程读（防 64KB 缓冲死锁）→ 轮询退出 + 超时杀进程树（taskkill /T /F）。
// 参数数组直传（红线 1：不经过 shell，中文/空格/特殊字符不串参）。
use std::path::Path;
use std::process::{Child, Command, Stdio};
use std::time::{Duration, Instant};

use crate::error::Error;

/// 子进程执行结果。
#[derive(Debug)]
pub struct ChildOutput {
    /// 退出码；None = 被强杀（超时）。
    pub code: Option<i32>,
    /// 是否超时被强杀。
    pub timed_out: bool,
    pub stdout: String,
    pub stderr: String,
}

impl ChildOutput {
    pub fn combined(&self) -> String {
        format!("{}{}", self.stdout, self.stderr).trim().to_string()
    }
}

/// 执行子进程（超时杀进程树；spawn 失败 = 引擎文件存在但无法启动 → EngineCrashed）。
pub fn run(
    exe: &Path,
    args: &[String],
    cwd: Option<&Path>,
    timeout_ms: u64,
) -> Result<ChildOutput, Error> {
    let mut cmd = Command::new(exe);
    cmd.args(args)
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());
    if let Some(dir) = cwd {
        cmd.current_dir(dir);
    }
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        cmd.creation_flags(0x0800_0000); // CREATE_NO_WINDOW
    }
    let mut child = cmd.spawn().map_err(|e| {
        Error::engine_crashed(format!("启动引擎失败（{}）: {e}", exe.display()))
    })?;

    // 双管道线程读：输出可能远超 64KB 管道缓冲（ffmpeg/soffice 大文件），不读必死锁。
    let stdout = child.stdout.take().expect("stdout piped");
    let stderr = child.stderr.take().expect("stderr piped");
    let h_out = std::thread::spawn(move || read_to_string(stdout));
    let h_err = std::thread::spawn(move || read_to_string(stderr));

    let start = Instant::now();
    let deadline = Duration::from_millis(timeout_ms);
    let mut timed_out = false;
    loop {
        match child.try_wait() {
            Ok(Some(_)) => break,
            Ok(None) => {
                if start.elapsed() >= deadline {
                    timed_out = true;
                    break;
                }
                std::thread::sleep(Duration::from_millis(50));
            }
            Err(_) => break,
        }
    }

    let code = if timed_out {
        let pid = child.id();
        kill_tree(pid); // taskkill /T /F：杀整棵进程树（等价 C# Kill(entireProcessTree: true)）
        let _ = child.wait();
        None
    } else {
        child.wait().ok().and_then(|st| st.code())
    };

    let out = h_out.join().unwrap_or_default();
    let err = h_err.join().unwrap_or_default();
    Ok(ChildOutput {
        code,
        timed_out,
        stdout: out,
        stderr: err,
    })
}

fn read_to_string<R: std::io::Read>(mut r: R) -> String {
    let mut buf = Vec::new();
    let _ = r.read_to_end(&mut buf);
    String::from_utf8_lossy(&buf).to_string()
}

/// Windows 杀进程树（taskkill /T /F；子进程也一并强杀，防孤儿进程）。
fn kill_tree(pid: u32) {
    let mut cmd = Command::new("taskkill");
    cmd.args(["/PID", &pid.to_string(), "/T", "/F"])
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null());
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        cmd.creation_flags(0x0800_0000);
    }
    let _ = cmd.status(); // 等待 taskkill 完成，确保树被杀完
}

/// 产物路径辅助：temp_dir 下 <stem>.<ext>。
pub fn product_path(temp_dir: &Path, input: &Path, ext: &str) -> std::path::PathBuf {
    let stem = input
        .file_stem()
        .map(|s| s.to_string_lossy().to_string())
        .unwrap_or_else(|| "output".to_string());
    temp_dir.join(format!("{stem}.{ext}"))
}

/// 目录遍历辅助：匹配 temp_dir 下前缀 <stem>- 且扩展名 ∈ formats 的文件（Poppler 多产物 name-1.png…）。
pub fn collect_prefixed(
    temp_dir: &Path,
    stem: &str,
    formats: &[&str],
) -> Vec<std::path::PathBuf> {
    let Ok(entries) = std::fs::read_dir(temp_dir) else {
        return vec![];
    };
    let prefix = format!("{stem}-");
    let mut found = Vec::new();
    for e in entries.flatten() {
        let p = e.path();
        let name = p.file_name().map(|n| n.to_string_lossy().to_string()).unwrap_or_default();
        if !name.starts_with(&prefix) {
            continue;
        }
        let ext = p.extension().map(|x| x.to_string_lossy().to_lowercase()).unwrap_or_default();
        if formats.iter().any(|f| *f == ext) {
            found.push(p);
        }
    }
    // 【2026-09-14 修复】按**页号自然序**排序，不能用字符串序：
    // 字符串序会得到 doc-1, doc-10, doc-2, … → ≥10 页的 PDF 转图片时产物页序错乱，
    // 而 `publish_all` 又按该顺序命名 base-1..N，错序被固化进最终文件名（用户看到的图就是乱序的）。
    found.sort_by(|a, b| page_no(a).cmp(&page_no(b)).then_with(|| a.cmp(b)));
    found
}

/// 从 `<stem>-<n>.<ext>` 形态取页号（Poppler 多产物命名约定）。
///
/// 解析不出数字时取 `u64::MAX` 排到末尾（非 `<stem>-N` 形态的文件不参与页序），
/// 数字相同则以完整路径兜底比较 → 结果**确定**（不依赖 read_dir 的枚举顺序）。
fn page_no(path: &std::path::Path) -> u64 {
    path.file_stem()
        .map(|s| s.to_string_lossy().into_owned())
        .unwrap_or_default()
        .rsplit('-')
        .next()
        .and_then(|tail| tail.parse::<u64>().ok())
        .unwrap_or(u64::MAX)
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 用 cmd 或 powershell 模拟慢进程（测超时杀树）。
    fn slow_cmd_ms(ms: u32) -> Vec<String> {
        vec![
            "cmd".to_string(),
            "/C".to_string(),
            format!("ping -n {} 127.0.0.1 >nul", ms / 1000 + 1),
        ]
    }

    #[test]
    fn run_ok_captures_output() {
        // cmd /C echo hello（输出走 stdout）
        let args = vec!["/C".to_string(), "echo hello".to_string()];
        let out = run(Path::new("cmd.exe"), &args, None, 10_000).unwrap();
        assert_eq!(out.code, Some(0));
        assert!(out.stdout.contains("hello"));
        assert!(!out.timed_out);
    }

    #[test]
    fn run_exit_code_propagated() {
        let args = vec!["/C".to_string(), "exit 3".to_string()];
        let out = run(Path::new("cmd.exe"), &args, None, 10_000).unwrap();
        assert_eq!(out.code, Some(3));
    }

    #[test]
    fn run_times_out_and_kills_tree() {
        let start = Instant::now();
        let out = run(Path::new("cmd.exe"), &slow_cmd_ms(3000), None, 300).unwrap();
        assert!(out.timed_out, "应超时");
        assert_eq!(out.code, None);
        assert!(start.elapsed() < Duration::from_secs(2), "超时后应快速返回（杀树）");
    }

    #[test]
    fn run_spawn_failure_is_engine_crashed() {
        let err = run(Path::new(r"C:\不存在\no-such.exe"), &[], None, 100).unwrap_err();
        assert_eq!(err.code.as_str(), "EngineCrashed");
    }

    /// 【2026-09-14 回归 · 页序】多页产物必须按**页号自然序**返回。
    ///
    /// 旧断言固化的是字符串序（doc-1, doc-10, doc-2）—— 那是缺陷而不是契约：
    /// ≥10 页 PDF 转图片时，产物顺序错乱会被 `publish_all` 命名固化进最终文件名。
    #[test]
    fn collect_prefixed_sorts_pages_naturally() {
        let dir = std::env::temp_dir().join(format!("bdt-exec-test-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        let _ = std::fs::create_dir_all(&dir);
        for n in ["doc-1.png", "doc-2.png", "doc-10.png", "doc-11.png", "doc.txt", "other-1.png"] {
            std::fs::write(dir.join(n), b"x").unwrap();
        }
        let found = collect_prefixed(&dir, "doc", &["png"]);
        let names: Vec<String> = found
            .iter()
            .map(|p| p.file_name().unwrap().to_string_lossy().to_string())
            .collect();
        assert_eq!(
            names,
            vec!["doc-1.png", "doc-2.png", "doc-10.png", "doc-11.png"],
            "页号必须按数值升序（自然序），且过滤掉非匹配前缀/扩展名的文件"
        );
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn product_path_naming() {
        let dir = std::path::Path::new(r"C:\tmp");
        assert_eq!(
            product_path(dir, std::path::Path::new(r"C:\src\中文 报告.docx"), "pdf"),
            dir.join("中文 报告.pdf")
        );
    }
}
