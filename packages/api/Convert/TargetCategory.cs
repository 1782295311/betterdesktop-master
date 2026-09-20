namespace BetterDesktop.Shell.Convert.Contracts;

/// <summary>
/// 目标格式类别（转换菜单分组依据：文本类/文档类/表格类/图像类/音频类/视频类，2026-09-10 新增）。
/// 菜单按此分组显示「格式转换 ▸」内的目标；组序：Text → Document → Spreadsheet → Image → Audio → Video → Other。
/// </summary>
public enum TargetCategory
{
    /// <summary>文本类：md/html/txt/rtf/odt/tex/rst/org/mediawiki/asciidoc/textile/ipynb/docbook/man/context/texi/plain/yaml/json/xml 等。</summary>
    Text,

    /// <summary>文档类：docx/epub/pdf/pptx/odp 等封装文档。</summary>
    Document,

    /// <summary>表格类：csv/tsv/xlsx/ods。</summary>
    Spreadsheet,

    /// <summary>图像类。</summary>
    Image,

    /// <summary>音频类。</summary>
    Audio,

    /// <summary>视频类。</summary>
    Video,

    /// <summary>其他（加密/解密等安全操作目标）。</summary>
    Other,
}
