//! 批量重命名：搜索替换 + 序号 + 前缀/后缀。

use std::path::{Path, PathBuf};

/// 批量重命名结果。
pub struct RenameResult {
    pub renamed: Vec<(PathBuf, PathBuf)>,
    pub errors: Vec<String>,
}

/// 在目录中批量重命名文件。
///
/// - find/replace: 文件名中的字符串替换
/// - prefix/suffix: 前缀/后缀
/// - number: 序号起始值（0 = 不加序号）
pub fn batch_rename(
    dir: &Path,
    find: Option<&str>,
    replace: Option<&str>,
    prefix: Option<&str>,
    suffix: Option<&str>,
    start_number: u32,
    recursive: bool,
) -> Result<RenameResult, String> {
    let mut renamed = Vec::new();
    let mut errors = Vec::new();
    let mut counter = start_number;

    let entries = if recursive {
        walk_dir(dir)?
    } else {
        std::fs::read_dir(dir)
            .map_err(|e| format!("读取目录失败：{e}"))?
            .filter_map(|e| e.ok())
            .map(|e| e.path())
            .collect()
    };

    for path in entries {
        if !path.is_file() {
            continue;
        }
        let file_name = path.file_name().unwrap().to_string_lossy().to_string();
        let parent = path.parent().unwrap();

        let mut new_name = file_name.clone();
        // 1. 搜索替换
        if let (Some(f), Some(r)) = (find, replace) {
            new_name = new_name.replace(f, r);
        }
        // 2. 前缀
        if let Some(p) = prefix {
            new_name = format!("{p}{new_name}");
        }
        // 3. 序号
        if start_number > 0 || counter != start_number || (prefix.is_none() && suffix.is_none() && find.is_none()) {
            if counter > 0 || start_number > 0 {
                new_name = format!("{counter}_{new_name}");
                counter += 1;
            }
        }
        // 4. 后缀（加在扩展名前）
        if let Some(s) = suffix {
            if let Some(pos) = new_name.rfind('.') {
                new_name = format!("{}{}{}", &new_name[..pos], s, &new_name[pos..]);
            } else {
                new_name = format!("{new_name}{s}");
            }
        }

        if new_name != file_name {
            let new_path = parent.join(&new_name);
            match std::fs::rename(&path, &new_path) {
                Ok(_) => renamed.push((path, new_path)),
                Err(e) => errors.push(format!("{} → {}：{e}", file_name, new_name)),
            }
        }
    }

    Ok(RenameResult { renamed, errors })
}

fn walk_dir(dir: &Path) -> Result<Vec<PathBuf>, String> {
    let mut result = Vec::new();
    let mut stack = vec![dir.to_path_buf()];
    while let Some(d) = stack.pop() {
        let entries = std::fs::read_dir(&d).map_err(|e| format!("读取目录失败：{e}"))?;
        for entry in entries.filter_map(|e| e.ok()) {
            let path = entry.path();
            if path.is_dir() {
                stack.push(path);
            } else {
                result.push(path);
            }
        }
    }
    Ok(result)
}
