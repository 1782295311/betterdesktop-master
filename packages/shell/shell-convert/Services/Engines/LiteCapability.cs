namespace BetterDesktop.Shell.Convert.Services.Engines;

/// <summary>
/// lite（<c>convert-engine</c> 的进程内引擎）的能力**声明面** ——
/// Rust 侧 <c>native/convert-engine/src/lite.rs</c> 的 <c>LITE_INPUTS</c> / <c>LITE_TARGETS</c> 的 C# 镜像。
/// <para>
/// 【为什么 C# 也要一份】菜单显隐由 C# 决定（<c>ConvertMenuService.IsEngineReady</c> → <c>EngineRegistry.Resolve</c>），
/// 而路由由矩阵的 <c>Prefer</c> 决定 —— 两侧矩阵各自组装（<c>ConversionMatrix.cs</c> ↔ <c>matrix.rs</c>，
/// 本仓既有约定）。所以"lite 能做什么"必须两侧同口径，否则会出现"菜单显示了、点了说引擎缺失"。
/// </para>
/// <para>
/// 【漂移纪律】改这里必须同时改 <c>native/convert-engine/src/lite.rs</c>，反之亦然。
/// 两侧各有一条不变量测试钉住自己的那半（Rust: <c>no_lite_capable_edge_left_on_external_engines</c>）。
/// 语义边界：本表是**声明面**；真正能力以转换结果为准 —— 声明支持但执行失败时报 ConversionFailed，
/// 既不伪装成"引擎缺失"，也不被静默吞掉。
/// </para>
/// </summary>
public static class LiteCapability
{
    /// <summary>lite 能读的输入扩展名（不带点、小写）。</summary>
    public static readonly IReadOnlySet<string> Inputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // 文本/标记族
        "md", "markdown", "txt", "rst", "org", "textile", "dokuwiki", "wiki", "mediawiki",
        "ipynb", "html", "htm", "fb2", "opml", "csv", "json", "yaml", "yml",
        // 文档族（OOXML + 旧版 OLE 二进制）
        "docx", "docm", "doc", "odt", "rtf", "epub", "mobi", "dbk", "docbook", "jats", "xml",
        "tex", "latex",
        // 表格/演示
        "xlsx", "xlsm", "xls", "ods", "pptx", "pptm", "ppt",
        // 图片
        "png", "jpg", "jpeg", "bmp", "webp", "gif", "tiff",
        // PDF
        "pdf",
        // 压缩/加密
        "zip", "gz", "tar", "tgz", "cvlt",
    };

    /// <summary>lite 能写的目标格式（不带点、小写）。</summary>
    public static readonly IReadOnlySet<string> Targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // 文档/文本族 writer
        "md", "markdown", "html", "docx", "docm", "odt", "rtf", "epub", "ipynb", "mediawiki",
        "org", "docbook", "jats", "latex", "tex", "txt", "rst", "dokuwiki", "textile", "opendocument",
        "plain", "man", "texi", "context", "pptx", "pdf",
        // 表格族 writer
        "xlsx", "ods", "csv", "jsonl",
        // 图片 / PDF 提取
        "jpg", "jpeg", "png", "bmp", "gif",
        // 压缩/加密/解压/抽帧/探测
        "zip", "gz", "tar", "tgz", "unzip", "ungz", "untar", "enc", "dec", "collage", "jpgs", "probe", "json",
        // 清洗
        "clean",
    };

    /// <summary>lite 是否声明支持 <paramref name="inExt"/> → <paramref name="target"/>（入参可带点、任意大小写）。</summary>
    public static bool Supports(string? inExt, string? target)
    {
        var from = Norm(inExt);
        var to = Norm(target);
        if (from.Length == 0 || to.Length == 0)
        {
            return false;
        }

        // 源无关的目标：压缩打包（任意文件→包）/ 加密 / 文本清洗 —— 对照 Rust 侧同一分支，
        // 它们对**任意输入**成立，不能靠 Inputs 判定。
        if (to is "zip" or "gz" or "tar" or "tgz" or "enc" or "clean")
        {
            return !string.Equals(from, to, StringComparison.Ordinal);
        }

        if (string.Equals(from, to, StringComparison.Ordinal))
        {
            return false;
        }

        return Inputs.Contains(from) && Targets.Contains(to);
    }

    private static string Norm(string? ext) => (ext ?? string.Empty).TrimStart('.').ToLowerInvariant();
}
