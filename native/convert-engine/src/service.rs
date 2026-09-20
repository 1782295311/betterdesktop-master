// 安全输出（C# ConversionService 的 pdf-edit-safe-output 红线等价）：
// 输入校验 → 引擎写同卷临时目录（.bd-convert-*）→ 产物回读验证 → 原子 Move 发布 →
// 重名 (2)(3) 永不覆盖 → 输出路径绝不等于任一输入；临时目录 Drop 自清理。
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

use crate::constants::TEMP_DIR_PREFIX;
use crate::error::{ConvertError, Error};
use crate::matrix::ConversionMatrix;

/// 同卷临时目录（源文件同目录；Drop 时递归自清理——失败不阻断，等价 C# finally M10）。
pub struct TempDir {
    path: PathBuf,
}

impl TempDir {
    /// 在 `next_to`（源文件）同目录创建 .bd-convert-<pid>-<nanos>-<n> 目录。
    pub fn create(next_to: &Path) -> Result<TempDir, Error> {
        let directory = next_to.parent().ok_or_else(|| {
            Error::output_failed(format!("无法确定源文件目录: {}", next_to.display()))
        })?;
        if !directory.is_dir() {
            return Err(Error::output_failed(format!(
                "源文件目录不存在: {}",
                directory.display()
            )));
        }
        let nanos = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map(|d| d.as_nanos())
            .unwrap_or(0);
        let pid = std::process::id();
        for attempt in 0..16u32 {
            let name = format!("{TEMP_DIR_PREFIX}{pid}-{nanos:x}-{attempt}");
            let path = directory.join(name);
            match std::fs::create_dir(&path) {
                Ok(()) => return Ok(TempDir { path }),
                Err(e) if e.kind() == std::io::ErrorKind::AlreadyExists => continue,
                Err(e) => {
                    return Err(Error::output_failed(format!(
                        "创建临时目录失败（{}）: {e}",
                        path.display()
                    )))
                }
            }
        }
        Err(Error::output_failed("创建临时目录失败：重名冲突（16 次重试）"))
    }

    pub fn path(&self) -> &Path {
        &self.path
    }
}

impl Drop for TempDir {
    fn drop(&mut self) {
        // 清理失败不阻断结果（等价 C# M10 注释）。
        let _ = std::fs::remove_dir_all(&self.path);
    }
}

/// 输入校验（非空/无 \0/存在/矩阵登记），返回规范化绝对路径。
pub fn validate_input(path: &str, matrix: &ConversionMatrix) -> Result<PathBuf, Error> {
    if path.trim().is_empty() || path.contains('\0') {
        return Err(Error::input_invalid("输入路径非法"));
    }
    let p = Path::new(path);
    if !p.is_file() {
        return Err(Error::input_invalid("输入文件不存在"));
    }
    let ext = p
        .extension()
        .map(|e| format!(".{}", e.to_string_lossy().to_lowercase()))
        .unwrap_or_default();
    if !matrix.is_convertible(&ext) {
        return Err(Error::input_invalid(format!("不支持的类型: {ext}")));
    }
    match std::fs::canonicalize(p) {
        Ok(c) => Ok(c),
        Err(e) => Err(Error::input_invalid(format!("路径解析失败: {e}"))),
    }
}

/// 产物回读验证：全部存在且非空（C# RunVerified 的判定；失败重试策略在调用方）。
pub fn verify_products(products: &[PathBuf]) -> bool {
    !products.is_empty()
        && products.iter().all(|p| {
            std::fs::metadata(p)
                .map(|m| m.is_file() && m.len() > 0)
                .unwrap_or(false)
        })
}

/// Windows 路径语义下的大小写不敏感相等（同一目录内的文件名不区分大小写）。
fn same_path(a: &Path, b: &Path) -> bool {
    a.to_string_lossy().eq_ignore_ascii_case(&b.to_string_lossy())
}

