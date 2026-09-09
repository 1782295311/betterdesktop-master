namespace BetterDesktop.Shell.Convert.Services;

/// <summary>
/// 红线常量集中点（计划 §11）：超时、临时目录前缀、设置键前缀。
/// </summary>
public static class ConvertServiceConstants
{
    /// <summary>子进程默认超时（ms；红线 2：长任务必须超时控制 + 杀进程树）。</summary>
    public const int TimeoutMs = 120_000;

    /// <summary>FFmpeg 音视频转换超时（ms；大文件编码耗时长，按红线 2 允许按类型单独配置）。</summary>
    public const int MediaTimeoutMs = 600_000;

    /// <summary>同卷临时目录前缀（红线 3：先写临时目录、成功才原子发布）。</summary>
    public const string TempDirPrefix = ".bd-convert-";

    /// <summary>设置键前缀（P1-D 操作记忆：convert.last-target.&lt;ext&gt;）。</summary>
    public const string SettingsKeyPrefix = "convert.";

    /// <summary>txt 导出 UTF-8 filter（红线 6：soffice 默认文本编码非 UTF-8，中文必乱码）。</summary>
    public const string TxtUtf8Filter = "txt:Text (encoded):UTF8";

    /// <summary>
    /// csv 导出 filter（红线 6：StarCalc filter options 44=逗号、34=双引号、76=UTF-8 代码页；
    /// 精确串以 LibreOffice 实测锁定，验收=中文 xlsx→csv 用 UTF-8 读回无乱码）。
    /// </summary>
    public const string CsvUtf8Filter = "csv:Text - txt - csv (StarCalc):44,34,76";

    /// <summary>P1-D 操作记忆设置键（convert.last-target.&lt;ext&gt;）。</summary>
    public static string LastTargetKey(string extension) =>
        $"{SettingsKeyPrefix}last-target.{extension.TrimStart('.').ToLowerInvariant()}";
}
