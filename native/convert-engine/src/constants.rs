// 红线常量集中点（C# ConvertServiceConstants 等价物）：超时、临时目录前缀、设置键前缀、UTF-8 filters。

/// 子进程默认超时（ms；红线 2：长任务必须超时控制 + 杀进程树）。
pub const TIMEOUT_MS: u64 = 120_000;

/// FFmpeg 音视频转换超时（ms；大文件编码耗时长，按红线 2 允许按类型单独配置）。
pub const MEDIA_TIMEOUT_MS: u64 = 600_000;

/// 同卷临时目录前缀（红线 3：先写临时目录、成功才原子发布）。
pub const TEMP_DIR_PREFIX: &str = ".bd-convert-";

/// 设置键前缀（P1-D 操作记忆：convert.last-target.<ext>）。
pub const SETTINGS_KEY_PREFIX: &str = "convert.";

/// txt 导出 UTF-8 filter（红线 6：soffice 默认文本编码非 UTF-8，中文必乱码）。
pub const TXT_UTF8_FILTER: &str = "txt:Text (encoded):UTF8";

/// csv 导出 filter（红线 6：StarCalc filter options 44=逗号、34=双引号、76=UTF-8 代码页）。
pub const CSV_UTF8_FILTER: &str = "csv:Text - txt - csv (StarCalc):44,34,76";

/// P1-D 操作记忆设置键（convert.last-target.<ext>；由 C# 菜单层读写，Rust 侧不感知）。
pub fn last_target_key(extension: &str) -> String {
    format!(
        "{SETTINGS_KEY_PREFIX}last-target.{}",
        extension.trim_start_matches('.').to_lowercase()
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn last_target_key_matches_csharp() {
        assert_eq!(last_target_key(".PDF"), "convert.last-target.pdf");
        assert_eq!(last_target_key("Docx"), "convert.last-target.docx");
        assert_eq!(last_target_key(".mp4"), "convert.last-target.mp4");
    }

    #[test]
    fn utf8_filters_preserved() {
        // 红线 6 常量原样（中文读回无乱码的验收锚点）。
        assert_eq!(TXT_UTF8_FILTER, "txt:Text (encoded):UTF8");
        assert_eq!(CSV_UTF8_FILTER, "csv:Text - txt - csv (StarCalc):44,34,76");
    }
}