/// 为产物**原子占位**一个可用文件名：`name.ext` → `name (2).ext` → `name (3).ext` …
///
/// 【2026-09-14 修复 · 两道防线】
/// 1. 「输出不得等于任一输入」此前是**死代码**：输入集经 `canonicalize`（Windows 上返回
///    `\\?\C:\...` 扩展前缀路径），却与未规范化的目标直接 `==` 比较 → 恒为 false。
///    现统一为「规范化父目录 + 文件名」比较，并按 Windows 语义大小写不敏感。
///    命中输入名时**让位到下一个序号**（而非直接报错）—— 这正是
///    `publish_all_never_overwrites_input` 期望的 `doc.docx → doc (2).docx` 语义。
/// 2. `exists()` 探测与 `rename()`（Windows 覆盖语义）之间存在 TOCTOU：并发发布同一目录会互相覆盖，
///    与"重名 (2)(3) 永不覆盖"的承诺矛盾。改用 `create_new` **原子占位** —— 抢到名字的调用者
///    才拥有它，随后 `rename` 覆盖的是我们自己刚建的空占位文件，别人的文件永不进入覆盖路径。
fn reserve_target(
    directory: &Path,
    name: &str,
    extension: &str,
    input_set: &[PathBuf],
    dir_canonical: &Path,
) -> Result<PathBuf, Error> {
    for i in 1..=9999u32 {
        let candidate = if i == 1 {
            directory.join(format!("{name}.{extension}"))
        } else {
            directory.join(format!("{name} ({i}).{extension}"))
        };
        let candidate_cmp = candidate
            .file_name()
            .map(|n| dir_canonical.join(n))
            .unwrap_or_else(|| candidate.clone());
        if input_set.iter().any(|inp| same_path(inp, &candidate_cmp)) {
            continue; // 与输入同名 → 让位（绝不覆盖输入）
        }
        match std::fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&candidate)
        {
            Ok(_) => return Ok(candidate),
            // 已被占（既有文件 / 并发抢占）→ 取下一个序号
            Err(e) if e.kind() == std::io::ErrorKind::AlreadyExists => continue,
            Err(e) => {
                return Err(Error::output_failed(format!(
                    "无法占用输出路径（{}）: {e}",
                    candidate.display()
                )))
            }
        }
    }
    Err(Error::output_failed("输出重名冲突过多（已尝试 9999 个序号）"))
}

/// 原子发布（C# PublishAll 等价）：
/// 单产物 = 原名+新扩展名；多产物 = 原名-1..N；重名自动 (2)(3)；输出不得等于任一输入；同卷 Move = 原子。
pub fn publish_all(
    products: &[PathBuf],
    base_name: &str,
    target_format: &str,
    inputs: &[PathBuf],
) -> Result<Vec<PathBuf>, Error> {
    let directory = inputs[0]
        .parent()
        .ok_or_else(|| Error::output_failed("无法确定输出目录"))?;
    // 规范化父目录（存在，故 canonicalize 必成功；失败则退回原样，行为与旧实现一致）
    let dir_canonical = std::fs::canonicalize(directory).unwrap_or_else(|_| directory.to_path_buf());
    let input_set: Vec<PathBuf> = inputs
        .iter()
        .filter_map(|p| std::fs::canonicalize(p).ok())
        .collect();

    let mut outputs = Vec::with_capacity(products.len());
    for (i, product) in products.iter().enumerate() {
        let name = if products.len() > 1 {
            format!("{base_name}-{}", i + 1)
        } else {
            base_name.to_string()
        };
        let target = reserve_target(directory, &name, target_format, &input_set, &dir_canonical)?;
        // 同卷 Move = 原子发布。目标已由 reserve_target 原子占位（此刻那个空文件是我们自己的，
        // 覆盖它安全）—— 这正是"绝不覆盖别人的文件"能成立的原因。
        match std::fs::rename(product, &target) {
            Ok(()) => outputs.push(target),
            Err(e) => {
                // 发布失败：撤掉自己的空占位，避免留下 0 字节垃圾文件
                let _ = std::fs::remove_file(&target);
                return Err(Error::output_failed(format!(
                    "发布产物失败（{} → {}）: {e}",
                    product.display(),
                    target.display()
                )));
            }
        }
    }
    Ok(outputs)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn tmp_test_dir() -> PathBuf {
        let dir = std::env::temp_dir().join(format!(
            "bdt-convert-s3-test-{}-{:x}",
            std::process::id(),
            SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        std::fs::create_dir_all(&dir).unwrap();
        dir
    }

    #[test]
    fn temp_dir_created_next_to_source_and_cleaned() {
        let dir = tmp_test_dir();
        let source = dir.join("输入.docx");
        std::fs::write(&source, b"x").unwrap();
        let td = TempDir::create(&source).unwrap();
        assert!(td.path().starts_with(&dir));
        let name = td.path().file_name().unwrap().to_string_lossy().to_string();
        assert!(name.starts_with(TEMP_DIR_PREFIX), "前缀必须 {TEMP_DIR_PREFIX}: {name}");
        assert!(td.path().is_dir());
        let p = td.path().to_path_buf();
        drop(td);
        assert!(!p.exists(), "Drop 后临时目录应被自清理");
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn validate_input_rejects_invalid() {
        let dir = tmp_test_dir();
        let good = dir.join("a.docx");
        std::fs::write(&good, b"x").unwrap();
        let m = ConversionMatrix::default();
        assert!(validate_input("", &m).is_err());
        assert!(validate_input("bad\0path", &m).is_err());
        assert!(validate_input(&dir.join("missing.docx").to_string_lossy(), &m).is_err());
        let bad_ext = dir.join("a.xyz");
        std::fs::write(&bad_ext, b"x").unwrap();
        assert!(validate_input(&bad_ext.to_string_lossy(), &m).is_err());
        let ok = validate_input(&good.to_string_lossy(), &m).unwrap();
        assert!(ok.is_absolute());
        let _ = std::fs::remove_dir_all(&dir);
    }

    /// 【2026-09-14】重名让位改为**原子占位**：返回的名字必须已被占位（文件已存在），
    /// 且原有两个同名文件的内容一字未动（"永不覆盖，用户拍板"）。
    #[test]
    fn reserve_target_skips_taken_names_and_never_overwrites() {
        let dir = tmp_test_dir();
        let a = dir.join("报告.pdf");
        std::fs::write(&a, b"1").unwrap();
        let b = dir.join("报告 (2).pdf");
        std::fs::write(&b, b"2").unwrap();
        let canon = std::fs::canonicalize(&dir).unwrap();

        let t = reserve_target(&dir, "报告", "pdf", &[], &canon).unwrap();
        assert_eq!(t, dir.join("报告 (3).pdf"));
        assert!(t.exists(), "占位后该名字必须已被占用（消 exists→rename 的 TOCTOU）");
        assert_eq!(std::fs::read(&a).unwrap(), b"1", "既有文件不得被覆盖");
        assert_eq!(std::fs::read(&b).unwrap(), b"2");

        let t2 = reserve_target(&dir, "新文件", "pdf", &[], &canon).unwrap();
        assert_eq!(t2, dir.join("新文件.pdf"));
        let _ = std::fs::remove_dir_all(&dir);
    }

    /// 与输入同名时必须**让位**（不报错、不覆盖）—— 这是 `publish_all_never_overwrites_input`
    /// 能成立的前置；修复前该守卫因 `\\?\` 前缀不一致而恒不成立（死代码）。
    #[test]
    fn reserve_target_yields_to_input_name() {
        let dir = tmp_test_dir();
        let input = dir.join("doc.docx");
        std::fs::write(&input, b"original").unwrap();
        let canon = std::fs::canonicalize(&dir).unwrap();
        let inputs = vec![std::fs::canonicalize(&input).unwrap()];

        let t = reserve_target(&dir, "doc", "docx", &inputs, &canon).unwrap();
        assert_eq!(t, dir.join("doc (2).docx"), "与输入同名必须让位到 (2)");
        assert_eq!(std::fs::read(&input).unwrap(), b"original", "输入文件必须原样保留");
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn publish_all_single_and_multi_naming() {
        let dir = tmp_test_dir();
        let input = dir.join("src.docx");
        std::fs::write(&input, b"x").unwrap();
        // 单产物
        let p1 = dir.join("temp-prod.pdf");
        std::fs::write(&p1, b"pdf").unwrap();
        let outs = publish_all(&[p1], "src", "pdf", &[input.clone()]).unwrap();
        assert_eq!(outs, vec![dir.join("src.pdf")]);
        assert!(dir.join("src.pdf").exists());
        // 已存在 → (2)
        let p2 = dir.join("temp-prod2.pdf");
        std::fs::write(&p2, b"pdf2").unwrap();
        let outs2 = publish_all(&[p2], "src", "pdf", &[input.clone()]).unwrap();
        assert_eq!(outs2, vec![dir.join("src (2).pdf")]);
        // 多产物 → base-1..N
        let pa = dir.join("pa.pdf");
        let pb = dir.join("pb.pdf");
        std::fs::write(&pa, b"a").unwrap();
        std::fs::write(&pb, b"b").unwrap();
        let outs3 = publish_all(&[pa, pb], "合并", "pdf", &[input]).unwrap();
        assert_eq!(outs3, vec![dir.join("合并-1.pdf"), dir.join("合并-2.pdf")]);
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn publish_all_never_overwrites_input() {
        let dir = tmp_test_dir();
        let input = dir.join("doc.docx");
        std::fs::write(&input, b"original").unwrap();
        // 产物基名与输入相同 → unique_target 绕开输入，输出 (2)，输入原内容不动。
        let p = dir.join("temp-prod.docx");
        std::fs::write(&p, b"converted").unwrap();
        let outs = publish_all(&[p], "doc", "docx", &[input.clone()]).unwrap();
        assert_eq!(outs, vec![dir.join("doc (2).docx")]);
        assert_eq!(std::fs::read_to_string(&input).unwrap(), "original");
        assert_eq!(std::fs::read_to_string(&outs[0]).unwrap(), "converted");
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn verify_products_requires_nonempty() {
        let dir = tmp_test_dir();
        let empty = dir.join("empty.pdf");
        std::fs::write(&empty, b"").unwrap();
        let ok = dir.join("ok.pdf");
        std::fs::write(&ok, b"data").unwrap();
        assert!(verify_products(&[ok.clone()]));
        assert!(!verify_products(&[empty]));
        assert!(!verify_products(&[dir.join("missing.pdf")]));
        assert!(!verify_products(&[]));
        let _ = std::fs::remove_dir_all(&dir);
    }
}
